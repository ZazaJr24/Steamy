# Steamy's DepotDownloaderMod build

This is a small modification of the **existing**
[SteamAutoCracks/DepotDownloaderMod](https://github.com/SteamAutoCracks/DepotDownloaderMod)
fork, pinned to commit `c0f62fb7f020087f36ae76adfc51fde1446af344` (3.4.0, .NET 9).
SteamAutoCracks and SteamRE retain credit and their **GPL-2.0** license. The Steamy
patch and its tests are GPL-2.0 too; see [LICENSE](LICENSE).

## What changed

`-max-download-speed <bytes-per-second>` accepts a non-negative integer. Zero
means unlimited and preserves the original socket stream. A positive value uses
one shared token budget across **all HTTP/CDN connections in that tool process**.
Socket reads include HTTP/TLS overhead, so useful game data can be slightly below
the selected limit. Writes, depot verification and local file operations remain
unlimited. The initial read budget is approximately 50 ms of the chosen rate,
bounded to 64 KiB (at least one byte). Waiting is asynchronous and cancellable;
closing a connection also cancels its pending budget wait.

All parallel chunk connections within a job share the cap. If Steamy runs several
game jobs concurrently, each tool process has its own configured cap.
Changing the setting applies when the next tool process
starts, including when a paused download resumes. Independently launched tool
processes each have their own cap.

The existing `HttpClientFactory.ConnectCallback` wraps its owned `NetworkStream`;
SteamKit's configured HTTP client factory therefore uses the same limiter. Login
and Steam protocol connections outside that HTTP factory are not throttled.
No existing depot/source logic was replaced.

`--steamy-rate-limit-info` prints a small capability JSON object without login or
network access. `Steamy-rate-limit.json` records the exact executable's SHA-256;
the app passes the new flag only while the marker matches the selected binary.
Replacing the tool with an upstream/custom binary safely disables this flag.

`-steamy-progress` reports a bounded, versioned `STEAMY_PROGRESS|1|…` line at
most ten times per second, plus phase transitions. It counts validated existing
content separately from successfully fetched compressed CDN chunks and installed
content. It never uses preallocated file lengths. The transfer total becomes
known after existing files have been checked and the missing chunks are selected.
Completion remains the caller's decision after the process exits successfully.
The capability response and checksum marker explicitly report `progressTelemetry`;
custom or replaced executables do not receive this option.

## Rebuild and test

Install Git, Python 3.11+ and the .NET SDK selected by Steamy's `global.json`, then
run from the Steamy checkout:

```sh
python tools/DepotDownloaderMod/build.py
```

The script fetches the exact pinned source, checks and applies `rate-limit.patch`,
adds `RateLimitedReadStream.cs` and `SteamyProgress.cs`, tests the limiter and actual patched HTTP factory
against a local TCP server, and publishes a self-contained
Windows x64 executable. It replaces the old bundle only after successful build
and checks. The Windows CI also checks the published executable's capability
response and rejection of malformed limits. To run just the offline tests:

```sh
dotnet test tools/DepotDownloaderMod/Tests/DepotDownloaderMod.Tests.csproj -c Release
```

Use `--dotnet /path/to/dotnet` or `--output /path/to/output` when needed. Binary
bytes can vary with the SDK/runtime servicing version; the upstream revision
and source modifications are pinned and the capability checksum is generated
from the resulting executable.

## Corresponding source

Every distributed tool bundle includes `LICENSE`, this notice, and
`DepotDownloaderMod-source.zip`: the **complete patched upstream source**,
project/build files, Steamy patch, build script and tests. Extract it to inspect
or build the tool directly:

```sh
dotnet publish DepotDownloaderMod/DepotDownloader/DepotDownloaderMod.csproj -c Release -r win-x64 --self-contained true
```

Steamy's own application license does not apply to this GPL component.
