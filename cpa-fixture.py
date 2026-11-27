"""Offline loopback contract for the existing Windows CPA routing probe."""
import argparse
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

class QuotaHandler(BaseHTTPRequestHandler):
    def handle_request(self):
        if self.headers.get('Authorization') != 'Bearer fake-bound':
            self.send_error(401)
            return
        selection = self.server.selection
        if self.command == 'PUT' and self.path == '/v1/me/account-selection':
            alias = self.headers.get('X-Quota-Account')
            if alias and alias not in ('second-account', 'first-account'):
                self.send_error(400)
                return
            selection = {'mode': 'manual', 'alias': alias} if alias else {'mode': 'auto'}
            self.server.selection = selection
        elif self.command != 'GET' or self.path != '/v1/me/quotas':
            self.send_error(404)
            return
        accounts = [
            dict(alias='first-account', displayName='b.json@example.invalid', available=True, availabilityKnown=True,
                 supported=True, weekly=dict(remainingPercent=68)),
            dict(alias='second-account', displayName='a.json@example.invalid', available=True, availabilityKnown=True,
                 supported=True, weekly=dict(remainingPercent=45))
        ]
        body = json.dumps({'selection': selection, 'accounts': accounts}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    do_GET = do_PUT = handle_request

parser = argparse.ArgumentParser()
parser.add_argument('--port-file', type=Path, required=True)
args = parser.parse_args()
with HTTPServer(('127.0.0.1', 0), QuotaHandler) as server:
    server.selection = {'mode': 'auto'}
    args.port_file.write_text(str(server.server_port), encoding='ascii')
    server.serve_forever()
