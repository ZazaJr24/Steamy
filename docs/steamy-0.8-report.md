# Steamy 0.8 verification report

15 October 2026 · Windows x64 · local development build on `codex/discord-social`.

The newer [15 October follow-up](steamy-0.8-follow-up.md) covers the subsequent transparency, visual controller mapping, installer and download percentage fixes. Its portable executable and complete setup live in `artifacts/steamy-0.8/follow-up/release/`; the measurements and packages below describe the earlier preview.

## Result

The original Home and floating navigation are retained. The requested neutral charcoal workspace, dense cover library, compact download rows and redesigned Friends/party workspace are implemented. Primary pages are reused per window and the 320 ms page entrance animation is removed.

The functional implementation includes optional Discord/background restoration, notifications, safe message delivery states, drafts/read positions, animated GIFs and a KLIPY-default picker, party codes, private/party voice controls, community information, and a separate DS4Windows-based controller backend with profiles/navigation/driver setup. See [Discord details](discord-social.md), [controller setup](controllers.md) and the [implementation checklist](steamy-0.8-progress.md).

**This is a local preview, not a published or hardware-certified release.** The live checks listed below remain open.

## Automated checks

| Check | Result | Evidence under `artifacts/steamy-0.8` |
|---|---:|---|
| Core regression | 412/412 passed | `checks/features-core.trx` |
| Full integration/service/WPF suite | 249/249 passed | `checks/features-full-ui.trx` |
| Glass/contrast and controller lifecycle | 4/4 passed | `checks/glass-final.trx` |
| Scrolling and controller lifecycle/UI stop behavior | 5/5 passed | `checks/scroll-final.trx` |
| Social/GIF/voice UI and real native SDK lifecycle | 1/1 passed | `checks/voice-read-ui.trx` |
| Final design capture matrix | 1/1 passed, 42 PNGs | `checks/final-ui.trx`, `final-ui/` |
| Shipping executable startup/update/tracker | 5 scenarios passed | `final-startup/startup-report.json` |

The two main suites contain **661 passing tests**. Targeted runs repeat relevant coverage and are not added to that unique total. Fixtures correct the prior discarded layout expectations and use deterministic artwork/current catalog entries. Composed navigation text contrast is checked against rendered pixels, including the original Home scrim.

The social runtime check loads the actual SDK, pumps native callbacks and forces GC to exercise callback ownership. Real WPF checks cover long histories, contact/page changes, reading-position restoration, composer visibility, notifications, a fake call across navigation, keyboard/controller actions, and six GIF previews with a four-animation cap. They also verify minimize/offscreen pause, reduced-motion/manual playback, release on unload and cancellation of a slow offscreen media request. HTTP/parser/error/nonce/protocol tests use controlled fixtures, not a live Discord account or GIF search key.

Controller tests launch the real separate helper to check authenticated handshake, read-only driver detection, malformed frames, rejected secrets and cleanup. Pinned installer hashes and Windows signature/publisher verification pass on the original packages. Drivers were not executed or installed. Four lifecycle tests cover stopping delayed recovery, stopping an in-flight start, superseding recovery with a newer start, and disabling a disconnected backend through the UI.

## Responsiveness

`performance/responsiveness.json` records a local 1440×900 WPF Friends fixture with 180 messages and 120 dispatcher-input-to-next-composition probes:

| Metric | Measured |
|---|---:|
| Median | 1.73 ms |
| p95 | 2.60 ms |
| Maximum | 10.42 ms |
| Reused Friends page ready | 64.75 / 80.50 ms |

Other measured page-ready samples were Library 177.02 ms, Downloads 234.39 ms, Controllers 122.61 ms and Settings 282.51 ms. Navigation selection feedback is applied before deferred route layout. This fixture supports the local input responsiveness goal; it does not establish every navigation below 100 ms. These are synthetic dispatcher/composition measurements, not physical keyboard-to-screen latency, cold-start timings or Discord network round trips. Real network login/history/voice latency remains unmeasured.

## Startup and update preservation

The actual shipping app/tracker ran with global .NET lookup disabled. All GUI scenarios created a responsive WPF window with **Per Monitor V2** awareness and remained running during the eight-second health check.

| Scenario | File version | Window-open detection |
|---|---|---:|
| Previous version, isolated existing fixture | 0.7.1.4 | 7837 ms |
| Upgrade to 0.8, same fixture | 0.8.0.0 | 1292 ms |
| Restart 0.8, same fixture | 0.8.0.0 | 1307 ms |
| First-run setup, separate fixture | 0.8.0.0 | 1556 ms |
| Standalone disabled playtime tracker | .NET 10 host | Exit 0 |

