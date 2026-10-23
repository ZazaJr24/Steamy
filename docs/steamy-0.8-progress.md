# Steamy 0.8 — implementation and verification

Updated 15 October 2026. The [accepted specification](steamy-0.8-spec.md) records the original request and subsequent user corrections. See [the verification report](steamy-0.8-report.md) for final evidence and package details.

The [15 October follow-up](steamy-0.8-follow-up.md) records the transparent drawer/game navigation, visual controller mapping and installer guide, corrected download percentage, and cancellable phone flow. Its newer executable is under `artifacts/steamy-0.8/follow-up/release/portable/`.

## Design decisions

- The original Home artwork, floating toolbar and Steamy launcher remain. The permanent sidebar/global redesign was superseded by the user's correction on 14 October.
- The central toolbar now matches the launcher's quiet glass treatment; it does not continuously capture page content.
- The later references explicitly add compact horizontal download rows, a dense portrait game grid and a compact Friends/chat layout.
- The 15 October reference changes the dark workspace to neutral charcoal (`#202124`) with gray panels, restrained borders and purple chat/action accents. Colored dark workspace gradients and library hover recoloring have been removed. Light/high-contrast/reduced-effects modes remain available.

## Implemented

- [x] .NET 10 migration: SDK 10.0.402, self-contained runtime 10.0.13 for app, tracker and ControllerHost.
- [x] Neutral dark library, compact downloads and shared Friends/party layout, with the original Home and floating glass navigation.
- [x] Immediate navigation selection feedback, latest-request routing, removal of the 320 ms whole-page entrance animation, and reuse of primary pages within each window.
- [x] Optional Discord setup, background saved-session restoration, safe OAuth/browser fallback, in-app notifications and unread/taskbar badges.
- [x] Dedicated native SDK STA dispatcher, immutable UI snapshots, independent cancellable operations and batched presence updates.
- [x] Per-conversation outbox, visible Sending/Sent/Failed states, nonce/ID reconciliation, safe retry/history checks, bounded drafts and anchored read positions.
- [x] Animated GIF chat/viewer, offscreen/minimized pause, cancellation/release, reduced-motion manual playback and four-GIF automatic playback limit.
- [x] KLIPY-default/GIPHY-optional picker: trending/search/pagination/favorite IDs, debounce/cancellation, provider errors/limits and attribution. No provider key was supplied; direct trusted GIF links work without one.
- [x] Party codes, typed metadata, invalid-join cleanup, invitations and explicit launch of matching installed games.
- [x] Private two-person and party voice implementation, explicit microphone consent, persistent call bar, device/volume controls, mute/deafen/PTT/speaking indicators and lifecycle cleanup.
- [x] Public Steamy community card, current invite metadata and explicit external join action. No bot/member-directory/hosting dependency. Includes the requested GitHub star reminder.
- [x] Separate pinned DS4Windows 3.9.9 GPL ControllerHost, .NET 10 runtime, authenticated per-user framed named pipes, parent/disconnect cleanup and bounded recovery.
- [x] Controller page, telemetry, button mapping, deadzones/curves, rumble/lightbar commands and saved/game profiles with default restoration.
- [x] Foreground gamepad navigation, visible focus/prompts and an on-screen keyboard with shifts/symbols/German characters.
- [x] Read-only driver detection in Controllers and optional setup, verified pinned original installers and explicit install actions. No drivers installed during verification.
- [x] Backend Stop invalidates pending recovery and wins an in-flight Start; newer explicit Start supersedes old recovery.
- [x] Complete backend source/build ZIP, including the original pinned upstream archive, all modifications and licenses, embedded alongside the helper.

## Evidence collected

- Core regression: **412/412 passed**, `checks/features-core.trx`.
- Dedicated glass/lifecycle check: **4/4 passed**, `checks/glass-final.trx`.
- Real WPF social/native runtime check: **1/1 passed**, `checks/voice-read-ui.trx`: forced-GC native SDK callbacks, six GIF previews with a four-playback cap, slow-download cancellation, scroll/read restoration, fake voice bar across navigation, and keyboard/controller actions.
- Full integration/UI regression: **249/249 passed**, `checks/features-full-ui.trx`; together with the core suite, **661 tests passed**. Final capture passed with 42 PNGs in `final-ui/`.
- Final portable/app/source ZIPs are built and verified. App, tracker and ControllerHost include .NET 10.0.13; complete backend source and original notices are included. Hashes and file checks are in `release/verification.json`.
- Actual 0.7.1.4 → 0.8.0 → restart with the same isolated profile passed: library entry/settings/notes and DPAPI credential bytes were preserved. First-run and the standalone tracker also passed, with global .NET lookup disabled; `final-startup/startup-report.json`. Returning 0.8 window-open samples were 1292/1307 ms and first-run 1556 ms; these are ordered smoke samples, not a cold-start comparison.
- Local responsiveness fixture: 120 input-to-next-WPF-composition samples, p95 **2.60 ms**; reused Friends pages became ready in **65–81 ms** in that run. This is a synthetic local UI measurement, not physical keyboard latency, cold startup or Discord network latency.
- WPF renders cover both themes at 980×620 and 1440×900, plus 150% bitmap output. Per-monitor-V2 awareness is checked at actual executable startup. Actual Windows display-scaling changes are not a completed manual check.

The glass regression fixtures now test composed text contrast, use deterministic artwork when asserting logo/halo behavior, and follow the currently available spotlight entries rather than hard-coded past releases. Layout checks reflect the restored Settings category list and the requested compact Downloads layout.

## Open live checks

- [ ] Two-PC/two-account voice audio, live device switching, packet loss/reconnect and invitation acceptance/launch activation.
- [ ] Real provider GIF search/trending/quota behavior using a supplied KLIPY or GIPHY app key. HTTP parsing/cancellation/error cases are tested with fixtures.
- [ ] Actual USB/Bluetooth DS4/DualSense, Xbox navigation, hotplug, rumble/lightbar, virtual output, mapping and duplicate-input behavior. The user confirmed no controller is available.
- [ ] Actual Windows display-scaling changes on a live multi-monitor setup.

These live checks are not represented as passed. No user account session, real library or controller-driver installation is used by the isolated startup checks. Changes and packages remain local; no GitHub release has been published. The Defender-quarantined Goldberg executable deletion predates this work and has not been restored or bypassed.
