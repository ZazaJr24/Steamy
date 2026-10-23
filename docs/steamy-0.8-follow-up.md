# Steamy 0.8 follow-up — 15 October 2026

This preview keeps the existing Home, dark library and compact downloads. It addresses the subsequent transparency, controller setup and download percentage requests, plus the reported phone-button failure.

## Changes

- The game page's floating navigation samples the artwork directly underneath it. Text adapts to bright and dark backgrounds using a 160×12 sample of the page's top strip. The drawer samples only its own bounds and frosts the background so page text does not compete with navigation. High contrast and reduced effects retain solid fallbacks.
- Controllers has an original vector DualShock/DualSense diagram with selectable buttons. Selecting a button changes the mapping editor; its output updates the saved profile's existing mapping rows. Xbox controllers retain native XInput behavior.
- First-run setup has six steps, including an optional controller guide before the final downloader check. It explains USB connection, signed ViGEmBus installation, restarting when requested and profile setup. HidHide instructions include the exact controller-host path and the official guide.
- Installation options include “Open controller setup after installation.” This launches the installed app with `--controller-setup` and expands the guide. Existing installations can also open Controllers manually. Driver installation requires clicking its Install button and Windows confirmation; no driver was installed during development checks.
- The online installer now checks/downloads the matching .NET 10 Desktop runtime (minimum 10.0.13). The bundled tracker version is aligned with the 0.8 app and setup. The complete setup contains the portable runtime and verifies its embedded archive.
- The download header uses the downloader's measured content percentage when the compressed network total is unknown. It labels a per-depot percentage with its depot index. Unknown progress reads “Measuring…” instead of a false 0%; completion reads 100%.
- The phone action immediately displays a cancellable voice preparation bar. The host connects before an invitation is sent. Connection, cancellation and invitation failures release the owned lobby without resending an invitation. Self-calls are blocked. SDK failures identify the operation and retain only safe diagnostics in the app log.

## Verification

Evidence lives in `artifacts/steamy-0.8/follow-up/`:

**Passed:** 412 core tests plus 260 service/model/WPF tests (**672 unique app tests**), 46 dark/light layout captures, composed game-navigation contrast and transparency checks, 32 online-installer and 36 complete-installer checks, native installer UI checks and all five executable startup scenarios. The startup scenarios cover the previous preview, updated app, restart, first run and standalone tracker; settings, library entries and synthetic encrypted credentials survive the update fixture.

- `checks/follow-up-core.trx`: core regression suite.
- `checks/follow-up-full-ui.trx`: service, model and real WPF regression suite, including the unknown-network-total percentage and pending phone action.
- `checks/call-progress.trx`: focused call sequencing, cancellation, channel-error and progress checks.
- `checks/glass-controller-ui.trx`, `ui/`: dark/light layouts at 980×620, 1440×900 and 150% rendering; interactive mapping selection; expanded and collapsed controller guide.
- `checks/details-glass-ui.trx`, `details/`: game details navigation/scrolling and composed glass contrast above 4.5:1 on bright, dark and blue fixtures. Pixel checks verify artwork remains visible through navigation.
- `installer/ui-report.txt`: native setup options, language persistence, keyboard/UI Automation and preview isolation, without running a real installation.
- `startup/startup-report.json`: five passing startup scenarios with global .NET lookup disabled.
- `release/verification.json`: archive/required-file hashes, embedded .NET 10.0.13 runtimes, test counters and startup evidence. Both app and tracker carry version 0.8.0.0.

The reported HTTP 404/code 10003 means Discord could not find a channel ([Discord's error reference](https://github.com/discord/discord-api-docs/blob/main/developers/topics/opcodes-and-status-codes.mdx)). The screenshot and action identify the phone flow but do not establish which SDK request failed. The new build exposes the failing step and avoids invitations to a host connection that has already failed. A successful live call between two separate Discord accounts remains unverified. No controller hardware is available; physical USB/Bluetooth, vibration and game output tests remain open.

Builds remain local and unpublished. Use the portable executable under `follow-up/release/portable/`, or the complete setup in the same release folder. The generic online setup still resolves the public GitHub release.
