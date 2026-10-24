#include "Utils/SteamMetadata/ManifestDonor.h"

#include "Hook/Hooks_Manifest.h"
#include "Hook/Hooks_NetPacket.h"
#include "Hook/Hooks_Package.h"
#include "OSTPlatform/include/Http.h"
#include "OSTPlatform/include/Thread.h"
#include "Utils/Config/Config.h"
#include "Utils/Logging/Log.h"

#include <algorithm>
#include <atomic>
#include <charconv>
#include <chrono>
#include <random>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_set>
#include <vector>

namespace ManifestDonor {
namespace {

    std::atomic<bool> g_running{false};

    // Total across the process lifetime, so a long Steam session cannot
    // accumulate an unbounded number of Steam calls one cycle at a time.
    uint32_t g_mintedThisSession = 0;

    constexpr const char* kDefaultBaseUrl = "https://manifest.luastools.xyz";

    struct Wanted {
        AppId_t  appId = 0;      // may legitimately be 0 — see API.md
        uint32_t depotId = 0;
        uint64_t gid = 0;
    };

    // The wanted list is large (~5 MB) and barely changes minute to minute, so
    // it is pulled on its own slow cadence and reused across the faster mint
    // cycles. Freshness within a cycle comes from the per-manifest HEAD probe,
    // not from re-downloading the whole list.
    std::vector<Wanted>                   g_wantedCache;
    std::chrono::steady_clock::time_point g_wantedFetchedAt{};   // default = never

    std::string BaseUrl() {
        const std::string configured = Config::GetDonateSettings().url;
        std::string base = configured.empty() ? kDefaultBaseUrl : configured;
        while (!base.empty() && base.back() == '/') base.pop_back();
        return base;
    }

    // Parses one "a:b:c" line. Returns false on anything unexpected so a single
    // malformed row cannot silently become a request for depot 0.
    bool ParseWantedLine(std::string_view line, Wanted& out) {
        auto field = [](std::string_view s, uint64_t& v) {
            if (s.empty()) return false;
            return std::from_chars(s.data(), s.data() + s.size(), v).ec == std::errc{};
        };

        const size_t c1 = line.find(':');
        if (c1 == std::string_view::npos) return false;
        const size_t c2 = line.find(':', c1 + 1);
        if (c2 == std::string_view::npos) return false;

        uint64_t app = 0, depot = 0, gid = 0;
        if (!field(line.substr(0, c1), app))              return false;
        if (!field(line.substr(c1 + 1, c2 - c1 - 1), depot)) return false;
        if (!field(line.substr(c2 + 1), gid))             return false;
        if (!depot || !gid) return false;    // app_id 0 is valid; these are not

        out.appId   = static_cast<AppId_t>(app);
        out.depotId = static_cast<uint32_t>(depot);
        out.gid     = gid;
        return true;
    }

    std::vector<Wanted> FetchWanted() {
        std::vector<Wanted> wanted;

        // Pull the WHOLE missing set (no ?limit): the server serves all of it,
        // edge-cached, so every donor sees everything and mints only what it owns.
        // Raise the body cap far above the 256 KB default so a large list is never
        // silently truncated (that would hide most depots and starve minting).
        const std::string url = BaseUrl() + "/manifestwanted";
        constexpr uint32_t kMaxWantedBytes = 32u * 1024u * 1024u;
        auto r = OSTPlatform::Http::Execute(L"GET", url.c_str(), nullptr, 0, nullptr,
                                            5000, 5000, 10000, 30000, kMaxWantedBytes);

        if (!r.ok || r.status != 200) {
            LOG_MANIFEST_DEBUG("Donor: wanted list unavailable (ok={} status={})", r.ok, r.status);
            return wanted;
        }

        size_t start = 0;
        while (start < r.body.size()) {
            size_t end = r.body.find('\n', start);
            if (end == std::string::npos) end = r.body.size();

            std::string_view line(r.body.data() + start, end - start);
            while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.remove_suffix(1);

            Wanted w;
            if (!line.empty() && ParseWantedLine(line, w)) wanted.push_back(w);
            else if (!line.empty())
                LOG_MANIFEST_DEBUG("Donor: ignoring malformed wanted line \"{}\"", std::string(line));

            start = end + 1;
        }

        LOG_MANIFEST_DEBUG("Donor: {} wanted entr(ies)", wanted.size());
        return wanted;
    }

