# Dashboard and downloads refresh

The dashboard prioritizes current downloads, a single row of recent games and four everyday
shortcuts. Downloads has a featured job, device-network telemetry, status/search filters and a
recycling list with selected-job details. Status and search apply to both the list and the hero.

## Automated checks

From the repository root with the .NET SDK selected by `global.json`:

```sh
dotnet restore Steamy.sln
dotnet build Steamy.sln -c Release --no-restore
dotnet test tests/Steamy.Tests/Steamy.Tests.csproj -c Release --no-build
```

On Windows, also run `dotnet test tests/Steamy.UiTests/Steamy.UiTests.csproj -c Release`.
This constructs the real pages and view models with offline service fixtures, validates both
themes at narrow/wide sizes and checks settings search. It launches no downloader process.
CI captures the rendered pages in `Steamy-test-evidence`, alongside TRX test results, and
publishes the runnable Windows ZIP as `Steamy-win-x64`.

The cross-platform tests cover combined search/status filtering, unknown and invalid ETAs,
state ordering, retained row identity and an unchanged history of 10,000 rows without collection
notifications. They also run the existing archive, sharing and parser regressions.
Download reliability tests additionally cover single operation ownership, paused cleanup,
local presence checks, interrupted queue states and exact manifest snapshot persistence.

## Windows visual and interaction checks

WPF needs Windows; a Linux cross-build cannot verify these checks. Run `./run.ps1` on Windows:

- Check dashboard and downloads at 980×620, 1280×800 and maximized, with Windows scaling at
  100%, 150% and 200%. Check dark and light themes, long game names and missing artwork.
- Navigate using Tab/Shift+Tab and arrow keys. Confirm visible focus, accessible button names,
  scrolling to keyboard selection and no scroll jump when clicking a download action.
- Search by game name and App ID; combine each with Active, Queued, Paused, Completed, Failed
  and Cancelled. A job leaving a status must disappear from that filter, including its hero.
- Exercise preparation, downloading, verification, pause/resume, retry, cancellation and
  completion. Details must show the selected job's status/file/folder, including after scrolling.
- With multiple active jobs and one unknown ETA, the total must show “Estimating…”. Device
  network traffic must remain labelled separately from Steamy download traffic.
- Rescan a large library while interacting with the window. Check that scanning leaves the UI
  usable and that success/failure feedback is shown. Switch pages during scans as well.
- With a long download history, inspect realized ListBox containers in the Visual Studio Live
  Visual Tree: only viewport and cache rows should be present, not the whole history.
- Disable Windows animation effects and verify that entrance fades and progress interpolation
  stop. Navigate away and check that dashboard/download timers and progress rendering stop.

Measure scrolling, navigation, idle CPU and memory on the same Windows machine before and
after the change. No frame-rate or download-throughput improvement is asserted by the Linux tests.
