"""Select exact three/four-part release notes and append the built ZIP's checksum."""
import argparse
import hashlib
from pathlib import Path
import re


def release_notes(changelog, version, archive, tool_archive=None):
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?', version):
        raise ValueError('Expected a three- or four-part release version')
    if archive.name != f'Steamy-v{version}.zip':
        raise ValueError('App ZIP must match the release version')
    lines = changelog.splitlines()
    header = re.compile(r'^##\s+v?' + re.escape(version) + r'(?=\s|$)')
    start = next((i + 1 for i, line in enumerate(lines) if header.search(line)), None)
    if start is None:
        raise ValueError(f'Missing changelog section for {version}')
    end = next((i for i in range(start, len(lines)) if re.match(r'^##\s', lines[i])), len(lines))
    notes = '\n'.join(lines[start:end]).strip()
    if not notes:
        raise ValueError(f'Empty changelog section for {version}')
    with archive.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    checksums = [digest + '  ' + archive.name]
    if tool_archive is not None:
        if tool_archive.name != f'DepotDownloaderMod-v{version}-win-x64.zip':
            raise ValueError('DepotDownloaderMod archive must match the release version')
        with tool_archive.open('rb') as stream:
            tool_digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        checksums.append(tool_digest + '  ' + tool_archive.name)
    return notes + '\n\n### SHA-256\n\n```text\n' + '\n'.join(checksums) + '\n```\n'


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', required=True)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--tool-archive', type=Path)
    parser.add_argument('--changelog', type=Path, default=Path('CHANGELOG.md'))
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(release_notes(args.changelog.read_text(encoding='utf-8'), args.version, args.archive, args.tool_archive), encoding='utf-8')
