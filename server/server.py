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
    load_dotenv(Path(__file__).with_name(".env"), override=False)
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
WORKSPACE_CONFIG_PATH = Path(__file__).with_name(".revit_mcp_bridge.json")
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


@mcp.tool()
async def bridge_health() -> dict:
    """Verifica saúde e autenticação do bridge local sem modificar o projeto."""
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


# =============================================================================
# FERRAMENTAS MCP — Informações e diagnóstico
# =============================================================================

@mcp.tool()
async def ping() -> dict:
    """Verifica se o Revit está respondendo. Use para testar a conexão."""
    return await _rpc("ping")


@mcp.tool()
async def get_info() -> dict:
    """Estado do Revit e do documento ativo: versão, idioma, usuário, documentos abertos,
    projeto (nome/número/cliente), salvo/modificado/workshared, unidade de comprimento do projeto,
    contagem de níveis, eixos, paredes, portas, janelas, pisos, coberturas, ambientes, mobiliário
    e pranchas, total de avisos e vista ativa (tipo, escala, nível). Use antes de começar.
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
    """Níveis em ordem de elevação: elevação (m), altura até o próximo nível (pé-direito
    de piso a piso), se é pavimento, quantos elementos estão no nível, plantas associadas e se está fixado.
    """
    return await _rpc("list_levels")


@mcp.tool()
async def list_walls() -> list:
    """Paredes com tipo, função (externa/interna), nível base e de topo, pontos inicial/final,
    comprimento, altura, espessura, deslocamento da base, área, se é estrutural, se delimita ambiente
    e quantas portas/janelas hospeda. O ID é usado para inserir portas e janelas.
    """
    return await _rpc("list_walls")


@mcp.tool()
async def list_windows() -> list:
    """Janelas com família, tipo, nível, largura, altura, peitoril, altura da verga, marca,
    parede hospedeira e ambiente.
    """
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
    """Tipos de parede básica com espessura, função, quantas paredes usam cada tipo
    e camadas (função, material, espessura, núcleo).
    """
    return await _rpc("list_wall_types")


@mcp.tool()
async def list_floor_types() -> list:
    """Tipos de piso com espessura total, quantos pisos usam cada tipo e camadas (função, material, espessura).
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
async def create_project_parameter(name: str, categories: list, type: str = "text", instance: bool = True,
                                    group: str = "data") -> dict:
    """
    Cria um parâmetro de projeto vinculado a categorias (ex.: categories=["Walls","Doors"]).
    type: text, number, integer, yesno, length, area ou volume.
    instance=True vincula por instância; False vincula por tipo.
    group: aba das Propriedades onde aparece (data, text, identitydata, geometry, general).
    Usa um arquivo de parâmetros compartilhados local próprio do MCP.
    """
    return await _rpc("create_project_parameter", name=name, categories=categories, type=type,
                       instance=instance, group=group)


@mcp.tool()
async def list_project_parameters() -> list:
    """Lista os parâmetros de projeto já vinculados no documento (nome, instância/tipo, categorias)."""
    return await _rpc("list_project_parameters")


@mcp.tool()
async def create_view_filter(name: str, categories: list) -> dict:
    """Cria um filtro de vista (ParameterFilterElement) por categorias, sem regras de parâmetro.
    Use com apply_view_filter para controlar visibilidade/aparência de um conjunto de categorias de uma vez."""
    return await _rpc("create_view_filter", name=name, categories=categories)


@mcp.tool()
async def list_view_filters() -> list:
    """Lista os filtros de vista (ParameterFilterElement) disponíveis no projeto."""
    return await _rpc("list_view_filters")


@mcp.tool()
async def apply_view_filter(view_id: int, filter_name: str, visible: bool = True,
                             color: Optional[str] = None) -> dict:
    """Aplica um filtro já criado (create_view_filter) a uma vista: visibilidade e, opcionalmente,
    cor de linha (#RRGGBB ou R,G,B) para os elementos que casam com o filtro nessa vista."""
    args = {"view_id": view_id, "filter_name": filter_name, "visible": visible}
    if color:
        args["color"] = color
    return await _rpc("apply_view_filter", **args)


@mcp.tool()
async def list_phases() -> list:
    """Lista as fases do projeto (id, nome), na ordem da sequência construtiva."""
    return await _rpc("list_phases")


@mcp.tool()
async def set_element_phase(element_id: int, phase_created: Optional[str] = None,
                             phase_demolished: Optional[str] = None) -> dict:
    """Define a fase de criação e/ou demolição de um elemento (nomes de fases de list_phases)."""
    args = {"element_id": element_id}
    if phase_created:
        args["phase_created"] = phase_created
    if phase_demolished:
        args["phase_demolished"] = phase_demolished
    return await _rpc("set_element_phase", **args)


@mcp.tool()
async def create_group(element_ids: list, name: Optional[str] = None) -> dict:
    """Agrupa elementos existentes (pelos IDs) em um grupo de modelo. Reutilize com place_group."""
    args = {"element_ids": element_ids}
    if name:
        args["name"] = name
    return await _rpc("create_group", **args)


