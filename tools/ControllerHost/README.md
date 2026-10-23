# Steamy.ControllerHost

This separate GPL-3.0-or-later process integrates the DS4Windows engine from
`schmaldeo/DS4Windows` 3.9.9, commit `722c561c3d7aa65dcf063e716c04ca69e4ca33bd`.
The upstream desktop UI, updater, tray, auto-profile watcher and configuration-location
wizard are not started. Steamy owns the controls through the versioned local pipe.

Run `scripts/build-controller-host.ps1` from the repository root. The script verifies
the pinned upstream archive, creates a separate build tree, applies the documented
project changes and compiles our headless entry point with .NET 10. The complete
upstream source and these changes are included in the controller source package.
Existing DS4Windows configuration is never loaded: this host uses
`%LOCALAPPDATA%/Steamy/Controllers/Backend`.

The tested build uses .NET SDK 10.0.402 and self-contained runtime 10.0.13 on x64.
Project changes: net8 to net10; a headless `Steamy.ControllerHost.HostProgram` startup
object and assembly name; corrected checked-in `libs` paths; removal of the redundant
System.Memory reference; explicit WPF menu aliases for the .NET 10 compiler; suppression
of the engine's automatic elevation request subscription. All other upstream files are
preserved. The complete original source ZIP has SHA-256
`E81F5DE4E41AC3C205A5DA4CB7C733265A1D4D193D0DED936AABA938D625666D`.

Only the parent Steamy process may request a session. The pipe is restricted to the
current Windows user and requires a random, per-launch handshake secret. The host
stops its engine when the parent exits or the pipe disconnects. No TCP listener or
external service is used. Driver installation and device hiding are separate explicit
user actions; the engine cannot request elevation.

The upstream author notices and COPYING/LICENSE files remain in the source tree.
Our host changes are GPL-3.0-or-later; the wire DTOs are separately MIT licensed.
