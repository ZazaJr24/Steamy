# Changelog

Each GitHub release uses the section of its version as its release notes.
Add a new `## x.y.z` section at the top before tagging.

## 0.4.10 — 2026-10-02

- Minimal SteamTools workspace: one large, quiet drag-and-drop target for ZIP, Lua and manifest files, a simple title, a subtle background wash and a file picker. Removed the large game card, boxed status panels, decorative badges and explanatory paragraphs from the initial view.
- App ID / Steam link input and source selection sit behind a compact expander. Steam setup and detected metadata sit behind a second expander. Prefilled game IDs reveal the game controls; a missing backend reveals setup automatically.
- Existing automatic sources, configured API providers, backend checksum checks, safe file import, progress and cancellation remain intact. Status and Cancel stay inside the real Windows viewport while scrolling.
- Updated native Windows checks cover the uncluttered initial view, expanded controls, required setup, prefilled games and cancellation in the minimum supported window. README screenshots come from the actual Windows app.

## 0.4.9 — 2026-10-02

- Redesigned only Tools → BetterSteamTools, using the actual LuaTools Windows interface as the layout reference. Large transparent surfaces sit over a restrained blue/violet/teal light gradient; the rest of Steamy keeps its existing appearance.
- Prominent App ID / Steam link input, keyboard Enter action, source selection and Add game to Steam button. A larger ZIP/Lua/manifest drop target gives feedback for supported file drops, with a separate file picker.
- Game and file import panels sit side by side on wider windows and flow vertically with native scrolling on smaller windows. Compact Steam detection, folder selection, backend install/update and Start Steam controls remain below.
- Existing automatic sources, configured API providers, checksum verification, safe metadata imports, progress and cancellation remain intact. Operation status and Cancel stay visible while scrolling. Steam/backend labels reflect actual detection; disabled controls do not imply availability or successful installation.
- Includes the BetterSteamTools installation and metadata import features introduced in 0.4.8. Windows layout checks cover both themes, responsive panel placement and reachable controls; the README uses a real Windows screenshot.

## 0.4.8 — 2026-10-01

- README statistics restored: release downloads, stars, forks and current version.
- New Tools → BetterSteamTools page with Steam detection, one-click official backend installation/update and SHA-256 verification.
- Drag/drop or choose ZIP, Lua and manifest files for immediate import. App IDs are detected from configuration; an explicit ID selects a game from a multi-game bundle. Steamy Share hashes are checked, and standalone manifests are cached without inventing a game ID.
- Add to Steam from an App ID or official store link, with automatic source detection or Sushi, Zaza, Hubcap, Ryuu and DepotBox selection using existing credentials. Games details can open the tool with the selected App ID.
- Metadata uses Steam's config/stplug-in and root depotcache folders. Other backend settings remain intact, originals are backed up, failed writes roll back, cancellation is supported and unsupported Lua code is rejected without evaluation.
- Normal and optional four-part hotfix updates remain supported. The only uploaded release asset is the versioned app ZIP; its SHA-256 checksum is in the release info.

## 0.4.7 — 2026-10-01

- Neutral black transparent surfaces, a quiet sidebar and frameless, compact Games, Fixes and upcoming cards.
- Upcoming-only Spotlight with days, hours, minutes and seconds from Steam's published release schedules. Countdown sits at the bottom left and refreshes immediately when returning to Discover; unknown times keep honest day/TBA labels.
- Removed Check sources and View game from Spotlight. Up to eight previews on the right, calm artwork transitions and a small rotating indicator before the next slide.
- Gentle gallery entrance and hover motion with fixed hit areas; pause, inactivity, reduced effects and scroll position remain respected.
- Supports regular three-part releases and optional four-part hotfixes, including revision comparisons and the next regular release after a hotfix. The updater prefers the ZIP matching the release tag and excludes source archives.
- Release assets contain only the versioned app ZIP. Removed duplicate Steamy-latest, additional Steamy-source and separate checksum uploads. The exact app ZIP SHA-256 checksum appears directly in this version's GitHub release info after packaging; GitHub's tag source archives remain available.
- Includes the local ZIP/Lua package, pinned Resume, atomic Share export, searchable settings and download fixes from 0.4.6 and 0.4.6.1.

## 0.4.6.1 — 2026-10-01

