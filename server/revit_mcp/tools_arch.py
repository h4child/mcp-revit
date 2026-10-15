"""Arquitetura: tipos, ambientes/áreas, organização, modelagem e produção."""

from typing import Optional

from . import normas
from .core import _clean, _rpc, mcp


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
async def audit_code_compliance(municipio: str = "paracatu-mg", level: Optional[str] = None,
                                aberturas_extras: Optional[dict] = None) -> dict:
    """Confere o modelo com o Código de Obras do município (somente leitura).

    Por ambiente (classificado pelo nome): área e dimensão mínimas, vão de
    iluminação/ventilação (fração da área do piso), janelas fixas, acesso e
    pé-direito. Gerais: banheiro principal, altura e largura de portas, escadas
    (espelho, piso, Blondel, patamar) e depósito de lixo. Cada item traz status
    (ok/falha/atencao), valor exigido, valor do projeto e artigo da lei.

    municipio: slug cadastrado (ver resource revit://normas).
    aberturas_extras: {ambiente: m²} para panos de vidro e portas envidraçadas que
        iluminam o ambiente mas não são janelas no Revit, ex.: {"T09": 15.6}.
    Regras e artigos: revit://normas/municipios/{municipio}.
    """
    regras = normas.carregar_regras(municipio)
    relatorio = await _rpc("get_room_report", **_clean(level=level))
    janelas = await _rpc("list_windows")
    portas = await _rpc("find_elements", **_clean(category="Doors", level=level,
                                                  return_parameters=normas.DOOR_PARAMS, limit=2000))
    escadas = await _rpc("find_elements", **_clean(category="Stairs", level=level,
                                                   return_parameters=normas.STAIR_PARAMS, limit=200))
    return normas.avaliar(regras, relatorio, janelas, portas, escadas, aberturas_extras)


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

# --- Produção ---------------------------------------------------------------------

@mcp.tool()
async def renumber_elements(category: str, prefix: str = "", start: int = 1, digits: int = 2,
                            order: str = "position", restart_per_level: bool = False,
                            level: Optional[str] = None, parameter: Optional[str] = None,
                            dry_run: bool = False) -> dict:
    """Numera elementos em sequência e mostra valor anterior e novo de cada um.

    Ambientes usam Número; demais categorias usam Marca (ou parameter).
    order="position": por nível e posição na planta (cima→baixo, esquerda→direita);
    order="room": portas/janelas recebem o número do ambiente (101, 101a, 101b...).
    Ex.: portas P01..Pnn → category="Doors", prefix="P"; ambientes do 1º pavimento
    101.. → category="Rooms", prefix="1", level="Pavimento 1". dry_run=True só simula.
    """
    return await _rpc("renumber_elements", **_clean(
        category=category, prefix=prefix, start=start, digits=digits, order=order,
        restart_per_level=restart_per_level, level=level, parameter=parameter, dry_run=dry_run))


@mcp.tool()
async def export_images(view_ids: Optional[list[int]] = None, folder: Optional[str] = None,
                        pixels: int = 2400, format: str = "png", prefix: Optional[str] = None) -> dict:
    """Exporta vistas/pranchas como imagens (PNG ou JPG) em lote para apresentações,
    retornando os arquivos gerados. Padrão: vista ativa."""
    return await _rpc("export_images", **_clean(view_ids=view_ids, folder=folder, pixels=pixels, format=format, prefix=prefix))


# --- Auditoria -------------------------------------------------------------------

@mcp.tool()
async def audit_project_setup() -> dict:
    """Checklist do projeto arquitetônico com nota de 0 a 100: informações do projeto,
    arquivo salvo, norte, níveis/plantas/eixos fixados, paredes com tipos definidos,
    pisos, cobertura, esquadrias, avisos, ambientes (delimitados, únicos, nomeados,
    acabamentos), quadro de áreas, numeração de portas/janelas, cortes, elevações,
    pranchas, tabelas e revisões. Cada item pendente indica a ferramenta que corrige."""
    return await _rpc("audit_project_setup")
