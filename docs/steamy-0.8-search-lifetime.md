# Cancel superseded search work and release inactive indexes — 22 October 2026

The library now releases its normalized-title/fuzzy-word search index when leaving the page or replacing its catalog input. A pending index build is cancelled at the same time. Query text and filter selections are preserved; changing the query while the same catalog remains active reuses its existing index. Reopening the page can build a fresh index in the background.

`GameSearchIndex` accepts a cancellation token for construction, while preserving its original three-argument constructor. Enumeration/title normalization and vocabulary construction check that token. Fuzzy candidate accumulation also checks query cancellation. Existing accent, punctuation, numeric-ID, exact-match and typo-match behavior is retained.

Local game filtering has its own replaceable request cancellation. New searches, newer local refreshes and page deactivation cancel earlier local requests. Publication checks both the page's generation and the identity of the local request, including page changes within one filter generation. Local index construction and searching receive the token. Sorting checks cancellation before and after materialization; it does not interrupt the underlying sort at every comparison. An obsolete catalog-render continuation also stops before scheduling another local refresh.

## Windowless evidence

- `library-search-lifetime-before.trx` reproduces a retained search index after leaving the previous implementation's page model, with the model intentionally kept alive.
- `library-search-release.trx`: **82/82 selected background tests passed**, including three new library checks: collection of the released index with query preservation, active-index reuse/fresh rebuilding after leaving, and rapid queries publishing the latest catalog/local results.
- `library-search-core-release.trx`: **18/18 pure search tests passed**. Pre-cancelled construction does not enumerate input; cancellation during title extraction stops after 16 of a possible 100,000 inputs. Existing search semantics and bounded fuzzy-vocabulary behavior remain covered.
- The UI-model fixtures seed an isolated immutable catalog and use local fake services. They make no network or Steam requests and create no Application, Window or visible surface. The selected suite retains earlier image-cache, progressive banner/logo, virtual grid, scroll motion, metadata backoff, game-information and BetterSteamTools source/ZIP checks.

The collection check verifies ownership of the search index, not a whole-application RAM number. No Steamy/Steam window, screenshot, startup probe or interactive UI test was launched. Desktop FPS, startup, idle RAM, final visual interaction and actual Steam import remain unverified. The broader smoothness/memory goal remains open.

## Portable

`artifacts/performance-refresh/portable-search-lifetime/Steamy.exe`, self-contained .NET 10.0.13. Keep the directory together. The existing glass/game-page design, compact Play/+ and BetterSteamTools source/own-ZIP flow are retained. Discord remains disabled and its backup remains intact. Static package checks and test/source hashes are in `artifacts/performance-refresh/release-search-lifetime-validation.json`.