    void Submit(const std::string& body, size_t count) {
        const std::string url = BaseUrl() + "/manifestcode/submit";
        auto r = OSTPlatform::Http::Execute(
            L"POST", url.c_str(), body.data(), static_cast<uint32_t>(body.size()),
            L"Content-Type: text/plain\r\n");

        if (r.ok && r.status == 200)
            LOG_MANIFEST_INFO("Donor: submitted {} code(s)", count);
        else
            LOG_MANIFEST_WARN("Donor: submit failed (ok={} status={}) for {} code(s)",
                              r.ok, r.status, count);
    }

    // Ask the archive whether a manifest is already stored, without minting.
    // The store serves these permanently cached at the edge, so a hit is a free
    // CDN response and never touches the origin; a miss is a cheap index lookup.
    // Returns true ONLY on a definitive 200 — a 404, a timeout, or any transport
    // error falls through to false so a network blip can never make us skip a
    // real gap (better to occasionally re-mint than to leave a manifest missing).
    bool AlreadyCached(uint32_t depot, uint64_t gid) {
        const std::string url =
            BaseUrl() + "/m/" + std::to_string(depot) + "/" + std::to_string(gid);
        auto r = OSTPlatform::Http::Execute(L"HEAD", url.c_str(), nullptr, 0, nullptr,
                                            5000, 5000, 5000, 5000);
        return r.ok && r.status == 200;
    }

    // Refresh g_wantedCache from the server if it has gone stale (or was never
    // fetched). Shuffled on every refresh so that thousands of donors do not all
    // march the identical list top-down in lockstep — spreading the order means
    // that by the time one donor reaches a given manifest another has often
    // already supplied it, and the HEAD probe then skips it.
    void RefreshWantedIfStale(const Config::DonateSettings& cfg) {
        const auto now = std::chrono::steady_clock::now();
        const bool never = g_wantedFetchedAt.time_since_epoch().count() == 0;
        const auto ageSecs =
            std::chrono::duration_cast<std::chrono::seconds>(now - g_wantedFetchedAt).count();

        if (never || ageSecs >= static_cast<long long>(cfg.wantedRefreshSecs)) {
            std::vector<Wanted> fresh = FetchWanted();
            if (!fresh.empty()) {
                static std::mt19937 rng{std::random_device{}()};
                std::shuffle(fresh.begin(), fresh.end(), rng);
                g_wantedCache = std::move(fresh);
                g_wantedFetchedAt = now;
            } else if (never) {
                // First fetch failed outright: mark the attempt so we honour the
                // cadence before retrying, rather than hammering on every cycle.
                g_wantedFetchedAt = now;
            }
        }
    }

    void RunCycle(const Config::DonateSettings& cfg) {
        const auto owned = Hooks_Package::GetOwnedDepots();
        if (!owned.ready) {
            // Not "owns nothing" — the license list has not been resolved yet.
            LOG_MANIFEST_DEBUG("Donor: owned depots not resolved yet, skipping cycle");
            return;
        }
        // maxMintsPerSession == 0 means unlimited (mint for the whole session).
        if (cfg.maxMintsPerSession && g_mintedThisSession >= cfg.maxMintsPerSession) {
            LOG_MANIFEST_DEBUG("Donor: session cap reached ({}), idle until restart",
                               cfg.maxMintsPerSession);
            return;
        }

        const std::unordered_set<uint32_t> ownedSet(owned.depots.begin(), owned.depots.end());

        // Pull the list on its own slow cadence and reuse it; freshness comes
        // from the per-manifest HEAD probe below, not from re-downloading 5 MB.
        RefreshWantedIfStale(cfg);
        const std::vector<Wanted>& wanted = g_wantedCache;
        if (wanted.empty()) return;

        std::string body;
        size_t submitted = 0, skipped = 0, refused = 0, alreadyHave = 0;
        bool   minted = false;   // has any mint happened yet this cycle?

        for (const Wanted& w : wanted) {
            if (!g_running) break;
            if (submitted >= cfg.maxMintsPerCycle) break;
            if (cfg.maxMintsPerSession && g_mintedThisSession >= cfg.maxMintsPerSession) break;

            // Not owned: say nothing. Silence is what keeps the rest of the
            // library undisclosed — only matches are ever reported.
            if (!ownedSet.count(w.depotId)) { ++skipped; continue; }

            // Already in the archive (likely supplied by another donor since the
            // list was fetched). Skip the Steam call entirely — this is the whole
            // point: a manifest is minted about once, not once per owner. The
            // probe is a free edge hit, and the skip happens before the mint
            // spacing so it costs no time.
            if (AlreadyCached(w.depotId, w.gid)) { ++alreadyHave; continue; }

            // Spacing goes before each mint rather than after it. The gap
            // between consecutive Steam calls is identical either way, but
            // nothing is left sleeping after the last one — a trailing wait is
            // pure delay on a batch whose codes are already rotating.
            if (minted && cfg.minMintIntervalMs) {
                std::this_thread::sleep_for(std::chrono::milliseconds(cfg.minMintIntervalMs));
                if (!g_running) break;
            }
            minted = true;

            // The server may legitimately send app_id 0; it only satisfies
            // Steam's access check and is not part of the code. It is accepted
            // most of the time but not always — an observed request answered
            // eresult=8 (InvalidParam) for a depot this account owns — so
            // prefer a real app id whenever one is known.
            AppId_t appId = w.appId;
            if (!appId) {
                AppId_t knownApp = 0;
                uint64_t knownGid = 0;
                if (Hooks_Manifest::LookupDepot(w.depotId, knownApp, knownGid) && knownApp) {
                    appId = knownApp;
                    LOG_MANIFEST_DEBUG("Donor: depot={} app_id 0 -> {} (from BuildDepotDependency)",
                                       w.depotId, appId);
                }
            }

            const auto mintedAt = std::chrono::steady_clock::now();
            auto fut = Hooks_NetPacket::RequestManifestCode(appId, w.depotId, w.gid);
            const uint64_t code = fut.get();
            ++g_mintedThisSession;

            if (!code) { ++refused; }
            else {
                const auto ageMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - mintedAt).count();
                body += std::to_string(w.depotId) + ':' + std::to_string(w.gid) + ':' +
                        std::to_string(code) + '\n';
                ++submitted;
                LOG_MANIFEST_DEBUG("Donor: minted depot={} gid={} in {}ms", w.depotId, w.gid, ageMs);
            }
        }

