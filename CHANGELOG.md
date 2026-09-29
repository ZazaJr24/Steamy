# Changelog

Each GitHub release uses the section of its version as its release notes.
Add a new `## x.y.z` section at the top before tagging.

## 0.2.9

### Fixed
- **Resume works again for every source.** A paused download continued looking for its manifests only in Ryuu's own work folder, so resuming a Zaza, Hubcap or DepotBox download always stopped with "No cached manifests found". Resume now searches every manifest work folder, re-reads the Lua script (depot decryption keys are kept) and writes a fresh key file before it continues.
- **Resume refreshes from the original source.** The manifest refresh before a resume fetched from Zaza no matter where the download came from; it now uses the source the job was started with (Zaza stays the fallback for old jobs).
- **Pause in the library download window.** The overlay gained a Pause button; the Start button becomes Resume and reuses the paused job instead of creating a duplicate row for the same game.
- **No more double starts.** Clicking Resume twice no longer runs two DepotDownloaderMod processes over the same files — neither on the Downloads page nor in the library overlay.
- **"Auto resume after restart" now does something.** The setting was saved but never passed to the queue restore; interrupted downloads are restored as queued (one click to continue) instead of always paused.
- Pure DepotDownloader jobs are no longer routed into the Mod resume path, where they failed with "DepotDownloaderMod not found".
- Sharing a ZIP no longer dies with the page when packing throws; the error is shown as a dismissable result.

### Improved
- **Share finds every game again.** Steamy's own `ryuu-workdir` (Lua + manifests of Ryuu downloads) is now scanned next to the shared manifest work directory, so games that were never installed also show up with their Lua and manifests.

## 0.2.8

### New
- **Share: every game, not only installed ones.** The Share page now finds the depot manifests of every game you have them for — Steam's `depotcache`, SteamTools' `config/depotcache` and `config/stplug-in` Lua scripts, and the Lua + manifests of Steamy's own downloads. Each Lua brings the manifests it pins with `setManifestid`, loose manifests are assigned to their game through the bundled app list, and everything is grouped into one entry per game. New filters: **Installed**, **Lua**, **Other**.
- **Game Fixes: 7z, RAR and ZIP.** Downloaded and manually picked fixes are extracted in any of the three formats — RAR5, solid and multi-part archives (`.part1.rar`, `.7z.001`) included. Password-protected fixes are opened with the usual fix-site passwords. **Apply ZIP** is now **Apply archive**.

### Fixed
- **Goldberg can be downloaded again.** SharpCompress 1.0.0 (0.2.7) decoded 7z about seven times slower, so the emulator install seemed to hang at "Extracting…"; SharpCompress 0.50.4 takes a few seconds again and has no known vulnerability. Empty files in the gbe_fork archive are no longer mistaken for encrypted ones.
- Goldberg falls back to the GitHub release page when the API limit (60 requests per hour) is reached, tries the current and older asset names (`emu-win-release-vs26.7z`, `emu-win-release.7z`), shows extraction progress and reports a Defender block when the emulator DLLs vanish right after extraction.
- Archive extraction never writes outside the target folder (entries with `../` or absolute paths are skipped).

### Removed
- **Depot Dumper** (page, sidebar entry, Dashboard tile and its Settings row). Sharing lives on the Share page; Lua files an earlier Depot Dumper wrote are still picked up there. The Dashboard tile is now **Denuvo Activation**.

## 0.2.7

### New
- **Share page** (new sidebar entry): every manifest you have in one list — Depot Dumper folders *and* the manifests Steam keeps for your installed games. Search, filter (All / New / Dumps / Steam library), **Select new** or **Select all**, then send everything in one go.
- **One commit per share**: a selection of any size goes to the dump repository as a single commit (Git Data API) with one ZIP per game plus a batch index, instead of one commit per file. Rate limits are waited out automatically; an empty repository is initialised on the first share.
- **Already shared is remembered**: Steamy keeps a local share history per repository, so "New" only shows games whose manifests changed since your last share — nothing is sent twice.
- **Save as ZIP**: export any selection into one ZIP (a folder per game plus `index.json`) to pass on anywhere — no token needed.
- **steamy.json in every pack**: app, depots, manifest ids and a SHA-256 per file, so a pack can be checked without unpacking it.
- **Privacy by default**: appmanifest files are cleaned before packing (`LastOwner` / SteamID and `LauncherPath` are removed) and no local folder names end up in the metadata.
- **Dashboard share card** with the number of games that have new manifests and a **Share all new** button.
- New app icon, window icon and version footer in the sidebar.

