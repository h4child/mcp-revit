# =============================================================================
# server.py — Ponto de entrada do servidor MCP para Revit 2025
# =============================================================================
#
#   Cliente MCP (IA) ──stdio──▶ este processo ──HTTP local + token──▶ add-in no Revit
#
# As ferramentas ficam no pacote revit_mcp/ (um módulo por área). Este arquivo
# mantém o caminho de execução documentado e reexporta as ferramentas para
# scripts e testes que importam `server`.
#
# Uso:
#   python server.py           modo MCP (stdio), usado pelo cliente
#   python server.py --check   testa a conexão com o Revit e sai
#
# Configuração: veja server/.env.example (REVIT_MCP_URL, REVIT_MCP_TIMEOUT...).
# =============================================================================

import argparse
import asyncio
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import revit_mcp  # noqa: E402  (registra todas as ferramentas)
from revit_mcp import core  # noqa: E402
from revit_mcp.annotations import classify_tool  # noqa: E402,F401
from revit_mcp.core import _bridge_token, _clean, _rpc, mcp  # noqa: E402,F401
from revit_mcp.tools_arch import *  # noqa: E402,F401,F403
from revit_mcp.tools_editing import *  # noqa: E402,F401,F403
from revit_mcp.tools_model import *  # noqa: E402,F401,F403
from revit_mcp.tools_pro import *  # noqa: E402,F401,F403


async def _check() -> int:
    """Diagnóstico de linha de comando: python server.py --check."""
    print(f"Bridge: {core.BRIDGE_URL}")
    print(f"Token: {'encontrado' if _bridge_token() else 'AUSENTE'} "
          f"(procurado em REVIT_MCP_TOKEN, {core.CONFIG_PATH}, {core.WORKSPACE_CONFIG_PATH})")
    print(f"Ferramentas registradas: {len(mcp._tool_manager.list_tools())}")
    try:
        health = await revit_mcp.tools_model.bridge_health()
        print("Health:", json.dumps(health, ensure_ascii=False))
        info = await _rpc("get_info")
        print("Revit:", json.dumps(info, ensure_ascii=False, default=str))
    except Exception as exc:  # noqa: BLE001 - relatório para o usuário
        print(f"FALHA: {exc}")
        return 1
    print("OK: o MCP está pronto para uso.")
    return 0


def main() -> None:
    parser = argparse.ArgumentParser(description="Servidor MCP para Autodesk Revit 2025.")
    parser.add_argument("--check", action="store_true",
                        help="testa a conexão com o add-in do Revit e sai")
    options = parser.parse_args()
    if options.check:
        raise SystemExit(asyncio.run(_check()))
    mcp.run()


if __name__ == "__main__":
    main()
