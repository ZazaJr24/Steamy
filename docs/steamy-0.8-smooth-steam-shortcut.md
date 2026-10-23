# Smooth scrolling and Add to Steam — 22 October 2026

Installed local games now have a compact 42-pixel glass `+` beside Play. It adds the configured game executable and launch arguments as a non-Steam shortcut to the most recently used local Steam account. Existing Steam installations do not show this action. This creates a shortcut, not a Steam license.

When Steam is running, the initial click offers **Restart Steam and add** inline. Only that explicit second action requests Steam's normal shutdown; there is no force-kill. A 30-second timeout leaves running Steam untouched. After an approved shutdown, Steam is reopened even if adding fails or the user leaves the page. A missing/invalid game executable directs the user to Launch options.

The binary shortcut editor appends an entry while retaining all existing entries, including unknown nested fields, byte-for-byte. It refuses unsupported/truncated input, avoids duplicates by executable, uses the standard high-bit CRC32 shortcut ID, and replaces the file atomically with a timestamped `.steamy-backup-*.bak` beside it. A changed original is not overwritten. Several accounts without a known most recent login are not guessed.

## Motion and artwork

- Wheel motion keeps velocity when extending its destination instead of replacing an animation clock for every wheel input. Direction changes remain immediate; section jumps and BringIntoView cancel stale motion. Hidden, unloaded and deactivated views detach their rendering subscription.
- Game page ambient, shell blur, Home ambient and hover ambient render on one lazy background STA. Drawing Home no longer synchronously builds a continuation texture. UI publication uses the owning dispatcher; old requests are cancelled or discarded. Cached Home textures can be applied immediately without an empty intervening frame.
- The existing banner, long fade, spatial ambient colors, narrow Play button, playtime/last played, **Game details → Preview → description**, and image-frame-plus-X viewer are retained. Discord remains excluded from the normal build.

## Evidence and limits

The reproduced old scroll failure pulled an explicit 1100-pixel jump back to 700 pixels; the corresponding regression now passes. Numeric motion checks compare 60/120 Hz integration, directional bounds, frame hitches and per-step allocations. These are not measurements of physical display refresh rate.

The local game-ambient processing benchmark changed from 29.37 ms median / 33.01 ms P95 to 26.82 / 31.64 ms, with a maximum pixel-channel difference of 1/255 (`artifacts/performance-refresh/ambient-processing-comparison.json`). The principal change is moving this work off the window thread; this benchmark alone does not establish whole-app FPS or memory use.

Blocked-worker checks exercise responsive input, latest-source publication, cache reuse and cancellation on unload. The targeted glass run passed 10 checks; the corrected lifecycle run passed 4. The Add to Steam page flow was captured at 980 and 1440 pixels in Dark and Light before the user's subsequent background-only instruction. Its game page checks passed in that targeted run; one backend error-handling check failed, was corrected, and now passes in the windowless suite.

**Current user constraint: do all further work in the background. Do not launch Steamy, Steam, startup probes, window-based smoke tests, screenshot capture or other app windows automatically.** The full UI run was interrupted accordingly. Earlier captures used the archived social integration's test fixture; they are not evidence that Discord is enabled in the shipped build. The final normal-build UI flow and actual Steam-client import have not been retested by opening applications.

Final windowless checks: `smooth-steam-headless-final.trx` (18/18: 12 Steam shortcut and 6 motion checks) and `smooth-steam-core.trx` (412/412). Shortcut tests operate only on isolated temporary files and fake Steam processes. They do not edit the user's Steam account or start Steam. The new portable is built with self-contained .NET 10.0.13. The complete FPS, startup, RAM, and repeated-navigation acceptance criteria remain open; no 220 MiB or whole-app smoothness claim is made.

Portable folder: `artifacts/performance-refresh/portable-smooth-steam-shortcut/`. Keep all its files together. Verification details and hashes are recorded in `artifacts/performance-refresh/release-smooth-steam-shortcut-validation.json` after publishing. The prior Discord backup remains in `backups/discord-before-removal-20261021-213933/`.

Format references: Valve's [binary KeyValues implementation](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/tier1/KeyValues.cpp) and the [Steam non-Steam-game support article](https://help.steampowered.com/en/faqs/view/4B8B-9697-2338-40EC).
