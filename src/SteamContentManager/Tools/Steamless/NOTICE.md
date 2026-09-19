# Bundled: Steamless

This folder is shipped with the application so nobody has to download a tool first. The files are
third-party software and remain under their own license — they are not covered by the license of
this application.

## What is bundled

| File | Purpose |
| --- | --- |
| `Steamless.CLI.exe` | Command line build, version **3.1.0.5**, by atom0s |
| `Steamless.CLI.exe.config` | Runtime configuration (needs .NET Framework 4.5.2+) |
| `Plugins/Steamless.API.dll` | Plugin contract used by the CLI |
| `Plugins/SharpDisasm.dll` | Disassembler used by the unpackers |
| `Plugins/Steamless.Unpacker.Variant*.dll` | One unpacker per SteamStub variant (1.0 → 3.1) |

`Plugins/` must stay next to `Steamless.CLI.exe`; the CLI loads the unpackers from there at start-up.
The graphical `Steamless.exe` and the example plugin from the release archive are intentionally not
bundled: the application provides the user interface itself.

## Source and author

* Project: <https://github.com/atom0s/Steamless>
* Author: atom0s
* Version shipped: 3.1.0.5 (build dated 2024-03-29)

## License of the bundled files

Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International (CC BY-NC-ND 4.0) —
full text in `LICENSE.txt`.

What that means for this application:

* **Attribution (BY)** — the author and the license must be named, which is why this file exists.
* **NonCommercial (NC)** — the application must not be sold or otherwise used commercially while
  this folder is part of it.
* **NoDerivatives (ND)** — the binaries must stay unmodified. Rebuild them from source if you ever
  need a change instead of patching the files here.

Steamless itself requires the .NET Framework 4.5.2 or newer, which is part of every current
Windows installation, so nothing extra has to be installed on the user's machine.
