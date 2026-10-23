<p align="center">
  <img src="src/Steamy/Resources/Brand/steamy-256.png" width="88" height="88" alt="Steamy logo" />
</p>

<h1 align="center">Steamy</h1>

<p align="center">
  <strong>Browse your games. Manage your downloads. Set up your tools.</strong>
</p>

<p align="center">
  A Windows desktop app for your installed Steam and PC games, game downloads,<br />
  and tools such as Steamless and Goldberg — together in one interface.
</p>

<p align="center">
  <a href="https://github.com/ZazaJr24/Steamy/releases/latest"><img src="docs/readme/download.svg" width="232" height="40" alt="Download Steamy for Windows" /></a>
  <a href="https://discord.gg/eYyaFABabC"><img src="docs/readme/discord.svg" width="144" height="40" alt="Join the Steamy Discord community" /></a>
</p>

<p align="center">
  <sub>Windows 10 / 11 · 64-bit · Installer or portable ZIP</sub>
</p>

<p align="center">
  <a href="https://github.com/ZazaJr24/Steamy/stargazers"><img src="https://img.shields.io/github/stars/ZazaJr24/Steamy?style=flat-square&amp;color=8b9dff&amp;label=Stars" alt="GitHub stars" /></a>
  <a href="https://github.com/ZazaJr24/Steamy/releases"><img src="https://img.shields.io/github/downloads/ZazaJr24/Steamy/total?style=flat-square&amp;color=8b9dff&amp;label=Downloads" alt="Total release downloads" /></a>
</p>

<p align="center">
  <a href="#what-steamy-does">Features</a> &nbsp;·&nbsp;
  <a href="#get-started">Get started</a> &nbsp;·&nbsp;
  <a href="#screenshots">Screenshots</a> &nbsp;·&nbsp;
  <a href="CHANGELOG.md">Changelog</a>
</p>

---

<p align="center">
  <a href="docs/screenshots/dashboard.png"><img src="docs/screenshots/dashboard.png" alt="Steamy Home showing a featured game, installed games and upcoming releases" /></a>
  <br />
  <sub>Featured games, your installed library and upcoming releases.</sub>
</p>

## What Steamy does

Steamy is for Windows players who want to handle their game library, downloads and tool settings from the same app.

- **Find and play your games.** Scan Steam, Epic, GOG and standalone game folders. Launch installed games, see play time and manage notes and launch options.
- **Discover your next game.** Browse the Steam catalog, view screenshots and system requirements, and find recommendations based on your installed library.
- **Keep downloads organised.** Choose a supported source or import a local ZIP/Lua package. Follow transfer progress, speed and remaining time, and manage the queue.
- **Configure game tools.** Dedicated pages guide you through Steamless, Goldberg, GreenLuma, UC Online and DLC tool settings.

Downloads depend on the source and files available for each game. Ryuu and DepotBox use the Steamy HTTPS relay automatically; shared provider keys stay on the server. Optional personal keys and other providers can be configured in **Settings**. Steam integration uses your installed Steam client. See [relay deployment and limits](docs/provider-relay.md).

## Get started

