# Third-party notices

Steamy's original code is licensed under the MIT License (see `LICENSE`).
The components below retain their own licenses and distribution requirements.

## Bundled in the release

| Component | Author | License | Where |
|---|---|---|---|
| .NET runtime and WPF | Microsoft | MIT | self-contained runtime |
| [Steamy DepotDownloaderMod](tools/DepotDownloaderMod) 1.0.0, derived from [SteamAutoCracks/DepotDownloaderMod](https://github.com/SteamAutoCracks/DepotDownloaderMod/tree/c0f62fb7f020087f36ae76adfc51fde1446af344) 3.4.0 | Steamy maintainers; SteamAutoCracks and SteamRE contributors retain upstream credit | GPL-2.0 (see bundled LICENSE) | `Tools/DepotDownloaderMod/`, maintained source fork; complete corresponding source in `DepotDownloaderMod-source.zip` |
| [WPF-UI](https://github.com/lepoco/wpfui) 4.2.0 | lepo.co | MIT | UI framework |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.0 | .NET Foundation | MIT | view models |
| [XamlAnimatedGif](https://github.com/XamlAnimatedGif/XamlAnimatedGif/tree/3873ad7a35a7d296bc7cf1899234b2c714ba55a8) 2.3.2 | Thomas Levesque | Apache-2.0 | bounded, in-memory animated chat media; license in `licenses/XamlAnimatedGif/` |
| [GIPHY attribution marks](https://developers.giphy.com/docs/api/) | GIPHY | GIPHY API attribution requirements; marks remain owned by GIPHY | unmodified official light/dark logos in `Resources/GifProviders/`; provenance in its `SOURCE.md` |
| [Steamy.ControllerHost](tools/ControllerHost), derived from [DS4Windows](https://github.com/schmaldeo/DS4Windows/tree/722c561c3d7aa65dcf063e716c04ca69e4ca33bd) 3.9.9 | upstream DS4Windows contributors and Steamy maintainers | GPL-3.0-or-later | separate process in `Tools/ControllerHost/`; original notices/licenses included; full pinned upstream archive, modifications and build script in `Steamy-ControllerHost-source.zip` |
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

The ControllerHost GPL license applies to that separate helper and its modifications,
not to Steamy's original application code. The wire DTOs carry an MIT license header.
The original DS4Windows desktop entry point, updater and tray are not started; our
headless entry point uses a private local pipe and its own configuration directory.
The source package can be rebuilt with `scripts/build-controller-host.ps1`.
ViGEmBus and HidHide installers are fetched only after the user selects Install;
SHA-256 and Windows Authenticode/publisher verification precede launching them.
They retain Nefarius' original licenses and are not repackaged as Steamy drivers.

## Downloaded or supplied by the user, not bundled

Discord-enabled custom builds may include the official [Discord Social SDK](https://docs.discord.com/developers/discord-social-sdk)
and its generated C# bindings in `Tools/Discord/`, imported locally with `scripts/import-discord-sdk.ps1`.
These remain subject to Discord's Developer/Social SDK terms; original package notices are copied into
`Tools/Discord/licenses/`. The SDK binaries and sources are not included in this repository.
The Steamy adapter and Friends UI are original application code.

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

## Steam identification mark

The small `LibrarySteamMark` vector in the library uses Valve's official header mark from https://store.fastly.steamstatic.com/public/shared/images/header/logo_steam.svg, converted to WPF geometry without changing its outline. Source retrieved 23 October 2026; Steam branding reference: https://partner.steamgames.com/doc/marketing/branding.

©2026 Valve Corporation. Steam and the Steam logo are trademarks and/or registered trademarks of Valve Corporation in the U.S. and/or other countries. Steamy is not affiliated with or endorsed by Valve.