@mcp.tool()
async def list_groups() -> list:
    """Lista os tipos de grupo de modelo já criados (id, nome, quantidade de instâncias no projeto)."""
    return await _rpc("list_groups")


@mcp.tool()
async def place_group(x_m: float, y_m: float, name: Optional[str] = None,
                       group_type_id: Optional[int] = None, z_m: float = 0) -> dict:
    """Posiciona uma nova instância de um grupo já existente (por group_type_id ou name de list_groups)."""
    args = {"x_m": x_m, "y_m": y_m, "z_m": z_m}
    if name:
        args["name"] = name
    if group_type_id:
        args["group_type_id"] = group_type_id
    return await _rpc("place_group", **args)


@mcp.tool()
async def set_category_override(view_id: int, category: str, color: Optional[str] = None,
                                 line_weight: Optional[int] = None, halftone: Optional[bool] = None) -> dict:
    """Aplica override gráfico (cor/peso de linha/meio-tom) a uma categoria inteira em uma vista."""
    args = {"view_id": view_id, "category": category}
    if color:
        args["color"] = color
    if line_weight is not None:
        args["line_weight"] = line_weight
    if halftone is not None:
        args["halftone"] = halftone
    return await _rpc("set_category_override", **args)


@mcp.tool()
async def set_element_override(view_id: int, element_id: int, color: Optional[str] = None,
                                line_weight: Optional[int] = None, halftone: Optional[bool] = None) -> dict:
    """Aplica override gráfico a um elemento específico em uma vista (sobrepõe o override de categoria)."""
    args = {"view_id": view_id, "element_id": element_id}
    if color:
        args["color"] = color
    if line_weight is not None:
        args["line_weight"] = line_weight
    if halftone is not None:
        args["halftone"] = halftone
    return await _rpc("set_element_override", **args)


@mcp.tool()
async def reset_overrides(view_id: int, category: Optional[str] = None, element_id: Optional[int] = None) -> dict:
    """Remove overrides de categoria e/ou de elemento em uma vista, restaurando a aparência padrão."""
    args = {"view_id": view_id}
    if category:
        args["category"] = category
    if element_id is not None:
        args["element_id"] = element_id
    return await _rpc("reset_overrides", **args)


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
                              level: Optional[str] = None, height_offset_m: float = 2.7) -> dict:
    """Cria forro retangular entre os cantos (x1,y1) e (x2,y2), a height_offset_m acima do nível.

    Mesmo comportamento de create_ceiling — mantido por alinhamento com o roadmap.
    """
    # O add-in compartilha a implementação de create_ceiling, que espera o
    # retângulo como mínimos/máximos e a altura do forro acima do nível.
    args = {
        "x_min_m": min(x1_m, x2_m), "y_min_m": min(y1_m, y2_m),
        "x_max_m": max(x1_m, x2_m), "y_max_m": max(y1_m, y2_m),
        "height_offset_m": height_offset_m,
    }
    if level: args["level"] = level
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
# FLUXOS PROFISSIONAIS — dados, documentação, coordenação, estrutura e MEP
# =============================================================================
#
# Unidades: comprimentos em metros, áreas em m², volumes em m³, ângulos em
# graus. Parâmetros de outras grandezas usam a unidade de exibição do projeto
# (ou texto, ex.: "25 mm", interpretado pelo Revit).

def _clean(**kwargs) -> dict:
    """Remove argumentos não informados para o add-in aplicar seus padrões."""
    return {k: v for k, v in kwargs.items() if v is not None}


# --- Dados do projeto --------------------------------------------------------

@mcp.tool()
async def get_project_info() -> dict:
    """Lê as informações do projeto (nome, número, cliente, endereço, status, autor e demais parâmetros)."""
    return await _rpc("get_project_info")


@mcp.tool()
async def set_project_info(name: Optional[str] = None, number: Optional[str] = None,
                           client: Optional[str] = None, address: Optional[str] = None,
                           status: Optional[str] = None, issue_date: Optional[str] = None,
                           building_name: Optional[str] = None, author: Optional[str] = None,
                           organization: Optional[str] = None, params: Optional[dict] = None) -> dict:
    """Preenche as informações do projeto usadas nos carimbos.

    params = outros parâmetros de Informações do Projeto, ex.: {"Responsável técnico": "Fulano"}.
    """
    return await _rpc("set_project_info", **_clean(
        name=name, number=number, client=client, address=address, status=status,
        issue_date=issue_date, building_name=building_name, author=author,
        organization=organization, params=params))


@mcp.tool()
async def get_project_location() -> dict:
    """Ponto base, ponto de levantamento, coordenadas compartilhadas, norte verdadeiro e latitude/longitude."""
    return await _rpc("get_project_location")






@mcp.tool()
async def create_workset(names: list[str]) -> dict:
    """Cria worksets de usuário em modelo workshared (ex.: ["Arquitetura", "Estrutura", "MEP"])."""
    return await _rpc("create_workset", names=names)


