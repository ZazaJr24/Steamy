# Third-party notices

ResonanceTools' own code is licensed under the MIT License (see `LICENSE`).
The components below are not covered by that license; each keeps its own terms.

## Bundled in the release

| Component | Author | License | Where |
|---|---|---|---|
| .NET runtime and WPF | Microsoft | MIT | self-contained runtime |
| [WPF-UI](https://github.com/lepoco/wpfui) 4.2.0 | lepo.co | MIT | UI framework |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.0 | .NET Foundation | MIT | view models |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) 8.0.8 | Microsoft | MIT | local database |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) 2.1.6 | Eric Sink | Apache-2.0 | SQLite bindings (`e_sqlite3.dll`) |
| [SQLite](https://www.sqlite.org/copyright.html) | SQLite authors | Public domain | database engine |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) 8.0.1 | Microsoft | MIT | service setup |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) 8.0.0 | Microsoft | MIT | encrypted credential storage |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) 0.39.0 | Adam Hathcock | MIT | archive handling |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) 1.0.2792.45 | Microsoft | BSD-style (Microsoft WebView2 license) | embedded browser |
| [Steamless](https://github.com/atom0s/Steamless) 3.1.0.5 | atom0s | CC BY-NC-ND 4.0 | `Tools/Steamless/`, shipped unmodified |
| CreamAPI `steam_api.dll` / `steam_api64.dll` (via [CreamInstaller](https://github.com/FroggMaster/CreamInstaller)) | original authors | not stated; rights stay with the authors | embedded for the DLC Unlocker |

## Downloaded or supplied by the user, not bundled

These are fetched on demand or selected by the user and keep their own licenses:
[gbe_fork](https://github.com/Detanup01/gbe_fork) (Goldberg), [SmokeAPI](https://github.com/acidicoala/SmokeAPI),
GreenLuma, [DepotDownloader](https://github.com/SteamRE/DepotDownloader).

## Design attribution

The UI layout and Fluent styling are inspired by [CloudRedirect](https://github.com/Selectively11/CloudRedirect) (MIT).
No CloudRedirect source code is included.
