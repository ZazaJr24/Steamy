# Third-party notices

Steamy' own code is licensed under the Steamy License (see `LICENSE`).
The components below are not covered by that license; each keeps its own terms.

## Bundled in the release

| Component | Author | License | Where |
|---|---|---|---|
| .NET runtime and WPF | Microsoft | MIT | self-contained runtime |
| [Steamy DepotDownloaderMod](tools/DepotDownloaderMod) 1.0.0, derived from [SteamAutoCracks/DepotDownloaderMod](https://github.com/SteamAutoCracks/DepotDownloaderMod/tree/c0f62fb7f020087f36ae76adfc51fde1446af344) 3.4.0 | Steamy maintainers; SteamAutoCracks and SteamRE contributors retain upstream credit | GPL-2.0 (see bundled LICENSE) | `Tools/DepotDownloaderMod/`, maintained source fork; complete corresponding source in `DepotDownloaderMod-source.zip` |
| [WPF-UI](https://github.com/lepoco/wpfui) 4.2.0 | lepo.co | MIT | UI framework |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.0 | .NET Foundation | MIT | view models |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) 9.0.9 | Microsoft | MIT | local database |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) 2.1.6 | Eric Sink | Apache-2.0 | SQLite bindings (`e_sqlite3.dll`) |
| [SQLite](https://www.sqlite.org/copyright.html) | SQLite authors | Public domain | database engine |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) 9.0.9 | Microsoft | MIT | service setup |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) 9.0.9 | Microsoft | MIT | encrypted credential storage |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) 0.39.0 | Adam Hathcock | MIT | archive handling |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) 1.0.2792.45 | Microsoft | BSD-style (Microsoft WebView2 license) | embedded browser |
| [Steamless](https://github.com/atom0s/Steamless) 3.1.0.5 | atom0s | CC BY-NC-ND 4.0 | `Tools/Steamless/`, shipped unmodified |
| CreamAPI `steam_api.dll` / `steam_api64.dll` (via [CreamInstaller](https://github.com/FroggMaster/CreamInstaller)) | original authors | not stated; rights stay with the authors | embedded for the DLC Unlocker |

The Steamy DepotDownloaderMod fork adds bounded cancellable retries, safe manifest
and target handling, resilient checkpoints, graceful host cancellation, shared
cancellable HTTP/socket rate limiting, and versioned progress telemetry. The full
corresponding source, provenance, build files, license and tests accompany its binary.
Rebuild instructions are in the bundled `STEAMY-FORK.md` and
[`tools/DepotDownloaderMod/README.md`](tools/DepotDownloaderMod/README.md).
Steamy's application license does not cover that GPL component.

## Downloaded or supplied by the user, not bundled

These are fetched on demand or selected by the user and keep their own licenses:
[gbe_fork](https://github.com/Detanup01/gbe_fork) (Goldberg), [SmokeAPI](https://github.com/acidicoala/SmokeAPI),
GreenLuma, [DepotDownloader](https://github.com/SteamRE/DepotDownloader).

## Manifest source attribution

[SushiTools games repository](https://github.com/sushi-dev55/sushitools-games-repo)
is provided by **sushi-dev55** under the MIT License. Steamy fetches its Lua metadata and
manifest archives on request; the repository content is not bundled in Steamy releases.
Credit also appears in the game source picker and Settings → Connections.
Community: [SushiTools](https://discord.gg/sushitools). This invite is the placeholder supplied
by the user and has not been verified; update it when a confirmed invite is available.

```text
MIT License

Copyright (c) 2025 sushi-dev55

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Design attribution

The UI layout and Fluent styling are inspired by [CloudRedirect](https://github.com/Selectively11/CloudRedirect) (MIT).
No CloudRedirect source code is included.

## Optional BetterSteamTools integration

[BetterSteamTools](https://github.com/madoiscool/BetterSteamTools) is an independently maintained GPL-3.0 Steam backend. It is not bundled with Steamy; the user can download its official Windows release from its publisher through the Tools page. Its license and corresponding source are available in that repository.

The metadata installation workflow references [LuaTools](https://github.com/madoiscool/LuaTools) (MIT). No LuaTools binaries or source code are bundled. Steamy's detection, validation, transaction and UI code are independently implemented.