- Neutral black transparent surfaces, a quiet sidebar and frameless Games, Fixes and upcoming cards.
- Compact Spotlight and cover grids with shorter titles and descriptions, calm entrance transitions and gentle hover motion with fixed hit areas.
- Upcoming-only Spotlight with a real bottom-left days/hours/minutes/seconds countdown from Steam's published release schedules. Date-only releases keep honest day/TBA labels.
- Removed Check sources and View game from Spotlight. Up to eight previews on the right, with a small rotating indicator before the next slide; pause, inactivity, reduced motion and scrolling remain respected.
- Hotfix revision numbers retained by the version display and updater; source archives are excluded from update downloads.
- Updated build retains version 0.4.6.1. Download and extract the app ZIP again if already installed. Upgrades from 0.4.6 also require a manual ZIP update because its updater ignores revision numbers.

## 0.4.6 — 2026-10-01

- Larger game and fixes galleries, transparent captions and translucent blue surfaces over a richer blue/violet ambient background throughout the app.
- Upcoming-only Steam Spotlight with three cards, bottom-left countdowns beside the download actions, a larger title, artwork previews, pause/resume and stable native scrolling.
- Optional persistent Dashboard search; compact General settings with quieter autosave status.
- Your package: safe local ZIP/Lua metadata import, multi-game selection, verified Steamy hashes and independent preparation copies.
- Local download filtering and pinned manifest selections retained through Resume after deleting the original package.
- Share ZIP exports report actual counts and omitted games, fail clearly on missing files, support cancellation and preserve existing ZIPs with atomic replacement.
- Windows regressions cover timer rotations, pause/resume, responsive pages, aligned dialogs and package persistence. Real Windows screenshots and complete release source are included.

## 0.4.5

### Changed
- Transparent game and fix captions replace the solid footer plates; cover artwork has rounded corners on all sides, with keyboard focus outlines retained.
- A deeper charcoal palette, restrained translucent panels and matching Fluent controls carry the same theme across the app.
- Spotlight has a larger artwork canvas, clearer title and actions, and compact navigation inside the artwork. Six new/upcoming games follow it.
- Removed search-history controls and query recording from Games and Dashboard, and removed Dashboard's installed-library, history and download-summary blocks.
- Public artwork keeps up to 2048 pixels of original detail without enlarging smaller images; native Dashboard scrolling and five-second Spotlight rotation remain in place.

## 0.4.4

### Fixed
- Animated scrolling now writes its final base offset before removing the WPF animation clock, preventing an immediate return to the top. Interrupting a scroll retains the current position as well.
- Dashboard uses a single native scroll presenter with a reserved scrollbar column, without a second animated offset controller. Wheel, touchpad and scrollbar input retain native scrolling behavior.
- Spotlight still rotates every five seconds with artwork fades and fixed geometry; keyboard navigation can still reveal controls while automatic focus requests remain suppressed.
- Windows regression checks explicitly run real scroll animation clocks even when Windows motion is disabled in CI. They also hold the Dashboard position through two timed Spotlight rotations, then exercise upward wheel input, thumb tracking and a library/activity refresh.

## 0.4.3

### Fixed
- Automatic Spotlight and binding focus requests no longer bring a manually scrolled Dashboard back to the top. Spotlight keeps a fixed height during rotation; explicit keyboard navigation still reveals focused controls.
- Capture the blurred game gallery in local coordinates, removing the duplicated page margin that shifted the background right and down when opening a game.
- Keep source controls locked while resuming a saved download, including when the source step is hidden; a new selection explicitly unlocks them again.
- Load game-fix dialog artwork asynchronously through the shared cached artwork service, with protection against late results from a previously opened game.

### Improved
- An artwork-led Dashboard with readable overlay controls and new/upcoming games directly below Spotlight.
- Compact source selection with two free community cards, quieter credential-provider rows and a visible selection check.
- Restored the large featured-download artwork, progress and network-activity card at common window sizes; existing queue, pause/resume and recovery controls remain available.
- Responsive game-fix cards, a clear search header, keyboard activation and reduced-effect support.
- Windows regression checks for scroll stability during Spotlight changes and correctly aligned frozen backgrounds.

## 0.4.2