Requires **Windows 10 or 11, 64-bit**. Open the [latest release](https://github.com/ZazaJr24/Steamy/releases/latest) and choose a package:

- **Setup — guided installation.** Download the file ending in `-Setup.exe`, run it and choose an installation folder. Setup downloads the app and supported tools from GitHub, plus any missing Microsoft prerequisites from Microsoft's official sources. An internet connection is required.
- **Portable — extract and run.** Download `Steamy-v*.zip` without the `-app` suffix, extract it and open `Steamy.exe`. The .NET runtime is included; keep the extracted files and `Tools` folder together.

### Your first session

1. Follow the setup prompts to choose your Steam and download folders and find installed games. You can scan a folder or all drives, and scan again later from **Home**.
2. Open **Games → Your games** to launch an installed game, or **Browse all** to explore the Steam catalog and its available installation steps.
3. Use **Downloads** to check transfers and manage queued jobs. Open **Tools** when you want to configure a game tool.

### 0.8 development preview

The current source adds a neutral charcoal library, denser portrait covers, compact download rows while keeping Home and the floating glass toolbar. Frequently used pages are reused and the whole-page entrance animation has been removed.

The game details page now combines a large banner with an artwork-colored background across the full page, four previews, Steam review summaries, achievements and developer information. Downloads uses a small toolbar icon. See the [current design notes](docs/steamy-0.8-details-refresh.md). The [latest polish](docs/steamy-0.8-social-polish.md) extends the banner fade, adds live friend previews and uses locally supplied SF Pro when available.

Discord integration is disabled in current builds: Friends/Community navigation, connection settings, setup login, automatic connection and the native Discord runtime are excluded. The previous source and working portable version are backed up locally; see [removal and restoration notes](docs/discord-removal.md).

**Tools → Controllers** exposes a separate DS4Windows-based backend, mappings, profiles, driver checks and controller navigation with an on-screen keyboard. The portable package includes its complete source/build package. See [controller setup](docs/controllers.md).

Real two-PC voice and controller hardware checks remain open. The [implementation checklist](docs/steamy-0.8-progress.md) and [verification report](docs/steamy-0.8-report.md) distinguish automated evidence from unperformed live tests. These development changes have not been published as a GitHub release.

## Screenshots

<details>
<summary><strong>Explore the library, download queue and tools</strong></summary>

### Game library

[![Steamy game library with game artwork and browsing controls](docs/screenshots/games.png)](docs/screenshots/games.png)

### Download queue

[![Steamy Downloads showing an active transfer, queued games and download history](docs/screenshots/downloads.png)](docs/screenshots/downloads.png)

### Steamless

[![Steamy Steamless page with executable selection and tool options](docs/screenshots/steamless-page.png)](docs/screenshots/steamless-page.png)

</details>

## Community

Join [Discord](https://discord.gg/eYyaFABabC) for discussion and feedback, or [open a GitHub issue](https://github.com/ZazaJr24/Steamy/issues) to report a bug. Release changes live in the [changelog](CHANGELOG.md).

Enjoying Steamy? Don't forget to [star the repo](https://github.com/ZazaJr24/Steamy). Thanks for your support!

<details>
<summary><strong>Build from source</strong></summary>

Use Windows and the .NET SDK specified in [global.json](global.json).

```powershell
git clone https://github.com/ZazaJr24/Steamy.git
cd Steamy
./scripts/build-controller-host.ps1 -OutputDirectory tools/ControllerHost/runtime -SourcePackage
dotnet build Steamy.sln -c Release
dotnet test tests/Steamy.Tests/Steamy.Tests.csproj -c Release
$env:STEAMY_UI_LIBRARY_INSTALL_OFFSCREEN_ONLY = '1'
dotnet test tests/Steamy.UiTests/Steamy.UiTests.csproj -c Release
./scripts/package-distribution.ps1
```

The normal package does not require or include the Discord SDK. Archived Discord-enabled builds require an explicit build opt-in and the official SDK described in the [integration guide](docs/discord-social.md). The controller source package and its separate GPL notices accompany the helper binary.

</details>

<details>
<summary><strong>Credits</strong></summary>

Created and maintained by [ZazaJr24](https://github.com/ZazaJr24).

Special thanks to **Mstra**, **yima3** and **teachervituu**.

Thanks to the authors of the tools and libraries used by Steamy. Their credits, licenses and provenance are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The maintained downloader source is in [tools/DepotDownloaderMod](tools/DepotDownloaderMod).

</details>

---

Steamy's original code uses the [MIT License](LICENSE). Third-party components retain their own licenses. Steam and game artwork belong to their respective owners; Steamy is an independent project.
## Building from GitHub

The app references the supplied `lib/Steamy.RelayClient.dll`; its implementation source is kept locally by the owner.
Provider keys stay on the VPS and are unnecessary for local or GitHub builds.
See [the GitHub workflow](docs/github-workflow.md) and [relay security](docs/provider-relay.md).

