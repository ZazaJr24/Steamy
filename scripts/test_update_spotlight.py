import unittest
from datetime import datetime, timezone
from update_spotlight import select_game, plain_text, confirmed_release_time, enrich_releases, enrich_artwork


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

    def test_curated_major_releases_keep_their_actual_released_status(self):
        self.game['release_date'] = {'coming_soon': False, 'date': '25 Feb, 2022'}
        game = select_game(self.game, self.now, allow_released=True)
        self.assertFalse(game['comingSoon'])
        self.assertEqual('2022-02-25', game['releaseDate'])

    def test_requested_studios_are_discovered_only_with_confirmed_upcoming_status(self):
        for publisher in ('S-GAME Publishing', 'Secret Mode', 'Activision', 'Amazon Game Studios'):
            self.game['publishers'] = [publisher]
            self.assertTrue(select_game(self.game, self.now)['comingSoon'])
            self.game['release_date'] = {'coming_soon': False, 'date': '27 Aug, 2026'}
            self.assertIsNone(select_game(self.game, self.now))
            self.game['release_date'] = {'coming_soon': True, 'date': 'To be announced'}

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

    def test_hashed_portrait_artwork_uses_the_official_asset_format(self):
        game = {'appId': 123, 'portraitUrl': 'old-header', 'heroUrl': 'old-hero'}
        response = {'response': {'store_items': [{'appid': 123, 'assets': {
            'asset_url_format': 'steam/apps/123/${FILENAME}?t=1234',
            'library_capsule_2x': 'hash/library_capsule_2x.jpg',
            'library_hero': 'other/library_hero.jpg'}}]}}
        result = enrich_artwork([game], response)[0]
        self.assertEqual('https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/123/hash/library_capsule_2x.jpg?t=1234', result['portraitUrl'])
        self.assertIn('/other/library_hero.jpg', result['heroUrl'])
        response['response']['store_items'][0]['assets']['asset_url_format'] = 'https://untrusted.example/${FILENAME}'
        self.assertEqual(game, enrich_artwork([game], response)[0])

    def test_official_full_date_schedule_keeps_real_hours_and_seconds(self):
        game = {'appId':123, 'releaseDate':'2026-10-02'}
        scheduled = datetime(2026,10,2,17,30,15,tzinfo=timezone.utc)
        release = {'is_coming_soon':True, 'coming_soon_display':'date_full', 'steam_release_date':int(scheduled.timestamp())}
        self.assertEqual(confirmed_release_time(game,release,self.now),scheduled)
        for precision in ('date_month','date_year','date_quarter','text',''):
            self.assertIsNone(confirmed_release_time(game,{**release,'coming_soon_display':precision},self.now))

    def test_us_store_date_can_precede_the_actual_utc_launch_day(self):
        scheduled = datetime(2026,10,3,5,tzinfo=timezone.utc)
        release = {'is_coming_soon':True,'coming_soon_display':'date_full','steam_release_date':int(scheduled.timestamp())}
        self.assertEqual(confirmed_release_time({'releaseDate':'2026-10-02'},release,self.now),scheduled)
        self.assertIsNone(confirmed_release_time({'releaseDate':'2026-09-28'},release,self.now))

    def test_released_or_expired_schedules_are_excluded(self):
        games = [{'appId':1,'releaseDate':'2026-09-30'}, {'appId':2,'releaseDate':'2026-10-02'}]
        response = {'response':{'store_items':[
            {'appid':1,'release':{'is_coming_soon':True,'coming_soon_display':'date_full', 'steam_release_date':int(self.now.timestamp())-1}},
            {'appid':2,'release':{'is_coming_soon':False}}]}}
        self.assertEqual(enrich_releases(games,response,self.now),[])


if __name__ == '__main__':
    unittest.main()
