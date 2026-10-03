# Steamy DepotDownloaderMod

Steamy maintains and ships this GPL-2.0 source fork as its own downloader engine. It is derived from
[SteamAutoCracks/DepotDownloaderMod 3.4.0](https://github.com/SteamAutoCracks/DepotDownloaderMod)
at revision `c0f62fb7f020087f36ae76adfc51fde1446af344`, itself based on SteamRE/DepotDownloader.
The complete corresponding source is checked into [`Source/`](Source/); builds do not fetch, patch,
or execute source from another repository. SteamAutoCracks and SteamRE attribution and license are
preserved in [`Source/LICENSE`](Source/LICENSE) and [`Source/UPSTREAM.md`](Source/UPSTREAM.md).

## Fork features

- **Finite, cancellable retries.** Manifest and CDN chunk requests use a configurable number of
  additional attempts (0–10), exponential backoff and bounded jitter. Waiting observes cancellation.
- **Ordered stop.** `-steamy-cancel-file <absolute-path>` lets Steamy request cancellation while the
  child drains and closes its work. Ctrl+C follows the same cancellation path. The host kills the
  process tree after a bounded grace period if it does not stop.
- **Protected checkpoints.** Resume configuration and cached manifests are flushed to a same-volume
  temporary file before replacement. The last valid version is retained as `.bak`; damaged config
  recovers from that copy and forces file verification.
- **Safer target writes.** A process-wide OS file lease prevents two downloader processes from
  writing into the same install root. Manifest paths are checked for traversal, Windows device names,
  links/junctions, duplicates, incorrect depot/version IDs, impossible ranges and oversized chunks.
  Existing files get a rollback copy before a destructive update; failures and the next resumed run
  restore those valid originals, while newly downloaded files are kept only after chunk verification.
- **Secret-safe key parsing.** Depot keys are validated as unique depot IDs with exactly 32 bytes of
  hex key material. Errors never echo supplied key contents.
- **Shared speed cap and real telemetry.** `-max-download-speed <bytes-per-second>` shares one
  cancellable budget across the fork's HTTP/CDN connections. `-steamy-progress` reports bounded,
  versioned transfer telemetry and keeps downloaded, verified and installed bytes distinct.
- **Binary identity.** `--steamy-info` reports the fork version and capabilities. The adjacent
  `Steamy-depotdownloader-mod.json` binds that identity to the executable SHA-256; Steamy sends
  fork-only flags only when that check succeeds.

The speed cap measures HTTP socket bytes, including protocol overhead, so useful game data may be
slightly below the selected rate. The cap applies per process. Content verification and local disk
operations are not throttled. A graceful stop happens between cancellable operations; the bounded
host timeout remains the fallback for any operation that cannot stop promptly.

## Build

Install the .NET SDK pinned by the repository's `global.json` and Python 3.11+, then run:

```sh
python tools/DepotDownloaderMod/build.py
```

The script runs the fork's tests, publishes a self-contained Windows x64 executable, creates a
source archive and writes a capability marker with source and executable hashes. On Windows it also
starts the published executable to verify its identity and confirms malformed options are rejected
before any account or network operation. The release workflow builds and packages this same checked-in
source.

The resulting `DepotDownloaderMod-source.zip` contains the full source tree, pinned SDK file,
license, provenance, build script and tests. You may build the project directly with:

```sh
dotnet test tools/DepotDownloaderMod/Tests/DepotDownloaderMod.Tests.csproj -c Release
dotnet publish tools/DepotDownloaderMod/Source/DepotDownloader/DepotDownloaderMod.csproj -c Release -r win-x64 --self-contained true
```

This fork is distributed under GPL-2.0. Steamy's application license does not apply to it.
