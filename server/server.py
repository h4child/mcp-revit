# =============================================================================
# server.py — Servidor MCP para Revit 2025
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

import logging
import os
import json
from pathlib import Path
from typing import Optional
from urllib.parse import urlparse

import httpx
from mcp.server.fastmcp import FastMCP

try:
    from dotenv import load_dotenv
    load_dotenv(Path(__file__).with_name(".env"), override=False)
except ImportError:
    # O servidor continua funcional com variáveis do sistema; requirements.txt
    # instala python-dotenv no ambiente recomendado.
    pass

# --- Configuração de log ---
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("revit-mcp")

# --- Conexão com o bridge HTTP dentro do Revit ---
BRIDGE_URL = os.environ.get("REVIT_MCP_URL", "http://127.0.0.1:14733/rpc")
TIMEOUT = float(os.environ.get("REVIT_MCP_TIMEOUT", "120"))
CONFIG_PATH = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / ".local"))) / "RevitMcp" / "bridge.json"
WORKSPACE_CONFIG_PATH = Path(__file__).with_name(".revit_mcp_bridge.json")


def _bridge_token() -> str:
    """Lê o token atual dinamicamente, permitindo reload seguro sem reiniciar o MCP."""
    token = os.environ.get("REVIT_MCP_TOKEN")
    if token:
        return token
    for config_path in (WORKSPACE_CONFIG_PATH, CONFIG_PATH):
        try:
            value = str(json.loads(config_path.read_text(encoding="utf-8-sig")).get("token") or "")
            if value:
                return value
        except (OSError, ValueError, TypeError):
            continue
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
        async with httpx.AsyncClient(timeout=TIMEOUT) as client:
            r = await client.post(BRIDGE_URL, json=payload, headers=_headers())
    except httpx.RequestError as e:
        raise RuntimeError(
            f"Não foi possível conectar ao Revit em {BRIDGE_URL}. "
            "Verifique se o Revit 2025 está aberto com o add-in RevitMcp carregado. "
            f"Detalhe: {e}"
        )

    try:
        data = r.json()
    except Exception:
        raise RuntimeError(
            f"Resposta inválida do Revit (HTTP {r.status_code}): {r.text[:300]}"
        )

    if not data.get("ok"):
        request_id = data.get("request_id")
        suffix = f" (requisição {request_id})" if request_id else ""
        raise RuntimeError((data.get("error") or f"Erro HTTP {r.status_code}") + suffix)

    return data.get("result")


@mcp.tool()
async def bridge_health() -> dict:
    """Verifica saúde e autenticação do bridge local sem modificar o projeto."""
    _validate_bridge_url()
    health_url = BRIDGE_URL.rsplit("/", 1)[0] + "/health"
    try:
        async with httpx.AsyncClient(timeout=min(TIMEOUT, 15)) as client:
            response = await client.get(health_url, headers=_headers())
            data = response.json()
    except (httpx.RequestError, ValueError) as exc:
        raise RuntimeError(f"Bridge do Revit indisponível: {exc}")
    if not data.get("ok"):
        raise RuntimeError(data.get("error") or f"Health falhou (HTTP {response.status_code})")
    return data


# =============================================================================
# FERRAMENTAS MCP — Informações e diagnóstico
# =============================================================================

@mcp.tool()
async def ping() -> dict:
    """Verifica se o Revit está respondendo. Use para testar a conexão."""
    return await _rpc("ping")


@mcp.tool()
async def get_info() -> dict:
    """
    Retorna informações do Revit: versão, usuário, documento aberto,
    vista ativa. Use para verificar o estado atual antes de começar.
    """
    return await _rpc("get_info")


@mcp.tool()
async def list_model_warnings() -> dict:
    """Lista avisos persistentes do Revit e os IDs dos elementos envolvidos.

    Execute antes e depois de operações em lote. Se houver avisos de paredes
    sobrepostas, corrija/remova os elementos indicados antes de continuar.
    """
    return await _rpc("list_model_warnings")


@mcp.tool()
async def list_categories() -> list:
    """
    Lista as categorias de elementos no projeto com contagem.
    Exemplo: Paredes: 12, Portas: 3, Mobiliário: 5.
    Útil para entender o que já existe no projeto.
    """
    return await _rpc("list_categories")


@mcp.tool()
async def list_levels() -> list:
    """
    Lista os níveis (andares) do projeto com elevação em metros.
    Níveis definem onde paredes e pisos são criados.
    """
    return await _rpc("list_levels")


@mcp.tool()
async def list_walls() -> list:
    """
    Lista todas as paredes do projeto com ID e comprimento em metros.
    O ID é necessário para inserir portas e janelas.
    """
    return await _rpc("list_walls")


@mcp.tool()
async def list_windows() -> list:
    """Lista janelas colocadas, com ID, tipo, dimensões e peitoril.
    Use o ID retornado em resize_window para modificar somente uma janela."""
    return await _rpc("list_windows")


@mcp.tool()
async def list_families(category: Optional[str] = None) -> list:
    """
    Lista famílias (tipos de elementos) carregadas no projeto.
    Filtre por categoria: 'Doors' (portas), 'Windows' (janelas),
    'Furniture' (mobiliário).
    """
    args = {}
    if category:
        args["category"] = category
    return await _rpc("list_families", **args)


@mcp.tool()
async def list_elements(category: str, limit: int = 100) -> dict:
    """
    Lista elementos de qualquer categoria do documento (ex.: 'Walls',
    'Doors', 'Windows', 'Furniture'). Aceita o nome da BuiltInCategory
    sem o prefixo 'OST_', ou os aliases usados em place_component
    ('furniture', 'plumbing', 'casework', 'planting', 'parking', etc).
    Use quando não houver um lister específico (list_walls, list_windows)
    para a categoria desejada.
    """
    return await _rpc("list_elements", category=category, limit=limit)


@mcp.tool()
async def get_element(element_id: int) -> dict:
    """
    Retorna categoria, família, tipo, nível e todos os parâmetros
    (com valores e se são somente-leitura) de um elemento qualquer pelo ID.
    Use para inspecionar um elemento antes de modify_parameters ou
    para investigar um ID retornado por list_elements/list_walls/etc.
    """
    return await _rpc("get_element", element_id=element_id)


@mcp.tool()
async def list_views() -> list:
    """
    Lista todas as vistas do projeto (id, nome, tipo, template aplicado).
    Útil para localizar o ID de uma vista para place_view_on_sheet,
    set_view_template ou screenshot.
    """
    return await _rpc("list_views")


@mcp.tool()
async def get_active_view() -> dict:
    """Retorna id, nome, tipo e template da vista atualmente ativa na UI do Revit."""
    return await _rpc("get_active_view")


@mcp.tool()
async def get_selection() -> dict:
    """
    Retorna os elementos atualmente selecionados pelo usuário na UI do Revit
    (id, nome, categoria). Use para agir sobre o que o usuário selecionou
    manualmente antes de chamar esta ferramenta.
    """
    return await _rpc("get_selection")


@mcp.tool()
async def list_materials(filtro: Optional[str] = None) -> list:
    """
    Lista os materiais disponíveis no projeto, com ID, nome e cor RGB.
    Passe 'filtro' para buscar por parte do nome.
    Exemplos: list_materials(filtro="Madeira"), list_materials(filtro="Concreto")
    Use antes de set_material para descobrir o nome exato do acabamento.
    """
    args = {}
    if filtro:
        args["filtro"] = filtro
    return await _rpc("list_materials", **args)


