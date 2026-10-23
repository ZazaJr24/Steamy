# Controllers in Steamy

Open **Tools → Controllers** or the Steamy menu. First-run setup includes an optional controller step with connection and driver instructions. Installation options can open this guide after installing Steamy; drivers are installed only through their labelled Install buttons.

## Connecting and navigation

Xbox/XInput controllers navigate Steamy directly. The DS4Windows-based backend adds DualShock 4 and DualSense input, mapping and virtual Xbox output over USB/Bluetooth. Select Start backend when needed. A saved enabled backend starts in the background on later app launches. Stop backend disables that restoration.

D-pad/stick moves focus, A/Cross confirms, B/Circle goes back, LB/RB changes pages, and X/Square opens the on-screen keyboard for a focused text field. Focus and temporary button hints are visible. Held confirm/back input is ignored until neutral after reconnecting. Input is consumed only while Steamy is foreground; the Sony physical snapshot takes priority over its virtual XInput output. The keyboard supports shifts, symbols, numbers, German characters, selection-aware insertion and backspace.

## Profiles and drivers

The page shows connected devices, USB/Bluetooth, battery/charging and live stick/trigger/button values. Profiles contain button mappings, stick deadzones/curves and RGB lightbar values. Test vibration is bounded to 400 ms. Assign a saved profile to an installed game; it is applied before launch and the default returns when the game ends, the launch fails or the active game changes.

Select a button in the controller diagram, choose its virtual Xbox output below, then save the profile. The complete mapping list remains available. The diagram and profile editor work without a connected gamepad; physical input and vibration require hardware. Xbox controllers continue to use native XInput.

Check drivers is read-only. ViGEmBus supplies virtual Xbox output; HidHide is optional for games that see both physical and virtual devices. The Install buttons download pinned original packages, verify SHA-256, Windows trust/revocation and the publisher, then open the normal installer with Windows confirmation. No driver is installed automatically. Close a separate DS4Windows instance before starting the integrated backend. Use one mapper per game when Steam Input is enabled. ViGEmBus is retired; the Driver information button opens the upstream compatibility/status information. Steamy does not silently change HidHide settings.

## Separate backend and source

`Steamy.ControllerHost` is a separate GPL-3.0-or-later process built from DS4Windows 3.9.9 at commit `722c561c3d7aa65dcf063e716c04ca69e4ca33bd`. The original UI/updater/tray and automatic elevation hooks are not started. It uses its own `%LOCALAPPDATA%/Steamy/Controllers/Backend` directory and does not load another DS4Windows profile folder.

IPC is a versioned, per-user local named pipe with a fresh per-launch secret and 64 KiB message limit. There is no TCP service. The helper stops on parent exit or pipe loss; recovery is bounded. No arbitrary executable commands are part of the profile protocol. The shared wire contract is separately MIT licensed.

The portable package includes `Tools/ControllerHost/Steamy-ControllerHost-source.zip`, original GPL/license notices and build instructions. The source ZIP contains the full pinned upstream archive, every Steamy modification, the build script and SDK selection. Build with:

```powershell
./scripts/build-controller-host.ps1 -SourcePackage
```

See [ControllerHost build details](../tools/ControllerHost/README.md) and [the verification report](steamy-0.8-report.md).

**No controller was available on the test PC.** Real USB/Bluetooth hotplug, mappings, virtual output, rumble/lightbar, duplicate-input behavior and game operation are unverified. Actual helper-process authentication, framing, read-only detection, signature checks, profile policy and simulated navigation are covered by automated checks. Drivers were not installed during verification.
