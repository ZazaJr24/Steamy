"""Publish only the verified app ZIP of GitHub's current published release."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import urllib.error
from publish_screenshots import GitHubApi


def manifest(release, archive, repository):
    tag = release.get('tag_name', '')
    if not re.fullmatch(r'v\d+\.\d+\.\d+(?:\.\d+)?', tag):
        raise ValueError('Invalid release version')
    if release.get('draft') is not False or release.get('prerelease') is not False or not release.get('published_at'):
        raise ValueError('A published regular release is required')
    name = f'Steamy-{tag}.zip'
    if archive.name != name:
        raise ValueError('Archive does not match the release')
    assets = [asset for asset in release.get('assets', []) if asset.get('name') == name]
    if len(assets) != 1:
        raise ValueError('One versioned app ZIP is required')
    asset = assets[0]
    with archive.open('rb') as stream:
        digest = 'sha256:' + hashlib.file_digest(stream, 'sha256').hexdigest()
    if asset.get('digest') != digest or asset.get('size') != archive.stat().st_size:
        raise ValueError('Published asset differs from the actual packaged ZIP')
    url = f'https://github.com/{repository}/releases/download/{tag}/{name}'
    if asset.get('browser_download_url') != url:
        raise ValueError('Unexpected release download URL')
    return {'tag_name': tag, 'draft': False, 'prerelease': False, 'published_at': release['published_at'],
            'assets': [{'name': name, 'browser_download_url': url, 'size': asset['size'], 'digest': digest}]}


def publish(api, description):
    # A later release may have completed while this workflow was packaging.
    if api('GET', '/releases/latest')['tag_name'] != description['tag_name']:
        return 'Skipped an older release; the latest manifest stays intact.'
    payload = (json.dumps(description, indent=2) + '\n').encode()
    blob = api('POST', '/git/blobs', {'content': base64.b64encode(payload).decode(), 'encoding': 'base64'})['sha']
    for attempt in range(3):
        if api('GET', '/releases/latest')['tag_name'] != description['tag_name']:
            return 'Skipped an older release; the latest manifest stays intact.'
        parent = api('GET', '/git/ref/heads/main')['object']['sha']
        base = api('GET', '/git/commits/' + parent)['tree']['sha']
        tree = api('POST', '/git/trees', {'base_tree': base, 'tree': [
            {'path': 'updates/latest.json', 'mode': '100644', 'type': 'blob', 'sha': blob}]})['sha']
        if tree == base:
            return parent
        commit = api('POST', '/git/commits', {'message': 'updates: publish verified ' + description['tag_name'],
            'tree': tree, 'parents': [parent], 'author': {'name': 'github-actions[bot]',
            'email': '41898282+github-actions[bot]@users.noreply.github.com'}})['sha']
        try:
            api('PATCH', '/git/refs/heads/main', {'sha': commit, 'force': False})
            return commit
        except urllib.error.HTTPError as error:
            if error.code != 422 or attempt == 2:
                raise
    raise RuntimeError('Could not publish update metadata')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', required=True)
    parser.add_argument('--archive', required=True, type=Path)
    args = parser.parse_args()
    repository = os.environ['GITHUB_REPOSITORY']
    api = GitHubApi(repository, os.environ['GITHUB_TOKEN'])
    release = api('GET', '/releases/tags/v' + args.version)
    print(publish(api, manifest(release, args.archive, repository)))
