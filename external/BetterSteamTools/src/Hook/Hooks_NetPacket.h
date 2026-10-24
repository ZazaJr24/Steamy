#pragma once

#include "dllmain.h"

#include <cstdint>
#include <future>

namespace Hooks_NetPacket {
    void Install();
    void Uninstall();

    // Ask Steam for a manifest request code, as this account.
    //
    // Steam only issues a code for a depot the account can access, which is the
    // whole point: it reaches depots an anonymous session cannot. The code is
    // bound to (depot, manifest) and rotates a few minutes after issuance, so
    // it must be used promptly rather than stored.
    //
    // The future yields 0 for every failure — not sent, refused by Steam, or no
    // reply within the timeout — and is always fulfilled, never dropped.
    // `appId` may be 0; it only satisfies Steam's access check and does not
    // affect the resulting code.
    std::future<uint64_t> RequestManifestCode(AppId_t appId, uint32_t depotId, uint64_t gid);

    // Convenience for manual testing: resolves appId/gid from
    // Hooks_Manifest::LookupDepot, so the depot must have been seen during an
    // install or update this session. Logs the outcome and discards the code.
    bool ProbeManifest(uint32_t depotId);
}
