import copy
import hashlib
from pathlib import Path
import tempfile
import unittest
from publish_update_manifest import manifest, publish
from test_publish_screenshots import Repository


class UpdateManifestTests(unittest.TestCase):
    def test_publication_checks_packaged_bytes_and_requires_a_real_release(self):
        with tempfile.TemporaryDirectory() as folder:
            archive = Path(folder) / 'Steamy-v0.4.16.zip'
            archive.write_bytes(b'packaged app fixture')
            release = {'tag_name': 'v0.4.16', 'draft': False, 'prerelease': False,
                       'published_at': '2026-10-02T15:02:00Z', 'assets': [{
                       'name': archive.name, 'size': archive.stat().st_size,
                       'browser_download_url': 'https://github.com/ZazaJr24/Steamy/releases/download/v0.4.16/' + archive.name,
                       'digest': 'sha256:' + hashlib.sha256(archive.read_bytes()).hexdigest()}]}
            self.assertEqual(manifest(release, archive, 'ZazaJr24/Steamy')['tag_name'], 'v0.4.16')
            for field, value in [('draft', True), ('prerelease', True), ('published_at', None)]:
                modified = copy.deepcopy(release)
                modified[field] = value
                with self.assertRaises(ValueError): manifest(modified, archive, 'ZazaJr24/Steamy')
            archive.write_bytes(b'changed package bytes')
            with self.assertRaises(ValueError): manifest(release, archive, 'ZazaJr24/Steamy')

    def test_nonforced_update_preserves_concurrent_source_changes(self):
        repo = Repository()
        repo.race = True
        def api(method, path, data=None):
            if path == '/releases/latest': return {'tag_name': 'v0.4.16'}
            return repo(method, path, data)
        publish(api, {'tag_name': 'v0.4.16'})
        current = repo.trees[repo.commits[repo.head]['tree']['sha']]
        self.assertIn('updates/latest.json', current)
        self.assertEqual(current['src/app.cs'], 'app-updated')

    def test_finishing_old_release_cannot_replace_latest_manifest(self):
        calls = []
        def api(method, path, data=None):
            calls.append(path)
            return {'tag_name': 'v0.4.17'}
        self.assertIn('Skipped', publish(api, {'tag_name': 'v0.4.16'}))
        self.assertEqual(calls, ['/releases/latest'])
