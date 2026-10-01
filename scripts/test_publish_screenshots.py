import base64
import copy
import hashlib
import json
import unittest
import urllib.error
from publish_screenshots import publish


class Repository:
    def __init__(self):
        self.trees = {'original': {'README.md': 'readme-original', 'src/app.cs': 'app-original'}}
        self.commits = {'initial': {'tree': {'sha': 'original'}}}
        self.head = 'initial'
        self.blobs = {}
        self.race = False
        self.patches = 0

    def __call__(self, method, path, data=None):
        if path == '/git/blobs':
            contents = base64.b64decode(data['content'])
            sha = hashlib.sha256(contents).hexdigest()
            self.blobs[sha] = contents
            return {'sha': sha}
        if path == '/git/ref/heads/main':
            return {'object': {'sha': self.head}}
        if method == 'GET' and path.startswith('/git/commits/'):
            return self.commits[path.rsplit('/',1)[1]]
        if path == '/git/trees':
            tree = copy.deepcopy(self.trees[data['base_tree']])
            tree.update({entry['path']: entry['sha'] for entry in data['tree']})
            if tree == self.trees[data['base_tree']]:
                return {'sha': data['base_tree']}
            sha = hashlib.sha256(json.dumps(tree, sort_keys=True).encode()).hexdigest()
            self.trees[sha] = tree
            return {'sha': sha}
        if path == '/git/commits':
            sha = str(len(self.commits))
            self.commits[sha] = {'tree': {'sha': data['tree']}, 'parents': data['parents']}
            return {'sha': sha}
        if path == '/git/refs/heads/main':
            self.patches += 1
            if self.race:
                self.race = False
                self.trees['concurrent'] = {**self.trees['original'], 'src/app.cs': 'app-updated'}
                self.commits['concurrent'] = {'tree': {'sha': 'concurrent'}}
                self.head = 'concurrent'
            if data['force'] or self.commits[data['sha']]['parents'] != [self.head]:
                raise urllib.error.HTTPError('https://example.invalid', 422, 'Not a fast forward', {}, None)
            self.head = data['sha']
            return {'object': {'sha': self.head}}
        raise AssertionError((method, path))


class PublishTests(unittest.TestCase):
    def test_all_captures_publish_together_and_keep_other_files(self):
        repo = Repository()
        publish(repo, {'dashboard.png': b'first capture', 'games.png': b'second capture'})
        tree = repo.trees[repo.commits[repo.head]['tree']['sha']]
        self.assertEqual(tree['README.md'], 'readme-original')
        self.assertEqual(repo.blobs[tree['docs/screenshots/games.png']], b'second capture')
        self.assertIn('docs/screenshots/dashboard.png', tree)
        self.assertEqual(repo.patches, 1)

    def test_concurrent_main_update_survives_screenshot_publish(self):
        repo = Repository()
        repo.race = True
        publish(repo, {'dashboard.png': b'capture'})
        tree = repo.trees[repo.commits[repo.head]['tree']['sha']]
        self.assertEqual(tree['src/app.cs'], 'app-updated')
        self.assertIn('docs/screenshots/dashboard.png', tree)
        self.assertEqual(repo.patches, 2)

    def test_identical_captures_do_not_create_another_commit(self):
        repo = Repository()
        first = publish(repo, {'dashboard.png': b'capture'})
        self.assertEqual(publish(repo, {'dashboard.png': b'capture'}), first)
        self.assertEqual(repo.patches, 1)
