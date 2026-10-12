# Steam Deck downloads in Steamy

The Downloads page is a native WPF component in the existing .NET 9 application. Its full XAML and C# implementation is in the repository; no Python, web view, CSS runtime or framework migration is required for this page.

## Code

- `src/Steamy/Pages/DownloadsPage.xaml`: complete layout, Steam palette, buttons, responsive queue, grouped history, footer and options/recovery overlay.
- `src/Steamy/Pages/PageCodeBehind.cs`: responsive banner sizes and page lifecycle.
- `src/Steamy/Pages/DownloadsPage.Input.cs`: keyboard commands, focus management and Windows XInput controller input.
- `src/Steamy/Controls/DownloadRateGraph.cs`: native bounded graph of actual network samples, blue bars and a green five-sample average.
- `src/Steamy/Controls/DownloadHoverGlow.cs`: 120 ms hover highlights; respects reduced motion and releases animation clocks/subscriptions.
- `src/Steamy/Controls/NextDownloadVisibilityConverter.cs`: identifies the actual next waiting game.
- `src/Steamy/Models/DownloadDeckPresentation.cs`: network/content progress, bit rates, session peak, bounded history and precise remaining time.
- `src/Steamy/ViewModels/DownloadsViewModel.cs`: existing queue commands, selected download, live artwork and footer presentation.
- `src/Steamy/MainWindow.xaml`, `src/Steamy/MainWindow.xaml.cs` and `src/Steamy/Resources/Shell.xaml`: the Steamy brand/menu and centered navigation remain visible on Downloads. The active game's download shortcut has a separate progress line below its artwork, including indeterminate preparation and paused/error colors.
- `src/Steamy/Pages/LibraryPage.xaml`: larger 2:3 covers without nested card frames, underlined library tabs, separate titles/genres and a blue hover/focus outline. Cover grids retain native virtualization and pixel scrolling.
- Download services carry the measured process-write rate through both direct and per-depot downloads.

## Data and behavior

The blue progress bar uses transferred bytes divided by the reported transfer total. The green bar uses processed content bytes divided by the reported content total. Unknown totals stay unknown; the UI never derives download completion from process writes, file reservations or elapsed time. Multiple-depot content progress is explicitly labeled with the current depot when only its total is available.

Network and disk values are converted from bytes/s to decimal Mbit/s or Gbit/s. The disk metric measures the downloader process's write I/O, including its file output; it is not a physical SSD benchmark. The graph keeps at most 60 samples for a job even when the page is closed. The green line averages five measured network samples. Session peak survives pause/resume and page changes. No synthetic throughput is shown in production.

The right-hand queue status reflects Steamy's real automatic-resume preference. It does not claim that automatic game-update scheduling exists. Completed/failed/cancelled jobs are grouped in the history, and ordering/repair/removal controls are enabled only for applicable job states.

Keyboard: F10 opens options, Ctrl+F opens search, Escape closes options or goes back, and Space pauses/resumes when a text field or button is not handling the key. Tab/Shift+Tab navigate controls. On XInput controllers, A activates a focused action or toggles the active download, B goes back, Menu opens options, View opens the app menu and the D-pad traverses focus. Physical controller hardware has not been exercised in this environment.

Native layout rounding, device-pixel snapping, ClearType and high-quality artwork scaling are enabled. The queue uses pixel scrolling and recycling virtualization, including grouped history. Sampling/input timers stop when the page unloads; hover animations release their subscriptions when a recycled row unloads.

The shell download shortcut uses a 36-pixel glass face with 12-pixel corners, inset rounded artwork and a 16-pixel status badge. A 160 ms hover lift/scale, 120 ms highlight, short press feedback and artwork entrance respect the existing reduced-motion preferences. Its progress line remains below the icon.

The centered navigation uses less white tint, a finer glass edge and sharper artwork with a 0.025 shader blur intensity. Tab visuals enlarge by 7.5% over 150 ms inside fixed button hitboxes; the highlight follows in 175 ms. Both respect reduced motion.