        LOG_MANIFEST_INFO("Donor: cycle done — {} minted, {} already archived, {} not owned, "
                          "{} refused by Steam ({} this session)",
                          submitted, alreadyHave, skipped, refused, g_mintedThisSession);

        // Sent as one batch the moment the cycle ends. Codes rotate within
        // minutes, so anything that delays this wastes the mint.
        if (submitted) Submit(body, submitted);
    }

    uint32_t WorkerMain() {
        LOG_MANIFEST_INFO("Donor: started");

        while (g_running) {
            const auto cfg = Config::GetDonateSettings();

            // Re-read every cycle so toggling [donate] off takes effect without
            // a Steam restart, matching how the config watcher behaves.
            if (cfg.enabled) {
                RunCycle(cfg);
            }

            // Slept in short slices so Stop() is honoured promptly rather than
            // after a full interval.
            for (uint32_t i = 0; i < cfg.intervalSecs && g_running; ++i)
                std::this_thread::sleep_for(std::chrono::seconds(1));
        }

        LOG_MANIFEST_INFO("Donor: stopped");
        return 0;
    }

} // namespace

void SubmitCapturedCode(uint32_t depotId, uint64_t gid, uint64_t code) {
    if (!depotId || !gid || !code) return;
    if (!Config::GetDonateSettings().enabled) return;

    // Fire-and-forget: the caller is on Steam's network thread and must not
    // block on an HTTP round trip.
    //
    // No existence probe here (unlike the donor): a captured code is free — it
    // was already minted by a real download — so there is no scarce Steam call to
    // protect. The server dedups cheaply on its side (ingest checks store.has()
    // before ever touching the CDN), so a submit for an already-archived manifest
    // is a no-op there; a HEAD first would only add a round trip and, on a real
    // miss (the case that matters), be pure overhead.
    OSTPlatform::Thread::StartDetached([depotId, gid, code]() -> uint32_t {
        std::string body = std::to_string(depotId) + ':' + std::to_string(gid) + ':' +
                           std::to_string(code) + '\n';
        Submit(body, 1);
        return 0;
    });
}

void Start() {
    if (!Config::GetDonateSettings().enabled) {
        LOG_MANIFEST_INFO("Donor: disabled by config");
        return;
    }
    if (g_running.exchange(true)) {
        LOG_MANIFEST_WARN("Donor: already running");
        return;
    }
    if (!OSTPlatform::Thread::StartDetached(WorkerMain)) {
        LOG_MANIFEST_WARN("Donor: failed to start worker thread");
        g_running = false;
    }
}

void Stop() {
    g_running = false;
}

} // namespace ManifestDonor
