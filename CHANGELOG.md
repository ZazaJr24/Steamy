# Changelog

Each GitHub release uses the section of its version as its release notes.
Add a new `## x.y.z` section at the top before tagging.

## 0.2.3

### Fixed
- Hubcap and DepotBox downloads work now. The Lua parser only accepted `setManifestid(depot, "id")`, but both services write a third argument (`setManifestid(depot, "id", size)`), so every depot was silently dropped and the download stopped with "no depots found". A depot now also counts when its Lua has no key.
- DepotBox uses its documented one-request flow: `/api/direct-download?appid=` returns a ZIP with the Lua *and* every depot `.manifest`. The Lua-only endpoint stays as a fallback, so the source still works when the package cannot be built.
- A source that cannot be reached no longer locks the Start button. Only a definite answer from the API ("no manifest for this app", "no key configured") disables it; anything else says "you can still try".
- A missing API key now names the exact Settings section instead of failing with a technical message.
- Hubcap sends its key as both `Authorization: Bearer` and `X-API-Key`, and an answer that only contains the Lua or only the ZIP is no longer treated as a failure.
- Game covers are sharp on scaled displays again. The `BitmapCache` on the cover frames rendered the art at 1x and WPF then scaled that bitmap, which is exactly what made the covers blurry.

### Improved
- Settings now looks like the Dashboard: aurora header with the live save state and the page actions, icon section headers, cards that light up on hover and a staggered fade-in.
- DepotBox has its own **Test connection** button: it calls the free `/api/stats` endpoint and then shows your own request counts for the stored key.
- Game Fixes, Hubcap and DepotBox cards show where to get the key, and every test button now carries an icon and a shorter label.

### New
- Settings: DepotBox test connection, usage line and a clearer credential status per service.

## 0.2.2

### Fixed
- Hubcap works again: the Lua now comes from its own `/api/v1/lua` endpoint and the depot manifests from `/api/v1/manifest`, exactly like the Hubcap API documents it
- Hubcap availability check no longer spends daily quota — it uses the free `/api/v1/status` endpoint instead of downloading the manifest
- Hubcap failures show the API's own error message instead of a bare HTTP code
- DepotBox availability check now uses the correct lightweight API endpoint instead of triggering a full file generation
- DepotBox understands every response shape (zip archive, plain Lua, JSON with a file link or error) and sends the API key in both accepted header styles
- HTTP timeout increased to 15 minutes to match DepotBox API recommendations

### Improved
- Hubcap game catalog loads through the free `/api/v1/library` endpoint with pagination — the complete list in a few requests

## 0.2.1

### New
- Completely redesigned Dashboard: cleaner layout, big hero banner, slim stats bar and one tidy System card
- Game covers with titles below the art and a smooth hover glow

### Improved
- Much smoother UI: no more lag when hovering or scrolling
- Sharp text and covers everywhere (no more blur)
- Soft fade-in when opening the Dashboard

### Fixed
- DepotBox source works again

## 0.2.0

### New
- DepotBox as a new manifest source (replaces Resonance)
- Dashboard redesign with System Overview, manifest sharing and better visuals
- Share Manifests section to help the community

### Improved
- Dashboard cards with hover animations and accent highlights
- Download dialog is now cleaner and more minimalistic
- API keys are stored more securely with encrypted credential fallback

### Fixed
- Hubcap downloads work again (correct endpoint)
- Hubcap API key is now resolved properly everywhere

## 0.1.5

### Fixed
- Ryuu downloads no longer fail with "No Ryuu auth code configured" when the key is saved in Settings; the encrypted credential store is now checked first instead of the plain-text settings file

## 0.1.4

### New
- DLC Unlocker page completely redesigned: clean minimal two-column layout with games list and DLC checklist side by side, toolbar in the header row, no cards or borders cluttering the view
- Denuvo Activation page redesigned: all settings in one compact card — proxy DLL dropdown, Capcom and Debug checkboxes on a single row, expandable launch script section with browse-for-exe support
- Browse executable option added to Denuvo launch script settings

### Improved
- DLC Unlocker: removed Advanced Settings expander and all secondary options from the main view for a cleaner experience
- DLC Unlocker: game list shows only game names, no AppID badges
- DLC Unlocker: DLC list shows only checkbox + name, no extra metadata
- Denuvo Activation: options use compact checkboxes instead of tall toggle-switch cards
- Overall UI is more minimal, modern and consistent across all tool pages

## 0.1.3

### Fixed
- Goldberg: the emulator download finds the Windows build again after gbe_fork renamed its release files; if nothing matches, the message lists the release's files.

## 0.1.2

### New
- Downloads redesigned like a game launcher: the current download is featured in a large banner on its game art with a big progress bar, percentage, speed, time left, size and the live internet curve.
- "Up next" shows the queue and "Recently finished" the done and failed downloads, as clean rows with game art.
- With nothing running, the banner offers "Start next" for the first waiting download.

## 0.1.1

### New
- Auto-updater: at startup Steamy checks GitHub for a new version and shows the current and latest version — "Update now" downloads it and restarts. "Check now" in Settings does the same on demand.
- Live download panel: internet speed with a 30-second graph, combined download rate, time left and overall progress.
- Download progress moves smoothly in 0.1 % steps instead of jumping once per finished file; several depots are combined into one overall value.
- Speed, size estimate and time left for every download.
- Settings: connections per download (-max-downloads) and Lancache support.

### Improved
- Redesigned download cards: state stripe, steady digits, current file, smooth animations and a clearer empty state.
- Clearer error messages, taken from the tool's own output.
- Finished downloads show their verified size.
- The stuck-download watchdog now also counts disk writes, so a single large file no longer stops it.

### Fixed
- Depots were sometimes reported as failed although they had finished.
- The Downloads page jumped when pressing Start or Pause.
- Restored downloads were labelled "Demo fallback".
