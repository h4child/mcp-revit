"""Informações, projeto, criação de elementos, pisos/coberturas/forros e visualização."""

from typing import Optional

from .core import _clean, _health, _rpc, mcp


@mcp.tool()
async def bridge_health() -> dict:
    """Verifica saúde e autenticação do bridge local sem modificar o projeto."""
    return await _health()


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
async def set_material(id: int, material: str, side: str = "all") -> dict:
    """
    Aplica um material (acabamento) a um elemento existente, pintando suas faces.
    Funciona em paredes, pisos, telhados, forros e móveis.

    id       — ID do elemento (obtido em list_walls, create_floor, etc)
    material — nome do material (busca parcial), ex: "Madeira", "Cerâmica",
               "Concreto", "Vidro", "Tijolo"
    side     — qual face pintar, sem dividir o elemento em dois (o Revit suporta
               material por face nativamente): "all" (padrão, pinta tudo — mesmo
               comportamento de antes), "exterior"/"interior" em paredes,
               "top"/"bottom" em pisos, forros e coberturas. Use para dar um
               acabamento à vista interna de uma parede sem pintar a fachada
               externa (ou o ambiente do outro lado) junto.

    Muda a aparência em vistas sombreadas/realistas sem afetar outros elementos.
    Exemplo: set_material(id=298787, material="Azulejo cerâmico", side="interior")
    """
    return await _rpc("set_material", **_clean(id=id, material=material, side=side))


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
