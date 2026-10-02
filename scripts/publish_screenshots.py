"""Publish Windows captures in one commit without a large binary Git upload."""
import base64
import json
import os
from pathlib import Path
import urllib.error
import urllib.request

FILES = (
    'dashboard.png', 'discover-games.png', 'upcoming-details.png', 'better-steamtools.png', 'downloads.png', 'settings.png', 'games.png', 'games-hover.png',
    'game-details.png', 'local-package.png', 'download-depots.png',
    'download-location.png', 'hypervisor-fixes.png', 'game-fixes.png', 'dlc-unlocker.png',
)


class GitHubApi:
    def __init__(self, repository, token):
        self.url = 'https://api.github.com/repos/' + repository
        self.token = token

    def __call__(self, method, path, data=None):
        request = urllib.request.Request(
            self.url + path,
            data=json.dumps(data).encode() if data is not None else None,
            method=method,
            headers={'Authorization': 'Bearer ' + self.token, 'Accept': 'application/vnd.github+json',
                     'Content-Type': 'application/json', 'User-Agent': 'Steamy-Windows-screenshots'},
        )
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.load(response)


def publish(api, captures):
    # Upload individual blobs first. Only the final non-forced ref update changes main.
    entries = []
    for name, contents in captures.items():
        blob = api('POST', '/git/blobs', {'content': base64.b64encode(contents).decode(), 'encoding': 'base64'})
        entries.append({'path': 'docs/screenshots/' + name, 'mode': '100644', 'type': 'blob', 'sha': blob['sha']})
    for attempt in range(3):
        parent = api('GET', '/git/ref/heads/main')['object']['sha']
        base = api('GET', '/git/commits/' + parent)['tree']['sha']
        tree = api('POST', '/git/trees', {'base_tree': base, 'tree': entries})['sha']
        if tree == base:
            return parent
        commit = api('POST', '/git/commits', {
            'message': 'docs: refresh screenshots from the Windows app', 'tree': tree, 'parents': [parent],
            'author': {'name': 'github-actions[bot]', 'email': '41898282+github-actions[bot]@users.noreply.github.com'},
        })['sha']
        try:
            api('PATCH', '/git/refs/heads/main', {'sha': commit, 'force': False})
            return commit
        except urllib.error.HTTPError as error:
            if error.code != 422 or attempt == 2:
                raise
            # A concurrent main update stays intact; rebuild from its new tree and parent.
    raise RuntimeError('Could not publish captures after concurrent main updates')


if __name__ == '__main__':
    # Validate every capture before creating any remote objects.
    captures = {name: (Path('artifacts/ui') / name).read_bytes() for name in FILES}
    if any(not contents.startswith(b'\x89PNG\r\n\x1a\n') for contents in captures.values()):
        raise ValueError('A Windows capture is not a PNG')
    api = GitHubApi(os.environ['GITHUB_REPOSITORY'], os.environ['GITHUB_TOKEN'])
    print('Windows screenshots published at ' + publish(api, captures))