@mcp.tool()
async def set_element_workset(ids: list[int], workset: str) -> dict:
    """Move elementos para um workset (modelo workshared; o Revit empresta os elementos se preciso)."""
    return await _rpc("set_element_workset", ids=ids, workset=workset)






@mcp.tool()
async def find_elements(category: Optional[str] = None, categories: Optional[list[str]] = None,
                        where: Optional[list[dict]] = None, level: Optional[str] = None,
                        type_contains: Optional[str] = None,
                        return_parameters: Optional[list[str]] = None, limit: int = 200) -> dict:
    """Consulta elementos por categoria, nível, tipo e condições de parâmetros.

    where = [{"parameter": "Marca", "op": "empty"},
             {"parameter": "Largura", "op": "gte", "value": 0.9}]
    op: equals, not_equals, contains, not_contains, starts_with, gt, gte, lt, lte, empty, not_empty.
    Números em m/m²/m³/graus. return_parameters traz os valores desses parâmetros.
    """
    return await _rpc("find_elements", **_clean(
        category=category, categories=categories, where=where, level=level,
        type_contains=type_contains, return_parameters=return_parameters, limit=limit))


@mcp.tool()
async def set_parameters_bulk(params: dict, ids: Optional[list[int]] = None,
                              filter: Optional[dict] = None, target: str = "instance") -> dict:
    """Define parâmetros em vários elementos de uma vez (uma única transação).

    Use ids OU filter (mesmos campos de find_elements, ex.:
    {"category": "Doors", "level": "Térreo"}). target="type" altera os tipos.
    Exemplo: params={"Classificação de incêndio": "EI 60", "Comentários": "Revisar"}.
    """
    return await _rpc("set_parameters_bulk", **_clean(params=params, ids=ids, filter=filter, target=target))


# --- Vistas ------------------------------------------------------------------

@mcp.tool()
async def duplicate_view(view_id: Optional[int] = None, mode: str = "with_detailing",
                         name: Optional[str] = None, copies: int = 1,
                         names: Optional[list[str]] = None) -> dict:
    """Duplica uma vista (ativa por padrão). mode: duplicate, with_detailing ou dependent."""
    return await _rpc("duplicate_view", **_clean(view_id=view_id, mode=mode, name=name, copies=copies, names=names))


@mcp.tool()
async def set_view_properties(view_id: Optional[int] = None, name: Optional[str] = None,
                              scale: Optional[int] = None, detail_level: Optional[str] = None,
                              discipline: Optional[str] = None, phase: Optional[str] = None,
                              crop: Optional[bool] = None, crop_visible: Optional[bool] = None,
                              template_id: Optional[int] = None) -> dict:
    """Ajusta nome, escala (ex.: 50 = 1:50), nível de detalhe (coarse/medium/fine),
    disciplina (Architectural, Structural, Mechanical, Electrical, Plumbing, Coordination),
    fase, recorte e template de uma vista."""
    return await _rpc("set_view_properties", **_clean(
        view_id=view_id, name=name, scale=scale, detail_level=detail_level, discipline=discipline,
        phase=phase, crop=crop, crop_visible=crop_visible, template_id=template_id))


@mcp.tool()
async def create_elevations(x_m: float, y_m: float, plan_view_id: Optional[int] = None,
                            interior: bool = False, scale: int = 100,
                            directions: Optional[list[str]] = None,
                            name_prefix: Optional[str] = None) -> dict:
    """Cria elevações a partir de um marcador no ponto (x, y) da planta.

    directions: subconjunto de ["Norte", "Sul", "Leste", "Oeste"] (padrão: as quatro).
    interior=True usa o tipo de elevação interna, para elevações de ambientes.
    """
    return await _rpc("create_elevations", **_clean(
        x_m=x_m, y_m=y_m, plan_view_id=plan_view_id, interior=interior, scale=scale,
        directions=directions, name_prefix=name_prefix))


@mcp.tool()
async def create_3d_view(name: Optional[str] = None, element_ids: Optional[list[int]] = None,
                         margin_m: float = 1.0, x_min_m: Optional[float] = None,
                         y_min_m: Optional[float] = None, z_min_m: Optional[float] = None,
                         x_max_m: Optional[float] = None, y_max_m: Optional[float] = None,
                         z_max_m: Optional[float] = None, hide_categories: Optional[list[str]] = None,
                         activate: bool = True) -> dict:
    """Cria uma vista 3D nomeada, com caixa de corte ajustada a elementos ou a limites,
    e categorias ocultas (ex.: ["Roofs"] para ver o interior)."""
    return await _rpc("create_3d_view", **_clean(
        name=name, element_ids=element_ids, margin_m=margin_m, x_min_m=x_min_m, y_min_m=y_min_m,
        z_min_m=z_min_m, x_max_m=x_max_m, y_max_m=y_max_m, z_max_m=z_max_m,
        hide_categories=hide_categories, activate=activate))


@mcp.tool()
async def create_drafting_view(name: str, scale: int = 20) -> dict:
    """Cria uma vista de desenho (detalhes 2D sem modelo), ex.: 'Detalhe de soleira'."""
    return await _rpc("create_drafting_view", name=name, scale=scale)


