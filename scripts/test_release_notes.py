import hashlib
from pathlib import Path
import tempfile
import unittest
from release_notes import release_notes


class ReleaseNotesTests(unittest.TestCase):
    def test_base_and_hotfix_sections_are_distinct_and_hash_the_actual_archive(self):
        changelog = '# Changelog\n\n## 0.4.7.1 — today\n\nHotfix notes.\n\n## 0.4.7 — today\n\nBase notes.\n\n## 0.4.6\n\nOld notes.\n'
        with tempfile.TemporaryDirectory() as directory:
            for version, expected, excluded in [('0.4.7', 'Base notes.', 'Hotfix notes.'), ('0.4.7.1', 'Hotfix notes.', 'Base notes.')]:
                archive = Path(directory) / f'Steamy-v{version}.zip'
                archive.write_bytes(b'actual packaged ZIP bytes')
                notes = release_notes(changelog, version, archive)
                self.assertIn(expected, notes)
                self.assertNotIn(excluded, notes)
                self.assertNotIn('Old notes.', notes)
                self.assertIn(hashlib.sha256(archive.read_bytes()).hexdigest() + '  ' + archive.name, notes)

    def test_missing_or_empty_section_and_mismatched_archive_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            archive = Path(directory) / 'Steamy-v0.4.7.zip'
            archive.write_bytes(b'zip')
            for text, version in [('## 0.4.7.1\nHotfix', '0.4.7'), ('## 0.4.7\n\n## 0.4.6\nOld', '0.4.7'), ('## 0.4.7\nNotes', '0.4.7.1'), ('## 0.4.7\nNotes', '0.4')]:
                with self.assertRaises(ValueError):
                    release_notes(text, version, archive)
