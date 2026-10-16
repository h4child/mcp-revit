"""Manipulação, anotação, modelagem avançada, componentes, pranchas, exportação e configuração."""

from typing import Optional

from .core import _clean, _rpc, mcp


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
# ONDA 8 — GEOMETRIA 2D DE DETALHE, LINHA DE MODELO E PADRÕES GRÁFICOS
# =============================================================================
#
# ⚠️ Ferramentas novas desta rodada (lacunas do catálogo de 1010 ações).
# Implementadas sem acesso a uma instância real do Revit nesta sessão — ver
# docs/NOVAS_ACOES_IMPLEMENTADAS.md antes de usar em produção.

@mcp.tool()
async def create_detail_arc(center_x_m: float, center_y_m: float, radius_m: float,
                            start_angle_deg: float = 0, end_angle_deg: float = 180) -> dict:
    """Cria arco de detalhe (2D) na vista ativa, por centro, raio e ângulos
    em graus (sentido anti-horário a partir do eixo X)."""
    return await _rpc("create_detail_arc", center_x_m=center_x_m, center_y_m=center_y_m,
                      radius_m=radius_m, start_angle_deg=start_angle_deg, end_angle_deg=end_angle_deg)


@mcp.tool()
async def create_detail_circle(center_x_m: float, center_y_m: float, radius_m: float) -> dict:
    """Cria círculo de detalhe (2D) completo na vista ativa, por centro e raio."""
    return await _rpc("create_detail_circle", center_x_m=center_x_m, center_y_m=center_y_m, radius_m=radius_m)


@mcp.tool()
async def create_detail_ellipse(center_x_m: float, center_y_m: float, radius_x_m: float,
                                radius_y_m: float, rotation_deg: float = 0) -> dict:
    """Cria elipse de detalhe (2D) completa na vista ativa, por centro e semieixos."""
    return await _rpc("create_detail_ellipse", center_x_m=center_x_m, center_y_m=center_y_m,
                      radius_x_m=radius_x_m, radius_y_m=radius_y_m, rotation_deg=rotation_deg)


@mcp.tool()
async def create_detail_polygon(center_x_m: float, center_y_m: float, radius_m: float,
                                sides: int = 6, rotation_deg: float = 0) -> dict:
    """Cria polígono regular (3 a 24 lados) como linhas de detalhe fechadas na vista ativa.
    radius_m é o raio do círculo circunscrito (centro a cada vértice)."""
    return await _rpc("create_detail_polygon", center_x_m=center_x_m, center_y_m=center_y_m,
                      radius_m=radius_m, sides=sides, rotation_deg=rotation_deg)


@mcp.tool()
async def create_model_line(x1_m: float, y1_m: float, x2_m: float, y2_m: float,
                            z1_m: float = 0, z2_m: float = 0) -> dict:
    """Cria linha de modelo (geometria 3D real, não anotação) entre dois pontos.

    Diferente de create_detail_line/create_detail_lines (2D, só aparecem na vista
    onde foram criadas): a linha de modelo existe no espaço 3D do projeto e
    aparece em qualquer vista que a corte ou projete. Um plano de trabalho
    vertical contendo a linha é escolhido automaticamente.
    """
    return await _rpc("create_model_line", x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m, z1_m=z1_m, z2_m=z2_m)


@mcp.tool()
async def create_masking_region(x1_m: float, y1_m: float, x2_m: float, y2_m: float) -> dict:
    """Cria região mascarada (oculta linhas atrás dela) num retângulo da vista ativa.

    Diferente de create_filled_region (hachura visível): usa ou cria um tipo
    de região preenchida marcado como máscara (sem padrão gráfico aparente).
    """
    return await _rpc("create_masking_region", x1_m=x1_m, y1_m=y1_m, x2_m=x2_m, y2_m=y2_m)


@mcp.tool()
async def ungroup_elements(group_id: int) -> dict:
    """Desagrupa uma instância de grupo de modelo (por ID da instância), liberando os
    elementos membros para edição individual. Veja create_group, list_groups, place_group."""
    return await _rpc("ungroup_elements", group_id=group_id)


@mcp.tool()
async def copy_parameters(source_id: int, target_ids: list[int], parameters: Optional[list[str]] = None) -> dict:
    """Copia valores de parâmetro de um elemento de origem para um ou mais elementos de destino.

    parameters: nomes específicos a copiar (padrão: todos os parâmetros de
    instância graváveis e com valor na origem). Parâmetros que não existem no
    destino, ou com tipo de armazenamento diferente, são reportados em 'erros'
    sem interromper a cópia dos demais.
    """
    return await _rpc("copy_parameters", source_id=source_id, target_ids=target_ids, **_clean(parameters=parameters))


@mcp.tool()
async def list_line_styles() -> dict:
    """Lista os estilos de linha (subcategorias de 'Linhas') com peso de projeção e cor."""
    return await _rpc("list_line_styles")


@mcp.tool()
async def list_fill_patterns() -> dict:
    """Lista os padrões de preenchimento (hachuras) do documento, com destino
    (desenho ou modelo) e se é o preenchimento sólido."""
    return await _rpc("list_fill_patterns")