@mcp.tool()
async def create_rule_filter(name: str, categories: list[str], rules: Optional[list[dict]] = None,
                             logic: str = "and", view_id: Optional[int] = None, apply: bool = True,
                             color: Optional[str] = None, fill: bool = True, halftone: bool = False,
                             transparency: Optional[int] = None, line_weight: Optional[int] = None,
                             visible: bool = True) -> dict:
    """Cria (ou atualiza) um filtro de vista com regras de parâmetro e já o aplica com cor/visibilidade.

    Para filtros só por categoria, use create_view_filter + apply_view_filter.

    rules = [{"parameter": "Classificação de incêndio", "op": "equals", "value": "EI 60"}]
    op: equals, not_equals, contains, not_contains, starts_with, gt, gte, lt, lte,
    has_value, has_no_value. color='#FF0000'. visible=False oculta os elementos filtrados.
    """
    return await _rpc("create_rule_filter", **_clean(
        name=name, categories=categories, rules=rules, logic=logic, view_id=view_id, apply=apply,
        color=color, fill=fill, halftone=halftone, transparency=transparency,
        line_weight=line_weight, visible=visible))


@mcp.tool()
async def override_elements_in_view(ids: list[int], mode: str = "override",
                                    view_id: Optional[int] = None, color: Optional[str] = None,
                                    fill: bool = True, halftone: bool = False,
                                    transparency: Optional[int] = None,
                                    line_weight: Optional[int] = None) -> dict:
    """Altera a aparência de elementos numa vista. mode: override, reset, hide, unhide ou isolate."""
    return await _rpc("override_elements_in_view", **_clean(
        ids=ids, mode=mode, view_id=view_id, color=color, fill=fill, halftone=halftone,
        transparency=transparency, line_weight=line_weight))


@mcp.tool()
async def set_category_visibility(categories: list[str], visible: bool = False,
                                  view_id: Optional[int] = None) -> dict:
    """Mostra/oculta categorias inteiras numa vista (ex.: ["Furniture", "Planting"])."""
    return await _rpc("set_category_visibility", **_clean(categories=categories, visible=visible, view_id=view_id))


@mcp.tool()
async def tag_elements(category: str, view_id: Optional[int] = None, ids: Optional[list[int]] = None,
                       leader: bool = False, skip_tagged: bool = True,
                       offset_x_m: float = 0, offset_y_m: float = 0) -> dict:
    """Etiqueta todos os elementos visíveis de uma categoria na vista (Doors, Windows,
    Walls, StructuralColumns, DuctCurves...). Exige família de etiqueta carregada."""
    return await _rpc("tag_elements", **_clean(
        category=category, view_id=view_id, ids=ids, leader=leader, skip_tagged=skip_tagged,
        offset_x_m=offset_x_m, offset_y_m=offset_y_m))


# --- Pranchas, revisões, tabelas e exportação -------------------------------

@mcp.tool()
async def list_sheets() -> list:
    """Lista pranchas com número, nome, vistas posicionadas e revisões."""
    return await _rpc("list_sheets")


@mcp.tool()
async def list_titleblocks() -> list:
    """Lista os carimbos (title blocks) carregados."""
    return await _rpc("list_titleblocks")


@mcp.tool()
async def create_sheets(sheets: list[dict], titleblock: Optional[str] = None) -> dict:
    """Cria várias pranchas: sheets=[{"number": "A-101", "name": "Planta térreo"}, ...].

    titleblock = trecho do nome da família/tipo do carimbo (padrão: o primeiro).
    """
    return await _rpc("create_sheets", **_clean(sheets=sheets, titleblock=titleblock))


@mcp.tool()
async def list_revisions() -> list:
    """Lista as revisões do projeto (sequência, número, descrição, data, emissão)."""
    return await _rpc("list_revisions")


@mcp.tool()
async def create_revision(description: str, date: Optional[str] = None,
                          issued_by: Optional[str] = None, issued_to: Optional[str] = None) -> dict:
    """Cria uma revisão (ex.: 'Emissão para aprovação na prefeitura')."""
    return await _rpc("create_revision", **_clean(description=description, date=date, issued_by=issued_by, issued_to=issued_to))


@mcp.tool()
async def add_revision_to_sheets(revision_id: int, sheet_ids: Optional[list[int]] = None) -> dict:
    """Adiciona a revisão às pranchas indicadas (padrão: todas)."""
    return await _rpc("add_revision_to_sheets", **_clean(revision_id=revision_id, sheet_ids=sheet_ids))


@mcp.tool()
async def list_schedules() -> list:
    """Lista as tabelas do projeto com categoria e campos."""
    return await _rpc("list_schedules")


@mcp.tool()
async def create_schedule_advanced(category: str, fields: list[str], name: Optional[str] = None,
                                   sort_by: Optional[list[str]] = None,
                                   filters: Optional[list[dict]] = None,
                                   itemize: bool = True, grand_total: bool = False) -> dict:
    """Cria tabela com campos, ordenação e filtros.

    Ex.: category="Doors", fields=["Marca", "Tipo", "Largura", "Altura", "Nível"],
    sort_by=["Nível", "Marca"], filters=[{"field": "Nível", "op": "equals", "value": "Térreo"}].
    Os nomes de campo seguem o idioma do Revit; se algum não existir, a resposta lista os disponíveis.
    """
    return await _rpc("create_schedule_advanced", **_clean(
        category=category, fields=fields, name=name, sort_by=sort_by, filters=filters,
        itemize=itemize, grand_total=grand_total))


