# Sharper rendering, scrolling and VPS verification

Local portable build, 23 October 2026. No GitHub upload was performed.

## Changes

- Wheel input updates its destination using actual elapsed time. Only the rendering
  callback commits a scroll position. The old extra 1/60-second advance per wheel
  message made movement depend on input rate and caused intermediate layout work.
- The virtual game panel moves retained rows with one vector translation inside
  the scroll presenter's stationary clip. It measures and arranges when rows,
  sizes or items change; external transforms, backgrounds and clips use the normal
  layout path. Recycling, keyboard navigation and hit geometry remain functional.
- Global text, the shell, library and game page use grayscale antialiasing instead
  of forced ClearType. Layout and physical scroll offsets remain pixel aligned.
  This removes subpixel color fringes from these composited surfaces; text is not
  cached as a scaled screenshot.
- Game banners prepare a frozen fade at physical display resolution, rounded up
  in 256-pixel buckets, capped at 4096 pixels. Source hero decoding increased from
  1600 to 2560 pixels. The shared decoded-image cache remains bounded at 32 MiB.
  Large high-DPI banners consequently use more memory while visible.
- The existing subtle hero fade, pinned library filters and fixed game-page Back
  button remain. The button stayed at Y=76 before and after scrolling in both themes.

## Measured scope

The benchmark uses the real detached LibraryPage with 3,010 items and eight fixed
Steam covers. Each of two process runs per panel implementation measures 720
ScrollToVerticalOffset + UpdateLayout steps per theme, including row crossings
and reversal. Only the panel implementation changes between comparison runs.

| UI layout CPU time | Previous panel | New panel | Reduction |
| --- | ---: | ---: | ---: |
| Dark median | 0.163 ms | 0.075 ms | 54.3% |
| Dark p95 | 0.280 ms | 0.139 ms | 50.5% |
| Light median | 0.220 ms | 0.080 ms | 63.9% |
| Light p95 | 0.399 ms | 0.143 ms | 64.2% |

These are UI layout costs, not measured presentation FPS or a whole-app speed
multiplier. Row changes can still cost about 10–12 ms in this fixture. Offscreen
testing cannot verify how smooth a particular monitor/GPU presents the app.

Evidence is under `artifacts/smooth-sharp/`: comparison-inputs.json records source
and artwork hashes, performance-comparison.json summarizes the four runs, and
panel-baseline-{0,1}/panel-final-{0,1}/ retain reports and console output.
The image checks set the visual tree's actual DPI before rendering at 100%, 150%
and 200%; they do not simply upscale cached 96-DPI text.

## Security verification

The published Steamy.dll was decompiled with ILSpy. The portable files, nested
archives, reconstructed sources and source distribution were checked for the
supplied provider keys, original VPS password and private-key material. Exact
credential comparisons cover UTF-8 and both UTF-16 byte orders. Reports contain
findings and hashes, never the credential values.

The source export also excludes the downloaded Goldberg working cache. Its
prebuilt third-party binaries triggered the generic private-key-block check;
they contained none of the supplied credentials. The generated cache is now
ignored for Git as well, and is unnecessary for compiling the app's source.

A separate console probe references the published DLL, without a source project
reference or personal credential access. DepotBox availability/archive and Ryuu
archive requests use HTTPS relay URLs and contain only User-Agent, with no
provider authentication headers. Its local Ryuu fixture does not establish that
the live Ryuu credential is valid; renewing it remains deferred as requested.

Artifact-derived public endpoint probes could not retrieve environment files,
Git metadata or process environments, or redirect requests to internal URLs.
Invalid IDs, unexpected query parameters and traversal paths returned 404.
Password-only SSH authentication with the original root password was rejected.
The backend port 8787 was unreachable from this client; an independent admin-key
check confirmed the loopback listener, firewall and protected environment file.
That authorized admin check is not counted as access gained from the app.

Additional service isolation was deployed: ProtectProc=invisible, ProcSubset=pid,
SystemCallArchitectures=native and KeyringMode=private. The service remained active
and a real DepotBox ZIP returned HTTP 200 after restarting it. Existing filesystem,
privilege, resource and request limits remain enabled. No credential is compiled
into the app; provider credentials stay on the VPS.

The relay URL remains discoverable, and its intentionally public download routes
can be called outside the app. Existing quotas limit that usage. This verification
found no route to retrieve the provider keys or gain VPS access; it is not a proof
against every possible attack.

## Validation and launch

- 423 core tests and 9 relay tests passed.
- 102 selected UI tests passed: panel recycling and hit geometry, wheel burst
  coalescing, motion, image pipeline, game details, fade and provider requests.
- Real-page offscreen checks cover the library, multiple DPI values and the
  game-page fade/Back button. The whole legacy UI smoke suite is not claimed green.
- The real portable EXE completed 30 seconds of isolated startup/idle testing,
  with zero foreground violations and zero startup probe timeouts. Its 93 steady
  message responses had a maximum of 0.750 ms. This checks responsiveness at idle,
  not interactive FPS.
- The background test now ignores hidden native handles created during window
  construction, and checks placement once the actual window becomes visible.

Extract `Steamy-v0.8.0-smooth-sharp.zip` and run its Steamy.exe. The corresponding
source ZIP contains public source and example configuration only. The installer
that downloads an older GitHub release should not be used for this local build.
