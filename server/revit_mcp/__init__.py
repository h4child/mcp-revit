"""Servidor MCP para Autodesk Revit 2025.

Módulos:
    core          configuração, instância FastMCP e chamada RPC ao add-in
    tools_model   informações, projeto, criação de elementos e visualização
    tools_editing manipulação, anotação, componentes, pranchas e exportação
    tools_pro     dados do projeto, vistas, documentação, coordenação, estrutura e MEP
    tools_arch    tipos, ambientes/áreas, organização, modelagem e produção
    annotations   classificação leitura/escrita/destrutivo das ferramentas

Importar este pacote registra todas as ferramentas em ``core.mcp``.
"""

from . import core, tools_arch, tools_editing, tools_model, tools_pro  # noqa: F401
from .annotations import annotate_tools, classify_tool  # noqa: F401
from .core import mcp  # noqa: F401

annotate_tools()