@mcp.tool()
async def get_schedule_data(schedule_id: int, max_rows: int = 500) -> dict:
    """Lê o conteúdo de uma tabela (linhas e colunas como texto) para conferência ou relatórios."""
    return await _rpc("get_schedule_data", schedule_id=schedule_id, max_rows=max_rows)


@mcp.tool()
async def export_schedule_csv(schedule_id: int, folder: Optional[str] = None,
                              name: Optional[str] = None, delimiter: str = ";") -> dict:
    """Exporta uma tabela para CSV (compatível com Excel em português com delimitador ';')."""
    return await _rpc("export_schedule_csv", **_clean(schedule_id=schedule_id, folder=folder, name=name, delimiter=delimiter))


@mcp.tool()
async def export_sheets_pdf(folder: Optional[str] = None, sheet_ids: Optional[list[int]] = None,
                            number_prefix: Optional[str] = None, combine: bool = True,
                            name: Optional[str] = None) -> dict:
    """Exporta pranchas para PDF (todas, por IDs ou por prefixo de número, ex.: 'A-').

    combine=True gera um único PDF; False gera um por prancha.
    """
    return await _rpc("export_sheets_pdf", **_clean(
        folder=folder, sheet_ids=sheet_ids, number_prefix=number_prefix, combine=combine, name=name))


@mcp.tool()
async def export_sheets_dwg(folder: Optional[str] = None, sheet_ids: Optional[list[int]] = None,
                            number_prefix: Optional[str] = None, prefix: str = "",
                            merged_views: bool = True) -> dict:
    """Exporta pranchas para DWG (um arquivo por prancha)."""
    return await _rpc("export_sheets_dwg", **_clean(
        folder=folder, sheet_ids=sheet_ids, number_prefix=number_prefix, prefix=prefix,
        merged_views=merged_views))


# --- Coordenação e qualidade --------------------------------------------------

@mcp.tool()
async def analyze_clashes(category_a: str, category_b: str, ids_a: Optional[list[int]] = None,
                          limit: int = 200, ignore_joined: bool = True) -> dict:
    """Detecta interferências físicas entre duas categorias (ex.: StructuralFraming x DuctCurves).

    Ignora hospedeiro/hospedado e elementos unidos. Somente leitura.
    """
    return await _rpc("analyze_clashes", **_clean(
        category_a=category_a, category_b=category_b, ids_a=ids_a, limit=limit, ignore_joined=ignore_joined))


@mcp.tool()
async def analyze_quantities(categories: Optional[list[str]] = None, level: Optional[str] = None) -> dict:
    """Quantitativo por categoria e tipo: contagem, comprimento (m), área (m²) e volume (m³)."""
    return await _rpc("analyze_quantities", **_clean(categories=categories, level=level))


@mcp.tool()
async def analyze_material_takeoff(categories: Optional[list[str]] = None) -> dict:
    """Levantamento de materiais: área e volume por material nas categorias informadas."""
    return await _rpc("analyze_material_takeoff", **_clean(categories=categories))


@mcp.tool()
async def preview_purge_unused() -> dict:
    """Mostra famílias, tipos e materiais não usados que podem ser removidos (somente leitura)."""
    return await _rpc("preview_purge_unused")


@mcp.tool()
async def purge_unused(confirm: bool = False, passes: int = 3) -> dict:
    """Remove itens não usados (exclusão permanente). Exige confirm=True após preview_purge_unused."""
    return await _rpc("purge_unused", confirm=confirm, passes=passes)


@mcp.tool()
async def list_links() -> dict:
    """Lista vínculos Revit (caminho, carregado) e CAD (vinculado/importado, vista)."""
    return await _rpc("list_links")


@mcp.tool()
async def load_revit_link(path: str, positioning: str = "origin") -> dict:
    """Vincula um modelo .rvt (ex.: estrutura, MEP). positioning: origin ou shared."""
    return await _rpc("load_revit_link", path=path, positioning=positioning)


@mcp.tool()
async def reload_link(link_type_id: int, unload: bool = False) -> dict:
    """Recarrega (ou descarrega, com unload=True) um vínculo Revit."""
    return await _rpc("reload_link", link_type_id=link_type_id, unload=unload)


# --- Estrutura ------------------------------------------------------------------

