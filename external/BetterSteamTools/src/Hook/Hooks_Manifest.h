#pragma once

#include "dllmain.h"

#include <cstdint>

namespace Hooks_Manifest {
    void Install();
    void Uninstall();

    // Look up the app id and manifest GID last seen for a depot.
    //
    // Populated from BuildDepotDependency, which is the only place a depot's
    // current GID passes through — Steam supplies it while working out what to
    // install, so a depot only appears here once its app has been installed or
    // updated in this session. The GID recorded is Steam's own, captured before
    // any setManifestid override is applied.
    //
    // Returns false if the depot has not been seen.
    bool LookupDepot(uint32_t depotId, AppId_t& outAppId, uint64_t& outGid);

    // Record a depot whose manifest is not archived yet (a definitive 404) and,
    // after a short quiet window, surface one MessageBox listing everything
    // collected. Safe to call from any thread. Called from the real-download path
    // (HandleSend) so the prompt only appears when a download actually needs a
    // manifest that isn't ready — never during the startup depot sweep.
    void ReportMissingManifest(uint32_t depotId, uint64_t gid);
}
