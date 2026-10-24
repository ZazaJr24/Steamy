#pragma once

#include "Steam/Types.h"

#include <cstdint>

// ─────────────────────────────────────────────────────────────────
//  ManifestCache — pre-seed <steam>\depotcache from the OST archive.
//
//  OST fetches an archived depot manifest from the archive (base URL
//  baked into ManifestCache.cpp) and drops it into the ROOT depotcache
//  before Steam asks for a manifest request code. Steam then reads the
//  cached manifest and skips the request-code/CDN path entirely
//  (verified: a manifest present in <steam>\depotcache installs with no
//  "manifest request received" for that depot).
//
//  All calls are best-effort: a miss (404), a bad body, or a disabled
//  feature simply leaves no file, and Steam falls through to the
//  existing request-code path. Safe to call from a detached worker.
// ─────────────────────────────────────────────────────────────────
namespace ManifestCache {

    // Ensure <steam>\depotcache\<depot>_<gid>.manifest exists, fetching it
    // from {archive}/m/<depot>/<gid> if missing. Returns true when
    // the file is present afterwards (already cached or freshly written).
    // Returns false — quietly — when the feature is off, the manifest is not
    // archived, or the body fails validation. Never throws.
    //
    // recvTimeoutMs bounds the body-receive wait. It defaults to the generous
    // value used by the detached (async) callers; the synchronous pre-seed in
    // BuildDepotDependency passes a short value so one slow/missing manifest
    // cannot stall Steam's thread. 0 keeps the built-in default.
    //
    // outNotArchived, when provided, is set to true ONLY when the archive gave a
    // definitive 404 (the manifest is not archived yet) — not for timeouts or
    // other transient failures. Callers use it to tell "queued, try later" apart
    // from a passing blip.
    //
    // bypassNegativeCache forces a fresh fetch even for a depot:gid that recently
    // 404'd. A depot that returns 404 is remembered for a short TTL and skipped
    // (no GET) on subsequent calls — this stops Steam's ~30s scheduled-update
    // retries from hammering the archive. Pass true only when a download is
    // actually starting, so a just-supplied manifest is picked up immediately.
    bool EnsureCached(AppId_t app, uint32_t depot, uint64_t gid,
                      uint32_t recvTimeoutMs = 0, bool* outNotArchived = nullptr,
                      bool bypassNegativeCache = false);

}