@mcp.tool()
async def create_beam(x1_m: Optional[float] = None, y1_m: Optional[float] = None,
                      x2_m: Optional[float] = None, y2_m: Optional[float] = None,
                      beams: Optional[list[dict]] = None, level: Optional[str] = None,
                      offset_m: float = 0, family: Optional[str] = None,
                      type: Optional[str] = None) -> dict:
    """Cria vigas estruturais entre dois pontos, ou várias com beams=[{x1_m,y1_m,x2_m,y2_m}].

    family/type filtram a família de Structural Framing (ex.: type="20x50").
    """
    if beams is None and None in (x1_m, y1_m, x2_m, y2_m):
        raise ValueError("Informe x1_m, y1_m, x2_m, y2_m ou a lista beams.")
    return await _rpc("create_beam", **_clean(
        x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m, beams=beams, level=level,
        offset_m=offset_m, family=family, type=type))


@mcp.tool()
async def create_foundation(kind: str = "isolated", points: Optional[list[dict]] = None,
                            wall_ids: Optional[list[int]] = None, level: Optional[str] = None,
                            offset_m: float = 0, family: Optional[str] = None,
                            type: Optional[str] = None) -> dict:
    """Cria fundações: kind="isolated" (sapatas nos points [{x_m,y_m}]) ou
    kind="wall" (sapata corrida sob as paredes wall_ids)."""
    return await _rpc("create_foundation", **_clean(
        kind=kind, points=points, wall_ids=wall_ids, level=level, offset_m=offset_m,
        family=family, type=type))


# --- MEP ------------------------------------------------------------------------

@mcp.tool()
async def list_mep_types() -> dict:
    """Lista sistemas e tipos de dutos, tubos, eletrocalhas e eletrodutos do projeto."""
    return await _rpc("list_mep_types")


@mcp.tool()
async def create_mep_run(kind: str, points: list[dict], level: Optional[str] = None,
                         offset_m: float = 2.7, system_type: Optional[str] = None,
                         type: Optional[str] = None, width_m: Optional[float] = None,
                         height_m: Optional[float] = None, diameter_m: Optional[float] = None,
                         connect: bool = True) -> dict:
    """Cria um trecho contínuo de duto, tubo, eletrocalha ou eletroduto pela polilinha points.

    kind: duct, pipe, cable_tray ou conduit. offset_m = altura acima do nível.
    Dutos retangulares: width_m/height_m; redondos e tubos: diameter_m.
    connect=True insere curvas entre segmentos (conforme preferências de roteamento).
    """
    return await _rpc("create_mep_run", **_clean(
        kind=kind, points=points, level=level, offset_m=offset_m, system_type=system_type,
        type=type, width_m=width_m, height_m=height_m, diameter_m=diameter_m, connect=connect))


@mcp.tool()
async def create_spaces_from_rooms(level: Optional[str] = None) -> dict:
    """Cria espaços MEP (Spaces) nos ambientes existentes, copiando nome e número."""
    return await _rpc("create_spaces_from_rooms", **_clean(level=level))


# --- Modelagem geral ------------------------------------------------------------

@mcp.tool()
async def array_elements(ids: list[int], count: int, dx_m: float = 0, dy_m: float = 0,
                         dz_m: float = 0) -> dict:
    """Cria cópias em série (count inclui o original) com passo (dx, dy, dz) em metros."""
    return await _rpc("array_elements", ids=ids, count=count, dx_m=dx_m, dy_m=dy_m, dz_m=dz_m)






@mcp.tool()
async def create_room_separators(lines: list[dict], level: Optional[str] = None) -> dict:
    """Cria linhas separadoras de ambiente (lines=[{x1_m,y1_m,x2_m,y2_m}]) para dividir
    ambientes sem paredes, como sala/cozinha integradas."""
    return await _rpc("create_room_separators", **_clean(lines=lines, level=level))


# =============================================================================
# ARQUITETURA — tipos, áreas, aberturas, cotas e organização do modelo
# =============================================================================

# --- Tipos ------------------------------------------------------------------

@mcp.tool()
async def list_element_types(category: str, contains: Optional[str] = None) -> dict:
    """Lista os tipos de uma categoria com dimensões, camadas e quantas instâncias usam cada um.

    Ex.: category="Walls" mostra espessura total e camadas (função, material,
    espessura) de cada tipo de parede; "Doors"/"Windows" mostram largura e altura.
    """
    return await _rpc("list_element_types", **_clean(category=category, contains=contains))


@mcp.tool()
async def duplicate_type(new_name: str, type_id: Optional[int] = None, type_name: Optional[str] = None,
                         category: Optional[str] = None, params: Optional[dict] = None) -> dict:
    """Duplica um tipo existente com novo nome e ajusta parâmetros de tipo.

    Ex.: porta 90x210 a partir de 80x210 → type_name="80x210", category="Doors",
    new_name="90x210", params={"Largura": 0.9}. Comprimentos em metros.
    """
    return await _rpc("duplicate_type", **_clean(
        new_name=new_name, type_id=type_id, type_name=type_name, category=category, params=params))


@mcp.tool()
async def change_element_type(ids: list[int], type_id: Optional[int] = None,
                              type_name: Optional[str] = None, category: Optional[str] = None) -> dict:
    """Troca o tipo de elementos existentes (paredes, portas, janelas, pisos...), informando o tipo anterior de cada um."""
    return await _rpc("change_element_type", **_clean(ids=ids, type_id=type_id, type_name=type_name, category=category))


