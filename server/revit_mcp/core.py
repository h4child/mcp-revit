# =============================================================================
# revit_mcp/core.py — Configuração, servidor FastMCP e comunicação com o add-in
# =============================================================================
#
# Este arquivo conecta o Claude ao Revit através do protocolo MCP.
#
# ARQUITETURA:
#
#   Claude (IA) ──MCP──▶ server.py (este arquivo) ──HTTP──▶ Revit (add-in C#)
#
# Como funciona:
#   1. O Claude chama uma ferramenta (ex: "criar_parede")
#   2. Este servidor traduz para uma requisição HTTP POST
#   3. O add-in C# dentro do Revit executa o comando
#   4. O resultado volta pelo mesmo caminho
#
# CONFIGURAÇÃO:
#   - REVIT_MCP_URL: endereço do bridge (padrão: http://127.0.0.1:14733/rpc)
#   - REVIT_MCP_TIMEOUT: timeout em segundos (padrão: 120)
#
# Para iniciar: python server.py
# =============================================================================

import argparse
import asyncio
import json
import logging
import os
import sys
from pathlib import Path
from typing import Optional
from urllib.parse import urlparse

import httpx

# Pasta server/ (onde ficam .env e a cópia do token).
SERVER_DIR = Path(__file__).resolve().parent.parent

try:
    from mcp.server.fastmcp import FastMCP
except ImportError as _exc:  # mcp 2.x removeu o módulo fastmcp
    sys.stderr.write(
        "revit-mcp requer o pacote 'mcp' 1.x (mcp>=1.2,<2). "
        "Execute: pip install -r server/requirements.txt\n"
        f"Detalhe: {_exc}\n"
    )
    raise SystemExit(1)

try:
    from dotenv import load_dotenv
    load_dotenv(SERVER_DIR / ".env", override=False)
except ImportError:
    # O servidor continua funcional com variáveis do sistema; requirements.txt
    # instala python-dotenv no ambiente recomendado.
    pass

# --- Configuração de log ---
# stdout é o canal do protocolo MCP (stdio); logs vão sempre para stderr.
logging.basicConfig(
    stream=sys.stderr,
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("revit-mcp")
# Uma linha por requisição ao bridge polui o log do cliente MCP.
logging.getLogger("httpx").setLevel(logging.WARNING)

# --- Conexão com o bridge HTTP dentro do Revit ---
TIMEOUT = float(os.environ.get("REVIT_MCP_TIMEOUT", "120"))
CONFIG_PATH = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / ".local"))) / "RevitMcp" / "bridge.json"
WORKSPACE_CONFIG_PATH = SERVER_DIR / ".revit_mcp_bridge.json"
DEFAULT_PORT = 14733


def _read_config(config_path: Path) -> dict:
    try:
        data = json.loads(config_path.read_text(encoding="utf-8-sig"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError, TypeError):
        return {}


def _bridge_url() -> str:
    """URL do bridge: REVIT_MCP_URL > REVIT_MCP_PORT > "port" do bridge.json > 14733."""
    url = os.environ.get("REVIT_MCP_URL")
    if url:
        return url
    port = os.environ.get("REVIT_MCP_PORT") or _read_config(CONFIG_PATH).get("port") or DEFAULT_PORT
    return f"http://127.0.0.1:{int(port)}/rpc"


BRIDGE_URL = _bridge_url()


def _bridge_token() -> str:
    """Lê o token atual dinamicamente, permitindo reload seguro sem reiniciar o MCP.

    O bridge.json em %LOCALAPPDATA% é a fonte usada pelo Revit, então tem
    prioridade sobre a cópia ao lado do servidor (que pode ficar desatualizada
    se o add-in gerar um token novo).
    """
    token = os.environ.get("REVIT_MCP_TOKEN")
    if token:
        return token
    for config_path in (CONFIG_PATH, WORKSPACE_CONFIG_PATH):
        value = str(_read_config(config_path).get("token") or "")
        if value:
            return value
    return ""


def _validate_bridge_url() -> None:
    parsed = urlparse(BRIDGE_URL)
    allowed_hosts = {"127.0.0.1", "localhost", "::1"}
    if parsed.scheme != "http" or parsed.hostname not in allowed_hosts:
        if os.environ.get("REVIT_MCP_ALLOW_REMOTE") != "1":
            raise RuntimeError("REVIT_MCP_URL deve apontar para um bridge HTTP local. Para uma configuração remota auditada, defina REVIT_MCP_ALLOW_REMOTE=1.")


def _headers() -> dict:
    token = _bridge_token()
    if not token:
        raise RuntimeError("Token do RevitMcp não encontrado. Execute reload-revit-mcp.ps1 para inicializar o bridge seguro.")
    return {"X-Revit-Mcp-Token": token}


def _client(timeout: float) -> httpx.AsyncClient:
    # trust_env=False: proxies corporativos (HTTP_PROXY/HTTPS_PROXY) não devem
    # interceptar o tráfego para 127.0.0.1.
    return httpx.AsyncClient(timeout=timeout, trust_env=False)

# --- Servidor MCP ---
mcp = FastMCP("revit-mcp")


# =============================================================================
# Comunicação com o Revit via HTTP
# =============================================================================

async def _rpc(cmd: str, **args) -> object:
    """
    Envia um comando para o add-in do Revit e retorna o resultado.
    Lança RuntimeError se o Revit não estiver acessível ou retornar erro.
    """
    _validate_bridge_url()
    payload = {"cmd": cmd, "args": args}
    log.debug("RPC → %s %s", cmd, args)
    try:
        async with _client(TIMEOUT) as client:
            r = await client.post(BRIDGE_URL, json=payload, headers=_headers())
    except httpx.RequestError as e:
        raise RuntimeError(
            f"Não foi possível conectar ao Revit em {BRIDGE_URL}. "
            "Verifique se o Revit 2025 está aberto com o add-in RevitMcp carregado. "
            f"Detalhe: {e}"
        )

    try:
        data = r.json()
    except ValueError:
        raise RuntimeError(
            f"Resposta inválida do Revit (HTTP {r.status_code}): {r.text[:300]}"
        )

    if not isinstance(data, dict):
        raise RuntimeError(f"Resposta inesperada do Revit (HTTP {r.status_code}).")
    if not data.get("ok"):
        request_id = data.get("request_id")
        suffix = f" (requisição {request_id})" if request_id else ""
        raise RuntimeError((data.get("error") or f"Erro HTTP {r.status_code}") + suffix)

    return data.get("result")


def _clean(**kwargs) -> dict:
    """Remove argumentos não informados para o add-in aplicar seus padrões."""
    return {k: v for k, v in kwargs.items() if v is not None}


async def _health() -> dict:
    """GET /health autenticado; usado por bridge_health e por server.py --check."""
    _validate_bridge_url()
    health_url = BRIDGE_URL.rsplit("/", 1)[0] + "/health"
    try:
        async with _client(min(TIMEOUT, 15)) as client:
            response = await client.get(health_url, headers=_headers())
            data = response.json()
    except (httpx.RequestError, ValueError) as exc:
        raise RuntimeError(
            f"Bridge do Revit indisponível em {health_url}: {exc}. "
            "Confirme que o Revit 2025 está aberto e que o add-in RevitMcp carregou "
            "(veja %LOCALAPPDATA%\\RevitMcp\\logs\\revit_mcp_startup.log ou rode diagnose-revit-mcp.ps1)."
        )
    if not data.get("ok"):
        raise RuntimeError(data.get("error") or f"Health falhou (HTTP {response.status_code})")
    return data