@mcp.tool()
async def set_material(id: int, material: str) -> dict:
    """
    Aplica um material (acabamento) a um elemento existente, pintando suas faces.
    Funciona em paredes, pisos, telhados, forros e móveis.

    id       — ID do elemento (obtido em list_walls, create_floor, etc)
    material — nome do material (busca parcial), ex: "Madeira", "Cerâmica",
               "Concreto", "Vidro", "Tijolo"

    Muda a aparência em vistas sombreadas/realistas sem afetar outros elementos.
    Exemplo: set_material(id=298787, material="Madeira")
    """
    return await _rpc("set_material", id=id, material=material)


@mcp.tool()
async def create_material(name: str, color: str = "#B0B0B0", transparency: int = 0,
                          texture_path: Optional[str] = None) -> dict:
    """Cria ou atualiza material com cor, transparência e textura PNG/JPG/BMP opcional."""
    args = {"name": name, "color": color, "transparency": transparency}
    if texture_path:
        args["texture_path"] = texture_path
    return await _rpc("create_material", **args)


@mcp.tool()
async def create_rectangular_prism(x_m: float, y_m: float, width_m: float, depth_m: float,
                                   height_m: float = 0.1, material: Optional[str] = None,
                                   category: str = "GenericModel", name: str = "Bloco MCP",
                                   level: Optional[str] = None) -> dict:
    """Cria bloco retangular 3D em GenericModel, Furniture, Casework, Planting ou Parking."""
    args = {"x_m": x_m, "y_m": y_m, "width_m": width_m, "depth_m": depth_m,
            "height_m": height_m, "category": category, "name": name}
    if material:
        args["material"] = material
    if level:
        args["level"] = level
    return await _rpc("create_rectangular_prism", **args)


# =============================================================================
# FERRAMENTAS MCP — Projeto (criar, abrir, salvar)
# =============================================================================

@mcp.tool()
async def new_project(template_path: Optional[str] = None) -> dict:
    """
    Cria um novo projeto Revit (.rvt).
    Se template_path não for informado, usa o template padrão do Revit.
    Se nenhum template existir, cria projeto métrico básico.
    """
    args = {}
    if template_path:
        args["template_path"] = template_path
    return await _rpc("new_project", **args)


@mcp.tool()
async def open_document(path: str) -> dict:
    """Abre um arquivo .rvt existente no Revit."""
    return await _rpc("open_document", path=path)


@mcp.tool()
async def save_document() -> dict:
    """Salva o documento ativo no mesmo arquivo .rvt, sem abrir diálogos do Revit."""
    return await _rpc("save_document")


@mcp.tool()
async def save_as(path: str) -> dict:
    """
    Salva o projeto ativo no caminho especificado.
    Exemplo: save_as(path="C:/Projetos/minha_casa.rvt")
    Sobrescreve se o arquivo já existir.
    """
    return await _rpc("save_as", path=path)


@mcp.tool()
async def delete_element(id: int, confirm: bool = False) -> dict:
    """
    Exclui um elemento pelo ID após confirmação explícita.
    Primeiro use preview_delete; então chame com confirm=True.
    """
    return await _rpc("delete_element", id=id, confirm=confirm)


@mcp.tool()
async def preview_delete(id: int) -> dict:
    """Lista o elemento e dependentes que seriam removidos; não altera o modelo."""
    return await _rpc("preview_delete", id=id)


@mcp.tool()
async def backup_document(folder: Optional[str] = None) -> dict:
    """Salva e cria cópia de restauração do RVT local sem trocar o documento ativo.

    Bloqueado para modelos workshared, que exigem o fluxo central/local da equipe.
    """
    args = {}
    if folder:
        args["folder"] = folder
    return await _rpc("backup_document", **args)


@mcp.tool()
async def plan_batch(operations: list) -> dict:
    """Valida um lote e informa operações permitidas antes de alterar o modelo."""
    return await _rpc("plan_batch", operations=operations)


@mcp.tool()
async def execute_batch(operations: list, commit: bool = False) -> dict:
    """Executa lote atômico. Por padrão faz dry-run com rollback; commit=True confirma tudo."""
    return await _rpc("execute_batch", operations=operations, commit=commit)


# =============================================================================
# FERRAMENTAS MCP — Criação de elementos
# =============================================================================

@mcp.tool()
async def create_level(elevation_m: float = 3.0, name: Optional[str] = None) -> dict:
    """
    Cria um novo nível (andar) na elevação especificada em metros.
    Nome é opcional — o Revit gera automaticamente se omitido.
    Exemplo: create_level(elevation_m=3.0, name="Pavimento Superior")
    """
    args = {"elevation_m": elevation_m}
    if name:
        args["name"] = name
    return await _rpc("create_level", **args)


@mcp.tool()
async def list_wall_types() -> list:
    """
    Lista os tipos de parede básica disponíveis (nome e espessura em metros).
    Use o nome no parâmetro wall_type de create_wall/create_walls.
    Exemplos comuns: "Genérico - 200 mm", "Parede divisória", "Alvenaria".
    """
    return await _rpc("list_wall_types")


@mcp.tool()
async def list_floor_types() -> list:
    """
    Lista os tipos de piso disponíveis (nome e espessura em metros).
    Use o nome no parâmetro floor_type de create_floor.
    """
    return await _rpc("list_floor_types")


@mcp.tool()
async def create_wall(
    x1_m: float,
    y1_m: float,
    x2_m: float,
    y2_m: float,
    height_m: float = 3.0,
    level: Optional[str] = None,
    wall_type: Optional[str] = None,
) -> dict:
    """
    Cria uma parede reta entre dois pontos, com coordenadas em metros.
    (x1_m, y1_m) = ponto inicial, (x2_m, y2_m) = ponto final.
    height_m = altura da parede (padrão: 3m).
    level = nome do nível (padrão: térreo).
    wall_type = tipo de parede (opcional; busca parcial, ver list_wall_types).
                Ex: "200 mm", "divisória", "alvenaria".
    """
    args = {
        "x1_m": x1_m, "y1_m": y1_m,
        "x2_m": x2_m, "y2_m": y2_m,
        "height_m": height_m,
    }
    if level:
        args["level"] = level
    if wall_type:
        args["wall_type"] = wall_type
    return await _rpc("create_wall", **args)


@mcp.tool()
async def create_walls(
    walls: list,
    level: Optional[str] = None,
    wall_type: Optional[str] = None,
) -> dict:
    """
    Cria várias paredes de uma vez (mais rápido que criar uma por uma).
    walls = lista de objetos com: x1_m, y1_m, x2_m, y2_m, height_m (opcional),
            wall_type (opcional, sobrepõe o tipo global naquela parede).
    wall_type = tipo aplicado a todas as paredes (opcional, ver list_wall_types).
    Exemplo:
      create_walls(walls=[
        {"x1_m": 0, "y1_m": 0, "x2_m": 5, "y2_m": 0},
        {"x1_m": 5, "y1_m": 0, "x2_m": 5, "y2_m": 4},
      ], wall_type="200 mm")
    """
    args = {"walls": walls}
    if level:
        args["level"] = level
    if wall_type:
        args["wall_type"] = wall_type
    return await _rpc("create_walls", **args)