@mcp.tool()
async def create_layered_type(name: str, layers: list[dict], kind: str = "wall",
                              base_type: Optional[str] = None, function: Optional[str] = None,
                              create_materials: bool = True) -> dict:
    """Cria tipo de parede, piso, forro ou cobertura por camadas (ordem: exterior/topo → interior/base).

    layers = [{"function": "finish1", "material": "Reboco externo", "thickness_m": 0.025},
              {"function": "structure", "material": "Bloco cerâmico", "thickness_m": 0.14},
              {"function": "finish2", "material": "Reboco interno", "thickness_m": 0.015}]
    function da camada: structure, substrate, insulation, finish1, finish2, membrane.
    kind: wall, floor, ceiling, roof. function (paredes): Exterior ou Interior.
    Materiais inexistentes são criados com o nome informado.
    """
    return await _rpc("create_layered_type", **_clean(
        name=name, layers=layers, kind=kind, base_type=base_type, function=function,
        create_materials=create_materials))


# --- Ambientes e áreas ----------------------------------------------------------

@mcp.tool()
async def get_room_report(level: Optional[str] = None) -> dict:
    """Relatório de ambientes: área, perímetro, volume, pé-direito, acabamentos,
    portas e janelas por ambiente, totais por nível e problemas (não posicionado,
    não delimitado, sem porta) com a correção sugerida."""
    return await _rpc("get_room_report", **_clean(level=level))


@mcp.tool()
async def list_area_schemes() -> list:
    """Lista esquemas de área (ex.: Área bruta, Área locável) e as plantas de área de cada um."""
    return await _rpc("list_area_schemes")


@mcp.tool()
async def create_area_plan(level: Optional[str] = None, scheme: Optional[str] = None,
                           name: Optional[str] = None) -> dict:
    """Cria planta de área de um nível no esquema indicado (padrão: área bruta).

    Fluxo do quadro de áreas: create_area_plan → create_area_boundaries → place_areas → get_area_report.
    """
    return await _rpc("create_area_plan", **_clean(level=level, scheme=scheme, name=name))


@mcp.tool()
async def create_area_boundaries(area_view_id: int, source: str = "lines",
                                 lines: Optional[list[dict]] = None, boundary: str = "finish") -> dict:
    """Cria linhas de limite de área numa planta de área.

    source="lines" usa lines=[{x1_m,y1_m,x2_m,y2_m}]; source="rooms" copia os contornos
    dos ambientes do nível (boundary: finish, center ou core), sem duplicar divisas.
    """
    return await _rpc("create_area_boundaries", **_clean(
        area_view_id=area_view_id, source=source, lines=lines, boundary=boundary))


@mcp.tool()
async def place_areas(area_view_id: int, from_rooms: bool = False,
                      points: Optional[list[dict]] = None, names: Optional[list[str]] = None) -> dict:
    """Posiciona áreas nos points [{x_m,y_m}] (com names opcionais) ou em cada ambiente
    do nível (from_rooms=True, copiando nome e número). Retorna a área calculada de cada uma."""
    return await _rpc("place_areas", **_clean(area_view_id=area_view_id, from_rooms=from_rooms, points=points, names=names))


@mcp.tool()
async def get_area_report(scheme: Optional[str] = None) -> dict:
    """Quadro de áreas por esquema e nível, com cada área (nome, número, m²) e totais."""
    return await _rpc("get_area_report", **_clean(scheme=scheme))

# --- Organização e documentação ---------------------------------------------

@mcp.tool()
async def create_shaft_opening(points: list[dict], bottom_level: str, top_level: str,
                               base_offset_m: float = 0, top_offset_m: float = 0) -> dict:
    """Cria shaft (abertura vertical) pelo contorno points [{x_m,y_m}] entre dois níveis,
    cortando lajes, forros e coberturas (poços de elevador, dutos, escadas)."""
    return await _rpc("create_shaft_opening", points=points, bottom_level=bottom_level, top_level=top_level,
                      base_offset_m=base_offset_m, top_offset_m=top_offset_m)


@mcp.tool()
async def dimension_grids(view_id: Optional[int] = None, offset_m: float = 1.5, overall: bool = True) -> dict:
    """Cota automaticamente os eixos de uma planta: uma corrente por direção e, com overall=True,
    a cota total. Retorna os valores de cada segmento em metros."""
    return await _rpc("dimension_grids", **_clean(view_id=view_id, offset_m=offset_m, overall=overall))


@mcp.tool()
async def pin_elements(ids: Optional[list[int]] = None, categories: Optional[list[str]] = None,
                       pinned: bool = True) -> dict:
    """Fixa (ou libera, pinned=False) elementos para evitar deslocamentos acidentais.
    Ex.: categories=["Grids", "Levels"] após definir eixos e níveis."""
    return await _rpc("pin_elements", **_clean(ids=ids, categories=categories, pinned=pinned))


@mcp.tool()
async def set_selection(ids: list[int], zoom: bool = True) -> dict:
    """Seleciona elementos na interface do Revit (e enquadra com zoom=True) para o usuário revisar."""
    return await _rpc("set_selection", ids=ids, zoom=zoom)


