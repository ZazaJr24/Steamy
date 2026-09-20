# ResonanceTools

A modern Windows desktop app for Steam game modding tools — built with WPF, .NET 8, and Fluent Design.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![WPF-UI](https://img.shields.io/badge/WPF--UI-4.2-0078D4)
![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D6?logo=windows)

## Tools

### Steamless
Integrated Steamless CLI — remove Steam DRM stubs from executables without downloading anything extra. Ships bundled with plugins.

### Denuvo Activation
Fetch Denuvo activation tickets for owned games. Enter an AppID, retrieve the ticket, write the config. Three-step workflow with clear status feedback.

### DLC Unlocker
Unlock DLCs using **CreamAPI** or **SmokeAPI** — toggle between modes with a single click.

- Auto-detects installed Steam games from all library folders
- Fetches complete DLC lists via SteamCMD API with concurrent name resolution
- Recursively scans game directories for `steam_api.dll` / `steam_api64.dll` (handles nested paths like `bin/win_x64/`)
- **CreamAPI**: embedded DLLs, writes `cream_api.ini` with `_o.dll` backups
- **SmokeAPI**: auto-downloaded from GitHub, writes `SmokeAPI.config.json`
- Select/deselect individual DLCs, one-click apply and restore

## Tech Stack

| Component | Technology |
|---|---|
| Framework | WPF on .NET 8 |
| UI | [WPF-UI 4.2](https://github.com/lepoco/wpfui) — Fluent Design with Mica/Acrylic backdrop |
| Architecture | MVVM with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) |
| DI | Microsoft.Extensions.DependencyInjection |
| Compression | SharpCompress 0.39 (7z/zip) |
| Backdrop | Mica / Acrylic (configurable in Settings) |

## Build

Requires .NET 8 SDK and Windows 10/11.

```powershell
dotnet build src/SteamContentManager/SteamContentManager.csproj -c Release
```

The output is in `src/SteamContentManager/bin/Release/net8.0-windows/`.

## Project Structure

```
src/SteamContentManager/
  Pages/           XAML pages (Dashboard, DLC Unlocker, Denuvo, Settings, ...)
  ViewModels/      MVVM ViewModels
  Services/        Business logic (CreamAPI, GameLocator, Themes, ...)
  Models/          Data models and enums
  Resources/       Styles, themes (Dark/Light), bundled CreamAPI DLLs
  Tools/           Bundled Steamless CLI + plugins
```

## Settings

- **Backdrop**: Mica or Acrylic window effect
- **Theme**: Dark / Light
- **Language**: DLC config language selection
- **Proxy**: optional HTTP proxy for API calls
- **Manifest source**: Resonance, SteamDB, custom

## Notes

- CreamAPI DLLs (v5.3.0.0) are embedded from [CreamInstaller](https://github.com/FroggMaster/CreamInstaller) resources
- SmokeAPI is downloaded on demand from [acidicoala/SmokeAPI](https://github.com/acidicoala/SmokeAPI) releases
- DLC fetching uses the [SteamCMD Web API](https://api.steamcmd.net) as primary source
- All backups use the `_o.dll` naming convention (CreamInstaller standard)
