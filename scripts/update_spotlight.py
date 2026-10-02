"""Build Steamy's public discovery feed from Steam's public store metadata. No API key."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from html import unescape
from pathlib import Path
import json
import re
import time
import urllib.parse
import urllib.request

PUBLISHERS = (
    'bandai namco', 'ubisoft', 'capcom', 'square enix', 'playstation', 'sony interactive',
    'electronic arts', 'bethesda', 'xbox game studios', 'activision', 'blizzard', 'rockstar',
    '2k', 'sega', 'konami', 'warner bros', 'cd projekt', 'thq nordic', 'deep silver',
    'focus entertainment', 'krafton', 'net ease', 'netease', 'io interactive', 'techland',
    'pearl abyss', 'nacon', 'dreamhaven', 'embark', 'bungie', 'fromsoftware', 'larian',
    's-game', 'secret mode', 'amazon game studios', 'remedy', '505 games', 'rebellion',
    'saber interactive', 'mundfish', 'nexon', '11 bit studios', 'coffee stain', 'ci games',
    'astrum entertainment', 'tencent', 'level infinite', 'keen games', 'annapurna',
)
REQUESTED = ('Modern Warfare 4', 'Phantom Blade Zero', 'Star Wars Galactic Racer',
             'Star Wars Zero Company', 'Tomb Raider Legacy of Atlantis', 'The Duskbloods',
             'Atomic Heart 2', 'Warhammer 40000 Dawn of War IV', 'Lords of the Fallen II',
             'Monster Hunter', 'Gothic 1 Remake', 'Clockwork Revolution', 'Light No Fire',
             'The Expanse Osiris Reborn', 'Lost Soul Aside', 'Dying Light', 'Subnautica 2',
             'Divinity', 'Kingdom Come', 'ACE COMBAT 8', 'Black Flag Resynced', 'Gears of War E-Day', '007 First Light',
             'Fable', 'The Blood of Dawnwalker', 'The Witcher 4', 'PRAGMATA',
             'Resident Evil Requiem', 'Grand Theft Auto VI', 'Marvel Wolverine',
             'Crimson Desert', 'Assassin Creed Hexe', 'Intergalactic', 'Silent Hill Townfall',
             'Metro 2039', 'Judas', 'Kingdom Hearts', 'Marvel Blade', 'Control Resonant')
MAJOR_RELEASES = (1245620, 1091500, 1174180, 2358720, 1086940, 2322010, 2050650, 1593500,
                  990080, 1716740, 1817070, 553850, 2054970, 292030, 3159330, 1979720,
                  2842040, 1903340, 2246340, 2677660, 1496790, 1328670, 1151640)
STORE = 'https://store.steampowered.com/'


def fetch_json(url):
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, headers={'User-Agent': 'Steamy-Spotlight/1.0', 'Accept': 'application/json'})
            with urllib.request.urlopen(req, timeout=25) as response:
                data = response.read(2_000_001)
            if len(data) > 2_000_000:
                raise ValueError('Store response exceeds the size limit')
            return json.loads(data)
        except Exception:
            if attempt == 2:
                raise
            time.sleep(2 ** attempt)


def plain_text(value):
    return re.sub(r'\s+', ' ', unescape(re.sub(r'<[^>]*>', ' ', str(value or '')))).strip()


def major_studio(data):
    labels = ' '.join(data.get('publishers', []) + data.get('developers', [])).casefold()
    return any(studio in labels for studio in PUBLISHERS)


def release_date(label):
    for fmt in ('%d %b, %Y', '%b %d, %Y', '%d %B, %Y', '%B %d, %Y'):
        try:
            return datetime.strptime(label, fmt).replace(tzinfo=timezone.utc)
        except ValueError:
            pass
    return None


def select_game(data, now, allow_released=False):
    if data.get('type') != 'game' or not major_studio(data):
        return None
    if 3 in data.get('content_descriptors', {}).get('ids', []):
        return None
    title = plain_text(data.get('name')).casefold()
    if re.search(r'\b(demo|soundtrack|dlc|complete edition|definitive edition|ultimate edition|collection|remaster(?:ed)?)\b', title):
        return None
    release = data.get('release_date', {})
    coming = bool(release.get('coming_soon'))
    date_label = plain_text(release.get('date'))
    date = release_date(date_label)
    if not coming and allow_released:
        pass
    elif not coming or (date and date.date() < now.date()):
        return None
    for fmt in ('%B %Y', '%b %Y', '%Y'):
        try:
            period = datetime.strptime(date_label, fmt)
            if coming and (period.year < now.year if fmt == '%Y' else (period.year, period.month) < (now.year, now.month)):
                return None
            break
        except ValueError:
            pass
    year_match = re.search(r'\b(20\d{2})\b', date_label)
    if coming and year_match and int(year_match.group(1)) < now.year:
        return None
    quarter = re.search(r'\bQ([1-4])\s+(20\d{2})\b', date_label, re.IGNORECASE)
    if coming and quarter and (int(quarter.group(2)), int(quarter.group(1))) < (now.year, (now.month-1)//3+1):
        return None
    if coming and date and date > now + timedelta(days=730):
        return None
    app_id = data.get('steam_appid')
    if not isinstance(app_id, int) or app_id <= 0:
        return None
    return {
        'appId': app_id, 'name': plain_text(data.get('name')),
        'description': plain_text(data.get('short_description'))[:350],
        'genres': ' · '.join(plain_text(g.get('description')) for g in data.get('genres', [])[:3]),
        'publisher': ' · '.join(data.get('publishers', [])),
        'comingSoon': coming, 'releaseLabel': date_label or ('Coming soon' if coming else 'New release'),
        'releaseDate': date.date().isoformat() if date else None,
        'storeUrl': f'{STORE}app/{app_id}/',
        'heroUrl': data.get('background_raw') or data.get('background') or data.get('header_image', ''),
        'headerUrl': data.get('header_image', ''),
        'portraitUrl': data.get('capsule_imagev5') or data.get('header_image', ''),
    }


def collect_candidates():
    ids = dict.fromkeys(MAJOR_RELEASES)
    requested_ids = set()
    released_ids = set(MAJOR_RELEASES)
    for term in REQUESTED:
        result = fetch_json(STORE + 'api/storesearch/?' + urllib.parse.urlencode({'term': term, 'l': 'english', 'cc': 'us'}))
        expected = re.sub(r'[^a-z0-9]', '', term.casefold())
        for item in result.get('items', []):
            title = re.sub(r'[^a-z0-9]', '', str(item.get('name', '')).casefold())
            if expected in title:
                ids[item['id']] = None
                requested_ids.add(item['id'])
    categories = fetch_json(STORE + 'api/featuredcategories/?cc=us&l=english')
    for key in ('coming_soon', 'new_releases', 'top_sellers'):
        for item in categories.get(key, {}).get('items', []):
            if isinstance(item.get('id'), int):
                ids[item['id']] = None
                if key != 'coming_soon':
                    released_ids.add(item['id'])
    for filter_name in ('popularcomingsoon', 'comingsoon'):
        query = urllib.parse.urlencode({'query': '', 'start': 0, 'count': 50, 'filter': filter_name,
                                       'category1': 998, 'infinite': 1, 'cc': 'us', 'l': 'english', 'ignore_preferences': 1})
        result = fetch_json(STORE + 'search/results/?' + query)
        for app_id in re.findall(r'data-ds-appid="(\d+)"', result.get('results_html', '')):
            ids[int(app_id)] = None
    for app_id in MAJOR_RELEASES:
        ids[app_id] = None
    return list(ids)[:240], requested_ids, released_ids


def confirmed_release_time(game, release, now):
    """Use Steam's published schedule only when its display precision is a full date."""
    timestamp = release.get('steam_release_date')
    if (release.get('is_coming_soon') is not True or release.get('coming_soon_display') != 'date_full'
            or not isinstance(timestamp, int) or isinstance(timestamp, bool) or timestamp <= 0
            or not game.get('releaseDate')):
        return None
    try:
        scheduled = datetime.fromtimestamp(timestamp, timezone.utc)
        store_day = datetime.fromisoformat(game['releaseDate']).date()
        # Store dates use the selected country. A US launch can fall on the next UTC day.
        if abs((scheduled.date() - store_day).days) > 1 or scheduled > now + timedelta(days=730):
            return None
        return scheduled
    except (ValueError, OverflowError, OSError):
        return None


