# Steam Content Manager

A native .NET 8 WPF desktop UI (Fluent/WPF-UI, inspired by [CloudRedirect](https://github.com/Selectively11/CloudRedirect)) that shows what is actually installed on **your own** machine and runs real downloads through **your own** copy of DepotDownloader.

## What is real now

- **Local Steam library.** The Games page reads the Steam path from the registry (with folder fallbacks), every registered library from `libraryfolders.vdf` and every installed app from `appmanifest_*.acf` (app id, name, install folder, size, state flags, build id, last update). Nothing is invented: if no Steam installation is found, the page stays empty and says why.
- **Filter, search, select, queue.** The library can be filtered (all / installed / update available / not installed / selected only), searched by name or app id, selected per card or in bulk, and the selection is handed to the real download queue. Queueing requires confirming that you are authorized to access the content.
- **Honest downloads.** A job only ever runs when a DepotDownloader executable is configured and present. If it is missing, the job fails immediately with a clear message — the app never simulates progress and never reports a download it did not perform. The exact command line (arguments only, never credentials), the exit code and the process output are stored on the job.
- **Real queue state.** Jobs and their state live in a local SQLite database. After a restart, finished jobs come back as history and jobs that were interrupted come back as *interrupted* — never as completed. Retries and backoff come from Settings.
- **Real verification.** "Verify" counts the files and bytes that exist in the target folder and says exactly that (`Local check passed: 12 file(s), 3.4 GB`). It does not claim a cryptographic verification it did not run.
- **Real manifests.** "Import manifest" reads a local `.manifest` file you already have: real file size, real SHA-1, app/depot id parsed from the standard `app_<appid>_depot_<depotid>.manifest` name, and a real re-hash on validation.
- **Real dashboard values.** Library count, active/queued jobs, current speed, free space of the configured download drive, provider states and the DepotDownloader status are read from the machine, not hard coded.
- **Local library only.** Depots, branches and achievements have no data source in this app, so their pages show empty states instead of sample rows.

## Scrolling (why pages have `ScrollViewer.CanContentScroll="False"`)

WPF-UI hosts page content in a `NavigationViewContentPresenter` that wraps the page in a `DynamicScrollViewer` unless `ScrollViewer.CanContentScroll` is `false` on the page. While that dynamic viewer is active the page is measured with infinite height, so neither the page's own `ScrollViewer` nor the host can scroll and mouse wheel plus keyboard scrolling silently do nothing. Every page therefore opts out explicitly and uses the shared `PageScrollHostStyle` (mouse wheel, keyboard, touch/trackpad). `PageLayoutTests` and `PageSmokeTests` fail if a page loses the opt-out.

## Safety boundaries

- The app never stores Steam passwords, Steam Guard codes, session tokens or API keys in source code, JSON settings, SQLite, logs or exports. Optional keys entered in Settings are stored separately with DPAPI; stored secret values are never displayed.
- Downloads run only through the unchanged, user-selected DepotDownloader executable, for content the user is authorized to access, with the user's own account. There is no raw argument escape hatch and the app never supplies credentials.
- **No game or manifest catalog is shipped or fetched.** The app does not include, download or bundle lists of commercial games, depot ids or manifest ids, and it does not bypass DRM, licensing, authentication or access controls.
- Manifests are only ever *read* from the local disk; nothing is uploaded or redistributed.

## Build and test

On a Windows machine with the .NET 8 SDK:

```powershell
dotnet restore SteamContentManager.sln
dotnet build SteamContentManager.sln
dotnet test SteamContentManager.sln
```

The test suite covers the VDF/ACF readers with real Steam file formats, the DepotDownloader output parser and argument builder, queue restore semantics, honest failure behaviour, the scroll-host guards, and a smoke test that builds the real DI container and loads every page with the application resources (missing `StaticResource` keys or unresolvable view models fail the test run).

## Project layout

- `src/SteamContentManager` – WPF application, pages, view models, models, services and resources.
- `src/SteamContentManager/Services/SteamLibraryService.cs` – VDF/ACF reader for the local Steam library.
- `src/SteamContentManager/Services/DownloadManager.cs` – real download pipeline (no demo mode).
- `src/SteamContentManager/Services/ServiceRegistration.cs` – the single container definition used by the app and the page smoke test.
- `tests/SteamContentManager.Tests` – unit tests, layout guards and the WPF page smoke tests.
- `src/SteamContentManager/Resources/LICENSE-CloudRedirect.txt` – attribution and MIT license notice for the referenced UI project.

## Getting the tool (DepotDownloader itself)

DepotDownloader is an open-source console tool by SteamRE, and its command line is documented in its
[README](https://github.com/SteamRE/DepotDownloader). Get it straight from GitHub:

```powershell
# Option A: download the latest release .zip and extract DepotDownloader.exe
#           https://github.com/SteamRE/DepotDownloader/releases/latest

# Option B: install it with the Windows Package Manager
winget install --exact --id SteamRE.DepotDownloader
```

The app runs the executable you select unchanged. It is never bundled or downloaded by this app.

## How a job downloads

The app builds a documented command line and runs it for you. Example of what lands in the job log:

```
"C:\Tools\DepotDownloader.exe" -app 730 -dir "D:\SteamLibrary\Counter-Strike 2" -depot 731 -username your_account -remember-password
```

1. Start the app; the Dashboard reads your local library and free space.
2. Games → **Scan local library** to (re-)read your installed apps; optionally point **Settings › Downloads › Steam folder** at a specific installation.
3. **Settings › Downloads** → select your `DepotDownloader.exe`, download folder and queue limits, then save.
4. Games → filter/search, tick the confirmation checkbox, select apps, **Queue selected**.
5. Downloads (or DepotDownloader) → start, pause, resume, cancel; the job log holds the exact command line, the exit code and the process output.

### Anonymous mode vs. your own account

DepotDownloader defaults to Steam's **anonymous account**, which only reaches apps that are published
for anonymous access (free-to-play and similar). Everything else needs a login with an account that is
licensed for the content.

- **Anonymous mode (default):** leave *Steam account* empty. The tool runs hidden, its output is parsed
  and the download progress is shown in the app.
- **Your own account:** set *Steam account (optional)* in Settings and switch on **Show DepotDownloader's
  console window**. The tool then shows its own console and asks you there for the password and Steam
  Guard code; the app passes `-username` and `-remember-password` (so the Steam session is reused
  instead of prompting for 2FA on every job) but never a password. While a job runs in that mode,
  progress lives in the tool's window — the app reports the job as running, then uses the real exit
  code and the local check. Retries are disabled for interactive runs so you do not get repeated login
  prompts.

If a prompt window ever fails to appear, the job log contains the exact command line, which you can run
in a terminal yourself.

## Getting started in the app (quick version)

1. Dashboard → check that the library and free space are read.
2. Games → **Scan local library**.
3. Settings › Downloads → `DepotDownloader.exe` path, account (optional), console window if needed, save.
4. Games → select apps → **Queue selected** → Downloads → **Start**.

## What this app deliberately does not do

- No game, depot, manifest **or key catalog** is shipped, fetched or imported from third parties, and
  the app does not turn such a catalog into download buttons. Manifest ids only ever come from what you
  type or read locally; depot decryption keys are never handled.
- No fork of the tool that specialises in bypassing ownership checks is supported, and no login, key or
  DRM circumvention is implemented. Downloads run through the unchanged upstream executable, with your
  own account, for content you are authorized to access.
- The *Steam account* name is stored in plain text because it is not a secret; it is left out of
  *Export safe settings*. Passwords, Steam Guard codes and session tokens are never asked for, stored
  or forwarded.