@mcp.tool()
async def create_plan_layout(
    walls: list,
    doors: Optional[list] = None,
    windows: Optional[list] = None,
    level: Optional[str] = None,
    wall_type: Optional[str] = None,
) -> dict:
    """Cria uma planta de forma atômica: paredes, portas e janelas em uma
    única transação. Cada parede usa `key`; portas/janelas usam `wall_key` e
    `position` entre 0 e 1. Se algo falhar, o Revit não deixa geometria parcial.
    """
    args = {"walls": walls}
    if doors:
        args["doors"] = doors
    if windows:
        args["windows"] = windows
    if level:
        args["level"] = level
    if wall_type:
        args["wall_type"] = wall_type
    return await _rpc("create_plan_layout", **args)


@mcp.tool()
async def create_box(
    width_m: float = 5.0,
    depth_m: float = 4.0,
    height_m: float = 3.0,
    cx_m: float = 0.0,
    cy_m: float = 0.0,
    level: Optional[str] = None,
) -> dict:
    """
    Atalho: cria 4 paredes formando um cômodo retangular.
    Centrado em (cx_m, cy_m) com largura width_m e profundidade depth_m.
    Bom para criar rapidamente um cômodo ou testar a conexão.
    """
    args = {
        "width_m": width_m, "depth_m": depth_m, "height_m": height_m,
        "cx_m": cx_m, "cy_m": cy_m,
    }
    if level:
        args["level"] = level
    return await _rpc("create_box", **args)


@mcp.tool()
async def create_door(
    wall_id: int,
    x_m: float,
    y_m: float,
    family: Optional[str] = None,
) -> dict:
    """
    Insere uma porta em uma parede existente.
    wall_id = ID da parede (obtido via list_walls).
    (x_m, y_m) = posição aproximada — o Revit projeta na parede.
    family = parte do nome da família (opcional, ex: "80cm").
    """
    args = {"wall_id": wall_id, "x_m": x_m, "y_m": y_m}
    if family:
        args["family"] = family
    return await _rpc("create_door", **args)


@mcp.tool()
async def create_window(
    wall_id: int,
    x_m: float,
    y_m: float,
    sill_m: Optional[float] = None,
    width_m: Optional[float] = None,
    height_m: Optional[float] = None,
    kind: Optional[str] = None,
    family: Optional[str] = None,
    type: Optional[str] = None,
) -> dict:
    """
    Insere uma janela em uma parede existente.
    wall_id = ID da parede (obtido via list_walls).
    (x_m, y_m) = posição aproximada — o Revit projeta na parede.
    kind = perfil do ambiente: generica, quarto, banheiro, sala, cozinha,
        lavanderia ou closet. "generica" cria uma janela neutra e permite
        definir livremente as medidas. Os demais perfis definem medidas e
        peitoris adequados quando não forem informados.
    sill_m = altura do peitoril em metros. Se omitido, o perfil usa o padrão.
    width_m e height_m = largura e altura em metros. O MCP cria/reutiliza
        um tipo dedicado, sem modificar as janelas já existentes.
    family = parte do nome da família (opcional).
    type = parte do nome do tipo de janela (opcional).
    """
    args = {"wall_id": wall_id, "x_m": x_m, "y_m": y_m}
    if sill_m is not None:
        args["sill_m"] = sill_m
    if width_m is not None:
        args["width_m"] = width_m
    if height_m is not None:
        args["height_m"] = height_m
    if kind:
        args["kind"] = kind
    if family:
        args["family"] = family
    if type:
        args["type"] = type
    return await _rpc("create_window", **args)


@mcp.tool()
async def resize_window(
    window_id: int,
    width_m: Optional[float] = None,
    height_m: Optional[float] = None,
    sill_m: Optional[float] = None,
    kind: str = "generica",
    family: Optional[str] = None,
    type: Optional[str] = None,
) -> dict:
    """Redimensiona uma janela já colocada, sem modificar as demais.
    window_id vem de list_windows. kind aceita generica, quarto, banheiro,
    sala, cozinha, lavanderia ou closet; medidas explícitas têm prioridade.
    type troca para um tipo real já carregado, garantindo a geometria visível."""
    args = {"window_id": window_id, "kind": kind}
    if width_m is not None:
        args["width_m"] = width_m
    if height_m is not None:
        args["height_m"] = height_m
    if sill_m is not None:
        args["sill_m"] = sill_m
    if family:
        args["family"] = family
    if type:
        args["type"] = type
    return await _rpc("resize_window", **args)


@mcp.tool()
async def create_floor_plan(level: Optional[str] = None) -> dict:
    """Cria a vista de planta de piso de um nível, quando o template não a
    criou automaticamente. Se a vista já existir, apenas a retorna."""
    args = {}
    if level:
        args["level"] = level
    return await _rpc("create_floor_plan", **args)


@mcp.tool()
async def set_plan_crop(x_min_m: float, y_min_m: float, x_max_m: float, y_max_m: float) -> dict:
    """Recorta a vista de planta ativa para o retângulo informado em metros.
    Use antes de screenshot para gerar imagens técnicas sem áreas vazias."""
    return await _rpc("set_plan_crop", x_min_m=x_min_m, y_min_m=y_min_m,
                      x_max_m=x_max_m, y_max_m=y_max_m)


@mcp.tool()
async def delete_text_notes(contains: list) -> dict:
    """Remove anotações de texto que contenham os trechos indicados, somente
    na vista ativa. Útil para corrigir pranchas sem afetar outras vistas."""
    return await _rpc("delete_text_notes", contains=contains)


@mcp.tool()
async def place_rooms() -> dict:
    """
    Cria cômodos (Rooms) nas regiões fechadas por paredes.
    Os cômodos recebem nomes automáticos: Sala, Cozinha, Banheiro.
    Os cômodos permitem calcular áreas e identificar espaços.
    """
    return await _rpc("place_rooms")


@mcp.tool()
async def place_rooms_at(rooms: list, level: Optional[str] = None) -> dict:
    """Cria ambientes Revit em posições exatas de uma planta.

    rooms contém objetos {name, x_m, y_m, number?}. Os pontos devem estar em
    regiões fechadas por paredes. Diferente de place_rooms, não usa pontos
    fixos: serve para qualquer programa arquitetônico.
    """
    args = {"rooms": rooms}
    if level:
        args["level"] = level
    return await _rpc("place_rooms_at", **args)


@mcp.tool()
async def place_furniture(items: list, level: Optional[str] = None) -> dict:
    """
    Coloca mobiliário em planta como blocos 2D modernos.
    items = lista de objetos com:
      - name: nome do móvel (ex: "Sofá", "Mesa", "Cama")
      - x, y: posição em metros
      - w, d, h: largura, profundidade e altura em metros
      - rotation: rotação opcional em graus

    Exemplo:
      place_furniture(items=[
        {"name": "Sofá", "x": 1, "y": 3, "w": 2.0, "d": 0.9, "h": 0.8},
        {"name": "Mesa de Jantar", "x": 6, "y": 1, "w": 1.2, "d": 0.8, "h": 0.75},
      ])
    """
    args = {"items": items}
    if level:
        args["level"] = level
    return await _rpc("place_furniture", **args)


