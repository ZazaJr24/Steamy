<p align="center">
  <a href="https://github.com/ZazaJr24/ResonanceTools/releases"><img src="https://img.shields.io/github/v/release/ZazaJr24/ResonanceTools?include_prereleases&style=for-the-badge&color=ff6b6b&label=Download&cacheSeconds=600" /></a>
  &nbsp;
  <a href="https://github.com/ZazaJr24/ResonanceTools/stargazers"><img src="https://img.shields.io/github/stars/ZazaJr24/ResonanceTools?style=for-the-badge&color=f59e0b&logo=github" /></a>
  &nbsp;
  <a href="https://github.com/ZazaJr24/ResonanceTools/releases"><img src="https://img.shields.io/github/downloads/ZazaJr24/ResonanceTools/total?style=for-the-badge&color=22c55e&label=Downloads" /></a>
  &nbsp;
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-6366f1?style=for-the-badge" /></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" />
  <img src="https://img.shields.io/badge/WPF--UI-4.2-0078D4?style=flat-square" />
  <img src="https://img.shields.io/badge/Windows-10%2F11-0078D6?style=flat-square&logo=windows11&logoColor=white" />
  <img src="https://img.shields.io/badge/C%23-12-239120?style=flat-square&logo=csharp&logoColor=white" />
</p>

<h1 align="center">
  <br/>
  ResonanceTools
  <br/>
</h1>

<h3 align="center">
  The only Steam modding toolkit you'll ever need.
</h3>

<p align="center">
  <em>One app. Every tool. Zero bloat.</em>
</p>

<p align="center">
  <a href="https://github.com/ZazaJr24/ResonanceTools/releases/latest"><b>Download Latest</b></a>&nbsp;&nbsp;&bull;&nbsp;&nbsp;<a href="#-tools">Features</a>&nbsp;&nbsp;&bull;&nbsp;&nbsp;<a href="#%EF%B8%8F-build-from-source">Build</a>&nbsp;&nbsp;&bull;&nbsp;&nbsp;<a href="#-credits">Credits</a>
</p>

---

<br/>

## What is ResonanceTools?

ResonanceTools replaces your folder full of scattered `.exe` files, batch scripts, and half-working tools with **one clean, modern desktop app**. Built with Fluent Design, it looks and feels like it belongs on Windows 11 — dark mode, Mica backdrop, smooth animations.

Every tool is wired up, configured, and ready to go. No command lines. No README hunting. No "which version do I need?"

<br/>

---

<br/>

## 🔧 Tools

<br/>

<details>
<summary><strong>⚡ Steamless</strong> — Remove Steam DRM</summary>
<br/>

Strip Steam DRM stubs from game executables. All plugins are bundled and ship with the app — works instantly without downloading anything extra.

- Supports all known Steam stub variants
- Bundled CLI with automatic plugin loading
- One-click process with status feedback

</details>

<details>
<summary><strong>🔑 Denuvo Activation</strong> — Fetch activation tickets</summary>
<br/>

Grab Denuvo activation tickets for games you own. Pick a game, fetch the ticket, write the config. Three steps. Done.

- Auto-detects installed Denuvo games
- Clean ticket retrieval workflow
- Writes config files automatically

</details>

<details>
<summary><strong>🔓 DLC Unlocker</strong> — CreamAPI + SmokeAPI</summary>
<br/>

Unlock DLCs using **CreamAPI** or **SmokeAPI** — switch between both modes with a single click.

- Auto-detects all Steam games across every library folder
- Fetches complete DLC lists with concurrent name resolution
- Deep-scans game directories for `steam_api.dll` / `steam_api64.dll`
- Select individual DLCs or unlock all
- Automatic backups with one-click restore

</details>

<details>
<summary><strong>👥 GreenLuma 2026</strong> — Family Share bypass</summary>
<br/>

Bypass Steam Family Sharing restrictions. Play shared library games without limits.

- Auto-installs from downloaded zip (password-protected archives supported)
- Generates slot-based `AppList.ini` in the correct format
- Stealth mode for reduced detection
- One-click Generate + Launch with DLLInjector
- Full uninstall with trace cleanup

</details>

<details>
<summary><strong>🎮 Goldberg Emulator</strong> — Full Steam emulator</summary>
<br/>

