# ResonanceTools

A modern Windows desktop app for Steam game modding — built with WPF, .NET 8, and Fluent Design.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![WPF-UI](https://img.shields.io/badge/WPF--UI-4.2-0078D4)
![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D6?logo=windows)
![License](https://img.shields.io/badge/License-MIT-green)

---

## Features

### Dashboard
Overview of installed tools, recent activity, and quick-launch shortcuts.

### Games Library
Browse all Steam games detected across every library folder. Responsive grid with cover art, search, filtering, and pagination.

### Tools

| Tool | Description |
|---|---|
| **Steamless** | Remove Steam DRM stubs from executables. Ships with bundled plugins — no extra downloads needed. |
| **Denuvo Activation** | Fetch Denuvo activation tickets for owned games. Enter AppID, retrieve the ticket, write the config. |
| **DLC Unlocker** | Unlock DLCs via **CreamAPI** or **SmokeAPI** — toggle between modes with one click. Auto-detects games, fetches DLC lists, handles backups. |
| **GreenLuma 2026** | Steam Family Share bypass. Auto-installs from a downloaded zip, generates slot-based `AppList.ini`, stealth mode, one-click launch with DLLInjector. |
| **Goldberg Emulator** | Steam emulator (gbe_fork by Detanup01). Auto-downloads from GitHub. Normal mode (DLL replacement) or ColdClient mode (loader). Full config: DLC unlock, overlay, achievements, offline, LAN, custom Steam ID. |

### Fixes

| Page | Description |
|---|---|
| **Online Fixes** | Browse and download community game fixes from multiple sources. |
| **Game Fixes** | View and manage applied fixes for your installed games. |

### Downloads
Track active and completed downloads with pause/resume, progress stats, verify integrity, and open/remove.

### Settings
Backdrop (Mica/Acrylic), theme (Dark/Light), language, HTTP proxy, manifest source selection, API key management.

---

## Tech Stack

| Component | Technology |
|---|---|
| Framework | WPF on .NET 8 |
| UI | [WPF-UI 4.2](https://github.com/lepoco/wpfui) — Fluent Design with Mica/Acrylic |
| Architecture | MVVM with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) |
| DI | Microsoft.Extensions.DependencyInjection |
| Compression | SharpCompress 0.39 (7z/zip/rar) |
| Credentials | DPAPI-encrypted secure storage |

## Build

Requires .NET 8 SDK and Windows 10/11.

```powershell
dotnet build src/SteamContentManager/SteamContentManager.csproj -c Release
```

Output: `src/SteamContentManager/bin/Release/net8.0-windows/ResonanceTools.exe`

## Project Structure

```
src/SteamContentManager/
  Pages/           XAML pages (Dashboard, Games, Steamless, Denuvo, DLC Unlocker, ...)
  ViewModels/      MVVM ViewModels
  Services/        Business logic (CreamAPI, Goldberg, GameLocator, Themes, ...)
  Models/          Data models and enums
  Controls/        Custom controls (AdaptiveGridPanel)
  Resources/       Styles, themes, bundled DLLs
  Tools/           Bundled Steamless CLI + plugins
```

## Credits

- [Steamless](https://github.com/atom0s/Steamless) by atom0s
- [CreamInstaller](https://github.com/FroggMaster/CreamInstaller) — CreamAPI DLLs (v5.3.0.0)
- [SmokeAPI](https://github.com/acidicoala/SmokeAPI) by acidicoala
- [Goldberg Emulator (gbe_fork)](https://github.com/Detanup01/gbe_fork) by Detanup01
- [GreenLuma 2026](https://cs.rin.ru) by Steam006
- [WPF-UI](https://github.com/lepoco/wpfui) by lepo.co