@mcp.tool()
async def place_blocks(items: list, level: Optional[str] = None) -> dict:
    """
    Alias explícito para desenhar blocos 2D modernos de mobiliário na planta.
    Use quando quiser evitar qualquer dependência de família externa.
    """
    args = {"items": items}
    if level:
        args["level"] = level
    return await _rpc("place_blocks", **args)


@mcp.tool()
async def load_family(path: str) -> dict:
    """
    Carrega uma família (.rfa) do disco para o projeto.
    Necessário porque o Revit 2025 não instala mobiliário localmente:
    baixe famílias (ex: do site da Autodesk) e carregue-as com este comando.

    path — caminho completo do arquivo .rfa

    Retorna a família e seus tipos, para usar em place_family_instance.
    Exemplo: load_family(path="C:/Familias/Sofa_3_lugares.rfa")
    """
    return await _rpc("load_family", path=path)


@mcp.tool()
async def create_furniture_family(
    name: str,
    width_m: float,
    depth_m: float,
    height_m: float,
    path: Optional[str] = None,
) -> dict:
    """
    Gera uma família Revit real (.rfa) de mobiliário, carrega-a no projeto
    aberto e retorna o caminho e o tipo criado.

    Usa o template nativo ``Mobiliário métrico.rft`` do Revit 2025. Serve como
    base editável para sofá, cama, mesa, cadeira e bancada quando uma biblioteca
    comercial ainda não está instalada. Medidas em metros.
    """
    args = {"name": name, "width_m": width_m, "depth_m": depth_m, "height_m": height_m}
    if path:
        args["path"] = path
    return await _rpc("create_furniture_family", **args)


@mcp.tool()
async def place_family_instance(
    family: str,
    x_m: float,
    y_m: float,
    type: Optional[str] = None,
    z_m: float = 0.0,
    rotation: float = 0.0,
    level: Optional[str] = None,
) -> dict:
    """
    Posiciona uma instância de uma família JÁ carregada, com rotação.
    Ao contrário de place_furniture (caixas), usa famílias reais
    (sofás, mesas, colunas). Carregue antes com load_family, se preciso.

    family   — nome (parcial) da família (ex: "Sofá", "Coluna")
    x_m, y_m — posição em metros
    type     — nome (parcial) do tipo/tamanho (opcional)
    z_m      — altura acima do nível (opcional)
    rotation — rotação em graus no eixo Z (opcional)
    level    — nome do nível (opcional; usa o térreo)

    Exemplo: place_family_instance(family="Coluna", x_m=2, y_m=2, rotation=45)
    """
    args = {"family": family, "x_m": x_m, "y_m": y_m, "z_m": z_m, "rotation": rotation}
    if type:
        args["type"] = type
    if level:
        args["level"] = level
    return await _rpc("place_family_instance", **args)


@mcp.tool()
async def place_component(category: str, x_m: float, y_m: float, family_name: Optional[str] = None,
                          type_name: Optional[str] = None, rotation: float = 0.0,
                          level: Optional[str] = None) -> dict:
    """
    Insere componente real por categoria: Furniture, PlumbingFixtures, Casework,
    Planting, Parking, SpecialityEquipment ou GenericModel.
    Use list_families(category=...) para descobrir as famílias já carregadas.
    """
    args = {"category": category, "x_m": x_m, "y_m": y_m, "rotation": rotation}
    if family_name:
        args["family_name"] = family_name
    if type_name:
        args["type_name"] = type_name
    if level:
        args["level"] = level
    return await _rpc("place_component", **args)


# =============================================================================
# FERRAMENTAS MCP — Piso, telhado e forro
# =============================================================================

@mcp.tool()
async def create_floor(
    x_min_m: float = 0,
    y_min_m: float = 0,
    x_max_m: float = 8,
    y_max_m: float = 5,
    points: Optional[list] = None,
    level: Optional[str] = None,
    floor_type: Optional[str] = None,
    material: Optional[str] = None,
) -> dict:
    """
    Cria um piso (laje) no nível especificado.
    Modo simples: retângulo definido por x_min_m, y_min_m, x_max_m, y_max_m.
    Modo avançado: polígono livre com points=[{"x":0,"y":0}, {"x":5,"y":0}, ...].

    floor_type — nome (parcial) do tipo de piso (opcional).
    material   — acabamento aplicado à face do piso (opcional; ver list_materials).
                 Permite pisos diferentes por cômodo (ex: madeira na sala,
                 cerâmica na cozinha) chamando create_floor uma vez por ambiente.

    Exemplo: create_floor(x_min_m=0, y_min_m=0, x_max_m=5, y_max_m=5, material="Madeira")
    """
    args = {"x_min_m": x_min_m, "y_min_m": y_min_m, "x_max_m": x_max_m, "y_max_m": y_max_m}
    if points:
        args["points"] = points
    if level:
        args["level"] = level
    if floor_type:
        args["floor_type"] = floor_type
    if material:
        args["material"] = material
    return await _rpc("create_floor", **args)


@mcp.tool()
async def create_roof(
    x_min_m: float = 0,
    y_min_m: float = 0,
    x_max_m: float = 8,
    y_max_m: float = 5,
    slope: float = 30,
    overhang_m: float = 0.5,
    base_height_m: float = 3.0,
    level: Optional[str] = None,
) -> dict:
    """
    Cria um telhado de duas águas sobre o contorno da casa.
    x_min_m..x_max_m, y_min_m..y_max_m = contorno em planta (metros).
    slope = inclinação em graus (padrão: 30°).
    overhang_m = tamanho do beiral (padrão: 0.5m).
    base_height_m = altura da base do telhado (topo das paredes, padrão: 3m).
    """
    args = {
        "x_min_m": x_min_m, "y_min_m": y_min_m,
        "x_max_m": x_max_m, "y_max_m": y_max_m,
        "slope": slope, "overhang_m": overhang_m,
        "base_height_m": base_height_m,
    }
    if level:
        args["level"] = level
    return await _rpc("create_roof", **args)


@mcp.tool()
async def create_ceiling(
    x_min_m: float = 0,
    y_min_m: float = 0,
    x_max_m: float = 8,
    y_max_m: float = 5,
    height_offset_m: float = 2.7,
    level: Optional[str] = None,
) -> dict:
    """
    Cria um forro (teto interno) no nível especificado.
    x_min_m..x_max_m, y_min_m..y_max_m = contorno em planta (metros).
    height_offset_m = altura do forro acima do nível (padrão: 2.7m).
    """
    args = {
        "x_min_m": x_min_m, "y_min_m": y_min_m,
        "x_max_m": x_max_m, "y_max_m": y_max_m,
        "height_offset_m": height_offset_m,
    }
    if level:
        args["level"] = level
    return await _rpc("create_ceiling", **args)


