# Reuse measured game tiles during pixel scrolling — 22 October 2026

The virtualized game grid no longer invalidates its full measurement on every pixel offset. When the layout is valid and the requested realization range is unchanged, scrolling invalidates arrangement only. Existing containers move to their new positions without repeating the generator/removal/preparation/measurement loop.

The same range calculation is used during layout and scrolling. Crossing either edge of the realization window, changing the directional reserve, changing the collection, or changing tile layout still triggers measurement. The viewport plus one directional reserve row remains bounded; this does not retain extra offscreen tiles. Offset clamping and the existing pixel-scroll motion are unchanged.

## Evidence without opening windows

- `virtual-scroll-measure-before.trx` reproduces unnecessary invalidation on a 12-pixel scroll in the previous implementation.
- `virtual-scroll-release.trx`: **79/79 selected tests passed**. The six grid cases instantiate and arrange isolated WPF controls on background STA threads; they create no Application, Window or visible surface.
- The grid checks exercise 1,008 items, both standard and portrait tiles, 20 successive pixel offsets within the same rows, exact tile displacement and retained container identity, row crossing, reversal and its backward reserve, collection removal/move, tile-size changes, filtering and end-of-list scrolling. Realized container counts remain bounded.
- An initial additional test expected zero offset for a single filtered cover; the cover exceeded the viewport, so its valid remaining offset was 14 pixels. The assertion now verifies the real clamped range rather than assuming the item fits.
- The remaining selected checks retain the preceding image-pipeline/cache, progressive banner/logo, logo allocation/pixel, BetterSteamTools + source/ZIP, Steam detection, game information, metadata backoff and scroll-motion coverage.

This establishes removal of a repeated layout path, not an application FPS result. No Steamy/Steam window, screenshot, startup probe or interactive UI test was launched. Full startup, desktop FPS, idle RAM, final visual interaction and actual Steam import remain unverified. The broader smoothness/memory objective remains open.

## Portable

`artifacts/performance-refresh/portable-virtual-scroll/Steamy.exe`, self-contained .NET 10.0.13. Keep the complete folder together. The existing game-page design and BetterSteamTools + flow are retained; Discord remains disabled with its backup intact. Static package verification and hashes are recorded in `artifacts/performance-refresh/release-virtual-scroll-validation.json`.
