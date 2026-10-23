# Visible Steam action and progressive game information — 22 October 2026

The compact glass **+** now appears immediately to the right of Play for every installed game. The previous build restricted it to local/non-Steam installations, which hid it on the user's BeamNG.drive page. The action now reports **This game is already registered in Steam** for installations identified as Steam games, without reading/writing shortcuts or restarting Steam. Local games retain the backed-up shortcut workflow and explicit restart confirmation described in `steamy-0.8-smooth-steam-shortcut.md`. Uninstalled catalog games still show Install instead.

Game information now loads progressively. Store metadata and preview URLs are published as soon as they are available, including from the existing disk cache; they no longer wait for News and review servers. Each optional response is applied independently. Unchanged preview and news collections are retained instead of cleared/rebuilt. Developer/similar-game loading starts once per selection, rather than once per optional response. Switching games clears the old developer link and rejects late updates. Leaving the data stream cancels its optional HTTP requests and observes their completion.

Completed overviews now use a thread-safe cache of the 64 most recently viewed titles. Recently viewed titles stay warm while older ones are evicted. Incomplete or cancelled streams are not retained as complete results. The existing `ReadOverviewAsync` API still returns the complete overview; older data services remain compatible with the page model.

## Evidence

- `game-overview-gating-before.trx` reproduced the old delay: description remained the generic launch text while News/reviews were blocked.
- `visible-steam-plus-progressive.trx`: 47/47 windowless checks passed, including existing game-details, metadata retry, scroll motion and Steam shortcut tests plus the new regressions.
- New checks seed the actual Store disk cache, hold both optional HTTP responses, and verify that description/preview/preferences are ready. Releasing only reviews updates ratings while News remains blocked. Releasing News completes the overview without rebuilding the screenshot collection.
- Other checks verify request cancellation, stale result/link isolation after game selection changes, the 64-entry cache and its recency policy, and the + command's availability for both local and Steam installations. A registered Steam game neither writes another shortcut nor requests a Steam restart.

All work follows the user's **background only** constraint. No application, Steam client, UI smoke test, screenshot capture or startup probe was launched for this revision. The changed UI binding has not been visually rechecked in a running app. These checks prove the loading/caching behavior, not the full FPS/RAM/startup acceptance targets, which remain open.

Portable folder: `artifacts/performance-refresh/portable-visible-steam-plus/` (self-contained .NET 10.0.13, Discord disabled). Keep its files together. Static package/runtime/hash verification is recorded in `artifacts/performance-refresh/release-visible-steam-plus-validation.json`. Previous releases and the Discord backup remain unchanged.
