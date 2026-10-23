import asyncio
import io
import json
import tempfile
import unittest
import zipfile

from aiohttp.test_utils import TestClient, TestServer

from relay import INFLIGHT_KEY, RELAY_KEY, MAX_FILE, Relay, create_app


def package():
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        archive.writestr("480.lua", 'addappid(480)\naddappid(481,0,"fixture-depot-key")')
        archive.writestr("481_123.manifest", "fixture manifest")
    return output.getvalue()


class Content:
    def __init__(self, body):
        self.body = body

    async def iter_chunked(self, size):
        for offset in range(0, len(self.body), size):
            await asyncio.sleep(0)
            yield self.body[offset:offset + size]


class Response:
    def __init__(self, body, status=200, length=None):
        self.status = status
        self.content_length = len(body) if length is None else length
        self.content = Content(body)

    async def __aenter__(self):
        return self

    async def __aexit__(self, *args):
        pass


class Upstream:
    def __init__(self):
        self.calls = []
        self.response = Response(package())

    def get(self, url, **kwargs):
        self.calls.append((url, kwargs))
        return self.response


class RelayTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.upstream = Upstream()
        self.app = create_app(self.directory.name, {"ryuu": "private-ryuu-fixture", "depotbox": "private-depotbox-fixture"}, self.upstream, daily_limit=2)
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()
        self.directory.cleanup()

    async def test_health_never_exposes_credentials_or_calls_upstream(self):
        response = await self.client.get("/v1/health")
        self.assertEqual(200, response.status)
        self.assertNotIn("private-", await response.text())
        self.assertFalse(self.upstream.calls)

    async def test_download_uses_fixed_upstream_and_server_key_without_redirects(self):
        response = await self.client.get("/v1/ryuu/480/archive", headers={"X-Auth-Key": "attacker", "Authorization": "attacker"})
        self.assertEqual(200, response.status)
        self.assertEqual(package(), await response.read())
        url, options = self.upstream.calls[0]
        self.assertEqual("https://generator.ryuu.lol/api/download/480", url)
        self.assertEqual({"X-Auth-Key": "private-ryuu-fixture"}, options["headers"])
        self.assertIs(False, options["allow_redirects"])
        self.assertNotIn("private-", str(response.headers))

    async def test_cache_does_not_repeat_generation(self):
        for expected in ("MISS", "HIT"):
            response = await self.client.get("/v1/depotbox/480/archive")
            self.assertEqual(200, response.status)
            self.assertEqual(expected, response.headers["X-Relay-Cache"])
            await response.read()
        self.assertEqual(1, len(self.upstream.calls))

    async def test_cached_downloads_consume_persistent_transfer_budget(self):
        relay = self.app[RELAY_KEY]
        relay.daily_bytes = len(package()) * 2
        for expected in ("MISS", "HIT"):
            response = await self.client.get("/v1/depotbox/480/archive")
            self.assertEqual(200, response.status)
            self.assertEqual(expected, response.headers["X-Relay-Cache"])
            await response.read()
        response = await self.client.get("/v1/depotbox/480/archive")
        self.assertEqual(429, response.status)
        self.assertEqual(1, len(self.upstream.calls))
        second = Relay(self.directory.name, {}, self.upstream, daily_bytes=relay.daily_bytes)
        try:
            self.assertFalse(second.reserve_delivery(len(package())))
        finally:
            second.db.close()

    async def test_ipv6_address_rotation_does_not_reset_package_limit(self):
        relay = self.app[RELAY_KEY]
        for index in range(12):
            self.assertTrue(relay.allow(f"2001:db8:1234:5678::{index + 1:x}", True))
        self.assertFalse(relay.allow("2001:db8:1234:5678::ffff", True))
        self.assertTrue(relay.allow("2001:db8:1234:5679::1", True))

    async def test_distributed_clients_cannot_exceed_global_request_rate(self):
        relay = self.app[RELAY_KEY]
        for index in range(180):
            self.assertTrue(relay.allow(f"10.0.0.{index + 1}", False))
        self.assertFalse(relay.allow("192.0.2.1", False))
        self.assertFalse(self.upstream.calls)

    async def test_invalid_paths_queries_and_methods_cannot_forward_urls(self):
        for path in ("/v1/ryuu/0/archive", "/v1/ryuu/2147483648/archive", "/v1/other/480/archive", "/v1/ryuu/480/lua", "/.env", "/v1/ryuu/480/archive?url=https://example.com"):
            response = await self.client.get(path)
            self.assertEqual(404, response.status, path)
        response = await self.client.post("/v1/ryuu/480/archive", data="evil")
        self.assertEqual(404, response.status)
        self.assertFalse(self.upstream.calls)

    async def test_upstream_error_and_redirect_cannot_leak_credentials(self):
        self.app[RELAY_KEY].daily_limit = 100
        for status in (301, 401, 403, 500):
            self.upstream.response = Response(b'{"error":"private-ryuu-fixture"}', status)
            response = await self.client.get(f"/v1/ryuu/{480 + status}/archive")
            self.assertEqual(503 if status in (401, 403) else 502, response.status)
            self.assertNotIn("private-", await response.text())

    async def test_bad_or_oversized_zip_is_not_published(self):
        for body, length in ((b"<html>private-ryuu-fixture</html>", 20), (b"PK", MAX_FILE + 1)):
            self.upstream.response = Response(body, length=length)
            response = await self.client.get("/v1/ryuu/480/archive")
            self.assertEqual(502, response.status)
            self.assertNotIn("private-", await response.text())
            self.assertFalse(list(self.app[RELAY_KEY].cache.glob("*.payload")))
            self.assertFalse(list(self.app[RELAY_KEY].cache.glob("*.partial")))

    async def test_availability_only_returns_boolean(self):
        self.upstream.response = Response(json.dumps({"data": {"available": True, "api_key": "private-depotbox-fixture"}}).encode())
        response = await self.client.get("/v1/depotbox/480/availability")
        self.assertEqual({"available": True}, await response.json())

    async def test_daily_limit_survives_restart(self):
        relay = self.app[RELAY_KEY]
        self.assertTrue(relay.reserve_quota())
        self.assertTrue(relay.reserve_quota())
        self.assertFalse(relay.reserve_quota())
        second = Relay(self.directory.name, {}, self.upstream, daily_limit=2)
        try:
            self.assertFalse(second.reserve_quota())
        finally:
            second.db.close()

    async def test_ip_limit_busy_and_duplicate_work_fail_without_fetching(self):
        relay = self.app[RELAY_KEY]
        for _ in range(12):
            self.assertTrue(relay.allow("fixture-ip", True))
        self.assertFalse(relay.allow("fixture-ip", True))
        self.app[INFLIGHT_KEY].add("ryuu-480-archive")
        response = await self.client.get("/v1/ryuu/480/archive")
        self.assertEqual(503, response.status)
        relay.active = 2
        response = await self.client.get("/v1/ryuu/481/archive")
        self.assertEqual(503, response.status)
        self.assertFalse(self.upstream.calls)


if __name__ == "__main__":
    unittest.main()