- Published the corrected app under a new version so installations running 0.4.1 can detect it through the updater.
- Includes the restored compact Source → Depots/manifest → Location download dialog with a blurred gallery background.
- Includes scrolling fixes for game cards and closed manifest selectors, while retaining the selected manifest version.
- Includes the five-second Spotlight rotation with artwork preloading, improved image fallback and the refreshed DLC selection page.

## 0.4.1

- Added a direct Spotlight Download action that opens source selection instead of the Steam store.
- Rebuilt the DLC Unlocker as a compact workspace with virtualized game/DLC lists, visible selection and fixed actions. Older DLC requests can no longer overwrite a newer game selection.
- Fixed scroll reversal during animation, stopped repeated card entrance flicker, and improved artwork fallback after CDN timeouts or disk-cache failures.

### Changed
- Spotlight now changes every five seconds without getting stuck after a card receives keyboard focus.
- Closed selectors and non-scrolling template viewers no longer intercept the surrounding page's wheel input; dashboard and DLC pages explicitly use their own scrolling host.
- Replaced the dashboard slogan and greeting block with full-width Spotlight artwork, clear release details, and compact library/continue/download cards.
- Download setup retains the compact dialog with source, depots/manifest, and location steps over a blurred gallery.
- Depot rows display the selection checkbox on the right, all source depots remain accessible in a virtualized list, and select-all updates the summary once.
- Successful download starts save the queue row before opening Downloads; the registered transfer continues after leaving setup.

### Added
- Optional public Steam app-info metadata through a cached SteamCMD mirror: depot names, platforms, languages, DLC/shared content, and SteamDB links.
- Steam sizes, compressed sizes, build labels and branches appear only when the manifest ID exactly matches the selected source version. Metadata never adds source depots, changes versions or refreshes resume pins.
- Bounded requests, shared lookups, timeout/outage fallback and cache limits keep metadata optional.
- Windows checks for compact setup steps, right-hand checkbox binding, source snapshot preservation and saved-first download navigation.

### Documentation
- Refreshed Windows dashboard and download-page screenshots, keeping the README download link on Latest.

## 0.3.10

### Added
- A three-step download assistant: source, source-provided depots/manifest versions, then location with final target and free space.
- Favorites for installed and catalog games, a Favorites filter, and recently opened games recorded by Steamy.
- Typo-tolerant and punctuation-aware title search, recent queries and cover suggestions.
- Queue priority, persistent ordering and transfer controls directly on Downloads.
- A real per-game speed limit for the bundled DepotDownloaderMod, shared by its parallel HTTP connections. The existing fork is pinned, rebuilt and tested; GPL license and complete patched source are included.
- Reduce effects setting for optional animation, gallery blur and native transparency.
- Local UI stall diagnostics and bounded log batching to keep bursts off the UI thread.

### Improved
- Quieter opaque game selection surfaces, compact download cards and shorter, stable-hit-area animations.
- Static gallery blur remains available when Windows motion is disabled; reduced effects and high contrast still turn it off.
- A running download can continue while another game's download assistant is opened.
- The README explains download setup and includes Windows screenshots of each step.

### Fixed
- Download start uses an immutable source snapshot and the exact selected depot versions, without a second provider fetch.
- Resume rejects missing or corrupt saved selections instead of silently switching to current source manifests; New selection offers explicit recovery while retaining files.
- Selected manifests, decryption keys and resume state are staged before launch; concurrent operations for the same app and folder are serialized.
- Queue restart counts already-running or still-finishing jobs against the parallel limit.
- Reorder and priority changes persist atomically, preserving progress and showing storage failures.
- Downloads are not started when their initial queue save fails; final save failures remain visible.
- Stale preparation, artwork and transfer results cannot overwrite a newer game dialog.
- Queue and log SQLite work runs outside the UI dispatcher, including initial database setup.
- Log buffers, artwork work and motion subscriptions remain bounded; shutdown queue/log flushing has timeouts.
- App shutdown stops its own registered download processes instead of killing unrelated tools by executable name.

## 0.3.9

### Added
- Automatic Spotlight for new and upcoming major studio releases, refreshed daily from public Steam metadata without an API key.
- ACE COMBAT 8: WINGS OF THEVE and Assassin's Creed Black Flag Resynced in the first verified feed, alongside other current releases.
- Release status, official release dates, publisher labels and direct Steam store links; upcoming games are not presented as installed or downloadable.
- A bundled feed, persistent local cache and shared background refresh keep discovery usable offline and current without an app update.