@mcp.tool()
async def get_element_relationships(element_id: int) -> dict:
    """Relações de um elemento: hospedeiro, elementos hospedados (portas/janelas da parede),
    elementos unidos, ambiente(s) (de/para em portas), grupo, workset, fases, opção de projeto
    e quantos dependentes seriam excluídos junto."""
    return await _rpc("get_element_relationships", element_id=element_id)


@mcp.tool()
async def list_view_templates() -> list:
    """Lista templates de vista com tipo, escala, nível de detalhe e quantas vistas usam cada um."""
    return await _rpc("list_view_templates")


@mcp.tool()
async def create_view_template(name: str, view_id: Optional[int] = None, apply_to_source: bool = False) -> dict:
    """Cria template de vista a partir de uma vista configurada (ativa por padrão), para padronizar plantas, cortes e elevações."""
    return await _rpc("create_view_template", **_clean(name=name, view_id=view_id, apply_to_source=apply_to_source))


@mcp.tool()
async def list_design_options() -> dict:
    """Lista opções de projeto (conjunto, principal, ativa) para estudos de alternativas."""
    return await _rpc("list_design_options")

# --- Modelagem --------------------------------------------------------------------

@mcp.tool()
async def flip_instances(ids: list[int], hand: bool = False, facing: bool = False) -> dict:
    """Inverte portas/janelas: hand=True troca o lado da dobradiça/abertura;
    facing=True troca o sentido (abre para dentro/fora)."""
    return await _rpc("flip_instances", ids=ids, hand=hand, facing=facing)


@mcp.tool()
async def create_floor_opening(host_id: int, points: list[dict], perpendicular: bool = True) -> dict:
    """Cria abertura num piso, forro ou cobertura pelo contorno points [{x_m,y_m}]
    (escadas, vazios de pé-direito duplo, passagens de prumadas)."""
    return await _rpc("create_floor_opening", host_id=host_id, points=points, perpendicular=perpendicular)


@mcp.tool()
async def join_geometry(mode: str = "join", pairs: Optional[list[list[int]]] = None,
                        ids_a: Optional[list[int]] = None, categories_b: Optional[list[str]] = None) -> dict:
    """Une, separa ou inverte a prioridade de geometria entre elementos (ex.: laje x paredes).

    mode: join, unjoin ou switch. Use pairs=[[id_a, id_b], ...] ou, para unir
    automaticamente tudo o que toca, ids_a=[laje] e categories_b=["Walls"].
    """
    return await _rpc("join_geometry", **_clean(mode=mode, pairs=pairs, ids_a=ids_a, categories_b=categories_b))


@mcp.tool()
async def attach_walls(wall_ids: list[int], target_id: int, location: str = "top",
                       detach: bool = False) -> dict:
    """Anexa o topo (ou a base, location="base") de paredes a uma cobertura, piso ou forro,
    acompanhando inclinações. detach=True remove o vínculo."""
    return await _rpc("attach_walls", wall_ids=wall_ids, target_id=target_id, location=location, detach=detach)


@mcp.tool()
async def set_true_north(angle_deg: float) -> dict:
    """Define o ângulo do norte verdadeiro em relação ao norte do projeto (graus, anti-horário positivo)."""
    return await _rpc("set_true_north", angle_deg=angle_deg)

# =============================================================================
# Anotações MCP — leitura x escrita x destrutivo
# =============================================================================
#
# Clientes MCP usam estas dicas para pedir confirmação só quando a ferramenta
# altera o modelo (readOnlyHint=False) e para destacar operações que
# sobrescrevem ou removem dados (destructiveHint=True).

from mcp.types import ToolAnnotations  # noqa: E402

READ_ONLY_PREFIXES = ("list_", "get_", "find_", "analyze_", "validate_", "preview_", "audit_")
READ_ONLY_TOOLS = {"ping", "bridge_health", "plan_batch"}
# Gravam arquivos fora do modelo, sem alterá-lo.
FILE_OUTPUT_PREFIXES = ("export_",)
FILE_OUTPUT_TOOLS = {"screenshot", "backup_document"}
# Sobrescrevem/removem dados existentes ou trocam o documento ativo.
DESTRUCTIVE_PREFIXES = ("delete_", "purge_", "set_", "modify_", "move_", "rotate_", "mirror_",
                        "align_", "resize_", "change_", "reset_", "override_", "reload_", "flip_",
                        "join_", "attach_")
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


def _annotate_tools() -> None:
    for tool in mcp._tool_manager.list_tools():
        tool.annotations = classify_tool(tool.name)


_annotate_tools()


# =============================================================================
# Ponto de entrada
# =============================================================================

async def _check() -> int:
    """Diagnóstico de linha de comando: python server.py --check."""
    print(f"Bridge: {BRIDGE_URL}")
    print(f"Token: {'encontrado' if _bridge_token() else 'AUSENTE'} "
          f"(procurado em REVIT_MCP_TOKEN, {CONFIG_PATH}, {WORKSPACE_CONFIG_PATH})")
    try:
        health = await bridge_health()
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
