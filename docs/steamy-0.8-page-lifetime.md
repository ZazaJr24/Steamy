# Stop obsolete library activation work — 22 October 2026

Library activation now owns a cancellation lifetime. Leaving cancels catalog/source waiting, feed refreshes, favorite refreshes, recommendation metadata and the existing image/filter requests. Reentering creates a new lifetime; an older activation or catalog request cannot clear the new load's busy state or publish its late results. The library scan timestamp is committed only after a scan finishes for the current activation.

Catalog/source entry points run off the window dispatcher and receive the lifetime token. Their waits terminate on cancellation even if a provider ignores that token. Per-provider timeout observation starts with the request, and detached provider failures are observed. Cached Steam items can still publish before optional sources finish. Cancellation is checked after background collection creation and during large catalog merging. Completed download jobs, notifications and other app-wide services are unaffected by this view-model lifetime.

Release-feed and favorite callbacks stop before refreshing an inactive page. Recommendation requests use a linked lifetime plus their existing timeout; page deactivation stops their metadata waits and prevents ranking/status/genre publication. Ranking now runs in the background. Explicit navigation reactivates the page, preserving its search/filter choices and previously published data.

## Evidence

- `library-page-load-before.trx` reproduces the previous behavior: a catalog response arriving after deactivation repopulates the inactive model with games.
- `library-page-lifetime-release.trx`: **86/86 selected windowless tests passed**. Four added lifecycle checks cover inactive catalog rejection, reentry while the previous provider is still blocked, release-feed cancellation without rebuilding the inactive search index, and ending recommendation waits while metadata providers remain blocked.
- Reentry verifies cancellation of the first request, an uncancelled replacement token, correct loading state, acceptance of the new catalog and rejection of the old one. All delayed services are isolated local fixtures that deliberately ignore cancellation.
- The existing 18 pure search tests in `library-search-core-release.trx` also remain applicable: the search-index source is unchanged from that passing run. Together with the current selected suite, the package retains **104 passing checks** across these reports. These are selected regressions, not the full interactive suite.

No Steamy/Steam window, screenshot, startup probe or interactive UI test was launched. Some underlying providers can continue shared/cache work after their consumer cancels; the page stops waiting and rejects their results. Desktop FPS, actual idle RAM/startup, final visual interaction and real Steam import remain unverified. The full smoothness/memory goal remains open.

## Portable

`artifacts/performance-refresh/portable-page-lifetime/Steamy.exe`, self-contained .NET 10.0.13. Keep its directory together. The existing glass/game-page design and BetterSteamTools source/own-ZIP + flow are retained; Discord remains disabled with its backup intact. Static package checks and source/report hashes are in `artifacts/performance-refresh/release-page-lifetime-validation.json`.
