#pragma once

#include "dllmain.h"

// Hooks targeting steamui.dll:

namespace Hooks_SteamUI {
    void Install();
    void Uninstall();

    // Queues an appId for removal from the library UI
    void QueueRemoval(AppId_t appId);
    // Cancels a queued removal when the app is added again before the UI drains it.
    void CancelRemoval(AppId_t appId);

    // How many apps are actively downloading right now (AppStateFlags has the
    // UpdateRunning/UpdateStarted bit). Fed from FillInAppOverview on the UI
    // thread and safe to read from any thread. Used to tell "a real download is
    // happening" apart from Steam's idle scheduled-update retries - the depot's
    // own app id can't be checked directly because a game's DLC depots carry the
    // DLC app id while only the base game is marked downloading.
    size_t ActiveDownloadCount();
}
