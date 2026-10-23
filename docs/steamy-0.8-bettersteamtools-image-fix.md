# BetterSteamTools + and nonblocking image-cache probes — 22 October 2026

The + beside Play opens the embedded BetterSteamTools sheet for installed games. The first step selects an existing source or the user's ZIP/Lua package; the second step explicitly adds it to Steam. Opening + does not import anything or launch Steam. Steam-folder discovery, backend verification and import run off the window dispatcher. The earlier native-shortcut implementation is not used by this button.

Image-cache existence checks also run in the background now. Previously `LoadAsync` checked the filesystem before its first asynchronous wait, while holding the shared image-cache lock. A slow disk could block the calling UI and unrelated requests for already decoded artwork. A shared pending request now probes outside that lock; cached files still bypass occupied network workers, and missing files enter the existing priority queue.

Visible requests can promote a pending prefetch. Cancelling one consumer does not cancel the others. A cancelled probe cannot remove a replacement request or start a stale download after its filesystem check returns. Private memory-only images do not incur a public-cache probe. The 32 MiB decoded-image cache and 512 MiB public disk-cache budgets are unchanged.

## Verification

- `image-cache-probe-before.trx` reproduces the old blocking call using a deliberately gated filesystem probe. This is causal test evidence, not a measurement of real disk speed or desktop FPS.
- `bettersteamtools-image-release.trx`: **65/65 selected windowless tests passed**, including source/own-ZIP imports against temporary fixtures, backend validation, the installed-game + command, asynchronous Steam detection, progressive game information, scroll motion, metadata backoff, bounded catalog access and image-pipeline tests.
- The new image tests cover nonblocking request creation, access to an already decoded image while another probe is blocked, cancellation/retry isolation, shared prefetch promotion and collection of uncached image objects while the pipeline's workers remain alive.
- The memory-lifetime check disables caching and observes six decoded images through weak references. It verifies ownership in that scenario; it does not establish the app's 220 MiB idle-memory target.

No Steamy/Steam window, screenshot, startup probe or interactive UI test was launched. Actual Steam modification, final visual interaction, application FPS, startup and idle memory were not remeasured. The broader performance goal remains open.

## Portable

`artifacts/performance-refresh/portable-bettersteamtools-image-fix/Steamy.exe` is the new self-contained .NET 10.0.13 portable. Keep its directory together. Discord remains disabled and its earlier backup is preserved. Static package checks, runtime versions, source hashes and test-report hashes are recorded in `artifacts/performance-refresh/release-bettersteamtools-image-fix-validation.json`.
