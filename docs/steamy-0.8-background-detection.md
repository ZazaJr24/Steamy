# Background Steam / tool detection — 22 October 2026

Opening the game page's BetterSteamTools sheet or the tools page no longer waits for Steam-folder discovery and backend file hashing in the tool-model constructor. Initial detection and the normal page, focus and wizard refresh paths now run asynchronously. Concurrent requests for the same pending root share one task. Changing the root invalidates an older request; its late result cannot replace the new folder's state.

Backend verification, setup and metadata import initiated by the game sheet also run outside the window dispatcher. The tools model runs its backend operations off-thread and marshals progress and detection results to the application dispatcher. The synchronous `RefreshDetection` compatibility API remains available for explicit callers, but normal app pages/commands no longer call it. Integrity verification itself is unchanged; no checksum validation was removed or replaced with a timing-based success cache.

While checking, connection/setup text reports **Checking Steam…** instead of briefly claiming that Steam or the backend is missing. An unchanged detection retains existing added-game rows, and changed rows are inserted/moved/removed individually. Duplicate/nonpositive IDs do not create duplicate rows or invalid move operations. Command notifications are batched at the end of applying a detection state.

The compact + flow remains **Source or your ZIP → Add to Steam with BetterSteamTools**. Discord stays disabled. Game-page layout, artwork/fade, ratings, playtime and progressive information loading are retained.

## Windowless evidence

- `steam-detection-blocking-before.trx` reproduced the old constructor's synchronous wait on a blocked detection service.
- `background-steam-detection-release.trx`: 47/47 selected tests passed. Five new detection tests cover nonblocking construction, pending-request coalescing, stale-folder rejection, row retention/command batching/duplicate IDs and publication on the owning dispatcher.
- The dispatcher test creates only an isolated background STA and message dispatcher. It does not create an Application, Window or visible surface; it checks that the file-detection thread differs from the publication thread.
- The selected suite also includes progressive game information/cancellation/cache checks, scroll motion, metadata backoff and BetterSteamTools verification/source/own-ZIP tests. All filesystem/backend tests use temporary fixtures, not the user's Steam installation.

No Steamy or Steam window, UI smoke test, screenshot capture or startup probe was launched, honoring the user's background-only request. Actual desktop FPS, idle memory, UI startup and final visual interaction have not been remeasured. This evidence establishes specific removed blocking paths, not completion of the entire smoothness/RAM objective.

Portable: `artifacts/performance-refresh/portable-background-detection/Steamy.exe`, self-contained .NET 10.0.13. Keep the directory together. Static runtime, Discord metadata, hashes and test evidence are recorded in `artifacts/performance-refresh/release-background-detection-validation.json`. The portable is built without launching it; earlier releases and the Discord backup remain intact.