Game details sample each artwork once at 96 × 54 pixels and cache its regional color palette by image. Hue clustering favors illuminated accents over dark backgrounds and ignores transparent/white margins. The artwork fades to full transparency over a shared ambient surface, so there is no horizontal cut between the banner and the action bar. The ambient drawing does not enlarge the scroll extent or run a live blur. Light mode uses lighter tints; high contrast uses system colors. Missing or monochrome images have neutral fallbacks.

Installation now uses one scrollable sheet with method cards, collapsible source/content options and the destination drives together. A single Install action loads the selected source's metadata, retains the chosen manifests, checks free space and persists the job before navigating to Downloads. Input and repeated Install actions are locked during preparation; closing the sheet cancels preparation while registered downloads retain their background operation. BetterSteamTools uses the same sheet with its Steam folder and source controls.

Downloads now use a pure black background with a smaller 216–270 px hero, compact rounded thumbnails and a thin footer. Hovering or keyboard-focusing a game previews its artwork palette across the page and shell. Two lightweight ambient drawings crossfade in 260 ms; leaving fades back to black. The page caches at most 64 palettes and cancels stale artwork requests, including when rows are recycled or the page closes. Hover never changes the active download. Reduced motion uses immediate transitions; high contrast keeps system colors. Other pages regain their original background when Downloads closes.

The Games library has its own permanent artwork gradient, independent of mouse position. It uses cached palettes from the first six cards on the current catalog page, selects contrasting artwork accents, and fades them toward a darker bottom. Late artwork updates and page/filter changes refresh the gradient after a short debounce; scrolling and hover do not recompute it. The same frozen drawing colors the shell above the grid. Light mode and system high contrast remain supported, and the previous workspace background is restored when leaving the library.

## Build and run

From the existing repository:

```powershell
dotnet build src/Steamy/Steamy.csproj -c Release
& ./scripts/package-distribution.ps1 -OutputDirectory artifacts/library-persistent-gradient/distribution
```

Run `artifacts/library-persistent-gradient/distribution/portable/Steamy.exe` from its complete portable folder. The complete portable ZIP includes the .NET runtime and tracker. The tiny online setup uses the published GitHub release and does not contain unpublished local UI changes.

The source-change ZIP contains the complete changed files with their repository paths. It applies to the existing Steamy project, not to an empty project.

Backups: `backups/navigation-grids-before-refinement-20261010.zip`, `backups/downloads-before-steam-deck-20261010.zip` and `backups/download-metrics-before-steam-deck-20261010.zip`. The earlier portable builds remain available.

Screenshots in `.probe-glass/navigation-grids/packaged` use deterministic test games and telemetry, not the user's downloads.

Home now uses the featured banner's cached regional artwork colors behind Installed games and Upcoming releases. A 220-pixel transparent transition joins the hero to the same palette and 940-pixel ambient gradient used on individual game pages. Artwork arrival, spotlight changes and theme changes refresh the frozen brushes; scrolling and mouse movement do not resample artwork. High contrast uses the system window color.

Home ambient now preserves 24 spatial banner regions (8 columns by 3 rows). Pale and gray areas remain neutral instead of inheriting a saturated accent from another part of the banner. Cached, frozen overlapping radial gradients continue the artwork into the page; the base stays neutral and no extra network requests or per-scroll sampling occur.

Home edge correction: the native vertical scrollbar overlays the full-width, clipped artwork presenter instead of reserving an unpainted 16-pixel column. Its handle is inset 4 pixels and starts below the window controls; hover never changes the viewport width. Wheel and scrollbar commands retain the native scrolling behavior.

Home banner titles always use real text instead of logo images. A dark, horizontally fading scrim behind the text preserves readability on pale artwork without dimming the entire banner. Titles support three lines, descriptions use 12px text, and high contrast hides the scrim. Unused Home logo fetches have been removed.

Home restores official logo images and asynchronous cached logo loading; text titles remain hidden. The left contrast scrim protects logos on bright artwork. Spatial ambient colors are now dimmer, concentrated near the hero edge and fade out sooner into the dark body, avoiding the pale horizontal wash behind the cards.

Home ambient now samples the same centered UniformToFill crop as the hero and incorporates the logo scrim's horizontal darkening into its local colors. The scrim continues to the hero's lower edge instead of fading back to the pale source on the left. Aspect-ratio samples are quantized and bounded to eight cached variants per image, refreshed on hero resize rather than scrolling.