@mcp.tool()
async def create_stairs(
    x_m: float = 0,
    y_m: float = 0,
    width_m: float = 1.0,
    direction: float = 0,
    base_level: Optional[str] = None,
    top_level: Optional[str] = None,
) -> dict:
    """
    Cria uma escada de lance reto entre dois níveis.

    x_m, y_m    — ponto inicial (base da escada) em metros
    width_m     — largura da escada (padrão: 1.0m)
    direction   — direção da subida no plano, em graus (0 = eixo +X, 90 = +Y)
    base_level  — nível inferior (opcional; usa o térreo)
    top_level   — nível superior (opcional; usa o próximo nível acima)

    O número de degraus é calculado automaticamente pela altura entre níveis.
    Precisa existir um nível acima do base — crie com create_level se necessário.
    Exemplo: create_stairs(x_m=1, y_m=1, width_m=1.2, direction=90)
    """
    args = {
        "x_m": x_m, "y_m": y_m,
        "width_m": width_m, "direction": direction,
    }
    if base_level:
        args["base_level"] = base_level
    if top_level:
        args["top_level"] = top_level
    return await _rpc("create_stairs", **args)


@mcp.tool()
async def create_column(
    x_m: Optional[float] = None,
    y_m: Optional[float] = None,
    points: Optional[list] = None,
    base_level: Optional[str] = None,
    top_level: Optional[str] = None,
    height_m: float = 3.0,
    family: Optional[str] = None,
    type: Optional[str] = None,
    structural: bool = True,
) -> dict:
    """
    Cria colunas (pilares) verticais — uma ou várias de uma vez.

    x_m, y_m   — posição de uma coluna, OU
    points     — lista de posições para uma grade: [[0,0], [4,0], [8,0]]
    base_level — nível da base (opcional; usa o térreo)
    top_level  — nível do topo (opcional; se omitido usa height_m)
    height_m   — altura quando não há top_level (padrão: 3m)
    family     — família da coluna (opcional; ver list_families)
    type       — tipo/tamanho (opcional, ex: "300 x 300")
    structural — True = pilar estrutural, False = coluna arquitetônica

    Exemplo (grade de 3 pilares):
      create_column(points=[[0,0], [4,0], [8,0]], top_level="Telhado")
    """
    args = {"height_m": height_m, "structural": structural}
    if points:
        args["points"] = points
    elif x_m is not None and y_m is not None:
        args["x_m"] = x_m
        args["y_m"] = y_m
    else:
        raise ValueError("Informe x_m e y_m, ou uma lista points.")
    if base_level:
        args["base_level"] = base_level
    if top_level:
        args["top_level"] = top_level
    if family:
        args["family"] = family
    if type:
        args["type"] = type
    return await _rpc("create_column", **args)


@mcp.tool()
async def create_dimension(
    wall_id: Optional[int] = None,
    wall_ids: Optional[list] = None,
    offset_m: float = 1.0,
    view: Optional[str] = None,
) -> dict:
    """
    Cria cotas do comprimento de paredes em uma vista de planta.
    A cota fica paralela à parede, deslocada perpendicularmente.

    wall_id  — ID de uma parede, OU
    wall_ids — lista de IDs (cria uma cota por parede)
    offset_m — distância da cota até a parede (padrão 1m; negativo = outro lado)
    view     — nome da vista (opcional; usa a planta do nível da parede)

    Obtenha os IDs com list_walls.
    Exemplo: create_dimension(wall_ids=[298705, 298706], offset_m=1.5)
    """
    args = {"offset_m": offset_m}
    if wall_ids:
        args["wall_ids"] = wall_ids
    elif wall_id is not None:
        args["wall_id"] = wall_id
    else:
        raise ValueError("Informe wall_id ou wall_ids.")
    if view:
        args["view"] = view
    return await _rpc("create_dimension", **args)


@mcp.tool()
async def tag_rooms(view: Optional[str] = None) -> dict:
    """
    Insere identificadores (tags) em todos os cômodos, mostrando nome e área.
    Requer cômodos criados antes com place_rooms.

    view — nome da planta (opcional; usa a vista ativa ou a primeira planta)
    """
    args = {}
    if view:
        args["view"] = view
    return await _rpc("tag_rooms", **args)


@mcp.tool()
async def create_storey(
    elevation_m: float,
    name: Optional[str] = None,
    x_min_m: float = 0,
    y_min_m: float = 0,
    x_max_m: float = 8,
    y_max_m: float = 5,
    wall_height_m: float = 3.0,
    wall_type: Optional[str] = None,
    with_floor: bool = True,
    floor_type: Optional[str] = None,
    floor_material: Optional[str] = None,
) -> dict:
    """
    Cria um pavimento COMPLETO de uma vez: nível + laje de piso + 4 paredes
    perimetrais. Atalho para edifícios de vários andares.

    elevation_m    — elevação do novo pavimento (ex: 3.0 para o 2º andar)
    name           — nome do nível (ex: "Pavimento Superior")
    x_min_m..y_max_m — contorno em planta (metros)
    wall_height_m  — altura das paredes (padrão: 3m)
    wall_type      — tipo de parede (opcional; ver list_wall_types)
    with_floor     — criar a laje de piso (padrão: True)
    floor_type     — tipo de piso (opcional; ver list_floor_types)
    floor_material — acabamento do piso (opcional; ver list_materials)

    Para paredes internas do novo pavimento, chame create_walls depois
    passando level com o nome deste pavimento.

    Exemplo: create_storey(elevation_m=3.0, name="2º Andar", x_max_m=8, y_max_m=5,
                           floor_material="Madeira")
    """
    args = {
        "elevation_m": elevation_m,
        "x_min_m": x_min_m, "y_min_m": y_min_m,
        "x_max_m": x_max_m, "y_max_m": y_max_m,
        "wall_height_m": wall_height_m,
        "with_floor": with_floor,
    }
    if name:
        args["name"] = name
    if wall_type:
        args["wall_type"] = wall_type
    if floor_type:
        args["floor_type"] = floor_type
    if floor_material:
        args["floor_material"] = floor_material
    return await _rpc("create_storey", **args)


@mcp.tool()
async def create_railing(
    points: list,
    level: Optional[str] = None,
    height_offset_m: float = 0.0,
    railing_type: Optional[str] = None,
) -> dict:
    """
    Cria um guarda-corpo independente ao longo de um caminho.
    Para mezaninos, varandas e bordas de laje (a escada já cria o próprio corrimão).

    points          — lista de pontos do caminho: [x, y] ou {"x_m":.., "y_m":..}.
                      Mínimo 2 pontos.
    level           — nível de base (opcional; usa o térreo)
    height_offset_m — deslocamento vertical acima do nível (opcional)
    railing_type    — nome (parcial) do tipo de guarda-corpo (opcional)

    Exemplo (varanda em L):
      create_railing(points=[[0, 0], [4, 0], [4, 3]])
    """
    args = {"points": points, "height_offset_m": height_offset_m}
    if level:
        args["level"] = level
    if railing_type:
        args["railing_type"] = railing_type
    return await _rpc("create_railing", **args)


# =============================================================================
# FERRAMENTAS MCP — Visualização
# =============================================================================

@mcp.tool()
async def set_section_box(
    x_min_m: float = -1,
    y_min_m: float = -1,
    z_min_m: float = -0.5,
    x_max_m: float = 9,
    y_max_m: float = 6,
    z_max_m: float = 4,
    enabled: bool = True,
) -> dict:
    """
    Define um Section Box na vista 3D para criar corte isométrico.
    O Section Box recorta a geometria, mostrando o interior da casa
    como em um corte arquitetônico 3D profissional.
    Todos os valores em metros.
    Use enabled=False para desativar o corte.
    """
    args = {
        "x_min_m": x_min_m, "y_min_m": y_min_m, "z_min_m": z_min_m,
        "x_max_m": x_max_m, "y_max_m": y_max_m, "z_max_m": z_max_m,
        "enabled": enabled,
    }
    return await _rpc("set_section_box", **args)


