# Novas ações implementadas — rodada "catálogo de 1010 ações"

> ⚠️ **Implementado mas não testado contra o Revit** — todo o código desta
> rodada (Python + C#) foi escrito e revisado por leitura/comparação linha a
> linha com os comandos vizinhos, mas sem acesso a uma instância real do
> Revit nesta sessão. Nenhuma das ferramentas abaixo foi executada contra um
> documento `.rvt` de verdade. Uma sessão com Revit instalado precisa validar
> cada uma antes de uso em produção (abrir um projeto, chamar a ferramenta,
> conferir o resultado e o estado do modelo).

## Contexto

Este documento registra o resultado de mapear o catálogo de 1010 ações
(`catalogo_mcp_revit_mais_de_1000_acoes.txt`) contra o inventário de
ferramentas já existente no servidor, identificar lacunas reais de
capacidade (não apenas nomes diferentes de algo que já existe) e implementar
o maior número possível delas com qualidade, amplitude e consistência de
padrão — em vez de tentar cobrir os 1010 itens numerados um a um.

Foram criadas **24 ferramentas novas**, cobrindo 10 das 36 seções do
catálogo. Cada uma tem uma função Python (`@mcp.tool()`) e o comando C#
correspondente, registrado no mesmo encadeamento de dispatch já usado pelo
projeto (`Commands.cs` → `CommandsPro.cs` → `CommandsArch.cs` →
`CommandsExtra.cs`, este último criado nesta rodada).

Arquivos alterados/criados:

- `server/revit_mcp/tools_editing.py` (10 ferramentas: geometria 2D, linha de
  modelo, região mascarada, desagrupar, copiar parâmetros, estilos/padrões)
- `server/revit_mcp/tools_arch.py` (5 ferramentas: níveis, eixos, plano de
  referência, ponto base, auditoria de níveis/eixos)
- `server/revit_mcp/tools_pro.py` (9 ferramentas: parâmetro por GUID, link,
  consultas espaciais, unidades do projeto, nomenclatura, inventário)
- `addin/CommandsExtra.cs` (**novo arquivo**: implementação C# das 24
  ferramentas, com seu próprio `ExecutarComandoExtra` no mesmo padrão de
  `ExecutarComandoPro`/`ExecutarComandoArch`)
- `addin/CommandsArch.cs` (1 linha: o `default` do switch agora chama
  `ExecutarComandoExtra` em vez de lançar "Comando desconhecido")

---

## 1. Geometria 2D, linhas e figuras (seção 2 do catálogo)

| Ferramenta Python | Comando C# | Descrição | Itens do catálogo |
|---|---|---|---|
| `create_detail_arc` | `create_detail_arc` | Arco de detalhe (2D) na vista ativa, por centro, raio e ângulos em graus. | 048, 049 (parcial — por centro/ângulos, não por 3 pontos nem tangente) |
| `create_detail_circle` | `create_detail_circle` | Círculo de detalhe completo, por centro e raio (técnica de arco 0–2π fechado). | 046 |
| `create_detail_ellipse` | `create_detail_ellipse` | Elipse de detalhe completa, por centro, semieixos e rotação. | 051 |
| `create_detail_polygon` | `create_detail_polygon` | Polígono regular (3 a 24 lados) como linhas de detalhe fechadas. | 052, 053, 054 (casos particulares com `sides=3..n`) |
| `create_model_line` | `create_model_line` | Linha de **modelo** (geometria 3D real, não anotação 2D como `create_detail_line`), com plano de trabalho escolhido automaticamente. | 042, contribui para 079 |
| `create_masking_region` | `create_masking_region` | Região mascarada (oculta linhas atrás dela), usando ou criando um `FilledRegionType` com `IsMasking=true`. | 060 |

Não cobertos nesta rodada (ver seção "não implementados" abaixo): círculo por
3 pontos (047), arco tangente (050), polígono **irregular** por vértices
arbitrários (056 — `create_detail_lines` já permite desenhar qualquer
polilinha fechada manualmente, então a capacidade de fato já existe, só não
como uma ferramenta dedicada), spline (057), curvas/planos de referência de
família (058, 063 — contexto de Family Editor), linha de divisão de
superfície (065), operações de edição de linha existente (offset/trim/
extend/split/intersect — 066 a 071, que exigem selecionar e modificar uma
curva já desenhada, não apenas criar geometria nova) e validações de
contorno (073 a 077, que dependem de geometria arbitrária de entrada e
teriam valor limitado sem testes reais).

## 2. Transformações, cópia e edição (seção 3)

| Ferramenta Python | Comando C# | Descrição | Itens |
|---|---|---|---|
| `ungroup_elements` | `ungroup_elements` | Desagrupa uma instância de grupo de modelo (`Group.UngroupMembers`). | 092 |
| `copy_parameters` | `copy_parameters` | Copia valores de parâmetro de um elemento de origem para um ou mais destinos, por nome (todos os graváveis, ou uma lista específica). Reporta por elemento o que foi copiado e os erros (parâmetro ausente no destino, tipo de armazenamento incompatível). | 098 |

`099. Aplicar tipo a vários elementos` já é coberto pela ferramenta
pré-existente `change_element_type`, que aceita uma lista de IDs — não é uma
lacuna real.

## 3. Níveis, eixos, coordenadas e referências (seção 4)

| Ferramenta Python | Comando C# | Descrição | Itens |
|---|---|---|---|
| `rename_level` | `rename_level` | Renomeia um nível por `level_id` ou nome atual. | 117 |
| `set_level_elevation` | `set_level_elevation` | Altera a elevação de um nível, absoluta (`elevation_m`) ou relativa (`offset_m`). | 118 |
| `create_reference_plane` | `create_reference_plane` | Cria plano de referência entre dois pontos, visível numa vista (`Document.Create.NewReferencePlane2`). | 122 |
| `set_project_base_point` | `set_project_base_point` | Move o Ponto Base do Projeto ou o Ponto de Levantamento (`target="survey"`), via os parâmetros `BASEPOINT_*_PARAM`. | 127 (parcial — ponto base; ponto de levantamento como alvo opcional) |
| `audit_levels_grids` | `audit_levels_grids` | Lista níveis e eixos e reporta nomes duplicados, elevações duplicadas entre níveis e eixos sem nome. Somente leitura. | 138, 140 |

`125, 126, 128, 129, 130` (consultar coordenadas de projeto/compartilhadas,
ponto de levantamento, origem interna) já são cobertos pela ferramenta
pré-existente `get_project_location`. `131, 132` (norte verdadeiro/norte do
projeto) já são cobertos por `set_true_north`.

## 4. Estilos de linha, padrões e gráficos (seção 13)

| Ferramenta Python | Comando C# | Descrição | Itens |
|---|---|---|---|
| `list_line_styles` | `list_line_styles` | Lista as subcategorias de "Linhas" com peso de projeção e cor. | 361 |
| `list_fill_patterns` | `list_fill_patterns` | Lista os `FillPatternElement` do documento, com destino (desenho/modelo) e se é o preenchimento sólido. | 369 |

`362`–`368` (criar estilo/padrão de linha tracejado, pontilhado, misto) não
foram implementados: a API do Revit não expõe criação de novos
`LinePatternElement` por uma lista simples de segmentos sem risco relevante
de erro sem testes reais; ficou fora desta rodada.

## 5. Parâmetros, identidade e dados BIM (seção 17)

| Ferramenta Python | Comando C# | Descrição | Itens |
|---|---|---|---|
| `read_parameter_by_guid` | `read_parameter_by_guid` | Lê um parâmetro de um elemento pelo GUID do parâmetro compartilhado (`Element.get_Parameter(Guid)`), estável entre documentos mesmo que o nome mude. | 488 |

`492` (criar parâmetro compartilhado a partir de arquivo/configuração) não
foi implementado como ferramenta nova porque `create_project_parameter`
(pré-existente) já gerencia um arquivo de parâmetros compartilhados próprio
do MCP internamente; importar um arquivo externo arbitrário do usuário
exigiria uma superfície de risco (caminho de arquivo, parsing de formato)
maior do que o justificável nesta rodada.

## 6. Links, coordenação e interoperabilidade (seção 24)

| Ferramenta Python | Comando C# | Descrição | Itens |
|---|---|---|---|
| `get_link_info` | `get_link_info` | Caminho, status de carregamento e transformação (posição + rotação em Z) de um vínculo Revit, por ID de tipo ou de instância. | 720, 721 |

`717` (consultar modelos vinculados) já é coberto por `list_links`
pré-existente. `718, 719` (descarregar/recarregar vínculo) já são cobertos
por `reload_link(unload=True/False)` pré-existente.

## 7. Interferências, geometria e consultas espaciais (seção 28)

| Ferramenta Python | Comando C# | Descrição | Itens |
|---|---|---|---|
| `get_bounding_box` | `get_bounding_box` | Caixa delimitadora de um elemento: mínimo, máximo, centro e dimensões em metros (opcionalmente específica de uma vista). | 821 |
| `get_elements_near_point` | `get_elements_near_point` | Elementos cuja bounding box intersecta uma caixa cúbica ao redor de um ponto, ordenados por distância. | 829 |
| `get_elements_in_box` | `get_elements_in_box` | Elementos cuja bounding box intersecta uma caixa 3D definida por mínimos/máximos. | 830 |

`822`–`828` (faces/arestas, volume, área de superfície, centro geométrico,
orientação local, transformação do elemento) exigiriam percorrer
`Element.get_Geometry()` com `Options` e lidar com `Solid`/`Face`/`Edge` de
forma genérica por categoria — já existe lógica especializada de geometria
em `Commands.cs` para casos específicos (paredes, pisos), mas generalizar
com segurança para qualquer elemento, sem poder testar no Revit, foi
considerado risco alto demais para esta rodada.

## 8. Configurações gerais do Revit e padrões do projeto (seção 20)

| Ferramenta Python | Comando C# | Descrição | Itens |
|---|---|---|---|
| `get_project_units` | `get_project_units` | Unidade, símbolo e precisão de exibição de comprimento, área, volume e ângulo do projeto. | 593 |
| `set_project_units` | `set_project_units` | Define as unidades de exibição (comprimento/área/volume/ângulo) e/ou a precisão de cada uma, usando a API `ForgeTypeId`/`FormatOptions`. | 594, 595, 596, 597 |

## 9. API, diagnóstico e inventário do modelo (seção 32)

| Ferramenta Python | Comando C# | Descrição | Itens |
|---|---|---|---|
| `get_model_inventory` | `get_model_inventory` | Inventário do documento: arquivo, caminho, worksharing, contagem de elementos por categoria de modelo, níveis, vistas, pranchas, famílias, materiais, worksets e vínculos. Somente leitura. | 932, 933, 945, 946 |

## 10. Padronização de nomes e governança BIM (seção 33)

| Ferramenta Python | Comando C# | Descrição | Itens |
|---|---|---|---|
| `validate_naming` | `validate_naming` | Valida nomes de vistas, pranchas, famílias, tipos, materiais, filtros, templates e worksets contra um padrão regex (.NET), caracteres proibidos e/ou tamanho máximo. Somente leitura. | 951, 952, 954, 955, 956, 957, 958, 959, 961 |

`953` (validar número de folha especificamente) não é coberto por
`validate_naming` (que valida o nome `ViewSheet.Name`, não o número da
folha) — pode ser conferido hoje com `find_elements`/`list_sheets` e uma
regra aplicada no lado do cliente.

---

## Itens do catálogo cobertos por ferramentas pré-existentes

Resumo por seção, para deixar claro o que **já estava resolvido** antes
desta rodada e não foi reimplementado:

- **1. Interface e documentos**: `open_document`, `new_project`, `save_document`,
  `save_as`, `get_info`, `bridge_health`, `get_selection`, `set_selection`,
  `find_elements`/`list_elements` (seleção por categoria/tipo/parâmetro),
  `get_worksharing_status`.
- **2. Geometria 2D**: `create_detail_line(s)`, `create_detail_rectangle`,
  `create_filled_region` já cobriam linha/retângulo/região preenchida.
- **3. Transformações**: `move_element`, `copy_element`, `rotate_element`,
  `mirror_element`, `align_elements`, `array_elements`, `create_group`,
  `pin_elements`, `change_element_type` (em massa), `join_geometry`,
  `delete_element`/`preview_delete`.
- **5–8. Paredes/pisos/estrutura/escadas**: `create_wall(s)`, `duplicate_type`,
  `create_layered_type`, `attach_walls`, `set_wall_join`, `create_door`,
  `create_window`, `create_opening`/`create_shaft_opening`, `create_floor`,
  `create_floor_opening`, `create_ceiling(s_from_rooms)`, `create_roof`/
  `modify_roof`, `create_column`, `create_beam`, `create_foundation`,
  `create_stairs`, `create_railing`.
- **9. Ambientes/áreas/MEP spaces**: `place_rooms(_at)`, `get_room_report`,
  `create_area_plan`/`create_area_boundaries`/`place_areas`/`get_area_report`,
  `create_spaces_from_rooms`, `create_room_separators`, `tag_rooms`.
- **10–11. Vistas e 3D**: `create_floor_plan`, `create_section_view`,
  `create_elevations`, `create_3d_view`, `set_camera`, `set_section_box`,
  `set_visual_style`, `duplicate_view`, `set_view_properties`,
  `set_view_template`/`create_view_template`, `apply_view_filter`/
  `create_rule_filter`, `override_elements_in_view`, `set_category_visibility`.
- **12. Materiais**: `list_materials`, `create_material`, `set_material`,
  `remove_paint`.
- **14–15. Texto/cotas/tags**: `create_text`, `create_dimension`,
  `tag_elements`/`tag_rooms`, `create_section_mark`, `create_spot_elevation`.
- **16. Famílias**: `load_family`, `list_families`, `place_family_instance`/
  `place_component`, `create_furniture_family`.
- **17. Parâmetros**: `modify_parameters`, `set_parameters_bulk`,
  `create_project_parameter`/`list_project_parameters`.
- **18. Tabelas**: `create_schedule(_advanced)`, `get_schedule_data`,
  `export_schedule_csv`, `analyze_quantities`, `analyze_material_takeoff`.
- **19. Folhas/revisões**: `create_sheet(s)`, `list_titleblocks`,
  `create_revision`/`add_revision_to_sheets`, `export_sheets_pdf/dwg`.
- **24. Links**: `list_links`, `load_revit_link`, `reload_link`, `import_cad`.
- **25. Fases/opções**: `list_phases`, `set_element_phase`,
  `list_design_options`.
- **26. Worksharing**: `create_workset`, `set_element_workset`,
  `get_worksharing_status`, `get_element_worksharing_info`.
- **27. Auditoria**: `validate_document`, `analyze_bim_quality`,
  `audit_project_setup`, `audit_code_compliance`, `audit_documentation`,
  `audit_hosted_cuts`, `list_model_warnings`, `list_audit_log`.
- **28. Interferências**: `analyze_clashes`.
- **29. Automação/lote**: `plan_batch`/`execute_batch` (dry-run, lote com
  isolamento de falha por item).
- **30. Exportação**: `export_pdf`, `export_dwg`, `export_ifc`,
  `export_images`.
- **36. Protocolo do servidor**: tratado pela arquitetura existente
  (`core.py`: validação de schema via tipos Python, `_rpc`, timeouts,
  `bridge_health`, logs separados stdout/stderr).

---

## Itens ainda não implementados / fora de alcance nesta rodada

Com justificativa honesta do motivo:

- **Armadura/rebar estrutural (209–215)**: a API `Rebar`/`RebarBarType`/
  `RebarShape` é notoriamente sensível a versão e a contexto do elemento
  hospedeiro (viga/pilar/laje), com várias sobrecargas e pré-condições não
  triviais. Implementar "no escuro" arriscaria produzir código que nunca
  cria a armadura corretamente ou que quebra o build por uma assinatura
  errada. Requer uma sessão com Revit real para iterar.
- **Rampas (188, 189, 236)**: diferente de `Stairs` (que tem uma API de
  criação por componente relativamente direta, já usada em `create_stairs`),
  `Ramp` no Revit é modelado via esboço (sketch) sem um equivalente simples
  de "criar por dois pontos"; replicar isso com segurança exigiria testes
  reais que não foram possíveis aqui.
- **Geometria paramétrica de Family Editor (455–480)**: extrusão, revolução,
  varredura, blend, parâmetros de família com fórmulas — tudo isso roda num
  `Document` de família (não de projeto), com uma API própria
  (`FamilyManager`, `FamilyItemFactory`) substancialmente diferente da usada
  no resto deste add-in. Fora do alcance desta rodada por escopo e risco.
- **Criar fase / criar opção de projeto (742, 750)**: a criação programática
  de novas fases e de novas opções de projeto (diferente de *consultar* ou
  *atribuir* fase/opção, que já são cobertos) depende de membros da API cuja
  existência e assinatura exatas não puderam ser confirmadas com confiança
  suficiente nesta sessão; preferiu-se não implementar a arriscar quebrar o
  build do add-in com um nome de método incorreto.
- **Cotas angulares, radiais, de diâmetro e de comprimento de arco
  (423–426)**: diferente de uma cota linear entre pontos livres, essas
  variantes exigem `Reference`s reais de arestas/curvas de elementos já
  modelados (não pontos XYZ arbitrários), e o comportamento varia por
  categoria. Implementar isso de forma genérica e confiável exigiria
  validação iterativa no Revit real.
- **Sincronização com central / worksharing colaborativo real (769–774)**:
  depende de uma sessão workshared ativa, rede e, possivelmente, outro
  usuário com elementos emprestados — não é simulável nem testável fora do
  Revit.
- **Renderização fotorrealística avançada (908–917)**: motor de render,
  iluminação, ambiente/sol e exportação de imagem final de alta qualidade
  dependem de assets e do motor de render do Revit; `export_images` e
  `set_visual_style`/`set_view_shaded` (pré-existentes) já cobrem a
  visualização básica, mas configuração fina de render fica fora do alcance.
- **COBie e fluxos de interoperabilidade especializados (739)**: depende de
  um fluxo externo (plugin/relatório COBie) não presente neste add-in.
- **Exportação DXF/DWF dedicada (737, 738)**: `export_dwg`/`export_ifc`
  pré-existentes cobrem os formatos mais usados; variantes DXF/DWF teriam
  valor incremental menor e foram deixadas de fora para priorizar amplitude
  em outras seções.
- **Criação de novos estilos/padrões de linha (362–368)**: ver seção 4 acima.
- **Círculo por 3 pontos, arco tangente, polígono irregular por vértices
  arbitrários, spline (047, 050, 056, 057)**: variantes de entrada das
  ferramentas de geometria 2D já implementadas; não implementadas
  individualmente para não multiplicar ferramentas quase-duplicadas (ver
  nota de "amplitude > profundidade" na seção 1 acima). Um polígono
  irregular, por exemplo, já pode ser desenhado hoje com
  `create_detail_lines` passando os segmentos manualmente.
