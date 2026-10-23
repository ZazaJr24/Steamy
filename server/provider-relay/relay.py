"""Small, bounded provider relay. No client-supplied upstream URLs or credentials."""
import asyncio
import json
import ipaddress
import logging
import os
import re
import sqlite3
import time
import uuid
import zipfile
from collections import OrderedDict
from pathlib import Path

from aiohttp import ClientError, ClientSession, ClientTimeout, TCPConnector, web

LOG = logging.getLogger("steamy-relay")
PROVIDERS = {"ryuu": ("RYUU_API_KEY", "X-Auth-Key"), "depotbox": ("DEPOTBOX_API_KEY", "X-API-Key")}
ROUTE = re.compile(r"/v1/(ryuu|depotbox)/([1-9][0-9]{0,9})/(archive|lua|availability)\Z")
MAX_FILE = 128 * 1024 * 1024
MAX_CACHE = 512 * 1024 * 1024
CACHE_TTL = 6 * 3600
INFLIGHT_KEY = web.AppKey("inflight", set)


def upstream_url(provider, app_id, operation):
    if provider == "ryuu" and operation == "archive":
        return f"https://generator.ryuu.lol/api/download/{app_id}"
    if provider == "depotbox":
        if operation == "availability":
            return f"https://depotbox.org/api/games/{app_id}/availability"
        if operation in ("archive", "lua"):
            endpoint = "direct-download" if operation == "archive" else "direct-lua"
            return f"https://depotbox.org/api/{endpoint}?appid={app_id}"
    raise web.HTTPNotFound()


def failure(status, message, retry=0):
    headers = {"Cache-Control": "no-store"}
    if retry:
        headers["Retry-After"] = str(retry)
    return web.json_response({"error": message}, status=status, headers=headers)