@mcp.tool()
async def set_camera(
    azimuth: float = 225,
    elevation: float = 30,
    distance_m: float = 20,
    target_x_m: Optional[float] = None,
    target_y_m: Optional[float] = None,
    target_z_m: Optional[float] = None,
) -> dict:
    """
    Orienta a câmera 3D com controle preciso — apresentações estilo
    "Ground Floor" (isométrico rebaixado, olhando um canto específico).

    azimuth    — giro horizontal em graus. 0=Leste, 90=Norte, 180=Oeste,
                 225=Sudoeste (padrão isométrico), 270=Sul
    elevation  — inclinação vertical em graus. 0=horizonte, 30=isométrico,
                 45=aéreo, 90=topo direto
    distance_m — distância da câmera ao alvo (padrão 20m)
    target_*   — ponto que a câmera olha (opcional; usa o centro do modelo)

    Combine com set_section_box para o efeito "casa cortada" da referência.
    Exemplo: set_camera(azimuth=225, elevation=35, distance_m=15)
    """
    args = {"azimuth": azimuth, "elevation": elevation, "distance_m": distance_m}
    if target_x_m is not None:
        args["target_x_m"] = target_x_m
    if target_y_m is not None:
        args["target_y_m"] = target_y_m
    if target_z_m is not None:
        args["target_z_m"] = target_z_m
    return await _rpc("set_camera", **args)


@mcp.tool()
async def set_visual_style(style: str = "shading") -> dict:
    """
    Altera o estilo visual da vista ativa.
    Estilos disponíveis:
      - wireframe: apenas linhas (aramado)
      - hidden: linhas ocultas removidas
      - shading: cores sólidas com sombreamento (padrão)
      - realistic: materiais e texturas realistas
    O modo "realistic" mostra materiais e texturas do projeto.
    """
    return await _rpc("set_visual_style", style=style)


@mcp.tool()
async def set_view_plan(level: Optional[str] = None) -> dict:
    """
    Muda para a vista de planta baixa do nível especificado.
    Padrão: térreo (Nível 1). Mostra a planta vista de cima.
    """
    args = {}
    if level:
        args["level"] = level
    return await _rpc("set_view_plan", **args)


@mcp.tool()
async def set_view_3d() -> dict:
    """
    Muda para a vista 3D padrão com zoom automático.
    Mostra o modelo em perspectiva tridimensional.
    """
    return await _rpc("set_view_3d")


@mcp.tool()
async def set_view_shaded() -> dict:
    """
    Muda para vista 3D com ângulo isométrico elevado.
    Melhor ângulo para visualizar a casa completa com móveis.
    """
    return await _rpc("set_view_shaded")


@mcp.tool()
async def zoom_fit() -> dict:
    """Ajusta o zoom para mostrar todo o conteúdo visível da vista ativa."""
    return await _rpc("zoom_fit")


@mcp.tool()
async def focus_element(element_id: int) -> dict:
    """Seleciona e enquadra um elemento no Revit. Útil para localizar uma
    janela, porta ou móvel específico após uma alteração."""
    return await _rpc("focus_element", element_id=element_id)


@mcp.tool()
async def screenshot(path: Optional[str] = None, pixels: int = 1600) -> dict:
    """
    Exporta a vista ativa como imagem PNG.
    path = caminho do arquivo (opcional, ex: "C:/Projetos/foto.png").
    pixels = resolução horizontal (padrão: 1600px).
    Se path for omitido, salva em pasta temporária.
    """
    args = {"pixels": pixels}
    if path:
        args["path"] = path
    return await _rpc("screenshot", **args)


# =============================================================================
# ONDA 1 — MANIPULAÇÃO DE ELEMENTOS
# =============================================================================

@mcp.tool()
async def move_element(element_id: int, dx_m: float = 0, dy_m: float = 0, dz_m: float = 0) -> dict:
    """Move um elemento por (dx, dy, dz) em metros."""
    return await _rpc("move_element", element_id=element_id, dx_m=dx_m, dy_m=dy_m, dz_m=dz_m)

@mcp.tool()
async def copy_element(element_id: int, dx_m: float = 0, dy_m: float = 0, dz_m: float = 0) -> dict:
    """Copia um elemento com deslocamento (dx, dy, dz) em metros. Retorna IDs criados."""
    return await _rpc("copy_element", element_id=element_id, dx_m=dx_m, dy_m=dy_m, dz_m=dz_m)

@mcp.tool()
async def rotate_element(element_id: int, angle_deg: float = 90, center_x_m: float = 0, center_y_m: float = 0) -> dict:
    """Rotaciona elemento em torno de eixo vertical em (center_x, center_y). Ângulo em graus."""
    return await _rpc("rotate_element", element_id=element_id, angle_deg=angle_deg,
                       center_x_m=center_x_m, center_y_m=center_y_m)

@mcp.tool()
async def mirror_element(element_id: int, x1_m: float, y1_m: float, x2_m: float, y2_m: float) -> dict:
    """Espelha um elemento em plano vertical definido pela linha (x1,y1)-(x2,y2)."""
    return await _rpc("mirror_element", element_id=element_id, x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m)

@mcp.tool()
async def modify_parameters(element_id: int, params: dict) -> dict:
    """
    Modifica parâmetros de um elemento. params = {"Altura": 3.2, "Comentário": "OK"}.
    Números são interpretados como metros para parâmetros double, inteiros para integer.
    """
    return await _rpc("modify_parameters", element_id=element_id, params=params)

@mcp.tool()
async def align_elements(ids: list, axis: str = "x") -> dict:
    """
    Alinha elementos no eixo especificado (x/y/z).
    O primeiro ID é a referência; os demais são movidos para casar a coordenada.
    """
    return await _rpc("align_elements", ids=ids, axis=axis)


# =============================================================================
# ONDA 2 — ANOTAÇÃO
# =============================================================================

@mcp.tool()
async def create_text(text: str, x_m: float = 0, y_m: float = 0,
                      text_size_mm: Optional[float] = None) -> dict:
    """Cria uma nota de texto na vista ativa em (x, y) metros."""
    args = {"text": text, "x_m": x_m, "y_m": y_m}
    if text_size_mm is not None:
        args["text_size_mm"] = text_size_mm
    return await _rpc("create_text", **args)

@mcp.tool()
async def create_section_mark(x1_m: float, y1_m: float, x2_m: float, y2_m: float,
                              bottom_m: float = 0, top_m: float = 6, depth_m: float = 5) -> dict:
    """Cria uma vista de corte (secção) definida pela linha (x1,y1)-(x2,y2)."""
    return await _rpc("create_section_mark", x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m,
                       bottom_m=bottom_m, top_m=top_m, depth_m=depth_m)

