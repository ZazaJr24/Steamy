# Steamy local platform fork

Upstream: https://github.com/madoiscool/BetterSteamTools
Pinned revision: `4747385bdc7c92fb422e63419a5093566b3bf626`.
License: GNU GPL v3, retained in `LICENSE`. This is a source snapshot, not a Git submodule.

The original Windows products and hooks remain intact. Steamy's additions implement
the existing OSTPlatform interfaces for POSIX systems and add executable conformance
tests. On Linux/macOS, CMake currently builds **the platform library and its tests**;
it does not build or install a working Steam unlocker. In particular, the PE pattern
loader, private Steam interfaces, detours, tickets and loader lifecycle still need
native ELF/Mach-O implementations and real Steam validation.

Build the implemented native platform slice:

```sh
cmake -S src -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j1
ctest --test-dir build --output-on-failure
```

No production Steam directory is modified by these tests. macOS code requires a
native macOS build/test run before it can be considered validated.
