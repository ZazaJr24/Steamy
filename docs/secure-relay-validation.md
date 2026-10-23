# Secure relay validation — 2026-10-23

The portable build is in `artifacts/secure-relay/portable/Steamy.exe`.
Distribute the entire portable ZIP, not its executable alone. No release was
published to GitHub during this work.

## Verified

- 423/423 core tests passed, including default relay routing, direct personal-key
  routing, invalid/stale ZIP replacement, concurrent requests and cancellation.
- 67/67 selected UI/service checks passed: provider packages, DepotBox imports
  without credentials, parallel catalog loading, search cancellation/lifetime,
  artwork latency, gallery recycling and background settings work.
- 9/9 Python relay tests passed locally and on the VPS. These cover fixed upstream
  URLs, server-only authentication, redirect refusal, sanitized errors, cache
  reuse, size limits, quota persistence and overload handling.
- A real DepotBox request for App 480 returned a valid 5,505-byte ZIP through the
  public HTTPS relay. A repeat returned `X-Relay-Cache: HIT`. The availability
  endpoint returned HTTP 200. Environment-file paths and arbitrary upstream URL
  parameters returned HTTP 404. TLS validation was enabled and HSTS was present.
- SSH key access was verified again after password authentication was disabled.
  UFW, Fail2ban, Caddy and the unprivileged relay service were active. The protected
  environment file was owned by root with mode 0600. The service used about 34 MiB
  when inspected and has a 192 MiB hard limit.
- The source pattern guard passed. A separate exact-value check covered the
  supplied provider keys and initial VPS password in UTF-8/UTF-16 across app files,
  portable files, release ZIPs, the source ZIP and nested ZIPs. No values were
  copied into the report. See `artifacts/secure-relay/security-verification.json`
  for the final file count, findings and release hashes.

## Background test and limits

The real portable application ran offscreen for 30 seconds with an isolated
profile, no taskbar entry and activation disabled. It did not take foreground
focus. The initial measured run recorded one window-message timeout during startup;
after the ten-second startup warmup it answered 93 probes, with a maximum response
of approximately 0.62 ms. End-of-run working set was approximately 221 MiB. These
are observations from that run, not a baseline comparison or interactive FPS test.
The final rebuilt package is tested again after the last label changes; its report
is stored alongside the initial result.

The optimizations move library filtering/sorting to background workers, start
optional catalogs alongside Steam, and cap Ryuu progress updates at ten per second.
No claim is made that every possible stall has been eliminated.

The additional existing full library installation smoke scenario was attempted.
Its legacy `LibraryInstallChecks.cs` assertion expects black immediately after
ambient is disabled, while the current documented design defaults to white. After
temporarily checking the newer color expectations, a later legacy assertion also
expected a completed DrawingBrush from an offscreen page whose ambient result was
still a SolidColorBrush. Those unrelated assertions were restored unchanged;
this extra scenario is not counted among the passing checks. A full interactive
visual acceptance test remains open.

Ryuu rejected the configured key with HTTP 403 even using its documented download
parameters. Updating that credential was postponed at the user's request. DepotBox
was confirmed working; the health endpoint indicates configuration, not provider
credential validity. Administrator access instructions are stored separately from
the source tree under the local user's `.codex/private/` directory.