class Relay:
    def __init__(self, cache_dir, keys, session=None, daily_limit=100, daily_bytes=2 * 1024**3):
        self.cache = Path(cache_dir)
        self.cache.mkdir(parents=True, exist_ok=True)
        self.keys = keys
        self.session = session
        self.owns_session = session is None
        self.daily_limit = daily_limit
        self.daily_bytes = max(0, daily_bytes)
        self.global_window = time.monotonic()
        self.global_requests = 0
        self.active = 0
        self.rates = OrderedDict()
        self.negative = OrderedDict()
        self.db = sqlite3.connect(self.cache / "quota.sqlite3")
        self.db.execute("CREATE TABLE IF NOT EXISTS quota (day TEXT PRIMARY KEY, count INTEGER NOT NULL)")
        self.db.commit()
        # Partial downloads from a killed process are never reusable cache entries.
        for path in self.cache.glob("*.partial"):
            path.unlink(missing_ok=True)
        self.prune()

    async def start(self, app):
        if self.owns_session:
            self.session = ClientSession(
                timeout=ClientTimeout(total=600, sock_connect=15, sock_read=90),
                connector=TCPConnector(limit=2, limit_per_host=2),
                headers={"User-Agent": "SteamyRelay/1.0", "Accept-Encoding": "identity"},
                trust_env=False, auto_decompress=False,
            )

    async def close(self, app):
        if self.owns_session and self.session:
            await self.session.close()
        self.db.close()

    def allow(self, ip, expensive):
        now = time.monotonic()
        # IPv6 privacy-address rotation must not reset one household's limits.
        try:
            address = ipaddress.ip_address(ip)
            ip = str(ipaddress.ip_network(f"{address}/64", strict=False)) if address.version == 6 else str(address)
        except ValueError:
            ip = "unknown"
        if now - self.global_window >= 60:
            self.global_window, self.global_requests = now, 0
        if self.global_requests >= 180:
            return False
        self.global_requests += 1
        # IPs are bounded in memory and never written to logs or disk. Forwarded headers
        # are safe only because the listener is loopback and Caddy overwrites this header.
        counts = self.rates.pop(ip, (now, 0, now, 0))
        minute, requests, hour, downloads = counts
        if now - minute >= 60:
            minute, requests = now, 0
        if now - hour >= 3600:
            hour, downloads = now, 0
        allowed = requests < 60 and (not expensive or downloads < 12)
        self.rates[ip] = (minute, requests + 1, hour, downloads + int(expensive))
        while len(self.rates) > 4096:
            self.rates.popitem(last=False)
        return allowed

    def reserve_quota(self, availability=False):
        # Persisted so restarting the service cannot reset the daily spending limit.
        date = time.strftime("%Y-%m-%d", time.gmtime())
        day = date + (":availability" if availability else ":downloads")
        limit = 10000 if availability else self.daily_limit
        with self.db:
            self.db.execute("DELETE FROM quota WHERE day < ?", (date,))
            self.db.execute("INSERT OR IGNORE INTO quota VALUES (?, 0)", (day,))
            changed = self.db.execute("UPDATE quota SET count = count + 1 WHERE day = ? AND count < ?", (day, limit))
            return changed.rowcount == 1

    def reserve_delivery(self, size):
        # Includes cached downloads and survives restarts. Charge the full file
        # before sending it, so reconnecting clients cannot evade the budget.
        day = time.strftime("%Y-%m-%d", time.gmtime()) + ":delivery-bytes"
        with self.db:
            self.db.execute("INSERT OR IGNORE INTO quota VALUES (?, 0)", (day,))
            changed = self.db.execute("UPDATE quota SET count = count + ? WHERE day = ? AND count <= ?",
                                      (size, day, self.daily_bytes - size))
            return changed.rowcount == 1

    def prune(self):
        now = time.time()
        files = sorted(self.cache.glob("*.payload"), key=lambda p: p.stat().st_mtime)
        remaining = []
        for path in files:
            if now - path.stat().st_mtime > CACHE_TTL:
                path.unlink(missing_ok=True)
            else:
                remaining.append((path, path.stat().st_size))
        total = sum(size for _, size in remaining)
        # Reserve space for both concurrent downloads, including their partial files.
        for path, size in remaining:
            if total <= MAX_CACHE - 2 * MAX_FILE:
                break
            path.unlink(missing_ok=True)
            total -= size

    async def health(self, request):
        if not self.allow(request.headers.get("X-Real-IP", request.remote or "unknown"), False):
            return failure(429, "Too many requests.", 60)
        return web.json_response({"status": "ok", "providers": {name: bool(self.keys.get(name)) for name in PROVIDERS}},
                                 headers={"Cache-Control": "no-store"})

    async def handle(self, request):
        match = ROUTE.fullmatch(request.path)
        if request.method != "GET" or not match or request.query_string:
            raise web.HTTPNotFound()
        provider, app_text, operation = match.groups()
        app_id = int(app_text)
        if app_id > 2147483647 or (provider == "ryuu" and operation == "lua"):
            raise web.HTTPNotFound()
        if not self.allow(request.headers.get("X-Real-IP", request.remote or "unknown"), operation != "availability"):
            return failure(429, "Request limit reached. Please try later.", 300)
        if not self.keys.get(provider):
            return failure(503, "This provider is not configured on the relay.")
        if provider == "ryuu" and operation == "availability":
            return web.json_response({"available": True, "certain": False})
        # Reject excess work immediately: no unbounded queue and no tasks waiting for locks.
        if self.active >= 2:
            return failure(503, "The relay is busy. Please retry shortly.", 15)
        self.active += 1
        partial = None
        try:
            identity = f"{provider}-{app_id}-{operation}"
            cached = self.cache / f"{identity}.payload"
            cache_hit = cached.is_file() and time.time() - cached.stat().st_mtime < (60 if operation == "availability" else CACHE_TTL)
            # One upstream fetch per identity; concurrent duplicates receive Retry-After.
            if identity in request.app[INFLIGHT_KEY]:
                return failure(503, "This package is being prepared. Please retry shortly.", 10)
            if not cache_hit:
                previous = self.negative.get(identity)
                if previous and previous[0] > time.monotonic():
                    return failure(previous[1], "Provider temporarily unavailable. Please retry later.", 60)
                if not self.reserve_quota(operation == "availability"):
                    return failure(429, "The relay's daily provider limit has been reached.", 3600)
                self.prune()
                request.app[INFLIGHT_KEY].add(identity)
                try:
                    partial = self.cache / f"{identity}-{uuid.uuid4().hex}.partial"
                    upstream = upstream_url(provider, app_id, operation)
                    _, auth_header = PROVIDERS[provider]
                    # Never follow redirects with an upstream credential. No upstream body,
                    # URL, cookies, error messages or auth headers are relayed on errors.
                    async with self.session.get(upstream, headers={auth_header: self.keys[provider]}, allow_redirects=False) as response:
                        if response.status != 200:
                            status = response.status if response.status in (404, 429) else 503 if response.status in (401, 403) else 502
                            self.negative[identity] = (time.monotonic() + 60, status)
                            while len(self.negative) > 1024:
                                self.negative.popitem(last=False)
                            return failure(status, "The provider could not deliver this package.", 60)
                        limit = 64 * 1024 if operation == "availability" else MAX_FILE
                        if response.content_length and response.content_length > limit:
                            return failure(502, "The provider package exceeds the size limit.")
                        received = 0
                        with partial.open("xb") as output:
                            async for chunk in response.content.iter_chunked(65536):
                                received += len(chunk)
                                if received > limit:
                                    return failure(502, "The provider package exceeds the size limit.")
                                output.write(chunk)
                        if operation == "availability":
                            document = json.loads(partial.read_bytes())
                            if not isinstance(document, dict):
                                return failure(502, "The provider returned invalid availability data.")
                            # Return just a boolean, never the provider's original metadata.
                            data = document.get("data", document)
                            if not isinstance(data, dict):
                                data = document
                            available = next((data[k] for k in ("available", "manifest_file_exists", "exists", "has_manifest", "hasManifest", "hasLua", "has_lua") if isinstance(data.get(k), bool)), None)
                            if available is None and isinstance(data.get("status"), str):
                                status = data["status"].lower()
                                available = True if status in ("available", "ok", "ready", "exists") else False if status in ("unavailable", "missing", "not_found") else None
                            if available is None:
                                return failure(502, "The provider's availability answer was inconclusive.")
                            partial.write_text(json.dumps({"available": available}), encoding="utf-8")
                        elif zipfile.is_zipfile(partial):
                            with zipfile.ZipFile(partial) as archive:
                                if not 0 < len(archive.infolist()) <= 10000 or sum(e.file_size for e in archive.infolist()) > 512 * 1024 * 1024:
                                    return failure(502, "The provider archive exceeds the extraction limit.")
                        elif provider == "depotbox":
                            text = partial.read_bytes()[:2 * 1024 * 1024].decode("utf-8-sig", errors="strict")
                            if received > 2 * 1024 * 1024 or "addappid" not in text or "<html" in text.lower():
                                return failure(502, "The provider returned invalid package data.")
                        else:
                            return failure(502, "The provider returned an invalid ZIP archive.")
                        partial.replace(cached)
                finally:
                    request.app[INFLIGHT_KEY].discard(identity)
            suffix = "json" if operation == "availability" else "zip" if zipfile.is_zipfile(cached) else "lua"
            content_type = {"zip": "application/zip", "lua": "text/plain", "json": "application/json"}[suffix]
            # Open before yielding, so cache eviction cannot race an in-progress reader.
            with cached.open("rb") as input_file:
                size = os.fstat(input_file.fileno()).st_size
                if not self.reserve_delivery(size):
                    return failure(429, "The relay's daily transfer limit has been reached.", 3600)
                result = web.StreamResponse(headers={"Content-Type": content_type,
                    "Content-Length": str(size),
                    "Content-Disposition": f'attachment; filename="{app_id}.{suffix}"',
                    "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff",
                    "X-Relay-Cache": "HIT" if cache_hit else "MISS"})
                try:
                    async with asyncio.timeout(300):
                        await asyncio.wait_for(result.prepare(request), 30)
                        while chunk := input_file.read(65536):
                            await asyncio.wait_for(result.write(chunk), 30)
                        await asyncio.wait_for(result.write_eof(), 30)
                except (asyncio.TimeoutError, ConnectionError):
                    # Headers might already have been sent; close instead of
                    # trying to append a second JSON response to a partial ZIP.
                    if request.transport:
                        request.transport.close()
                return result
        except (ClientError, asyncio.TimeoutError, OSError, ValueError, zipfile.BadZipFile):
            # No exception text: upstream libraries may include credential-bearing context.
            return failure(502, "The provider request failed. Please retry later.")
        finally:
            if partial:
                partial.unlink(missing_ok=True)
            self.active -= 1