### Improved
- Translucent glass surfaces, soft blue and violet background light, rounded Spotlight artwork and brighter glass edges throughout the app.
- A gently rotating Spotlight pauses while hovered, focused, searching or inactive and follows Windows animation preferences.
- Steam-provided artwork URLs support new games with hashed asset paths; artwork downloads are bounded and decoded off the UI thread.
- An evergreen README with a single Latest download, updated Windows screenshots, concise setup instructions and complete source credits.
- Every release includes Steamy-latest.zip for a stable direct download URL, alongside the versioned ZIP used by existing updates.

## 0.3.8

### Improved
- More balanced game grids with subtle glass edges, short hover/press animations and keyboard-accessible cards.
- Game details open over a frozen, blurred gallery backdrop; Escape/background click closes the dialog and restores keyboard focus.
- Detail artwork uses the cached asynchronous artwork service instead of decoding a remote hero image on the UI thread.
- A compact Downloads overview with smaller cover art, slimmer queue cards, clearer transfer metrics and direct Verify & repair controls.
- Settings category animations and Ctrl+F, Enter and Escape search shortcuts; motion respects Windows animation preferences.

### Added
- Hypervisor Fixes under Fixes, with an explicit Coming soon page.
- Verify & repair on the DepotDownloader page, separate from the read-only local file check.

### Fixed
- Newly queued DepotDownloader jobs are saved immediately, before being displayed in the queue.
- Start queued rechecks each waiting job after acquiring a slot, so cancelled or removed jobs are skipped.
- Duplicate detection uses the canonical target folder and avoids replacing a paused Mod download with a standard-tool job.
- The game detail picker preserves the original Mod source on resume and never routes an existing standard-tool job through the Mod downloader.
- Removal waits for the download manager to accept it; a still-finishing download keeps its queue row.
- Tool and queue action errors remain visible in the UI; busy state remains correct during overlapping operations.
- Collection reset and Settings unload release their event subscriptions.

### Documentation
- Updated Windows screenshots, including Games, the blurred game detail dialog and Hypervisor Fixes.

## 0.3.7

### Improved
- A distinct, minimal dashboard with an editorial introduction, compact spotlight card, game labels outside the artwork and quieter catalog suggestions.
- Removed the free-source promotion and catalog disclaimer from the dashboard.
- Simplified Settings, smaller shared page headings and less repeated sidebar branding.
- Home search now shows live results from the cached catalog, supports name/App ID searches, Enter, Escape and keyboard navigation into results.

### Fixed
- Library search cancels outdated work as soon as the query changes; older results cannot replace newer input while it is being debounced.
- Visible results update together, retain unchanged rows and reuse their artwork instead of rebuilding every card on each query.
- Search handles accents and multiple words; query terms are prepared once per search rather than for every catalog entry.

## 0.3.6

### Improved
- Rebuilt Home around a full-width featured game hero, manual carousel and landscape library cards, with more room for game artwork and fewer panels.
- Added a curated discovery row that opens the real game catalog; recommendations do not claim ownership or source availability.
- Added a right-side information button to Denuvo Activation with the requested community guidance and Discord link.
- Moved visible SushiTools author/repository/community credits to the README; the third-party MIT license notice remains bundled.
- Kept downloads at the bottom left and pause/resume/start actions in a compact dashboard queue.
- Featured artwork loads off the UI thread, uses a local disk cache and reuses each slide's image task. The carousel does not run an idle animation timer.

### Documentation
- New screenshots rendered from the actual Windows app with sample library data, including Dashboard, Downloads and Settings.
- Added a manual Windows screenshot workflow to keep the README images current.

## 0.3.5

### Improved
- Downloads is back at the bottom of the left navigation, above Settings.
- More compact dashboard with a redesigned hero and direct discovery buttons for free Sushi and Zaza sources.
- Smooth mouse-wheel scrolling, shorter category transitions and debounced search in Settings; Windows reduced-motion preferences are respected.
- Game and download lists can be filtered by manifest source, alongside existing search and status/type filters.

### Added
- SushiTools by sushi-dev55 as a free manifest source for DepotDownloaderMod; no API key is required.
- Cached complete GitHub source indexes, bounded ZIP metadata extraction, author/repository/community credits, and the source's MIT notice.