def enrich_releases(games, response, now):
    items = {item.get('appid'): item.get('release', {}) for item in response.get('response', {}).get('store_items', [])}
    result = []
    for game in games:
        release = items.get(game['appId'], {})
        if release.get('is_coming_soon') is False:
            continue
        scheduled = confirmed_release_time(game, release, now)
        if scheduled is not None and scheduled <= now:
            continue
        result.append({**game, 'releaseTime': scheduled.isoformat() if scheduled else None})
    return result


def enrich_artwork(games, response):
    items = {item.get('appid'): item.get('assets', {}) for item in response.get('response', {}).get('store_items', [])}
    result = []
    for game in games:
        assets = items.get(game['appId'], {})
        template = assets.get('asset_url_format', '')
        changes = {}
        if template.startswith(f"steam/apps/{game['appId']}/") and '${FILENAME}' in template:
            for target, keys in {'portraitUrl': ('library_capsule_2x', 'library_capsule'),
                                 'heroUrl': ('library_hero_2x', 'library_hero'),
                                 'headerUrl': ('header_2x', 'header')}.items():
                filename = next((assets.get(key) for key in keys if assets.get(key)), None)
                if isinstance(filename, str) and re.fullmatch(r'[A-Za-z0-9_./-]+\.(jpg|png|webp)', filename) and '..' not in filename:
                    changes[target] = 'https://shared.akamai.steamstatic.com/store_item_assets/' + template.replace('${FILENAME}', filename)
        result.append({**game, **changes})
    return result