Complete integration of [gbe_fork](https://github.com/Detanup01/gbe_fork) — the most advanced open-source Steam emulator.

- **Auto-downloads** the latest release from GitHub
- **Normal Mode** — seamless DLL replacement
- **ColdClient Mode** — loader-based, no file replacement needed
- **Everything configurable** — DLC unlock, overlay, achievements, offline, LAN, custom Steam ID, country, language
- **Achievement support** — fetches schemas from Steam API with popup notifications
- **Defender-aware** — detects quarantined DLLs and guides you through exclusions

</details>

<br/>

---

<br/>

## 🛡️ Fixes

| | |
|---|---|
| **Game Fixes** | Browse fixes from your own fixes source (a GitHub repository) and apply them to a game folder |

<br/>

---

<br/>

## 📚 Games Library

Your entire Steam library in a responsive grid with cover art. Search, filter, sort, paginate — every game auto-detected from all Steam library folders. Smooth scrolling, no lag.

<br/>

---

<br/>

## 📥 Downloads

Full-featured download manager built in.

- Pause / Resume / Cancel
- Live speed and progress stats
- Integrity verification
- Open in Explorer / Remove

<br/>

---

<br/>

## ⚙️ Settings

| | |
|---|---|
| **Window** | Mica or Acrylic backdrop |
| **Theme** | Dark / Light |
| **Language** | Per-tool configuration |
| **Network** | HTTP proxy support |
| **Sources** | Multiple manifest providers |
| **Security** | DPAPI-encrypted API key storage |

<br/>

---

<br/>

## 🏗️ Tech Stack

| | |
|---|---|
| **Runtime** | .NET 8 (Windows Desktop) |
| **UI** | [WPF-UI 4.2](https://github.com/lepoco/wpfui) — Fluent Design, Mica/Acrylic |
| **Pattern** | MVVM — [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) |
| **DI** | Microsoft.Extensions.DependencyInjection |
| **Compression** | SharpCompress 0.39 — 7z, zip, rar |
| **Security** | Windows DPAPI encrypted credential store |

<br/>

---

<br/>

## ⬇️ Download

Grab the latest release from the [**Releases**](https://github.com/ZazaJr24/ResonanceTools/releases) page.

> **Requirements:** Windows 10/11 with .NET 8 Desktop Runtime

<br/>

---

<br/>

## 🛠️ Build from Source

```powershell
git clone https://github.com/ZazaJr24/ResonanceTools.git
cd ResonanceTools
dotnet build src/SteamContentManager/SteamContentManager.csproj -c Release
```

> Output: `src/SteamContentManager/bin/Release/net8.0-windows/ResonanceTools.exe`

<br/>

---

<br/>

## 📁 Project Structure

```
src/SteamContentManager/
├── Pages/           UI pages — Dashboard, Games, Tools, Settings
├── ViewModels/      MVVM ViewModels
├── Services/        Core logic — CreamAPI, Goldberg, GameLocator, ...
├── Models/          Data models and enums
├── Controls/        Custom controls — AdaptiveGridPanel, ...
├── Resources/       Styles, themes, bundled DLLs
└── Tools/           Bundled Steamless CLI + plugins
```

<br/>

---

<br/>

## 🙏 Credits

| Project | Author | Used for |
|---|---|---|
| [Steamless](https://github.com/atom0s/Steamless) | atom0s | DRM removal engine |
| [CreamInstaller](https://github.com/FroggMaster/CreamInstaller) | FroggMaster | CreamAPI DLLs (v5.3.0.0) |
| [SmokeAPI](https://github.com/acidicoala/SmokeAPI) | acidicoala | Alternative DLC unlocker |
| [Goldberg Emulator](https://github.com/Detanup01/gbe_fork) | Detanup01 | Steam emulation (gbe_fork) |
| [GreenLuma 2026](https://cs.rin.ru) | Steam006 | Family Share bypass |
| [WPF-UI](https://github.com/lepoco/wpfui) | lepo.co | Fluent Design framework |

<br/>

## 📄 License

ResonanceTools' own code is released under the [MIT License](LICENSE).
Bundled third-party components keep their own licenses and are not covered by it — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Both files are included in every release ZIP.

<br/>

---

<br/>

<p align="center">
  <strong>ResonanceTools</strong><br/>
  <sub>No bloat. No telemetry. No nonsense.</sub><br/><br/>
  <img src="https://img.shields.io/github/stars/ZazaJr24/ResonanceTools?style=social" />
</p>
