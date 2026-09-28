# Changelog

Each GitHub release uses the section of its version as its release notes.
Add a new `## x.y.z` section at the top before tagging.

## 0.2.0

### New
- DepotBox manifest source with API key support — downloads manifests from depotbox.org
- Dashboard: SteamMidra-style System Overview card showing Steam path, library folders, detected apps, total game size and DepotDownloader version with status indicator
- Dashboard: Community section with manifest sharing — open your downloads folder to share manifest files with others
- Settings: DepotBox API key section with encrypted DPAPI credential storage

### Improved
- Dashboard: stat cards with hover animations, accent borders and drop shadows
- Dashboard: game cards scale on hover with smooth transitions
- Download dialog: more minimalistic layout — compact source pills, no separators, streamlined custom archive picker with inline label
- DPAPI credential fallback across all services (Hubcap, DepotBox, DenuvoActivation, FamilyShare, HubcapCatalog) — keys saved in the encrypted store are always checked first

### Fixed
- Hubcap: correct endpoint `/api/v1/manifest/{appId}` instead of the old search endpoint
- Hubcap: availability check and catalog service now use DPAPI fallback for the API key

### Removed
- Resonance manifest source (replaced by DepotBox)

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