These are single ordered smoke-test samples. The first process ran before warmed filesystem/runtime caches; **do not infer a cold-start speedup percentage** from the table. Window-handle detection does not measure completion of every background request.

The previous-version → upgrade → restart sequence retains a known library entry, chosen settings, exact game-note bytes and exact DPAPI credential bytes, with successful decryption after each run. The credential is synthetic and auto-connect is disabled; no fixture token is sent to Discord. This verifies local persistence across the update, not real-account token restoration. The user's own library/session was not copied, reset or used by these checks.

## Delivery

Build command:

```powershell
./scripts/package-distribution.ps1 -OutputDirectory artifacts/steamy-0.8/release
```

- Run: `artifacts/steamy-0.8/release/portable/Steamy.exe`.
- Portable ZIP: `artifacts/steamy-0.8/release/Steamy-v0.8.0.zip` — **235,388,097 bytes**.
- Framework-dependent app payload: `artifacts/steamy-0.8/release/Steamy-v0.8.0-app.zip` — **170,297,333 bytes**.
- Complete backend source/build ZIP: `artifacts/steamy-0.8/Steamy-ControllerHost-source.zip` — **3,636,567 bytes**, also embedded at `Tools/ControllerHost/Steamy-ControllerHost-source.zip` in both payloads.
- Package/source/runtime verification: `artifacts/steamy-0.8/release/verification.json`.
- Design previews: `artifacts/steamy-0.8/final-ui/`, both themes at 980×620 and 1440×900, plus 150% bitmap renders. They use fixture contacts/library items.

The app, tracker and ControllerHost bundle .NET **10.0.13** and target .NET 10; the selected SDK is **10.0.402**. The portable ZIP contains 979 entries, including the SDK's Windows libraries/notices, the controller helper, its full source ZIP/GPL notices and the XamlAnimatedGif license. The app/tracker publish succeeds; the pinned ControllerHost builds with pre-existing upstream warnings (unused fields/async calls and duplicate translated resource names). Build output is retained in `checks/distribution-build.log`.

Source verification compares the embedded host/protocol/build files with this workspace and checks the original DS4Windows archive digest. The external and bundled source ZIPs match. The base is DS4Windows 3.9.9, commit `722c561c3d7aa65dcf063e716c04ca69e4ca33bd`; original archive SHA-256 is `e81f5de4e41ac3c205a5da4cb7c733265a1d4d193d0ded936aaba938d625666d`.

| File | SHA-256 |
|---|---|
| Steamy-v0.8.0.zip | `815267a66dd69d0d348b07428f7869c976ff66fe9fd3a3bbb37ce64e2d186d58` |
| Steamy-v0.8.0-app.zip | `90d9bdeb2245493523c8c6e26d7f3a4f85dbe844d0f0e43450ea18460e28866f` |
| Steamy-ControllerHost-source.zip | `1549c666e284d4129aa22451301ea6de21dd973a831ba245d11cdd13c07b9651` |

Use the portable ZIP for this unpublished build. The generated small online setup resolves GitHub's published release; it does not install this local preview until the matching payload is published. No GitHub release/PR was created. The pre-existing Defender-quarantined Goldberg executable deletion was not restored or bypassed.

## Unperformed live checks

- **Two-PC/two-account Discord voice and invitations:** real audio, device changes, acoustic quality, packet loss/reconnect, invite acceptance and desktop launch activation.
- **GIF provider API:** no KLIPY/GIPHY app key was supplied. Embedded search is implemented and fixture-tested; actual provider search/trending/quota behavior remains open. Trusted direct GIF/media links work without a search key. [KLIPY API](https://docs.klipy.com/), [GIPHY integration requirements](https://developers.giphy.com/docs/api/).
- **Controller hardware:** the user confirmed no controller is available. USB/Bluetooth DS4/DualSense, real Xbox navigation, hotplug, mappings, rumble/lightbar, virtual output, duplicate-input behavior and live game operation are unverified.
- **Live Windows scaling changes:** 150% bitmap rendering and actual Per Monitor V2 startup were verified; changing real monitor DPI/scaling was not tested.

These limitations do not turn fixture results into live-feature validation. The [official Discord voice guide](https://docs.discord.com/developers/discord-social-sdk/development-guides/managing-voice-chat) describes the underlying SDK capabilities; Steamy's two-PC audio still needs an actual live check.
