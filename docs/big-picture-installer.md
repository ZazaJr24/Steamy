# Catalog pages and installer · 0.7.0

Every catalog card opens the same Big Picture details page as installed games. Catalog-only titles display a green Install button. It opens the existing DepotDownloader source/folder wizard; opening a page never starts a download. Installed titles retain Play. Public information, screenshots and artwork use the existing Steam metadata service.

First-run setup has four steps: installed games, language/appearance, folders with optional download preferences, and verified downloader readiness. Completion remains persisted. Connections can be changed in Settings. The versioned explanation is shorter and retains its scroll/acknowledgement requirement.

## Distribution

Build `scripts/package-distribution.ps1` on Windows. It creates:

- `Steamy-v0.7.0-Setup.exe`: approximately 1.84 MiB native WPF download installer. Downloads the matching app, installs missing Microsoft .NET/WebView2 runtimes and prepares supported tool packages automatically. Requires the matching 0.7.0 release to be published and never silently installs an older version.
- `Steamy-v0.7.0-FullSetup.exe`: optional complete setup with the matching portable app embedded, built only with `-IncludeCompleteSetup`. WebView2 and tool preparation can still require an internet connection.
- `Steamy-v0.7.0-app.zip`: framework-dependent app, tracker, verified DepotDownloader fork and bundled app resources.
- `Steamy-v0.7.0.zip`: full portable app with private .NET 9.0.20 runtimes for both app and tracker, plus the downloader. Extract and run Steamy.exe. Optional services/tools, metadata and browser features still need their corresponding online dependencies; the portable ZIP is not an offline copy of all external services or every optional tool.

Portable packaging copies only the tracker's host, assembly and two manifests into the app directory. Its Core-only runtime facade must not overwrite the app's WPF `WindowsBase.dll`. Packaging checks the WPF assembly version. Run `scripts/test-portable-startup.ps1` to launch the actual portable EXE with isolated returning-user and first-run profiles, check that a responsive WPF window survives its initial rendering, and verify the tracker host. The test disables global .NET lookup and closes only processes it started.

The small setup requests its own version's GitHub release. It prefers the app payload and installs the Microsoft .NET 9 Desktop Runtime only if missing or older than the required 9.0.20 patch. The optional full setup uses its embedded portable payload without a global .NET dependency. Missing WebView2 is downloaded from Microsoft's Evergreen bootstrapper and installed separately. The Microsoft bootstrapper has its Authenticode signature checked before execution. Runtime installers may prompt for Windows approval. The application remains per-user by default at `%LOCALAPPDATA%\Programs\Steamy`, with a selectable folder, Start menu shortcut and optional desktop shortcut.

SHA-256/size validation happens before app extraction. Microsoft .NET uses its published SHA-512. ZIP paths, symlinks, duplicates and oversized extraction are rejected. An unrelated nonempty target is never replaced. Updates retain the previous app directory as `.previous-<id>` and roll back failed directory replacement. Setup asks users to close a running Steamy and never kills it. The downloaded app is launched only with the final Open Steamy button.

Preferences and installer staging remain under `%APPDATA%\Steamy` or the configured data root. The locator remains in Roaming. Installer staging is removed at the end of each run. The installer does not change antivirus settings. Windows Installed Apps registration/uninstallation is not provided by this lightweight bootstrapper; remove its application directory/shortcuts manually if needed, retaining data as desired.

## Publishing and verification

The prepared local packages do not make a new GitHub release. Until v0.7.0 is published, the small setup reports that the matching app is unavailable. The portable ZIP and optional full setup can use the local build. `.github/workflows/build.yml` packages the app payload, portable ZIP and small Setup EXE. After actual publication, `publish_update_manifest.py` verifies the published ZIP bytes/digest and writes separate manifests: legacy portable `updates/latest.json` and installer app payload `updates/installer.json`. Old clients continue receiving a single portable asset. GitHub API is preferred; these files provide fallback only when their version matches the installer.

Run the installer with `--self-test <absolute-report-path>` for isolated download/hash/extraction/commit tests (no actual runtime/app install), `--preview <absolute-png-path>` for a static display-only preview, `--preview-welcome <absolute-png-path>` for the welcome view, or `--check-release <absolute-report-path>` for read-only published metadata verification. Runtime execution is intentionally not exercised against the developer's PC. A clean Windows VM remains the appropriate end-to-end environment for first-time Microsoft runtime installation.

Microsoft references: [.NET release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/9.0/releases.json), [WebView2 deployment](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).


## Installer appearance and sharpness

Setup now uses an opaque native WPF window with Per Monitor V2 DPI awareness. Its vector light field follows the native display composition clock (limited to 30 frames per second on software rendering). The background uses the Steamy palette rather than the Hydra reference colours. The moving light and optional GPU blur are confined to the backdrop; text is a separate visual. Minimize, close and Windows reduced-motion/high-contrast preferences stop the background callback. Primary-button hover takes 120 ms and is disabled with reduced motion.

The original Steamy logo is a shared DrawingImage geometry in both the installer and main app. Setup embeds the complete Regular and SemiBold Google Sans faces compressed losslessly, with their OFL license; the fonts decompress into `installer/ui-fonts` under Roaming or the configured data root. This accounts for most of the installer size rather than padding the file. Setup and the main window use ClearType, display text formatting, layout rounding and device-pixel snapping. Main artwork uses high-quality bitmap scaling.

Folder and desktop shortcut choices are under Installation options. English/German choice, normal WPF buttons/checkboxes, Tab/Enter/Space, Escape and UI Automation patterns remain available. Download, ready and error states use the same layout and original verified installation backend. Display-only previews cannot install or download.

`--ui-test <absolute-report-path>` verifies fonts, vector artwork, composition, text modes, choices, accessibility, progress, 100/150/200-percent render dimensions, live animation lifecycle, ready/error states and preview isolation. `--preview-options`, `--preview-ready` and `--preview-error` render those states. `--preview <path> <seconds> <scale>` samples the real vector painter at 1–4x resolution. `--preview-motion <absolute-directory> <scale>` creates 180 sampled PNG frames without any installation. PNG/WebP exports use grayscale/ideal glyph rasterization at their output resolution; the live interface keeps ClearType/display formatting.
