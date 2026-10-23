# Game page +: BetterSteamTools source / ZIP flow — 22 October 2026

The glass **+** beside Play is shown for every installed game. It opens an embedded two-step BetterSteamTools installation sheet:

1. Choose a metadata source or **Local ZIP / Lua** and browse/drop your package.
2. Review the Steam setup, then explicitly choose **Add to Steam**.

The selected game name and App ID are passed into the existing installation sheet; the BetterSteamTools method is preselected and its method chooser is skipped. Opening + only opens the source step. It does not write a native `shortcuts.vdf` entry, dismiss an already-registered game, restart Steam, or immediately import anything. Back on the source step closes the sheet and restores focus to +. A paused DepotDownloader job is not resumed by this action.

Remote selections use `IBetterSteamToolsService.AddFromSourceAsync`. Local packages use `ImportAsync`. For a local game without a matched positive Steam App ID, the source step asks for a local package and lets that package identify the game. The import's returned App ID is retained for the later user-triggered **Open in Steam** action. ZIPs in this flow are Steam metadata packages containing Lua/manifests, rather than archives of installed game binaries. Existing backend detection, verification, progress, cancellation and error handling remain in use.

The older native shortcut helper is no longer registered with the app and the game-page command no longer calls it. The page subscribes to the setup request while loaded and detaches it on unload; changing games dismisses a stale sheet. The same page retains its Install workflow for uninstalled catalog games.

This build also includes progressive Store information: description/preview arrive without waiting for News/reviews, auxiliary data preserves unchanged preview collections, and complete overviews are bounded to the 64 most recently visited titles. The earlier blocked-server regression and new cancellation/cache tests are described in `steamy-0.8-visible-plus-progressive-info.md`; its native-shortcut action description is superseded by this document.

## Background-only validation

The user prohibited opening applications. No Steamy window, Steam client, UI smoke test, screenshot capture or startup probe was launched for this revision. The source-step UI regression was updated for future interactive verification but was not run. The release command selected only windowless game-detail, metadata, scrolling and BetterSteamTools backend checks. Backend tests use isolated temporary fake Steam roots, fake HTTP and inert backend files; no real client or account is changed.

`bettersteamtools-plus-release.trx`: 42/42 selected checks passed. They include + request routing for local and Steam installations, progressive details and cache behavior, source import, backend verification and own ZIP import with both a known and package-detected App ID. Static package checks and release hashes are recorded in `artifacts/performance-refresh/release-bettersteamtools-plus-validation.json`.

Portable: `artifacts/performance-refresh/portable-bettersteamtools-plus/Steamy.exe`, self-contained .NET 10.0.13 with Discord disabled. Keep its entire directory together. The portable has not been launched automatically. Whole-app FPS/RAM/startup acceptance and final visual interaction remain unverified; the broader performance goal remains active.
