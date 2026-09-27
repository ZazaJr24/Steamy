# Changelog

Each release on GitHub uses the section of its version as release notes, and the in-app updater
shows the same text under "What's new". Add a new `## x.y.z` section at the top before tagging.

## 0.1.0

### New
- Auto-updater: at startup Steamy checks GitHub for a new version, shows what's new, downloads it and restarts — one click. "Check now" in Settings does the same on demand.
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
