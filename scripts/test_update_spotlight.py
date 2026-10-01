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

    def test_released_game_is_excluded(self):
        self.game['release_date'] = {'coming_soon': False, 'date': 'Sep 18, 2026'}
        self.assertIsNone(select_game(self.game, self.now))

    def test_dlcs_and_unknown_publishers_are_excluded(self):
        self.game['type'] = 'dlc'
        self.assertIsNone(select_game(self.game, self.now))
        self.game['type'] = 'game'
        self.game['publishers'] = ['Unknown studio']
        self.assertIsNone(select_game(self.game, self.now))

    def test_month_year_is_not_an_exact_date(self):
        self.game['release_date'] = {'coming_soon': True, 'date': 'October 2026'}
        self.assertIsNone(select_game(self.game, self.now)['releaseDate'])

    def test_expired_upcoming_dates_are_excluded(self):
        for label in ('September 2025', '2025', '20 Sep, 2026', 'Q2 2026', 'Late 2025'):
            self.game['release_date'] = {'coming_soon': True, 'date': label}
            self.assertIsNone(select_game(self.game, self.now))

    def test_near_upcoming_date_is_kept(self):
        self.game['release_date'] = {'coming_soon': True, 'date': '2 Oct, 2026'}
        self.assertEqual(select_game(self.game, self.now)['releaseDate'], '2026-10-02')

    def test_demo_soundtrack_and_old_editions_are_excluded(self):
        for title in ('Game Demo', 'Game Soundtrack', 'Game Complete Edition'):
            self.game['name'] = title
            self.assertIsNone(select_game(self.game, self.now))

    def test_store_markup_is_removed(self):
        self.assertEqual(plain_text('<b>A &amp; B</b><br>Adventure'), 'A & B Adventure')


if __name__ == '__main__':
    unittest.main()
