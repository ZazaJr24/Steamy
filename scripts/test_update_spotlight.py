import unittest
from datetime import datetime, timezone
from update_spotlight import select_game, plain_text


class SelectionTests(unittest.TestCase):
    def setUp(self):
        self.now = datetime(2026, 9, 30, tzinfo=timezone.utc)
        self.game = {'type': 'game', 'steam_appid': 123, 'name': 'Upcoming game', 'publishers': ['BANDAI NAMCO Entertainment'],
                     'developers': [], 'release_date': {'coming_soon': True, 'date': 'To be announced'}}

    def test_announced_release_has_honest_status(self):
        game = select_game(self.game, self.now)
        self.assertTrue(game['comingSoon'])
        self.assertIsNone(game['releaseDate'])
        self.assertEqual(game['releaseLabel'], 'To be announced')

    def test_old_catalog_games_do_not_return_as_new(self):
        self.game['release_date'] = {'coming_soon': False, 'date': '25 Feb, 2022'}
        self.assertIsNone(select_game(self.game, self.now))

    def test_fresh_game_is_kept(self):
        self.game['release_date'] = {'coming_soon': False, 'date': 'Sep 18, 2026'}
        self.assertEqual(select_game(self.game, self.now)['releaseDate'], '2026-09-18')

    def test_dlcs_and_unknown_publishers_are_excluded(self):
        self.game['type'] = 'dlc'
        self.assertIsNone(select_game(self.game, self.now))
        self.game['type'] = 'game'
        self.game['publishers'] = ['Unknown studio']
        self.assertIsNone(select_game(self.game, self.now))

    def test_store_markup_is_removed(self):
        self.assertEqual(plain_text('<b>A &amp; B</b><br>Adventure'), 'A & B Adventure')


if __name__ == '__main__':
    unittest.main()