def refresh(output):
    now = datetime.now(timezone.utc)
    candidates, requested_ids, released_ids = collect_candidates()
    def details(app_id):
        try:
            response = fetch_json(STORE + f'api/appdetails?appids={app_id}&l=english&cc=us')
            item = response.get(str(app_id), {})
            return select_game(item.get('data', {}), now, allow_released=app_id in released_ids) if item.get('success') else None
        except Exception as error:
            print(f'Skipped app {app_id}: {type(error).__name__}')
            return None
    with ThreadPoolExecutor(max_workers=3) as pool:
        games = [game for game in pool.map(details, candidates) if game]
    games.sort(key=lambda game: (game['releaseDate'] is None, game['releaseDate'] or '9999-12-31',
                                  game['appId'] not in requested_ids))
    upcoming = [game for game in games if game['comingSoon']]
    # Keep named large productions even when they have not published an exact launch day.
    priority = [game for game in upcoming if game['appId'] in requested_ids]
    other = [game for game in upcoming if game['appId'] not in requested_ids]
    upcoming = (priority[:28] + other)[:48]
    released = sorted((game for game in games if not game['comingSoon']), key=lambda game: game['releaseDate'] or '', reverse=True)[:20]
    if upcoming:
        query = {'ids': [{'appid': game['appId']} for game in upcoming + released],
                 'context': {'language': 'english', 'country_code': 'US', 'steam_realm': 1},
                 'data_request': {'include_release': True, 'include_assets': True}}
        url = 'https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=' + urllib.parse.quote(json.dumps(query))
        try:
            response = fetch_json(url)
            upcoming = enrich_releases(enrich_artwork(upcoming, response), response, now)
            released = enrich_artwork(released, response)
        except Exception as error:
            print(f'Exact Steam schedule unavailable: {type(error).__name__}; retaining date-only countdowns')
    games = sorted(upcoming, key=lambda game: (game['releaseDate'] is None, game['releaseDate'] or '9999-12-31')) + released
    if len(upcoming) < 3:
        raise RuntimeError('Too few verified recent/upcoming games; retaining the previous feed')
    document = {'schemaVersion': 1, 'updatedAt': now.isoformat(), 'games': games}
    output.parent.mkdir(parents=True, exist_ok=True)
    temp = output.with_suffix('.tmp')
    temp.write_text(json.dumps(document, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    temp.replace(output)
    for game in games:
        print(f"{game['appId']}: {game['name']} | {game['releaseLabel']} | {game['publisher']}")


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, default=Path('src/Steamy/Data/spotlight.json'))
    refresh(parser.parse_args().output)
