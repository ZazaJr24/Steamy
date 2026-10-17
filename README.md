<p align="center">
  <img src="src/Steamy/Resources/Brand/steamy-256.png" width="88" height="88" alt="Steamy logo" />
</p>

<h1 align="center">Steamy</h1>

<p align="center">
  Launch your PC games, manage downloads and configure tools like Steamless and Goldberg.
</p>

<p align="center">
  <a href="https://github.com/ZazaJr24/Steamy/releases/latest"><img src="docs/readme/download.svg" width="232" height="40" alt="Download Steamy for Windows" /></a>
  <a href="https://discord.gg/npWpm73z4B"><img src="docs/readme/discord.svg" width="144" height="40" alt="Join the Steamy Discord community" /></a>
</p>

<p align="center">
  <sub>For Windows 10 and 11 (64-bit)</sub>
</p>

<p align="center">
  <a href="https://github.com/ZazaJr24/Steamy/stargazers"><img src="https://img.shields.io/github/stars/ZazaJr24/Steamy?style=flat-square&amp;color=8b9dff&amp;label=Stars" alt="GitHub stars" /></a>
  <a href="https://github.com/ZazaJr24/Steamy/releases"><img src="https://img.shields.io/github/downloads/ZazaJr24/Steamy/total.svg?style=flat-square&amp;color=8b9dff&amp;label=Downloads" alt="Total release downloads" /></a>
</p>

<p align="center">
  <a href="#features">Features</a> &nbsp;·&nbsp;
  <a href="#installation">Installation</a> &nbsp;·&nbsp;
  <a href="#screenshots">Screenshots</a> &nbsp;·&nbsp;
  <a href="CHANGELOG.md">Changelog</a>
</p>

---

<p align="center">
  <a href="docs/screenshots/dashboard.png"><img src="docs/screenshots/dashboard.png" alt="Steamy Home showing a featured game, installed games and upcoming releases" /></a>
</p>

## Features

- Scan Steam, Epic, GOG and standalone game folders. Launch games and keep track of play time, notes and launch options.
- Search the Steam catalog for games, screenshots and system requirements. Get recommendations based on your library.
- Queue downloads from supported sources or import local ZIP/Lua packages. Check progress, speed and remaining time.
- Set up Steamless, Goldberg, GreenLuma, UC Online and DLC tools.

Some download sources require an account or API key, which you can add in **Settings**. Availability varies by game and source. Steam features use your installed Steam client.

## Installation

Download a package from the [latest release](https://github.com/ZazaJr24/Steamy/releases/latest):

- **Installer:** Run the file ending in `-Setup.exe` and choose a folder. It downloads Steamy, the supported tools and any missing Microsoft prerequisites. Requires an internet connection.
- **Portable:** Extract `Steamy-v*.zip` and run `Steamy.exe`. Choose the ZIP without `-app` in its name. It includes the .NET runtime; keep the extracted files and `Tools` folder together.

On first launch, choose your Steam and download folders, then scan for installed games. You can scan again from **Home**.

Your library is under **Games → Your games**. Use **Browse all** to search the Steam catalog, **Downloads** for the queue and **Tools** for tool settings.

## Screenshots

<details>
<summary>More screenshots</summary>

### Game library

[![Steamy game library with game artwork and browsing controls](docs/screenshots/games.png)](docs/screenshots/games.png)

### Download queue

[![Steamy Downloads showing an active transfer, queued games and download history](docs/screenshots/downloads.png)](docs/screenshots/downloads.png)

### Steamless

[![Steamy Steamless page with executable selection and tool options](docs/screenshots/steamless-page.png)](docs/screenshots/steamless-page.png)

</details>

## Bugs and feedback

Found a bug? [Open an issue](https://github.com/ZazaJr24/Steamy/issues) with what happened and how to reproduce it. For questions or feedback, join [Discord](https://discord.gg/npWpm73z4B).

See the [changelog](CHANGELOG.md) for release notes.

<details>
<summary><strong>Build from source</strong></summary>

Use Windows and the .NET SDK specified in [global.json](global.json).

```powershell
git clone https://github.com/ZazaJr24/Steamy.git
cd Steamy
dotnet build Steamy.sln -c Release
dotnet test tests/Steamy.Tests/Steamy.Tests.csproj -c Release
$env:STEAMY_UI_LIBRARY_INSTALL_OFFSCREEN_ONLY = '1'
dotnet test tests/Steamy.UiTests/Steamy.UiTests.csproj -c Release
./scripts/package-distribution.ps1
```

</details>

<details>
<summary><strong>Credits</strong></summary>

Created and maintained by [ZazaJr24](https://github.com/ZazaJr24).

Special thanks to **Mstra**, **yima3** and **teachervituu**.

Third-party credits and licenses are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The downloader source is in [tools/DepotDownloaderMod](tools/DepotDownloaderMod).

</details>

---

Steamy uses the [Steamy Proprietary Software License](LICENSE). Third-party components retain their own licenses. Steam and game artwork belong to their respective owners; Steamy is an independent project.