@mcp.tool()
async def create_spot_elevation(element_id: int, x_m: float, y_m: float, z_m: float = 0) -> dict:
    """Cria cota de nível (spot elevation) num ponto sobre um elemento."""
    return await _rpc("create_spot_elevation", element_id=element_id, x_m=x_m, y_m=y_m, z_m=z_m)

@mcp.tool()
async def create_detail_line(x1_m: float, y1_m: float, x2_m: float, y2_m: float) -> dict:
    """Cria linha de detalhe 2D (linha auxiliar) na vista ativa."""
    return await _rpc("create_detail_line", x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m)


@mcp.tool()
async def create_detail_lines(lines: list) -> dict:
    """
    Cria várias linhas de detalhe 2D em uma única transação do Revit.

    Cada item deve conter ``x1_m, y1_m, x2_m, y2_m`` em metros. A operação
    retorna as linhas criadas e os erros individuais, sem interromper o lote.
    É indicada para cotas gráficas, mobiliário esquemático, escadas e símbolos.
    """
    return await _rpc("create_detail_lines", lines=lines)


@mcp.tool()
async def create_detail_rectangle(x_m: float, y_m: float, width_m: float, height_m: float,
                                  filled: bool = False) -> dict:
    """Cria um retângulo de anotação 2D na vista ativa; filled cria uma região preenchida."""
    return await _rpc("create_detail_rectangle", x_m=x_m, y_m=y_m, width_m=width_m,
                      height_m=height_m, filled=filled)


@mcp.tool()
async def validate_document() -> dict:
    """Revisa avisos, elementos essenciais e ambientes não posicionados; não altera o modelo."""
    return await _rpc("validate_document")


@mcp.tool()
async def analyze_bim_quality() -> dict:
    """Audita ambientes, tags, pranchas e paredes duplicadas sem alterar o RVT."""
    return await _rpc("analyze_bim_quality")


@mcp.tool()
async def validate_room_program(requirements: list) -> dict:
    """Valida programa de ambientes sem presumir norma local.

    Cada regra contém name_contains, count_min (padrão 1), min_area_m2 (padrão 0)
    e label opcional. Ex.: [{"name_contains":"quarto", "count_min":3,
    "min_area_m2":8.0}]. Não altera o modelo.
    """
    return await _rpc("validate_room_program", requirements=requirements)


@mcp.tool()
async def audit_documentation() -> dict:
    """Revisa pranchas, viewports, vistas imprimíveis sem prancha e números duplicados."""
    return await _rpc("audit_documentation")


@mcp.tool()
async def get_worksharing_status() -> dict:
    """Informa se o modelo usa worksharing e lista worksets sem tomar posse nem sincronizar."""
    return await _rpc("get_worksharing_status")


@mcp.tool()
async def get_element_worksharing_info(element_id: int) -> dict:
    """Consulta criador, proprietário e último editor no worksharing; somente leitura."""
    return await _rpc("get_element_worksharing_info", element_id=element_id)


@mcp.tool()
async def list_audit_log(limit: int = 50) -> dict:
    """Lista trilha persistente de operações de escrita realizadas pelo MCP no RVT."""
    return await _rpc("list_audit_log", limit=limit)


@mcp.tool()
async def create_filled_region(x1_m: float, y1_m: float, x2_m: float, y2_m: float) -> dict:
    """Cria região preenchida (hachura) num retângulo na vista ativa."""
    return await _rpc("create_filled_region", x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m)

@mcp.tool()
async def create_callout(x1_m: float, y1_m: float, x2_m: float, y2_m: float) -> dict:
    """Cria callout (detalhe ampliado) num retângulo da vista ativa."""
    return await _rpc("create_callout", x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m)


# =============================================================================
# ONDA 3 — MODELAGEM AVANÇADA
# =============================================================================

@mcp.tool()
async def create_section_view(x1_m: float, y1_m: float, x2_m: float, y2_m: float,
                              bottom_m: float = 0, top_m: float = 6, depth_m: float = 5) -> dict:
    """Alias de create_section_mark — cria vista de corte."""
    return await _rpc("create_section_view", x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m,
                       bottom_m=bottom_m, top_m=top_m, depth_m=depth_m)

@mcp.tool()
async def create_grid(x1_m: float, y1_m: float, x2_m: float, y2_m: float, name: Optional[str] = None) -> dict:
    """Cria eixo estrutural (Grid) entre dois pontos. name = rótulo (ex: 'A', '1')."""
    args = {"x1_m": x1_m, "y1_m": y1_m, "x2_m": x2_m, "y2_m": y2_m}
    if name: args["name"] = name
    return await _rpc("create_grid", **args)

@mcp.tool()
async def create_opening(wall_id: int, x1_m: float, y1_m: float, x2_m: float, y2_m: float,
                         bottom_m: float = 0, top_m: float = 2.1) -> dict:
    """Cria abertura retangular numa parede existente (útil para shafts, passagens)."""
    return await _rpc("create_opening", wall_id=wall_id, x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m,
                       bottom_m=bottom_m, top_m=top_m)

@mcp.tool()
async def create_curtain_wall(x1_m: float, y1_m: float, x2_m: float, y2_m: float,
                              height_m: float = 3.0, level: Optional[str] = None) -> dict:
    """Cria parede cortina (fachada envidraçada com montantes)."""
    args = {"x1_m": x1_m, "y1_m": y1_m, "x2_m": x2_m, "y2_m": y2_m, "height_m": height_m}
    if level: args["level"] = level
    return await _rpc("create_curtain_wall", **args)

@mcp.tool()
async def create_topography(points: list) -> dict:
    """
    Cria superfície topográfica (terreno) a partir de pontos.
    points = [{"x_m":0,"y_m":0,"z_m":0}, {"x_m":10,"y_m":0,"z_m":0.5}, ...]
    """
    return await _rpc("create_topography", points=points)

@mcp.tool()
async def list_toposolid_types() -> list:
    """Lista tipos de Toposolid disponíveis no Revit 2025."""
    return await _rpc("list_toposolid_types")

@mcp.tool()
async def create_toposolid(points: list, level: Optional[str] = None,
                           toposolid_type: Optional[str] = None) -> dict:
    """Cria terreno Toposolid (Revit 2025) por pontos {x_m,y_m,z_m}."""
    args = {"points": points}
    if level:
        args["level"] = level
    if toposolid_type:
        args["toposolid_type"] = toposolid_type
    return await _rpc("create_toposolid", **args)

@mcp.tool()
async def create_building_pad(x1_m: float, y1_m: float, x2_m: float, y2_m: float,
                              level: Optional[str] = None) -> dict:
    """Cria platô (Building Pad) sobre topografia — recorta terreno onde a casa assenta."""
    args = {"x1_m": x1_m, "y1_m": y1_m, "x2_m": x2_m, "y2_m": y2_m}
    if level: args["level"] = level
    return await _rpc("create_building_pad", **args)

@mcp.tool()
async def create_slab_edge(x1_m: float, y1_m: float, x2_m: float, y2_m: float) -> dict:
    """Cria contorno de borda de laje (rodapé de concreto). Implementação simplificada."""
    return await _rpc("create_slab_edge", x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m)

