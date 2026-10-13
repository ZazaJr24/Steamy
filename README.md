<h1 align="center">Steamy</h1>
<p align="center">Browse and launch your PC games, manage downloads, and set up Steam tools on Windows.</p>

**Steamy is a Windows desktop app for managing your Steam and PC game library, downloading game files, and configuring tools such as Steamless and Goldberg.**

Use it when your games, downloads and tool settings are spread across different folders and programs. Steamy gives you one place to find an installed game, launch it, check a download queue and set up a supported game tool.

<p align="center">
  <a href="https://github.com/ZazaJr24/Steamy/releases/latest"><img src="https://img.shields.io/github/v/release/ZazaJr24/Steamy?style=flat&amp;color=8b9dff" alt="Latest version" /></a>
  <a href="https://github.com/ZazaJr24/Steamy/stargazers"><img src="https://img.shields.io/github/stars/ZazaJr24/Steamy?style=flat" alt="GitHub stars" /></a>
  <a href="https://github.com/ZazaJr24/Steamy/releases"><img src="https://img.shields.io/github/downloads/ZazaJr24/Steamy/total?style=flat&amp;color=8b9dff" alt="Downloads" /></a>
</p>

<p align="center">
  <a href="https://github.com/ZazaJr24/Steamy/releases/latest"><strong>Download for Windows</strong></a> &nbsp;·&nbsp;
  <a href="CHANGELOG.md">Changelog</a> &nbsp;·&nbsp;
  <a href="https://discord.gg/npWpm73z4B">Discord</a>
</p>

## What you can do

- **Find and launch installed games.** Scan your Steam, Epic, GOG and standalone game folders and browse the results in one library. Game pages give you play time, notes and launch options.
- **Browse Steam games before installing.** Search the catalog and read game descriptions, screenshots and system requirements. Get recommendations based on the genres and series in your installed library.
- **Manage game downloads.** Choose a supported download source or import a local ZIP/Lua package through the installation steps. Follow progress, speed and remaining time, and control the download queue from the Downloads page.
- **Set up game tools.** Use dedicated pages for Steamless, Goldberg, GreenLuma, UC Online and DLC tools to select game files and manage their configuration.

Downloads depend on the selected source and the files available for that game. Some sources require an account or API key, which you can configure in Settings. Steam integration uses your installed Steam client.

![Steamy Home: installed games, recommendations and download activity](docs/screenshots/dashboard.png)

## Get started

Requires **Windows 10 or 11, 64-bit**.

1. Open the [latest release](https://github.com/ZazaJr24/Steamy/releases/latest).
2. Choose how to run Steamy:
   - **Installer:** download the file ending in `-Setup.exe`, run it and choose an installation folder. Setup downloads the app and supported tool components from GitHub, along with any missing Microsoft prerequisites from Microsoft's official sources. An internet connection is required.
   - **Portable:** download `Steamy-v*.zip` without the `-app` suffix, extract it and run `Steamy.exe`. The .NET runtime is included. Keep the extracted files and `Tools` folder together.
3. Follow the first-run setup to select your Steam and download folders and find installed games. You can scan a specific folder or all drives, and scan again later from Home.

## Your first session

- Open **Games → Your games** to find and launch something already installed.
- Switch to **Browse all** to search the Steam catalog. Open a game and select **Install** for the available installation steps.
- Open **Downloads** to check active transfers and manage queued jobs.
- Choose a tool from the sidebar when you want to configure it for a game.

For changes between releases, see the [changelog](CHANGELOG.md).

## Build from source

<details>
<summary>Windows development instructions</summary>

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

## Community

Join the [Steamy Discord server](https://discord.gg/npWpm73z4B) for discussion, feedback and update announcements. For bug reports, please use [GitHub Issues](https://github.com/ZazaJr24/Steamy/issues).

## Credits

Created and maintained by [ZazaJr24](https://github.com/ZazaJr24).

Special thanks to **Mstra**, **yima3** and **teachervituu**.

Thanks to the authors of the tools and libraries used by Steamy. Their credits, licenses and provenance are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The maintained downloader source is in [tools/DepotDownloaderMod](tools/DepotDownloaderMod).

## License

Steamy uses the [Steamy Proprietary Software License](LICENSE). Third-party components retain their own licenses. Steam and game artwork belong to their respective owners; Steamy is an independent project.