RELAY_KEY = web.AppKey("relay", Relay)


def create_app(cache_dir=None, keys=None, session=None, daily_limit=None, daily_bytes=None):
    relay = Relay(cache_dir or os.environ.get("CACHE_DIR", "/var/cache/steamy-relay"),
                  keys if keys is not None else {name: os.environ.get(env, "") for name, (env, _) in PROVIDERS.items()},
                  session, daily_limit if daily_limit is not None else int(os.environ.get("DAILY_UPSTREAM_LIMIT", "100")),
                  daily_bytes if daily_bytes is not None else int(os.environ.get("DAILY_TRANSFER_BYTES", str(2 * 1024**3))))
    app = web.Application(client_max_size=1024, handler_args={"keepalive_timeout": 15})
    app[RELAY_KEY] = relay
    app[INFLIGHT_KEY] = set()
    app.router.add_get("/v1/health", relay.health)
    app.router.add_route("*", "/{tail:.*}", relay.handle)
    app.on_startup.append(relay.start)
    app.on_cleanup.append(relay.close)
    return app


if __name__ == "__main__":
    logging.basicConfig(level=logging.WARNING)
    # Only Caddy can reach the process; no access logs containing user IPs or queries.
    web.run_app(create_app(), host="127.0.0.1", port=8787, access_log=None, print=None)
