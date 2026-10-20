"""Loopback-only synthetic CPA API. Never connects to CPAplus or a provider."""
import argparse
import json
import ssl
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

BASE = {
    "accounts": [{"alias": "账号 abc123", "displayName": "模拟账号", "supported": True,
                  "available": True, "availabilityKnown": True, "frozen": False, "disabled": False,
                  "cacheStatus": "fresh", "stale": False, "lastSuccessAt": "2026-10-20T00:00:00Z",
                  "fiveHour": {"remainingPercent": 62, "resetAt": "2026-10-20T05:00:00Z"},
                  "weekly": {"remainingPercent": 46, "resetAt": "2026-10-25T00:00:00Z"}}],
    "serverTime": "2026-10-20T00:00:10Z", "staleAfterSeconds": 300}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass  # Never record Authorization or request contents.

    def send_json(self, code, body, headers=None):
        payload = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        for key, value in (headers or {}).items():
            self.send_header(key, value)
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        key = self.headers.get("Authorization")
        if self.path == "/credential-leak-sentinel":
            self.send_json(500, {})
            raise AssertionError("Redirect must never be followed")
        if self.path != "/v1/me/quotas":
            return self.send_json(404, {})
        if key == "Bearer cpa-fake-redirect":
            return self.send_json(302, {}, {"Location": "/credential-leak-sentinel"})
        if key == "Bearer cpa-fake-limited":
            return self.send_json(429, {}, {"Retry-After": "120"})
        if key == "Bearer cpa-fake-forbidden":
            return self.send_json(403, {})
        if key != "Bearer cpa-fake-key-only":
            return self.send_json(401, {})
        self.send_json(200, BASE)

    def do_POST(self):
        if self.path != "/v1/me/quotas/refresh":
            return self.send_json(404, {})
        if self.headers.get("Authorization") != "Bearer cpa-fake-key-only":
            return self.send_json(401, {})
        if self.headers.get("X-Quota-Account") != "abc123":
            return self.send_json(403, {})
        result = json.loads(json.dumps(BASE))
        result["accounts"][0]["lastSuccessAt"] = "2026-10-20T00:00:30Z"
        result["refreshStatus"] = "success"
        self.send_json(200, result)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--cert", required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--port-file", required=True)
    args = parser.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(args.cert, args.key)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    with open(args.port_file, "w", encoding="ascii") as output:
        output.write(str(server.server_address[1]))
    server.serve_forever()