### Improved
- **Complete UI rework**: new Steamy Night / Steamy Day palettes, a real blue accent for all primary buttons, toggles and check boxes (it was grey), white text on accent buttons, and one shared design system (cards, headers, tiles, segmented controls, motion) instead of per-page copies.
- **Dashboard rebuilt**: cleaner header with date, hero for the running download or latest game, four KPI cards with their details visible, a responsive game grid that always shows full rows, a quick-actions grid for all tools, and side panels for Share, Downloads and System. On narrow windows the side panels move below the games.
- **.NET 9 everywhere**: Microsoft packages on 9.0.20, SDK pinned through `global.json`, `run.ps1` points at the `net9.0-windows` output, README badges fixed.
- **SharpCompress 1.0.0**: removes the known vulnerability warning of 0.39.
- Tool console runs no longer block the calling thread while waiting for the tool to exit.

### Developer
- New `tests/Steamy.Tests` project (xUnit) for the sharing core and the VDF parser — runs on Windows, Linux and macOS.
- New **CI** workflow builds the solution and runs the tests on every push and pull request.

## 0.2.6

### New
- **Depot Dumper with your own account**: a switch on the dump card adds a DepotDownloaderMod pass under your own Steam login, so licensed depots are dumped too. The password and the 2FA / Steam Guard code are typed in the tool's own console window — Steamy never asks for, reads or stores them.
- **Cache & temp in Settings**: one card shows the current size of artwork cache, manifest work folder, Ryuu archives and update packages, and **Clean now** removes them on request. The DepotDownloaderMod download and your dump folders are kept unless you clean those separately.
- **Refresh manifests on resume**: a paused download re-fetches the Lua and depot manifests before it continues, so a pause that lasted days does not run on outdated data. The delay is configurable (Settings › Downloads).

### Improved
- **.NET 9**: the app now targets `net9.0-windows` and the release build runs on the .NET 9 SDK; Microsoft.Data.Sqlite, DependencyInjection and ProtectedData moved to 9.0.9.
- **Frame-rate friendly downloads page**: the internet sparkline geometry is rebuilt once per second instead of twice, and progress bars stop animating on sub-pixel steps — less composition work while a download runs, smoother counters.
- Settings hints updated for the new .NET 9 output path.

## 0.2.5

### New
- **Depot Dumper** (new page, sidebar entry and Dashboard card): dumps the Lua script plus every depot `.manifest` of an app into one folder and can send the packed ZIP into a private dump repository through the GitHub contents API. The upload is write-only on purpose — a contributor needs a fine-grained token with `Contents: Read and write` on that one repository, so dumps can be sent but not read by other contributors, and only the repository owner can open them.
- Settings → **Depot Dumper & sharing**: repository owner, repository, branch, dump folder and the sharing token (encrypted with DPAPI, never part of the settings file or an export).

### Fixed
- A multi-depot download shows the real amount of data across **all** depots ("12.6 GB in 14 depots") instead of "4 depots". The size of every depot the tool reports is added up, and the running depot is added on top, so the number is exact and grows as the download progresses.
- **Stable DNS**: the resolver mode chosen in Settings is now actually applied. Every HTTP client the app creates resolves host names through the selected DoH endpoint, reuses cached answers for five minutes and silently falls back to the machine's resolver, so a slow or unreachable resolver can no longer break catalogs, sources, updates or artwork.

### Improved
- The speed readout is smoothed over several seconds and holds its value within four percent, so it no longer dances between samples; the internet graph uses a longer smoothing window as well.
- The downloaded/speed numbers prefer the measured rate over the tool's instantaneous text.
- Left and bottom gutters are wider on the Dashboard and the Downloads page so cards no longer sit against the window edge, and the game tiles no longer use negative margins.
- A game without portrait cover art now falls back to its wide store header instead of a bare placeholder tile.
- DLC Unlocker rebuilt from scratch: header with Apply/Restore, a strip for unlocker mode, game folder and DLL pack (with an import button and a pack-status dot), game list with avatars, a DLC list with a proper empty state, and a dismissable result banner.
- Game covers keep `HighQuality` scaling everywhere (no cached bitmaps) and the Depot Dumper page uses the same motion and card language as the Dashboard.

## 0.2.4

### Fixed
- API keys and tokens typed into Settings are stored straight away. Secrets are not part of the settings file, so the autosave never saw them: a key only landed on disk when the user pressed Save by hand, and "Test connection" kept answering "No API key stored" no matter what was in the box.
- "Test connection" now stores a key that was just typed before it runs, so it always checks the value in the box instead of an older one.
- Every test button reports the API's own error text ("· Key rejected: HTTP 403 — Invalid API key") instead of only an HTTP code, and a missing key now says where to type it.
- A key typed right before leaving the page is stored instead of being lost to the debounce.

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
