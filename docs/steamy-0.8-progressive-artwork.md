# Progressive banners and background logo processing — 22 October 2026

The game page and download page publish their banner and logo independently. A ready banner no longer waits for a slow or unavailable logo; a ready logo also appears while its banner is still downloading. Each image has its own error handling. Game revision and cancellation checks reject late results after selection changes or page deactivation.

Logo cropping now runs on the existing background STA artwork worker. Its output remains frozen and is published through the model's captured context. The visibility threshold, crop bounds and source pixels are preserved. Scanning uses pooled strips of at most 32 rows rather than a complete 32-bit pixel copy, with cancellation checks between strips. There is no additional persistent logo cache. The normal image cache and its budgets are unchanged.

## Windowless evidence

- `game-artwork-latency-before.trx` reproduces three failures in the previous implementation: both ready-image scenarios wait for their other image, and a failed logo discards the ready banner.
- `progressive-artwork-release.trx`: **73/73 selected tests passed**. New checks cover both independent game images, logo failure, late previous-game results, cancelling logo work queued behind a blocked artwork worker, and the download banner plus late-logo rejection after stopping live stats.
- Pixel verification spans several scan strips and tests alpha values immediately below and above the crop threshold. Cropped pixels match the corresponding source rectangle exactly.
- With a 2048 × 1024 fixture, the warmed crop scan allocates less than 1 MiB on its worker thread. Previously the temporary pixel array alone occupied 8 MiB; the strip buffer for this width is 256 KiB. This measures managed allocation for this operation, not whole-process RAM or native WIC allocation.
- The selected suite also retains the earlier tests for the BetterSteamTools + source/own-ZIP flow, background Steam detection, image-cache probes and limits, progressive game information, scroll motion, metadata backoff and bounded recommendation access.

These checks create no Application or Window. No Steamy/Steam window, screenshot, startup probe or interactive UI test was launched. The banner, ambient styling, compact Play/+ actions and game-page section order are retained in source, but final visual interaction was not rechecked on screen. Actual application startup, FPS, idle memory and real Steam import remain unverified; the broader smoothness/memory goal remains open.

## Portable

`artifacts/performance-refresh/portable-progressive-artwork/Steamy.exe`, self-contained .NET 10.0.13. Keep the directory together. Discord remains disabled and its earlier backup is intact. Static package checks, hashes and test evidence are in `artifacts/performance-refresh/release-progressive-artwork-validation.json`.
