"""Infraestrutura de testes: um bridge HTTP falso que imita o add-in do Revit."""

import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "server"))

TOKEN = "token-de-teste"


class FakeBridge(BaseHTTPRequestHandler):
    calls: list = []

    def log_message(self, *args):  # silencia o log padrão
        pass

    def _send(self, status, payload):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _authorized(self):
        if self.headers.get("X-Revit-Mcp-Token") != TOKEN:
            self._send(401, {"ok": False, "error": "Não autorizado", "request_id": "r401"})
            return False
        return True

    def do_GET(self):
        if not self._authorized():
            return
        if self.path == "/health":
            self._send(200, {"ok": True, "service": "RevitMcp", "authenticated": True})
        else:
            self._send(404, {"ok": False, "error": "Endpoint não encontrado"})

    def do_POST(self):
        if not self._authorized():
            return
        req = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        FakeBridge.calls.append(req)
        if req["cmd"] == "boom":
            self._send(500, {"ok": False, "error": "falhou", "request_id": "abc"})
        else:
            self._send(200, {"ok": True, "result": {"echo": req}})


@pytest.fixture
def bridge(monkeypatch, tmp_path):
    server = ThreadingHTTPServer(("127.0.0.1", 0), FakeBridge)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    FakeBridge.calls = []

    import server as revit_server

    monkeypatch.setattr(revit_server, "BRIDGE_URL", f"http://127.0.0.1:{server.server_port}/rpc")
    monkeypatch.setenv("REVIT_MCP_TOKEN", TOKEN)
    # Um proxy no ambiente não pode interceptar chamadas ao bridge local.
    monkeypatch.setenv("HTTP_PROXY", "http://127.0.0.1:9")
    monkeypatch.setenv("HTTPS_PROXY", "http://127.0.0.1:9")
    yield revit_server, FakeBridge
    server.shutdown()
