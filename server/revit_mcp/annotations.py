"""Anotações MCP (leitura, escrita, destrutivo) aplicadas a todas as ferramentas."""

# =============================================================================
# Anotações MCP — leitura x escrita x destrutivo
# =============================================================================
#
# Clientes MCP usam estas dicas para pedir confirmação só quando a ferramenta
# altera o modelo (readOnlyHint=False) e para destacar operações que
# sobrescrevem ou removem dados (destructiveHint=True).

from mcp.types import ToolAnnotations

from .core import mcp

READ_ONLY_PREFIXES = ("list_", "get_", "find_", "analyze_", "validate_", "preview_", "audit_")
READ_ONLY_TOOLS = {"ping", "bridge_health", "plan_batch"}
# Gravam arquivos fora do modelo, sem alterá-lo.
FILE_OUTPUT_PREFIXES = ("export_",)
FILE_OUTPUT_TOOLS = {"screenshot", "backup_document"}
# Sobrescrevem/removem dados existentes ou trocam o documento ativo.
DESTRUCTIVE_PREFIXES = ("delete_", "purge_", "set_", "modify_", "move_", "rotate_", "mirror_",
                        "align_", "resize_", "change_", "reset_", "override_", "reload_", "flip_",
                        "join_", "attach_", "renumber_")
DESTRUCTIVE_TOOLS = {"save_document", "save_as", "open_document", "new_project", "execute_batch",
                     "apply_view_filter", "pin_elements", "create_material"}
# Navegação na interface: não altera o modelo.
UI_ONLY_TOOLS = {"set_view_plan", "set_view_3d", "set_view_shaded", "set_camera", "zoom_fit",
                 "focus_element", "set_selection"}


def classify_tool(name: str) -> ToolAnnotations:
    """Classificação usada nas anotações (exposta para os testes)."""
    if name in READ_ONLY_TOOLS or name.startswith(READ_ONLY_PREFIXES) or name in UI_ONLY_TOOLS:
        return ToolAnnotations(readOnlyHint=True, destructiveHint=False, idempotentHint=True, openWorldHint=False)
    if name in FILE_OUTPUT_TOOLS or name.startswith(FILE_OUTPUT_PREFIXES):
        return ToolAnnotations(readOnlyHint=False, destructiveHint=False, idempotentHint=True, openWorldHint=False)
    destructive = name in DESTRUCTIVE_TOOLS or name.startswith(DESTRUCTIVE_PREFIXES)
    return ToolAnnotations(readOnlyHint=False, destructiveHint=destructive,
                           idempotentHint=name.startswith(("set_", "reset_")), openWorldHint=False)


def annotate_tools() -> None:
    for tool in mcp._tool_manager.list_tools():
        tool.annotations = classify_tool(tool.name)
