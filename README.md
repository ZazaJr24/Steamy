<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" />
  <img src="https://img.shields.io/badge/WPF--UI-4.2-0078D4?style=for-the-badge" />
  <img src="https://img.shields.io/badge/Windows-10%2F11-0078D6?style=for-the-badge&logo=windows11&logoColor=white" />
  <img src="https://img.shields.io/badge/License-MIT-2dd4bf?style=for-the-badge" />
</p>

<h1 align="center">ResonanceTools</h1>

<p align="center">
  <strong>All-in-one Steam game modding toolkit</strong><br/>
  <sub>Built with WPF, .NET 8, Fluent Design — dark, fast, modern.</sub>
</p>

---

## Overview

ResonanceTools is a desktop application that brings together every tool you need for Steam game modding into one clean interface. No command lines, no scattered utilities — just one app with everything wired up and ready to go.

---

## Tools

### Steamless
Strip Steam DRM stubs from game executables. All plugins are bundled — works out of the box without downloading anything extra.

### Denuvo Activation
Fetch Denuvo activation tickets for games you own. Pick a game, grab the ticket, write the config. Three steps, done.

### DLC Unlocker
Unlock DLCs using **CreamAPI** or **SmokeAPI** — switch between both with a single click.

- Auto-detects all installed Steam games across every library folder
- Fetches complete DLC lists with concurrent name resolution
- Deep-scans game folders for `steam_api.dll` / `steam_api64.dll`
- Handles backups, apply, and restore automatically

### GreenLuma 2026
Steam Family Share bypass. Unlocks games in your shared library without buying them.

- Auto-installs from a downloaded zip (password-protected archives supported)
- Generates slot-based `AppList.ini` in the correct format
- Stealth mode to avoid detection
- One-click Generate + Launch with DLLInjector
- Full uninstall and trace cleanup

### Goldberg Emulator
Full integration of the [gbe_fork](https://github.com/Detanup01/gbe_fork) Steam emulator by Detanup01.

- **Auto-downloads** the latest release from GitHub on first use
- **Normal Mode** — replaces `steam_api(64).dll` with Goldberg's version
- **ColdClient Mode** — uses the SteamClient loader (no DLL replacement)
- **Full configuration** — DLC unlock, overlay, achievements, offline mode, LAN only, custom Steam ID, country, language
- **Achievement support** — fetches schemas from Steam API, popup notifications
- **Windows Defender detection** — warns if DLLs get quarantined
- One-click Apply, Restore, and Launch

---

## Fixes

| | |
|---|---|
| **Online Fixes** | Browse and download community game fixes from multiple sources |
| **Game Fixes** | View and manage fixes applied to your installed games |

---

## Games Library

Browse your entire Steam library in a responsive grid with cover art. Search, filter, paginate — all games auto-detected from every Steam library folder.

---

## Downloads

Full download manager with pause/resume, live progress stats, integrity verification, and file management.

---

## Settings

| Setting | Options |
|---|---|
| Window effect | Mica / Acrylic |
| Theme | Dark / Light |
| Language | Configurable per tool |
| Network | HTTP proxy support |
| Sources | Multiple manifest sources |
| Security | Encrypted API key storage (DPAPI) |

---

## Tech Stack

| | |
|---|---|
| **Framework** | WPF on .NET 8 |
| **UI Library** | [WPF-UI 4.2](https://github.com/lepoco/wpfui) — Fluent Design with Mica/Acrylic backdrop |
| **Architecture** | MVVM — [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) |
| **DI** | Microsoft.Extensions.DependencyInjection |
| **Compression** | SharpCompress 0.39 (7z / zip / rar) |
| **Security** | DPAPI-encrypted credential storage |

---

## Build from Source

> Requires **.NET 8 SDK** and **Windows 10/11**

```powershell
git clone https://github.com/ZazaJr24/ResonanceTools.git
cd ResonanceTools
dotnet build src/SteamContentManager/SteamContentManager.csproj -c Release
```

Output: `src/SteamContentManager/bin/Release/net8.0-windows/ResonanceTools.exe`

---

## Project Structure

```
src/SteamContentManager/
  Pages/           UI pages (Dashboard, Games, all tool pages, Settings)
  ViewModels/      MVVM ViewModels
  Services/        Business logic (CreamAPI, Goldberg, GameLocator, ...)
  Models/          Data models and enums
  Controls/        Custom controls (AdaptiveGridPanel, ...)
  Resources/       Styles, themes, bundled DLLs
  Tools/           Bundled Steamless CLI + plugins
```

---

## Credits

| Project | Author |
|---|---|
| [Steamless](https://github.com/atom0s/Steamless) | atom0s |
| [CreamInstaller](https://github.com/FroggMaster/CreamInstaller) | FroggMaster |
| [SmokeAPI](https://github.com/acidicoala/SmokeAPI) | acidicoala |
| [Goldberg Emulator (gbe_fork)](https://github.com/Detanup01/gbe_fork) | Detanup01 |
| [GreenLuma 2026](https://cs.rin.ru) | Steam006 |
| [WPF-UI](https://github.com/lepoco/wpfui) | lepo.co |

---

<p align="center">
  <sub>Made with purpose. No bloat, no telemetry, no nonsense.</sub>
</p>
