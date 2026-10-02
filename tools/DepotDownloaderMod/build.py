#!/usr/bin/env python3
# GPL-2.0; see LICENSE. Steamy modification, 2026-10-01.
"""Build the pinned existing upstream with Steamy's GPL-2.0 rate-limit patch."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

UPSTREAM = "https://github.com/SteamAutoCracks/DepotDownloaderMod.git"
REVISION = "c0f62fb7f020087f36ae76adfc51fde1446af344"
HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent


def run(*arguments: str | Path, cwd: Path | None = None) -> None:
    subprocess.run([str(argument) for argument in arguments], cwd=cwd, check=True)


def sha256(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def add_sources(source: Path, destination: Path) -> None:
    # Full patched corresponding source, project/restore/build files and license.
    # Build output and VCS metadata are intentionally omitted.
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as archive:
        for item in sorted(source.rglob("*")):
            relative = item.relative_to(source)
            if item.is_file() and not {".git", "bin", "obj"}.intersection(relative.parts):
                archive.write(item, Path("DepotDownloaderMod") / relative)
        for name in ("build.py", "rate-limit.patch", "RateLimitedReadStream.cs", "SteamyProgress.cs", "README.md", "LICENSE"):
            archive.write(HERE / name, Path("Steamy-patch") / name)
        for item in sorted((HERE / "Tests").rglob("*")):
            if item.is_file() and not {"bin", "obj"}.intersection(item.relative_to(HERE).parts):
                archive.write(item, Path("Steamy-patch") / item.relative_to(HERE))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet", help="Path to the .NET SDK executable")
    parser.add_argument("--output", type=Path, default=ROOT / "src/Steamy/Tools/DepotDownloaderMod/Release/net9.0")
    parser.add_argument("--skip-tests", action="store_true")
    arguments = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="Steamy-pinned-depot-") as temporary:
        work = Path(temporary)
        source = work / "source"
        source.mkdir()
        run("git", "init", "--quiet", source)
        # Match the checked-in LF patch on both Windows and Linux runners.
        run("git", "config", "core.autocrlf", "false", cwd=source)
        run("git", "remote", "add", "origin", UPSTREAM, cwd=source)
        run("git", "fetch", "--quiet", "--depth=1", "origin", REVISION, cwd=source)
        run("git", "checkout", "--quiet", "--detach", "FETCH_HEAD", cwd=source)
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
        if revision != REVISION:
            raise RuntimeError("The downloaded upstream revision does not match the pinned source.")
        run("git", "apply", "--check", HERE / "rate-limit.patch", cwd=source)
        run("git", "apply", HERE / "rate-limit.patch", cwd=source)
        shutil.copy2(HERE / "RateLimitedReadStream.cs", source / "DepotDownloader/RateLimitedReadStream.cs")

        shutil.copy2(HERE / "SteamyProgress.cs", source / "DepotDownloader/SteamyProgress.cs")

        # Use the SDK chosen for Steamy rather than upstream's exact patch-level SDK.
        # This replacement also goes into the corresponding source archive.
        shutil.copy2(ROOT / "global.json", source / "global.json")
        if not arguments.skip_tests:
            run(arguments.dotnet, "test", HERE / "Tests/DepotDownloaderMod.Tests.csproj", "-c", "Release",
                f"-p:DepotDownloaderSource={source}")
        publish = work / "publish"
        run(arguments.dotnet, "publish", source / "DepotDownloader/DepotDownloaderMod.csproj",
            "-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true", "-p:PublishReadyToRun=false",
            "-p:DebugType=None", "-p:DebugSymbols=false", "-p:ContinuousIntegrationBuild=true",
            # The pinned upstream contains pre-existing formatting/header analyzer errors;
            # retain normal compiler/analyzer warning checks without changing upstream style.
            "-p:EnforceCodeStyleInBuild=false",
            "-o", publish, cwd=source)

        executable = publish / "DepotDownloaderMod.exe"
        if not executable.is_file():
            raise RuntimeError("The Windows executable was not published.")
        marker = {"schemaVersion": 1, "maxDownloadSpeed": True, "progressTelemetry": True, "singleFile": True,
                  "exeSha256": sha256(executable), "upstreamRevision": REVISION}
        (publish / "Steamy-rate-limit.json").write_text(json.dumps(marker, indent=2) + "\n", encoding="utf-8")
        shutil.copy2(HERE / "README.md", publish / "STEAMY-PATCH.md")
        add_sources(source, publish / "DepotDownloaderMod-source.zip")

        if os.name == "nt":
            result = subprocess.run([str(executable), "--steamy-rate-limit-info"],
                                    capture_output=True, text=True, check=True, timeout=30)
            if json.loads(result.stdout) != {"schemaVersion": 1, "maxDownloadSpeed": True, "progressTelemetry": True}:
                raise RuntimeError("The published tool does not report rate-limit support.")
            for invalid in ("-1", "not-a-rate", "9223372036854775808"):
                result = subprocess.run([str(executable), "-max-download-speed", invalid],
                                        capture_output=True, text=True, timeout=30)
                if result.returncode != 1 or "non-negative integer" not in result.stderr:
                    raise RuntimeError(f"The published tool did not reject invalid rate {invalid!r}.")

        # Replace only after the complete build and checks succeed, keeping failed builds
        # from leaving a partial binary/marker pair or a stale upstream DLL beside it.
        output = arguments.output.resolve()
        output.mkdir(parents=True, exist_ok=True)
        obsolete = {"DepotDownloaderMod.dll", "DepotDownloaderMod.deps.json", "DepotDownloaderMod.runtimeconfig.json",
                    "QRCoder.dll", "SteamKit2.dll", "System.IO.Hashing.dll", "ZstdSharp.dll",
                    "protobuf-net.Core.dll", "protobuf-net.dll", "DepotDownloaderMod.pdb"}
        for name in obsolete:
            (output / name).unlink(missing_ok=True)
        for item in publish.iterdir():
            if item.is_file():
                shutil.copy2(item, output / item.name)
        print(f"Pinned GPL tool published to {output}; executable SHA-256 {marker['exeSha256']}")


if __name__ == "__main__":
    main()
