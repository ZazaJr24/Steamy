#!/usr/bin/env python3
"""Build Steamy's checked-in DepotDownloaderMod fork without fetching source."""

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

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
SOURCE = HERE / "Source"
UPSTREAM_REVISION = "c0f62fb7f020087f36ae76adfc51fde1446af344"
FORK_VERSION = "1.0.0"


def run(*arguments: str | Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run([str(argument) for argument in arguments], check=True, text=True)


def sha256(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def source_digest(source: Path) -> str:
    digest = hashlib.sha256()
    for item in sorted(source.rglob("*")):
        relative = item.relative_to(source)
        if item.is_file() and not {"bin", "obj", ".git"}.intersection(relative.parts):
            digest.update(relative.as_posix().encode("utf-8"))
            digest.update(b"\0")
            digest.update(item.read_bytes())
            digest.update(b"\0")
    return digest.hexdigest()


def add_sources(destination: Path) -> None:
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as archive:
        for root, prefix in ((SOURCE, Path("DepotDownloaderMod")), (HERE, Path("SteamyFork"))):
            for item in sorted(root.rglob("*")):
                relative = item.relative_to(root)
                if not item.is_file() or {"bin", "obj", ".git", "__pycache__"}.intersection(relative.parts):
                    continue
                if root == HERE and relative.parts[0] not in {"README.md", "LICENSE", "build.py", "Tests"}:
                    continue
                archive.write(item, prefix / relative)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet", help="Path to the .NET SDK executable")
    parser.add_argument("--output", type=Path,
                        default=ROOT / "src/Steamy/Tools/DepotDownloaderMod/Release/net9.0")
    args = parser.parse_args()
    if not SOURCE.joinpath("DepotDownloader/DepotDownloaderMod.csproj").is_file():
        raise SystemExit("The owned fork source is missing from tools/DepotDownloaderMod/Source.")
    revision_file = SOURCE / "UPSTREAM.md"
    if UPSTREAM_REVISION not in revision_file.read_text(encoding="utf-8"):
        raise SystemExit("The pinned upstream attribution does not match the checked-in source.")

    with tempfile.TemporaryDirectory(prefix="Steamy-DepotDownloaderMod-") as temporary:
        work = Path(temporary)
        publish = work / "publish"
        project = SOURCE / "DepotDownloader/DepotDownloaderMod.csproj"
        run(args.dotnet, "test", HERE / "Tests/DepotDownloaderMod.Tests.csproj", "-c", "Release",
            "-p:EnforceCodeStyleInBuild=false")
        run(args.dotnet, "publish", project, "-c", "Release", "-r", "win-x64",
            "--self-contained", "true", "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
            "-p:PublishReadyToRun=false", "-p:DebugType=None", "-p:DebugSymbols=false",
            "-p:EnforceCodeStyleInBuild=false", "-o", publish)

        executable = publish / "DepotDownloaderMod.exe"
        if not executable.is_file():
            raise RuntimeError("The Windows executable was not published.")
        marker = {
            "schemaVersion": 1,
            "forkName": "Steamy DepotDownloaderMod",
            "forkVersion": FORK_VERSION,
            "upstreamRevision": UPSTREAM_REVISION,
            "sourceSha256": source_digest(SOURCE),
            "maxDownloadSpeed": True,
            "progressTelemetry": True,
            "gracefulStop": True,
            "boundedRetries": True,
            "atomicResume": True,
            "safeManifestPaths": True,
            "exclusiveTarget": True,
            "singleFile": True,
            "exeSha256": sha256(executable),
        }
        marker_path = publish / "Steamy-depotdownloader-mod.json"
        marker_path.write_text(json.dumps(marker, indent=2) + "\n", encoding="utf-8")
        shutil.copy2(HERE / "README.md", publish / "STEAMY-FORK.md")
        source_archive = publish / "DepotDownloaderMod-source.zip"
        add_sources(source_archive)
        with zipfile.ZipFile(source_archive) as archive:
            required = {
                "DepotDownloaderMod/DepotDownloader/DepotDownloaderMod.csproj",
                "DepotDownloaderMod/DepotDownloader/SteamyEngine.cs",
                "DepotDownloaderMod/LICENSE",
                "DepotDownloaderMod/UPSTREAM.md",
                "SteamyFork/Tests/SteamyEngineTests.cs",
            }
            if archive.testzip() is not None or not required.issubset(archive.namelist()):
                raise RuntimeError("The complete corresponding fork source archive failed validation.")

        if os.name == "nt":
            result = subprocess.run([str(executable), "--steamy-info"], capture_output=True,
                                    text=True, check=True, timeout=30)
            info = json.loads(result.stdout)
            if info.get("name") != marker["forkName"] or info.get("version") != FORK_VERSION:
                raise RuntimeError("The published executable reports the wrong fork identity.")
            for arguments in (("-max-download-speed", "not-a-rate"), ("-max-retries", "-1"),
                              ("-steamy-cancel-file", "relative.signal")):
                result = subprocess.run([str(executable), *arguments], capture_output=True,
                                        text=True, timeout=30)
                if result.returncode != 1:
                    raise RuntimeError(f"The published tool accepted invalid arguments: {arguments!r}")

        output = args.output.resolve()
        output.mkdir(parents=True, exist_ok=True)
        for obsolete in ("STEAMY-PATCH.md", "Steamy-rate-limit.json"):
            (output / obsolete).unlink(missing_ok=True)
        for item in publish.iterdir():
            if item.is_file():
                shutil.copy2(item, output / item.name)
        print(f"Steamy DepotDownloaderMod {FORK_VERSION} built from checked-in source; SHA-256 {marker['exeSha256']}")


if __name__ == "__main__":
    main()
