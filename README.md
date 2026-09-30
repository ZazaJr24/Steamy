<p align="center">
  <img src="src/Steamy/Resources/Brand/steamy-256.png" width="88" alt="Steamy logo" />
</p>

<h1 align="center">Steamy</h1>
<p align="center"><strong>One place. More play.</strong><br/>Your Steam library, downloads and tools in a desktop app with a little more colour.</p>

<p align="center">
  <a href="https://github.com/ZazaJr24/Steamy/releases/latest/download/Steamy-latest.zip"><img src="https://img.shields.io/badge/Download-Latest-7c83ff?style=for-the-badge&logo=windows&logoColor=white" alt="Download Latest for Windows" /></a>
  <a href="https://github.com/ZazaJr24/Steamy/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/ZazaJr24/Steamy/ci.yml?branch=main&style=for-the-badge&label=Build" alt="Build status" /></a>
</p>
<p align="center"><a href="#start-playing">Get started</a> · <a href="#inside-steamy">Features</a> · <a href="#a-closer-look">Screenshots</a> · <a href="#build-it-yourself">Build</a> · <a href="#credits">Credits</a></p>

<p align="center"><img src="docs/screenshots/dashboard.png" width="1044" alt="Steamy's glass dashboard with automatic new and upcoming games, a sample installed library and download queue" /></p>

## Start playing