@mcp.tool()
async def create_ceiling_plan(x1_m: float, y1_m: float, x2_m: float, y2_m: float,
                              level: Optional[str] = None, ceiling_type: Optional[str] = None) -> dict:
    """Cria forro (mesmo que create_ceiling — mantido por alinhamento com o roadmap)."""
    args = {"x1_m": x1_m, "y1_m": y1_m, "x2_m": x2_m, "y2_m": y2_m}
    if level: args["level"] = level
    if ceiling_type: args["ceiling_type"] = ceiling_type
    return await _rpc("create_ceiling_plan", **args)

@mcp.tool()
async def modify_roof(element_id: int, params: dict) -> dict:
    """Ajusta parâmetros de um telhado existente (inclinação, offset, etc)."""
    return await _rpc("modify_roof", element_id=element_id, params=params)


# =============================================================================
# ONDA 4 — COMPONENTES
# =============================================================================

@mcp.tool()
async def place_plumbing_fixture(x_m: float, y_m: float, family_name: Optional[str] = None,
                                  level: Optional[str] = None) -> dict:
    """Posiciona louça sanitária (vaso, pia, chuveiro) da categoria PlumbingFixtures."""
    args = {"x_m": x_m, "y_m": y_m}
    if family_name: args["family_name"] = family_name
    if level: args["level"] = level
    return await _rpc("place_plumbing_fixture", **args)

@mcp.tool()
async def place_kitchen_counter(x_m: float, y_m: float, length_m: float = 2.4,
                                depth_m: float = 0.6, height_m: float = 0.9,
                                level: Optional[str] = None) -> dict:
    """Cria bancada de cozinha como caixa 3D."""
    args = {"x_m": x_m, "y_m": y_m, "length_m": length_m, "depth_m": depth_m, "height_m": height_m}
    if level: args["level"] = level
    return await _rpc("place_kitchen_counter", **args)

@mcp.tool()
async def place_vegetation(x_m: float, y_m: float, family_name: Optional[str] = None,
                           level: Optional[str] = None) -> dict:
    """Coloca planta/árvore (categoria Planting)."""
    args = {"x_m": x_m, "y_m": y_m}
    if family_name: args["family_name"] = family_name
    if level: args["level"] = level
    return await _rpc("place_vegetation", **args)

@mcp.tool()
async def place_car(x_m: float, y_m: float, family_name: Optional[str] = None,
                    level: Optional[str] = None) -> dict:
    """Coloca veículo estacionado (categoria Parking)."""
    args = {"x_m": x_m, "y_m": y_m}
    if family_name: args["family_name"] = family_name
    if level: args["level"] = level
    return await _rpc("place_car", **args)

@mcp.tool()
async def place_pool(x_m: float, y_m: float, width_m: float = 4, length_m: float = 8,
                     depth_m: float = 1.5, level: Optional[str] = None) -> dict:
    """Cria piscina retangular rebaixada."""
    args = {"x_m": x_m, "y_m": y_m, "width_m": width_m, "length_m": length_m, "depth_m": depth_m}
    if level: args["level"] = level
    return await _rpc("place_pool", **args)

@mcp.tool()
async def place_appliance(x_m: float, y_m: float, family_name: Optional[str] = None,
                          level: Optional[str] = None) -> dict:
    """Coloca eletrodoméstico (categoria SpecialityEquipment)."""
    args = {"x_m": x_m, "y_m": y_m}
    if family_name: args["family_name"] = family_name
    if level: args["level"] = level
    return await _rpc("place_appliance", **args)


# =============================================================================
# ONDA 5 — PRANCHAS
# =============================================================================

@mcp.tool()
async def create_sheet(number: str = "A-001", name: str = "Planta Geral") -> dict:
    """Cria nova prancha (folha) com selo padrão se disponível."""
    return await _rpc("create_sheet", number=number, name=name)

@mcp.tool()
async def place_view_on_sheet(sheet_id: int, view_id: int, x_m: float = 0.2, y_m: float = 0.2) -> dict:
    """Posiciona uma vista existente numa prancha."""
    return await _rpc("place_view_on_sheet", sheet_id=sheet_id, view_id=view_id, x_m=x_m, y_m=y_m)

@mcp.tool()
async def create_schedule(category: str) -> dict:
    """
    Cria tabela (schedule) para uma categoria.
    Ex: category='Doors', 'Windows', 'Rooms', 'Walls', 'Furniture'.
    """
    return await _rpc("create_schedule", category=category)

@mcp.tool()
async def create_legend(name: str = "Legenda MCP") -> dict:
    """Cria vista Legenda duplicando a primeira existente."""
    return await _rpc("create_legend", name=name)

@mcp.tool()
async def create_color_fill() -> dict:
    """Aplica esquema de cores em ambientes na vista ativa (Color Fill de Rooms)."""
    return await _rpc("create_color_fill")

@mcp.tool()
async def create_north_arrow(x_m: float = 0, y_m: float = 0) -> dict:
    """Insere símbolo de Norte na vista ativa. Requer família de norte carregada."""
    return await _rpc("create_north_arrow", x_m=x_m, y_m=y_m)


# =============================================================================
# ONDA 6 — EXPORTAÇÃO
# =============================================================================

@mcp.tool()
async def export_pdf(folder: Optional[str] = None, name: str = "export_mcp") -> dict:
    """Exporta a vista ativa para PDF."""
    args = {"name": name}
    if folder: args["folder"] = folder
    return await _rpc("export_pdf", **args)

@mcp.tool()
async def export_dwg(folder: Optional[str] = None, name: str = "export_mcp.dwg") -> dict:
    """Exporta a vista ativa para DWG."""
    args = {"name": name}
    if folder: args["folder"] = folder
    return await _rpc("export_dwg", **args)

@mcp.tool()
async def export_ifc(folder: Optional[str] = None, name: str = "export_mcp.ifc",
                     profile: str = "coordination", base_quantities: bool = True,
                     space_boundaries: Optional[int] = None) -> dict:
    """Exporta IFC com perfil coordination, architecture ou quantities e property sets BIM."""
    args = {"name": name, "profile": profile, "base_quantities": base_quantities}
    if folder: args["folder"] = folder
    if space_boundaries is not None: args["space_boundaries"] = space_boundaries
    return await _rpc("export_ifc", **args)


# =============================================================================
# ONDA 7 — CONFIGURAÇÃO
# =============================================================================

@mcp.tool()
async def set_view_template(template_id: int) -> dict:
    """Aplica um template de vista à vista ativa (por ID)."""
    return await _rpc("set_view_template", template_id=template_id)

@mcp.tool()
async def set_wall_join(wall_id: int, end: int = 0, mode: str = "allow") -> dict:
    """
    Controla junção de parede na extremidade (0 ou 1).
    mode='allow' liga, 'disallow' desliga (útil para cantos limpos).
    """
    return await _rpc("set_wall_join", wall_id=wall_id, end=end, mode=mode)

@mcp.tool()
async def set_line_style(category: str, weight: int = 3) -> dict:
    """
    Altera espessura de linha (line weight) de projeção de uma categoria.
    Ex: category='Walls', weight=5.
    """
    return await _rpc("set_line_style", category=category, weight=weight)

@mcp.tool()
async def import_cad(path: str) -> dict:
    """Importa arquivo CAD (DWG/DXF) na vista ativa."""
    return await _rpc("import_cad", path=path)


# =============================================================================
# Ponto de entrada
# =============================================================================

if __name__ == "__main__":
    mcp.run()
