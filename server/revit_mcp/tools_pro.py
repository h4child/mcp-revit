"""Fluxos profissionais: dados do projeto, vistas, documentação, coordenação, estrutura e MEP."""

from typing import Optional

from .core import _clean, _rpc, mcp


# =============================================================================
# FLUXOS PROFISSIONAIS — dados, documentação, coordenação, estrutura e MEP
# =============================================================================
#
# Unidades: comprimentos em metros, áreas em m², volumes em m³, ângulos em
# graus. Parâmetros de outras grandezas usam a unidade de exibição do projeto
# (ou texto, ex.: "25 mm", interpretado pelo Revit).



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