### Fixed
- Settings reads are cached instead of repeatedly reading JSON, scanning tool directories and resetting DNS from UI getters. Saves are serialized and atomic.
- Download output is batched before reaching WPF, and repeated background property changes are coalesced per model.
- Returning to Games reuses its loaded catalog; optional provider cache parsing and disk-space probing run away from the UI thread.

## 0.3.4

### Improved
- Redesigned dashboard, downloads and settings with consistent surfaces, readable status cues, shorter entrances and clear keyboard focus.
- Settings now has categories, search across every section and an always-visible autosave status. Existing settings and encrypted credential controls remain available.
- Downloads has a recycling list, combined name/App ID and status filters, retained selection, bounded artwork decoding and separate device/download speed telemetry.
- Added **Verify & repair** using the downloader's saved manifests; **Check local files** is explicitly a read-only presence check.

### Fixed
- Pause and cancel keep a download reserved until the process and its queue save finish. Repeated start/resume clicks cannot replace the active cancellation token.
- Resume pins depot/manifest versions per game and target folder, retains its original source and keys, and fails honestly if any required depot fails.
- Retry starts real work; regular whole-app DepotDownloader jobs are no longer routed to the Mod resume path merely because no depot ID is set.
- Local checks do not mark paused or failed jobs complete, do not count downloader metadata as game data, and avoid directory-junction loops.
- Tool detection uses the supported version flag, drains both output streams and reports runtime errors/timeouts as unavailable.

### Development
- Windows CI now runs offline WPF layout/resource smoke checks and uploads screenshots, test reports and a self-contained Windows ZIP.

## 0.3.3

### Fixed
- **The Share page lists every game, not only the installed ones.** The whole account library now loads automatically when the page opens — no more pressing "Load all account games" first. Games without local manifests show up as auto-fetch entries and are dumped like everything else, so a "select all + share" really covers the entire library.

## 0.3.2

### Fixed
- **Downloads resume instead of starting over.** DepotDownloader(Mod) is now started with `-verify-all` (the same flag its own scripts use), so it re-validates the files already on disk and only downloads what is missing. A paused or interrupted download continues where it stopped.
- **Progress % matches the GB readout.** The percentage used to be averaged over the number of depots (a 3.91 GB / 22 GB download could show 1.4 %). It is now byte-based and uses the very same two numbers as the size text, so the bar, the GB figure and the % always agree.

### New
- **ManifestHub as a manifest source.** Steamy now asks the free, always-on ManifestHub API for the exact `.manifest` of any depot that is not on disk yet, before falling back to Steam. Add your free 24 h API key under Settings → ManifestHub.
- **Mirrors for the GitHub manifest source.** File downloads fall back across two fast mirrors (raw.gitmirror.com, cdn.jsdmirror.com, raw.dgithub.xyz) when the primary CDN is slow or blocked, so downloads stay stable and quick.

## 0.3.1

### New
- **The whole account library on the Share page.** A new **Load all account games** button pulls every game of your Steam account from your public profile (no key, no login — only the SteamID the local client keeps). Every owned game that has no local manifests yet joins the list as an auto-fetch entry, so the page covers all your games — the installed ones, the ones you never installed and everything with a Lua.
- Sharing an auto-fetch game reads the Lua from the source as well, so the pack carries depot ids, manifest ids and keys instead of bare manifest files.
- The share result line caps the skipped list at three names instead of printing hundreds.

### Fixed
- The account library requires your Steam **game details** to be public; a clear message says so when the profile keeps them private.

## 0.3.0

### Fixed
- **Resume really continues.** The resume path refreshed manifests from the source first, and whenever the source had a newer manifest DepotDownloader re-validated every file against it — the download started over. Resume now continues from the cached manifests directly; the source is only asked when there is nothing cached at all.

### New
- **Share lists your whole Steam library.** Every installed game shows up, even when Steam no longer keeps its depot manifests on disk. Those games carry an "auto-fetch" badge; sharing them fetches the missing manifests from a manifest source (Zaza → Hubcap → DepotBox → Ryuu) and packs them like any other game. Save as ZIP fetches the same way and leaves out only what no source has.

### Improved
- **Share page redesign**: glowing header badge, sharper game rows with a bigger capsule and an auto-fetch badge, an auto-fetch counter in the toolbar and clearer action-bar hints.

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
