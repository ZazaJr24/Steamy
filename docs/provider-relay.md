# Provider relay and background testing

The app uses `https://steamy.213-171-214-218.sslip.io` for Ryuu and DepotBox
when no personal key is configured. The public address is compiled into the app;
the shared provider keys are never compiled into the app or distributed in source.
A desktop executable cannot safely hide a shared API key.

The request flow is: app → HTTPS relay → fixed provider endpoint → relay cache → app.
The relay returns a ZIP or the provider's Lua metadata. The app continues using its
existing package validation; server code never executes provider scripts.
Explicitly configured personal keys keep the existing direct-provider behavior.
Those keys are stored locally with Windows DPAPI and are never sent to the relay.

## VPS installation

The service uses Ubuntu's maintained `python3-aiohttp` and `caddy` packages.
Public code and configuration live in `server/provider-relay/`. On another server,
change the hostname in `Caddyfile` and `ProviderRelay.cs` and rebuild the app.
The current hostname uses sslip.io to resolve the VPS address; an operator-owned
DNS name can replace it without changing the protocol.

Install the packages, create the `steamy-relay` system user with a nologin shell,
copy `relay.py` into `/opt/steamy-relay/`, and install `steamy-relay.service` into
`/etc/systemd/system/`. Install the example environment file separately as
`/etc/steamy-relay/relay.env`, owned by root with mode `0600`; its directory must
have mode `0700`. Enter real keys there through an SSH session, never in the repo:

```text
RYUU_API_KEY=<operator supplies this on the VPS>
DEPOTBOX_API_KEY=<operator supplies this on the VPS>
DAILY_UPSTREAM_LIMIT=100
CACHE_DIR=/var/cache/steamy-relay
```

Install the Caddy configuration, validate it with `caddy validate`, then run
`systemctl daemon-reload`, `systemctl enable --now steamy-relay` and
`systemctl reload caddy`. Caddy obtains and renews a publicly trusted certificate;
the app uses ordinary TLS validation with no certificate bypass.

The deployed VPS has inbound TCP 22, 80 and 443 allowed by UFW. The Python process
listens only on `127.0.0.1:8787`. SSH password and keyboard-interactive login are
disabled after verifying key-based access. Fail2ban protects SSH. The service runs
as `steamy-relay`, with a read-only system filesystem, no capabilities, restricted
address families, a 192 MiB memory limit and 60% of one CPU core.

Keep the private administrator SSH key backed up outside the repository. Do not
overwrite an existing Caddy deployment when adapting these instructions.

## Resource limits and security boundaries

- Only `ryuu` and `depotbox`, positive 32-bit app IDs and fixed operations are allowed.
- Client URLs, query parameters, methods other than GET and incoming credentials
  cannot select or authenticate the provider request.
- Provider requests never follow redirects. Error responses return fixed messages,
  with no upstream bodies, credentials, cookies or exception text.
- Two requests can be active at once. Duplicate package generation returns a retry
  response instead of creating a second upstream job.
- Downloads are limited to 128 MiB. Archive metadata has a 512 MiB extraction
  ceiling and a 10,000-entry limit. Partial files are discarded after failures.
- Disk cache has a 512 MiB payload budget, including reservations for two concurrent
  transfers, and a six-hour lifetime. Cache eviction happens on new upstream work
  and startup; an idle expired cache is cleaned on the next request.
- Each client IP has 60 requests/minute and 12 package requests/hour. IP tracking
  is memory-only and bounded. There are no relay access logs.
- A SQLite counter persists a global limit of 100 new package/provider calls per
  UTC day. Availability has a separate 10,000-call budget and one-minute caching.
  Cache hits do not spend the upstream budget. Failed provider calls do spend it.
- Public access intentionally requires no shared client token. Anyone can recreate
  an open-source client. These limits contain costs and resource use; they cannot
  prevent someone from deliberately consuming the public allowance. Account-based
  quotas would require a separate sign-in design.

`GET /v1/health` reports service health and whether keys are configured. It does
not verify that a configured provider key is accepted. A download verifies that.

## Updating and recovery

Replace `/opt/steamy-relay/relay.py` over SSH, run its unit tests and restart
`steamy-relay`. Replace provider keys only in the protected environment file, then
restart the service. Do not put API keys into GitHub Actions secrets for client
builds: injecting them into an executable would expose them again.

Check `systemctl status steamy-relay caddy fail2ban`, `journalctl -u steamy-relay`
and the health endpoint when troubleshooting. The environment file and private SSH
key are excluded from source and distribution; protect their separate backups.

## Verification

```powershell
python scripts/check-source-secrets.py
python -m pip install -r server/provider-relay/requirements.txt
python -m unittest discover -s server/provider-relay -p 'test_*.py'
dotnet test tests/Steamy.Tests/Steamy.Tests.csproj -c Release
$env:STEAMY_UI_LIBRARY_INSTALL_OFFSCREEN_ONLY = '1'
dotnet test tests/Steamy.UiTests/Steamy.UiTests.csproj -c Release
./scripts/test-app-background.ps1 -AppPath artifacts/secure-relay/portable/Steamy.exe
```

`--background-test` opens the real application offscreen with activation disabled
and no taskbar entry. The script uses a separate profile, checks that the process
never takes foreground focus, samples memory and CPU, and verifies window-message
responsiveness after a ten-second startup warmup and records startup probe
timeouts separately. It closes only the process it started. This checks startup/idle;
it is not a before/after benchmark of interactive scrolling.

Library sorting/filtering now always runs off the UI dispatcher. Optional catalog
providers start alongside Steam. Ryuu progress is limited to ten updates per second,
and its cache publishes verified ZIPs atomically instead of caching partial files.
Original project code is MIT licensed; bundled third-party licenses still apply.

On 2026-10-23, DepotBox returned a real ZIP for App 480 through the HTTPS relay.
Ryuu returned HTTP 403 for the supplied auth key, including with the documented
`file_type=manifest` option. Its credentials need renewal; this was left pending at
the user's request. No key or password values are recorded in this document.


## Public client DLL and abuse limits (0.8.0)

The app references `lib/Steamy.RelayClient.dll`; its implementation source is
maintained locally by the owner. The DLL constructs the public HTTPS requests and contains no
provider key, SSH credential or shared client secret. It is included in the app
and repository, not as a separate release download. Obfuscation
cannot establish that a request came from the original app.

The service intentionally supports all app users without login. Its public
protocol can also be implemented by another client. A strict original-app-only
restriction would require a different trust model; it is not claimed here.

Protection includes fixed upstream hosts, no credential-bearing redirects,
per-IP quotas (IPv6 addresses grouped by /64), a global 180-request/minute cap,
100 uncached provider fetches/day, two active transfers, and a persistent 2 GiB/day
payload delivery budget that also charges cache hits. Delivery has a 30-second
write timeout and a five-minute overall deadline. The delivery budget is set by
`DAILY_TRANSFER_BYTES` on the server. Exhausted limits return 429, never a fallback
that embeds provider credentials. No rate limit can eliminate distributed denial
of service; these limits bound the relay's allowed work and delivery volume.