1. **[Download Latest](https://github.com/ZazaJr24/Steamy/releases/latest/download/Steamy-latest.zip)** and extract the entire ZIP into a folder.
2. Run **Steamy.exe**. Steam library folders are detected automatically; adjust them in **Settings** if needed.
3. Open **Games** to browse, or **Downloads** at the bottom left to continue your queue.

The release is portable and includes the Windows runtime. Use Windows 10 or 11 on a 64-bit PC; no separate .NET installation is needed. Keep the bundled `Tools` folder beside the app.

Already using Steamy? Open **Settings → Check now** to get the latest release. The app can also check automatically at startup. [Release notes](CHANGELOG.md) live in one place, so this page stays focused on the app.

## Inside Steamy

| Your next stop | What you'll find |
| --- | --- |
| **Dashboard** | An original glass layout with soft blue and violet light, your installed games and a compact queue overview. |
| **Spotlight** | New and upcoming major studio releases, automatically refreshed from public Steam metadata. Official artwork, publisher, release status and dates; a local cache works offline. |
| **Games** | A responsive cover grid, quick search by title or App ID, sorting, source filters and a detail dialog over a blurred background. |
| **Downloads** | Progress, speed, time left and a transfer graph. Pause, resume, cancel, retry and verify & repair, with the saved source and target folder retained. |
| **Settings** | Searchable categories, dark and light themes, Windows Mica/Acrylic, download limits, network options and source connections. |
| **Share** | Scan local Lua files and manifests, preview a pack, export an archive or upload to your own GitHub repository. |
| **Fixes** | Browse your configured fixes repository. **Hypervisor Fixes is Coming soon** and does not install anything yet. |

Cards lift gently on hover and respond to clicks. Page entrances, scrolling and dialogs use short transitions; animation follows the Windows motion preference. Desktop transparency depends on your selected backdrop and Windows support.

### A Spotlight that keeps moving

The feed checks Steam daily for recent releases and popular upcoming games from established publishers. **ACE COMBAT 8: WINGS OF THEVE** and **Assassin's Creed Black Flag Resynced** are included in the initial verified selection. New titles arrive without downloading another app release.

The app checks for feed updates in the background, shares concurrent requests and retains the last good data when offline. Slides rotate while the dashboard is active; hovering, keyboard focus or searching pauses the rotation. Spotlight cards open the official Steam store. Upcoming games carry a **Coming soon** status; appearing here does not imply a download is available.

### Downloads you can come back to

Queue a game, choose its folder and let the downloader work. Pausing retains downloaded files and saved depot/manifest information. Resume uses the original download mode and source; duplicate jobs targeting the same folder are rejected.

**Verify & repair** asks DepotDownloader to check and repair the files. The separate **local file check** reports what is already on disk. Queue actions show errors in the app, and waiting jobs are checked again before they start.

Sources include **Sushi, Zaza, Ryuu, Hubcap, DepotBox and ManifestHub**, where supported. Sushi and Zaza can be browsed without an API key; other providers may require their own credentials. Source filters help narrow the catalog and queue. Availability is checked for the selected game.

<details>
<summary><strong>Tools, in one place</strong></summary>

| Tool | Purpose |
| --- | --- |
| **DepotDownloader / DepotDownloaderMod** | Depot downloads, saved manifests, queue management and repair. The existing Mod fork is bundled. |
| **Steamless** | Run Steamless against a selected executable, with its options and output in the app. |
| **Denuvo Activation** | Configure the activation tool and inspect its output; the information button links to the community help. |
| **DLC Unlocker** | Configure supported CreamAPI or SmokeAPI integration for a selected game. |
| **GreenLuma / Family Share** | Configure the supported tool and its app list. |
| **Goldberg** | Install and configure the emulator, including ColdClient mode and game-specific settings. |
| **Game Fixes** | Download fix archives from your configured repository and apply them to the selected folder. |

Additional tools are fetched when needed. Credentials are kept in a Windows DPAPI-encrypted store; settings exports exclude them.

</details>

## A closer look

Screenshots are captured from the actual Windows app using a sample library and queue. The Spotlight uses verified public Steam data. Game artwork belongs to the respective rights holders.

**Games** — cover art, clear labels and room to breathe.

![The Games grid](docs/screenshots/games.png)

**Game details** — the selected game stays sharp over a blurred gallery.

![Game details over the blurred background](docs/screenshots/game-details.png)

**Downloads** — see what's running, what's next and what's ready to resume.

![Downloads with progress and queue controls](docs/screenshots/downloads.png)

**Settings** — find a setting, then make the app yours.

![Settings with searchable categories](docs/screenshots/settings.png)

<details>
<summary>Hypervisor Fixes preview</summary>

![Hypervisor Fixes Coming soon page](docs/screenshots/hypervisor-fixes.png)

</details>

## Build it yourself

Use the .NET SDK selected by [`global.json`](global.json). The desktop app and UI tests require Windows; the core test project also runs on Linux and macOS.

```powershell
git clone https://github.com/ZazaJr24/Steamy.git
cd Steamy
dotnet restore Steamy.sln
dotnet build Steamy.sln -c Release
dotnet test tests/Steamy.Tests/Steamy.Tests.csproj -c Release
dotnet test tests/Steamy.UiTests/Steamy.UiTests.csproj -c Release
./run.ps1
```

GitHub Actions builds and tests `main` on Windows. The UI checks exercise the real view models, layout in both themes, search, game dialogs and download queue interactions using offline test services. The screenshot workflow renders the app and updates this page's images.

<details>
<summary>Project map and automated discovery</summary>

- `src/Steamy/Pages` — pages and layouts.
- `src/Steamy/ViewModels` — UI state and commands.
- `src/Steamy/Services` — downloads, catalogs, storage, sharing and updates.
- `src/Steamy/Controls` — grid, artwork, scrolling and motion.
- `tests/Steamy.Tests` — core and download policy tests.
- `tests/Steamy.UiTests` — Windows layout and interaction tests.
- `scripts/update_spotlight.py` — public Steam metadata → discovery feed; no API key or extra Python packages.
- `.github/workflows/spotlight.yml` — daily discovery refresh, also runnable manually.

Publish a release by updating the app version and [`CHANGELOG.md`](CHANGELOG.md), then pushing a matching tag. Each release includes a versioned archive and **Steamy-latest.zip**, keeping the download button above current.

</details>

## Credits

Steamy is maintained by [ZazaJr24](https://github.com/ZazaJr24). Thanks to the authors whose work powers its tools and sources.

| Project | Creator / community | Used for |
| --- | --- | --- |
| [DepotDownloaderMod](https://github.com/SteamAutoCracks/DepotDownloaderMod) | SteamAutoCracks and SteamRE contributors | Bundled Mod downloader, distributed with its license |
| [DepotDownloader](https://github.com/SteamRE/DepotDownloader) | SteamRE | Standard depot downloader |
| [SushiTools games repository](https://github.com/sushi-dev55/sushitools-games-repo) | [sushi-dev55](https://github.com/sushi-dev55) · [SushiTools server](https://discord.gg/sushitools) | Free Lua metadata and depot manifests, used under MIT |
| [Steamless](https://github.com/atom0s/Steamless) | atom0s | Steamless engine |
| [CreamInstaller](https://github.com/FroggMaster/CreamInstaller) | FroggMaster and original CreamAPI authors | CreamAPI components |
| [SmokeAPI](https://github.com/acidicoala/SmokeAPI) | acidicoala | Optional DLC integration |
| [Goldberg / gbe_fork](https://github.com/Detanup01/gbe_fork) | Detanup01 and contributors | Steam emulator |
| GreenLuma | Steam006 | Family Share tooling |
| [WPF-UI](https://github.com/lepoco/wpfui) | lepo.co | Windows UI framework |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | .NET Foundation and contributors | View models |

Steam store metadata and game artwork are provided by Steam and the respective publishers. Steamy is an independent project.

## License

Steamy's own code uses the [Steamy License](LICENSE). Sharing and forking are welcome; rebranding or commercial redistribution requires permission. Third-party components retain their own terms: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Both documents are included in each release.
