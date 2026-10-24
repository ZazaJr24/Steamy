#include "ManifestClient.h"
#include "OSTPlatform/include/Http.h"
#include "Utils/Config/Config.h"
#include "Utils/Config/LuaConfig.h"
#include "Utils/Logging/Log.h"

#include <algorithm>
#include <charconv>
#include <mutex>
#include <string_view>

namespace ManifestClient {

    // ── parsers ────────────────────────────────────────────────────
    using Parser = bool (*)(std::string_view body, uint64_t* out);

    static bool ParsePlainUint(std::string_view body, uint64_t* out) {
        uint64_t code = 0;
        auto [_, ec] = std::from_chars(body.data(), body.data() + body.size(), code);
        if (ec != std::errc{}) return false;
        *out = code;
        return true;
    }

    static bool ParseSteamRunJson(std::string_view body, uint64_t* out) {
        size_t key = body.find("\"content\"");
        if (key == std::string_view::npos) return false;
        size_t q1 = body.find('"', key + 9);
        if (q1 == std::string_view::npos) return false;
        size_t q2 = body.find('"', q1 + 1);
        if (q2 == std::string_view::npos) return false;
        return ParsePlainUint(body.substr(q1 + 1, q2 - q1 - 1), out);
    }

    // ── provider table ────────────────────────────────────────────
    //
    // Adding a new provider: add one row to kProviders below.
    // host / port / tls / path are all derived from the URL template
    // by Make() at compile time.

    struct Provider {
        std::string_view name;          // matches [manifest] url = "..."
        const char*      urlTemplate;   // one %llu (gid) — the pre-2026-09-09 shape
        // Optional depot-aware form: %u app, %u depot, %llu gid. Preferred
        // whenever the caller knows the depot, and required for correctness —
        // see kProviders below. nullptr for providers that only speak gid.
        const char*      urlTemplateEx;
        Parser           parse;
    };

    consteval Provider Make(std::string_view name, const char* url, const char* urlEx, Parser parse) {
        return {name, url, urlEx, parse};
    }

    // Valve made the request code depot-bound on 2026-09-09: it is derived from
    // (depot_id, manifest_id, time, secret), and the CDN answers 401 for a code
    // minted against any other depot. A gid-only request cannot name the depot,
    // so the server falls back to a free-to-play carrier and the resulting code
    // only works for 731/571/441 — every other depot 401s at the CDN and Steam
    // reports "Failed downloading 1 manifests".
    //
    // So the three-segment form is not an optimisation, it is the only shape
    // that works for ordinary depots. The gid-only template is kept solely as a
    // fallback for when the depot is genuinely unknown, and for the two
    // third-party providers that expose no depot-aware route.
    static constexpr Provider kProviders[] = {
        Make("opensteamtool", "https://manifest.opensteamtool.com/%llu",
                              "https://manifest.opensteamtool.com/%u/%u/%llu",  ParsePlainUint),
        Make("wudrm",         "http://gmrc.wudrm.com/manifest/%llu",
                              nullptr,                                          ParsePlainUint),
        Make("steamrun",      "https://manifest.steam.run/api/manifest/%llu",
                              nullptr,                                          ParseSteamRunJson),
    };

    static const Provider* g_active = &kProviders[0];   // opensteamtool
    static std::mutex      g_mutex;

    bool SetProvider(std::string_view name) {
        std::lock_guard<std::mutex> lock(g_mutex);
        for (const auto& p : kProviders)
            if (p.name == name) { 
                g_active = &p; 
                return true; 
            }
        return false;
    }

    const char* ActiveProviderName() {
        std::lock_guard<std::mutex> lock(g_mutex);
        return g_active->name.data(); 
    }

    // ── request ───────────────────────────────────────────────────

    void Shutdown() {
        std::lock_guard<std::mutex> lock(g_mutex);
    }

    // ── fetch ─────────────────────────────────────────────────────

    static bool FetchActive(uint64_t gid, uint64_t* outCode, AppId_t appId, AppId_t depotId) {
        const Provider& p = *g_active;
        const Config::ManifestTimeouts timeouts = Config::GetManifestTimeouts();

        // app_id does not change the code — it only satisfies Steam's access
        // check on the named depot — so 0 is a valid value to send and only the
        // depot has to be right.
        char urlLog[256];
        const bool depotAware = p.urlTemplateEx && depotId;
        if (depotAware)
            std::snprintf(urlLog, sizeof(urlLog), p.urlTemplateEx, appId, depotId, gid);
        else
            std::snprintf(urlLog, sizeof(urlLog), p.urlTemplate, gid);

        auto r = OSTPlatform::Http::Execute(
            L"GET",
            urlLog,
            nullptr,
            0,
            nullptr,
            timeouts.resolve,
            timeouts.connect,
            timeouts.send,
            timeouts.recv);

        // Log which shape was used: a gid-only request for a non-carrier depot
        // yields a code the CDN will reject, and that is otherwise invisible
        // until the download fails.
        LOG_MANIFEST_INFO("Manifest {} status={} gid={} depot={} shape={}",
                          p.name, r.status, gid, depotId, depotAware ? "app/depot/gid" : "gid-only");

        if (!r.ok || r.status != 200) return false;
        return p.parse(r.body, outCode);
    }

    // ── public ────────────────────────────────────────────────────

    bool FetchManifestRequestCode(uint64_t manifestGid, uint64_t* outRequestCode,
                                  AppId_t appId, AppId_t depotId)
    {
        std::lock_guard<std::mutex> lock(g_mutex);

        if (appId && depotId && LuaConfig::HasManifestCodeFuncEx()) {
            if (LuaConfig::CallManifestFetchCodeEx(appId, depotId, manifestGid, outRequestCode)) {
                LOG_MANIFEST_INFO("Manifest gid={} resolved via fetch_manifest_code_ex", manifestGid);
                return true;
            }
            LOG_MANIFEST_WARN("Manifest gid={} fetch_manifest_code_ex returned nil, trying fetch_manifest_code", manifestGid);
        }

        if (LuaConfig::HasManifestCodeFunc()) {
            if (LuaConfig::CallManifestFetchCode(manifestGid, outRequestCode)) {
                LOG_MANIFEST_INFO("Manifest gid={} resolved via manifest.lua", manifestGid);
                return true;
            }
            LOG_MANIFEST_WARN("Manifest gid={} lua returned nil, falling back to config", manifestGid);
        }

        return FetchActive(manifestGid, outRequestCode, appId, depotId);
    }
}
