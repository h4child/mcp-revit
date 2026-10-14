// ============================================================================
// CommandsArch.cs — Ferramentas de projeto arquitetônico
// ============================================================================
//
// Recursos que o fluxo de arquitetura exige e que não cabiam nos módulos
// anteriores:
//
//   • Tipos: listar com camadas e dimensões, duplicar com novos valores,
//     trocar o tipo de instâncias e criar tipos de parede/piso/forro/
//     cobertura por camadas (CompoundStructure).
//   • Áreas: relatório de ambientes (áreas, acabamentos, portas, problemas),
//     planos de área, limites, áreas e quadro de áreas por esquema/nível.
//   • Organização: shafts, cotas de eixos, fixar elementos, seleção na
//     interface, relações de um elemento, templates de vista e opções de projeto.
//   • Modelagem: inverter portas/janelas, abertura em laje, unir geometria,
//     anexar topo/base de paredes e norte verdadeiro.
//   • Produção: numeração automática e exportação de imagens em lote.
//
// Regra de estabilidade (ver d618f8e): nunca encadear LINQ diretamente sobre
// coleções antigas da API (ParameterSet, PhaseArray, *Array, *Set); usar
// foreach. Coleções .NET (IList/ICollection, coletores) são seguras.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace RevitMcp
{
    public static partial class Commands
    {
        private static object ExecutarComandoArch(UIApplication uiApp, RpcRequest req)
        {
            var a = req.args;
            switch (req.cmd)
            {
                // --- Tipos ---
                case "list_element_types":   return ListElementTypes(uiApp, a);
                case "duplicate_type":       return DuplicateType(uiApp, a);
                case "change_element_type":  return ChangeElementType(uiApp, a);
                case "create_layered_type":  return CreateLayeredType(uiApp, a);

                // --- Ambientes e áreas ---
                case "get_room_report":      return GetRoomReport(uiApp, a);
                case "list_area_schemes":    return ListAreaSchemes(uiApp);
                case "create_area_plan":     return CreateAreaPlan(uiApp, a);
                case "create_area_boundaries": return CreateAreaBoundaries(uiApp, a);
                case "place_areas":          return PlaceAreas(uiApp, a);
                case "get_area_report":      return GetAreaReport(uiApp, a);

                // --- Organização e documentação ---
                case "create_shaft_opening": return CreateShaftOpening(uiApp, a);
                case "dimension_grids":      return DimensionGrids(uiApp, a);
                case "pin_elements":         return PinElements(uiApp, a);
                case "set_selection":        return SetSelection(uiApp, a);
                case "get_element_relationships": return GetElementRelationships(uiApp, a);
                case "list_view_templates":  return ListViewTemplates(uiApp);
                case "create_view_template": return CreateViewTemplate(uiApp, a);
                case "list_design_options":  return ListDesignOptions(uiApp);

                // --- Modelagem ---
                case "flip_instances":       return FlipInstances(uiApp, a);
                case "create_floor_opening": return CreateFloorOpening(uiApp, a);
                case "join_geometry":        return JoinGeometry(uiApp, a);
                case "attach_walls":         return AttachWalls(uiApp, a);
                case "set_true_north":       return SetTrueNorth(uiApp, a);

                // --- Produção ---
                case "renumber_elements":    return RenumberElements(uiApp, a);
                case "export_images":        return ExportImages(uiApp, a);

                default:
                    throw new ArgumentException($"Comando desconhecido: {req.cmd}");
            }
        }

        // =====================================================================
        // TIPOS
        // =====================================================================

        private static double? ArchLen(Element e, params BuiltInParameter[] bips)
        {
            foreach (var bip in bips)
            {
                try
                {
                    var p = e.get_Parameter(bip);
                    if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 0)
                        return Math.Round(FtToM(p.AsDouble()), 4);
                }
                catch { }
            }
            return null;
        }

        private static List<object> ArchLayers(Document doc, CompoundStructure cs)
        {
            var layers = new List<object>();
            if (cs == null) return layers;
            var list = cs.GetLayers();
            for (int i = 0; i < list.Count; i++)
            {
                var layer = list[i];
                layers.Add(new
                {
                    indice = i,
                    funcao = layer.Function.ToString(),
                    material = layer.MaterialId != ElementId.InvalidElementId ? doc.GetElement(layer.MaterialId)?.Name : "<Por categoria>",
                    espessura_m = Math.Round(FtToM(layer.Width), 4),
                    nucleo = i >= cs.GetFirstCoreLayerIndex() && i <= cs.GetLastCoreLayerIndex(),
                    estrutural = i == cs.StructuralMaterialIndex
                });
            }
            return layers;
        }

        /// <summary>Resumo de um tipo: dimensões principais, camadas e uso no modelo.</summary>
        private static object ArchDescribeType(Document doc, ElementType t, int instancias)
        {
            CompoundStructure cs = null;
            try { cs = (t as HostObjAttributes)?.GetCompoundStructure(); } catch { }
            string funcao = null;
            if (t is WallType wt)
            {
                try { funcao = wt.Function.ToString(); } catch { }
            }
            return new
            {
                id = t.Id.Value,
                categoria = t.Category?.Name,
                familia = t.FamilyName,
                nome = t.Name,
                instancias,
                tipo_parede = (t as WallType)?.Kind.ToString(),
                funcao,
                espessura_total_m = cs != null ? Math.Round(FtToM(cs.GetWidth()), 4) : ArchLen(t, BuiltInParameter.WALL_ATTR_WIDTH_PARAM),
                largura_m = ArchLen(t, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM),
                altura_m = ArchLen(t, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM),
                camadas = cs != null ? ArchLayers(doc, cs) : null
            };
        }

        private static object ListElementTypes(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var bic = ProCategory(GetS(args, "category"));
            string contains = GetS(args, "contains");
            var counts = new Dictionary<long, int>();
            foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                var tid = e.GetTypeId().Value;
                counts[tid] = counts.TryGetValue(tid, out var n) ? n + 1 : 1;
            }
            var alvo = contains == null ? null : Normalizar(contains);
            var tipos = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType()
                .Cast<ElementType>()
                .Where(t => alvo == null || Normalizar(t.FamilyName + " " + t.Name).Contains(alvo))
                .OrderBy(t => t.FamilyName).ThenBy(t => t.Name)
                .Select(t => ArchDescribeType(doc, t, counts.TryGetValue(t.Id.Value, out var n) ? n : 0))
                .ToList();
            return new
            {
                categoria = Category.GetCategory(doc, bic)?.Name ?? bic.ToString(),
                quantidade = tipos.Count,
                tipos,
                dica = tipos.Count == 0 ? "Nenhum tipo carregado nesta categoria. Carregue uma família (load_family) ou use outro template." : null
            };
        }

        private static ElementType ArchResolveType(Document doc, JsonElement args, string idKey = "type_id", string nameKey = "type_name")
        {
            if (ProHas(args, idKey, out var v) && v.ValueKind == JsonValueKind.Number)
                return doc.GetElement(new ElementId(v.GetInt64())) as ElementType
                    ?? throw new ArgumentException($"{idKey} {v.GetInt64()} não é um tipo.");
            string name = GetS(args, nameKey) ?? throw new ArgumentException($"Informe {idKey} ou {nameKey}.");
            var categoryName = GetS(args, "category");
            IEnumerable<ElementType> pool = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>();
            if (categoryName != null)
            {
                var bic = ProCategory(categoryName);
                pool = pool.Where(t => t.Category != null && t.Category.Id.Value == (long)bic);
            }
            var alvo = Normalizar(name);
            var list = pool.ToList();
            return list.FirstOrDefault(t => Normalizar(t.Name) == alvo)
                ?? list.FirstOrDefault(t => Normalizar(t.FamilyName + " " + t.Name) == alvo)
                ?? list.FirstOrDefault(t => Normalizar(t.Name).Contains(alvo))
                ?? throw new ArgumentException($"Tipo '{name}' não encontrado" + (categoryName != null ? $" em {categoryName}" : "") + ". Use list_element_types.");
        }

        private static object DuplicateType(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var source = ArchResolveType(doc, args);
            string newName = GetS(args, "new_name") ?? throw new ArgumentException("Argumento 'new_name' obrigatório.");
            var aplicados = new List<object>();
            ElementType created;
            using (var tx = ProTx(doc, "Duplicar tipo"))
            {
                created = source.Duplicate(newName);
                if (ProHas(args, "params", out var pmap) && pmap.ValueKind == JsonValueKind.Object)
                    foreach (var prop in pmap.EnumerateObject())
                    {
                        var erro = ProSetParam(doc, ProLookup(doc, created, prop.Name, includeType: false), prop.Value);
                        aplicados.Add(new { parametro = prop.Name, ok = erro == null, motivo = erro });
                    }
                tx.Commit();
            }
            return new
            {
                origem = new { id = source.Id.Value, nome = source.Name },
                tipo = ArchDescribeType(doc, created, 0),
                parametros = aplicados,
                proximo_passo = "Use change_element_type para aplicar o novo tipo a elementos existentes."
            };
        }

        private static object ChangeElementType(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var ids = ProIds(args, "ids", required: true);
            var target = ArchResolveType(doc, args);
            var resultado = new List<object>();
            int ok = 0;
            using (var tx = ProTx(doc, "Trocar tipo"))
            {
                foreach (var id in ids)
                {
                    var e = doc.GetElement(id);
                    string erro = null, anterior = null;
                    if (e == null) erro = "não existe";
                    else
                    {
                        anterior = ProTypeName(doc, e);
                        try
                        {
                            if (!e.IsValidType(target.Id)) erro = $"o tipo '{target.Name}' não é compatível com {e.Category?.Name}";
                            else e.ChangeTypeId(target.Id);
                        }
                        catch (Exception ex) { erro = ex.Message; }
                    }
                    if (erro == null) ok++;
                    resultado.Add(new { id = id.Value, ok = erro == null, tipo_anterior = anterior, motivo = erro });
                }
                tx.Commit();
            }
            return new { tipo_novo = new { id = target.Id.Value, nome = target.Name }, alterados = ok, falhas = ids.Count - ok, elementos = resultado };
        }

        private static MaterialFunctionAssignment ArchLayerFunction(string name)
        {
            switch ((name ?? "structure").Trim().ToLowerInvariant())
            {
                case "structure": case "estrutura": return MaterialFunctionAssignment.Structure;
                case "substrate": case "substrato": return MaterialFunctionAssignment.Substrate;
                case "insulation": case "isolamento": return MaterialFunctionAssignment.Insulation;
                case "finish1": case "acabamento1": case "finish_exterior": return MaterialFunctionAssignment.Finish1;
                case "finish2": case "acabamento2": case "finish": case "acabamento": return MaterialFunctionAssignment.Finish2;
                case "membrane": case "membrana": return MaterialFunctionAssignment.Membrane;
                case "deck": return MaterialFunctionAssignment.StructuralDeck;
                default: throw new ArgumentException($"Função de camada desconhecida: {name}. Use structure, substrate, insulation, finish1, finish2 ou membrane.");
            }
        }

        /// <summary>
        /// Cria um tipo de parede, piso, forro ou cobertura com camadas definidas
        /// (ordem: exterior/topo → interior/base). Materiais inexistentes são
        /// criados com o nome informado quando create_materials=true.
        /// </summary>
        private static object CreateLayeredType(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string kind = (GetS(args, "kind", "wall") ?? "wall").ToLowerInvariant();
            string name = GetS(args, "name") ?? throw new ArgumentException("Argumento 'name' obrigatório.");
            if (!ProHas(args, "layers", out var larr) || larr.ValueKind != JsonValueKind.Array || larr.GetArrayLength() == 0)
                throw new ArgumentException("Array 'layers' [{function, material, thickness_m}] obrigatório.");
            bool createMaterials = ProBool(args, "create_materials", true);

            HostObjAttributes baseType = kind switch
            {
                "wall" => ProFindType<WallType>(doc, GetS(args, "base_type"), t => t.Kind == WallKind.Basic),
                "floor" => ProFindType<FloorType>(doc, GetS(args, "base_type"), t => !t.IsFoundationSlab),
                "ceiling" => ProFindType<CeilingType>(doc, GetS(args, "base_type")),
                "roof" => ProFindType<RoofType>(doc, GetS(args, "base_type")),
                _ => throw new ArgumentException("kind deve ser wall, floor, ceiling ou roof.")
            };
            if (baseType == null) throw new InvalidOperationException($"Nenhum tipo base de {kind} no projeto para duplicar.");
            var existing = new FilteredElementCollector(doc).OfClass(baseType.GetType()).Cast<ElementType>()
                .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) throw new ArgumentException($"Já existe um tipo '{name}' (id {existing.Id.Value}). Escolha outro nome ou use duplicate_type.");

            var materiaisCriados = new List<string>();
            using (var tx = ProTx(doc, "Criar tipo por camadas"))
            {
                var layers = new List<CompoundStructureLayer>();
                foreach (var l in larr.EnumerateArray())
                {
                    var function = ArchLayerFunction(GetS(l, "function"));
                    double thickness = GetItemD(l, "thickness_m", function == MaterialFunctionAssignment.Membrane ? 0 : double.NaN);
                    if (double.IsNaN(thickness) || thickness < 0) throw new ArgumentException("Cada camada precisa de thickness_m (membrana pode ser 0).");
                    if (function == MaterialFunctionAssignment.Membrane) thickness = 0;
                    var matId = ElementId.InvalidElementId;
                    var matName = GetS(l, "material");
                    if (!string.IsNullOrWhiteSpace(matName))
                    {
                        var mat = FindMaterialByName(doc, matName);
                        if (mat == null)
                        {
                            if (!createMaterials) throw new ArgumentException($"Material '{matName}' não existe. Use list_materials ou create_materials=true.");
                            mat = doc.GetElement(Material.Create(doc, matName)) as Material;
                            materiaisCriados.Add(matName);
                        }
                        matId = mat.Id;
                    }
                    layers.Add(new CompoundStructureLayer(MToFt(thickness), function, matId));
                }
                if (layers.All(l => l.Width <= 0)) throw new ArgumentException("A espessura total precisa ser maior que zero.");

                var cs = CompoundStructure.CreateSimpleCompoundStructure(layers);
                int first = layers.FindIndex(l => l.Function == MaterialFunctionAssignment.Structure);
                int last = layers.FindLastIndex(l => l.Function == MaterialFunctionAssignment.Structure);
                if (first >= 0)
                {
                    // Camadas antes/depois da estrutura são cascas exterior/interior.
                    cs.SetNumberOfShellLayers(ShellLayerType.Exterior, first);
                    cs.SetNumberOfShellLayers(ShellLayerType.Interior, layers.Count - 1 - last);
                    cs.StructuralMaterialIndex = first;
                }

                var newType = (HostObjAttributes)baseType.Duplicate(name);
                newType.SetCompoundStructure(cs);
                if (newType is WallType wallType)
                {
                    var function = GetS(args, "function");
                    if (function != null)
                    {
                        if (!Enum.TryParse<WallFunction>(function, true, out var wf))
                            throw new ArgumentException("function deve ser Exterior, Interior, Foundation, Retaining, Soffit ou CoreShaft.");
                        wallType.Function = wf;
                    }
                }
                tx.Commit();
                return new
                {
                    tipo = ArchDescribeType(doc, newType, 0),
                    base_duplicada = baseType.Name,
                    materiais_criados = materiaisCriados,
                    proximo_passo = kind == "wall"
                        ? "Use create_wall/create_walls com wall_type igual ao nome, ou change_element_type em paredes existentes."
                        : "Use change_element_type para aplicar a elementos existentes."
                };
            }
        }

        // =====================================================================
        // AMBIENTES E ÁREAS
        // =====================================================================

        private static string ArchParamText(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p == null || !p.HasValue) return null;
                var s = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch { return null; }
        }

        private static double ArchParamSI(Element e, BuiltInParameter bip, ForgeTypeId unit)
        {
            try
            {
                var p = e.get_Parameter(bip);
                return p != null && p.HasValue && p.StorageType == StorageType.Double
                    ? Math.Round(UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit), 3) : 0;
            }
            catch { return 0; }
        }

        private static object GetRoomReport(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string levelFilter = GetS(args, "level");
            var portas = new Dictionary<long, int>();
            var janelas = new Dictionary<long, int>();
            void Count(Dictionary<long, int> map, Room r) { if (r != null) map[r.Id.Value] = map.TryGetValue(r.Id.Value, out var n) ? n + 1 : 1; }
            foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                try { Count(portas, fi.FromRoom); if (fi.ToRoom?.Id != fi.FromRoom?.Id) Count(portas, fi.ToRoom); } catch { }
            }
            foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                try { Count(janelas, fi.ToRoom ?? fi.FromRoom ?? fi.Room); } catch { }
            }

            var ambientes = new List<(string nivel, string numero, object item)>();
            var problemas = new List<object>();
            var porNivel = new Dictionary<string, (int qtd, double area)>();
            foreach (var room in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                string level = room.Level?.Name ?? "(sem nível)";
                if (levelFilter != null && Normalizar(level) != Normalizar(levelFilter)) continue;
                double area = Math.Round(UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters), 3);
                string status = room.Location == null ? "nao_posicionado" : area <= 0 ? "nao_delimitado" : "ok";
                if (status != "ok")
                    problemas.Add(new
                    {
                        id = room.Id.Value, numero = room.Number, nome = ArchParamText(room, BuiltInParameter.ROOM_NAME), problema = status,
                        correcao = status == "nao_posicionado" ? "Posicione o ambiente (place_rooms_at) ou exclua-o." : "Feche o contorno com paredes ou create_room_separators."
                    });
                int nPortas = portas.TryGetValue(room.Id.Value, out var np) ? np : 0;
                int nJanelas = janelas.TryGetValue(room.Id.Value, out var nj) ? nj : 0;
                if (status == "ok" && nPortas == 0)
                    problemas.Add(new { id = room.Id.Value, numero = room.Number, nome = ArchParamText(room, BuiltInParameter.ROOM_NAME), problema = "sem_porta", correcao = "Verifique o acesso ao ambiente." });
                ambientes.Add((level, room.Number ?? "", new
                {
                    id = room.Id.Value,
                    numero = room.Number,
                    nome = ArchParamText(room, BuiltInParameter.ROOM_NAME),
                    nivel = level,
                    status,
                    area_m2 = area,
                    perimetro_m = ArchParamSI(room, BuiltInParameter.ROOM_PERIMETER, UnitTypeId.Meters),
                    volume_m3 = ArchParamSI(room, BuiltInParameter.ROOM_VOLUME, UnitTypeId.CubicMeters),
                    pe_direito_m = Math.Round(FtToM(room.UnboundedHeight), 3),
                    departamento = ArchParamText(room, BuiltInParameter.ROOM_DEPARTMENT),
                    ocupacao = ArchParamText(room, BuiltInParameter.ROOM_OCCUPANCY),
                    acabamento_piso = ArchParamText(room, BuiltInParameter.ROOM_FINISH_FLOOR),
                    acabamento_parede = ArchParamText(room, BuiltInParameter.ROOM_FINISH_WALL),
                    acabamento_forro = ArchParamText(room, BuiltInParameter.ROOM_FINISH_CEILING),
                    rodape = ArchParamText(room, BuiltInParameter.ROOM_FINISH_BASE),
                    portas = nPortas,
                    janelas = nJanelas
                }));
                if (status == "ok")
                {
                    porNivel.TryGetValue(level, out var t);
                    porNivel[level] = (t.qtd + 1, t.area + area);
                }
            }
            return new
            {
                total_ambientes = ambientes.Count,
                area_total_m2 = Math.Round(porNivel.Values.Sum(v => v.area), 3),
                por_nivel = porNivel.OrderBy(kv => kv.Key).Select(kv => new { nivel = kv.Key, ambientes = kv.Value.qtd, area_m2 = Math.Round(kv.Value.area, 3) }).ToList(),
                ambientes = ambientes.OrderBy(x => x.nivel).ThenBy(x => x.numero, StringComparer.OrdinalIgnoreCase).Select(x => x.item).ToList(),
                problemas,
                observacao = "Áreas calculadas pelo Revit conforme as configurações de Área e Volume do projeto (face de acabamento por padrão)."
            };
        }

        private static object ListAreaSchemes(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.AreaPlan).ToList();
            return new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>()
                .Select(s => new
                {
                    id = s.Id.Value,
                    nome = s.Name,
                    bruta = s.IsGrossBuildingArea,
                    planos = plans.Where(v => v.AreaScheme?.Id == s.Id)
                        .Select(v => new { id = v.Id.Value, nome = v.Name, nivel = v.GenLevel?.Name }).ToList()
                }).ToList();
        }

        private static AreaScheme ArchScheme(Document doc, string name)
        {
            var schemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
            if (schemes.Count == 0) throw new InvalidOperationException("O projeto não tem esquemas de área.");
            if (string.IsNullOrWhiteSpace(name)) return schemes.FirstOrDefault(s => s.IsGrossBuildingArea) ?? schemes[0];
            var alvo = Normalizar(name);
            return schemes.FirstOrDefault(s => Normalizar(s.Name) == alvo) ?? schemes.FirstOrDefault(s => Normalizar(s.Name).Contains(alvo))
                ?? throw new ArgumentException($"Esquema de área '{name}' não encontrado. Disponíveis: {string.Join(", ", schemes.Select(s => s.Name))}.");
        }

        private static ViewPlan ArchAreaView(Document doc, JsonElement args)
        {
            var id = GetElementIdReq(args, "area_view_id");
            var view = doc.GetElement(id) as ViewPlan;
            if (view == null || view.ViewType != ViewType.AreaPlan) throw new ArgumentException($"{id.Value} não é uma planta de área. Use create_area_plan ou list_area_schemes.");
            return view;
        }

        private static object CreateAreaPlan(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var scheme = ArchScheme(doc, GetS(args, "scheme"));
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nível não encontrado.");
            using (var tx = ProTx(doc, "Planta de área"))
            {
                var view = ViewPlan.CreateAreaPlan(doc, scheme.Id, level.Id);
                var name = GetS(args, "name");
                if (!string.IsNullOrWhiteSpace(name)) view.Name = name;
                tx.Commit();
                return new
                {
                    area_view_id = view.Id.Value,
                    nome = view.Name,
                    esquema = scheme.Name,
                    nivel = level.Name,
                    proximo_passo = "create_area_boundaries(area_view_id, source='rooms' ou lines) e depois place_areas."
                };
            }
        }

        private static object CreateAreaBoundaries(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = ArchAreaView(doc, args);
            var level = view.GenLevel ?? throw new InvalidOperationException("A planta de área não tem nível associado.");
            string source = (GetS(args, "source", "lines") ?? "lines").ToLowerInvariant();
            var curves = new List<Curve>();
            if (source == "rooms")
            {
                var options = new SpatialElementBoundaryOptions
                {
                    SpatialElementBoundaryLocation = (GetS(args, "boundary", "finish") ?? "finish").ToLowerInvariant() switch
                    {
                        "center" => SpatialElementBoundaryLocation.Center,
                        "core" => SpatialElementBoundaryLocation.CoreBoundary,
                        _ => SpatialElementBoundaryLocation.Finish
                    }
                };
                var seen = new HashSet<string>();
                string Key(XYZ p) => $"{Math.Round(p.X, 3)},{Math.Round(p.Y, 3)}";
                foreach (var room in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
                {
                    if (room.LevelId != level.Id || room.Area <= 0) continue;
                    foreach (var loop in room.GetBoundarySegments(options))
                        foreach (var seg in loop)
                        {
                            var c = seg.GetCurve();
                            var a = Key(c.GetEndPoint(0)); var b = Key(c.GetEndPoint(1));
                            // Segmentos compartilhados entre ambientes vizinhos entram uma vez.
                            if (!seen.Add(string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a)) continue;
                            curves.Add(c);
                        }
                }
                if (curves.Count == 0) throw new InvalidOperationException($"Nenhum ambiente delimitado no nível '{level.Name}'.");
            }
            else
            {
                if (!ProHas(args, "lines", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("Informe lines [{x1_m,y1_m,x2_m,y2_m}] ou source='rooms'.");
                foreach (var l in arr.EnumerateArray())
                {
                    var p1 = new XYZ(MToFt(GetItemD(l, "x1_m", 0)), MToFt(GetItemD(l, "y1_m", 0)), level.Elevation);
                    var p2 = new XYZ(MToFt(GetItemD(l, "x2_m", 0)), MToFt(GetItemD(l, "y2_m", 0)), level.Elevation);
                    if (p1.DistanceTo(p2) > doc.Application.ShortCurveTolerance) curves.Add(Line.CreateBound(p1, p2));
                }
            }
            var ids = new List<long>();
            using (var tx = ProTx(doc, "Limites de área"))
            {
                var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation)));
                foreach (var c in curves)
                {
                    var flat = c;
                    if (Math.Abs(c.GetEndPoint(0).Z - level.Elevation) > 1e-6)
                        flat = c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, level.Elevation - c.GetEndPoint(0).Z)));
                    ids.Add(doc.Create.NewAreaBoundaryLine(plane, flat, view).Id.Value);
                }
                tx.Commit();
            }
            return new { area_view_id = view.Id.Value, origem = source, linhas = ids.Count, ids, proximo_passo = "place_areas(area_view_id, from_rooms=true) ou com points." };
        }

        private static object PlaceAreas(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = ArchAreaView(doc, args);
            var level = view.GenLevel;
            var targets = new List<(UV point, string name, string number)>();
            if (ProBool(args, "from_rooms", false))
            {
                foreach (var room in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
                {
                    if (room.LevelId != level.Id || room.Area <= 0 || !(room.Location is LocationPoint lp)) continue;
                    targets.Add((new UV(lp.Point.X, lp.Point.Y), ArchParamText(room, BuiltInParameter.ROOM_NAME), room.Number));
                }
            }
            else
            {
                foreach (var p in ProPoints(args, "points", 0))
                    targets.Add((new UV(p.X, p.Y), null, null));
                if (ProHas(args, "names", out var names) && names.ValueKind == JsonValueKind.Array)
                {
                    var list = names.EnumerateArray().Select(n => n.GetString()).ToList();
                    for (int i = 0; i < targets.Count && i < list.Count; i++) targets[i] = (targets[i].point, list[i], targets[i].number);
                }
            }
            if (targets.Count == 0) throw new ArgumentException("Nada a posicionar: informe points ou from_rooms=true (com ambientes delimitados neste nível).");
            var criadas = new List<long>();
            using (var tx = ProTx(doc, "Posicionar áreas"))
            {
                foreach (var t in targets)
                {
                    var area = doc.Create.NewArea(view, t.point);
                    if (!string.IsNullOrWhiteSpace(t.name)) area.Name = t.name;
                    if (!string.IsNullOrWhiteSpace(t.number)) try { area.Number = t.number; } catch { }
                    criadas.Add(area.Id.Value);
                }
                tx.Commit();  // o commit já regenera o modelo; as áreas abaixo estão calculadas
            }
            var detalhes = criadas.Select(id => doc.GetElement(new ElementId(id)) as Area).Where(x => x != null)
                .Select(x => new
                {
                    id = x.Id.Value,
                    nome = ArchParamText(x, BuiltInParameter.ROOM_NAME),
                    numero = x.Number,
                    area_m2 = Math.Round(UnitUtils.ConvertFromInternalUnits(x.Area, UnitTypeId.SquareMeters), 3),
                    delimitada = x.Area > 0
                }).ToList();
            return new
            {
                area_view_id = view.Id.Value,
                areas = detalhes,
                area_total_m2 = Math.Round(detalhes.Sum(d => d.area_m2), 3),
                nao_delimitadas = detalhes.Count(d => !d.delimitada)
            };
        }

        private static object GetAreaReport(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string schemeFilter = GetS(args, "scheme");
            var linhas = new List<(string esquema, string nivel, long id, string nome, string numero, double area)>();
            foreach (var area in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType().OfType<Area>())
            {
                string scheme = area.AreaScheme?.Name ?? "?";
                if (schemeFilter != null && !Normalizar(scheme).Contains(Normalizar(schemeFilter))) continue;
                linhas.Add((scheme, area.Level?.Name ?? "?", area.Id.Value, ArchParamText(area, BuiltInParameter.ROOM_NAME), area.Number,
                    Math.Round(UnitUtils.ConvertFromInternalUnits(area.Area, UnitTypeId.SquareMeters), 3)));
            }
            var esquemas = linhas.GroupBy(l => l.esquema).OrderBy(g => g.Key).Select(g => new
            {
                esquema = g.Key,
                area_total_m2 = Math.Round(g.Sum(l => l.area), 3),
                niveis = g.GroupBy(l => l.nivel).OrderBy(n => n.Key).Select(n => new
                {
                    nivel = n.Key,
                    area_m2 = Math.Round(n.Sum(l => l.area), 3),
                    areas = n.OrderBy(l => l.numero).Select(l => new { id = l.id, nome = l.nome, numero = l.numero, area_m2 = l.area, delimitada = l.area > 0 }).ToList()
                }).ToList()
            }).ToList();
            return new
            {
                esquemas,
                areas_nao_delimitadas = linhas.Count(l => l.area <= 0),
                dica = linhas.Count == 0 ? "Nenhuma área. Fluxo: create_area_plan → create_area_boundaries → place_areas." : null
            };
        }

        // =====================================================================
        // ORGANIZAÇÃO E DOCUMENTAÇÃO
        // =====================================================================

        private static object CreateShaftOpening(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var bottom = FindLevel(doc, GetS(args, "bottom_level")) ?? throw new Exception("Nível inferior não encontrado.");
            var topName = GetS(args, "top_level") ?? throw new ArgumentException("top_level obrigatório.");
            var top = FindLevel(doc, topName) ?? throw new Exception($"Nível superior '{topName}' não encontrado.");
            if (top.Elevation <= bottom.Elevation) throw new ArgumentException("top_level precisa estar acima de bottom_level.");
            var pts = ProPoints(args, "points", bottom.Elevation);
            if (pts.Count < 3) throw new ArgumentException("O contorno do shaft precisa de ao menos 3 pontos.");
            var profile = new CurveArray();
            for (int i = 0; i < pts.Count; i++) profile.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
            using (var tx = ProTx(doc, "Shaft"))
            {
                var opening = doc.Create.NewOpening(bottom, top, profile);
                double topOffset = GetD(args, "top_offset_m", 0), baseOffset = GetD(args, "base_offset_m", 0);
                if (topOffset != 0) opening.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(MToFt(topOffset));
                if (baseOffset != 0) opening.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.Set(MToFt(baseOffset));
                tx.Commit();
                double area = 0;
                for (int i = 0; i < pts.Count; i++) area += pts[i].X * pts[(i + 1) % pts.Count].Y - pts[(i + 1) % pts.Count].X * pts[i].Y;
                return new
                {
                    id = opening.Id.Value,
                    nivel_inferior = bottom.Name,
                    nivel_superior = top.Name,
                    vertices = pts.Count,
                    area_secao_m2 = Math.Round(Math.Abs(area) / 2 * 0.3048 * 0.3048, 3),
                    observacao = "O shaft corta pisos, forros e coberturas entre os níveis."
                };
            }
        }

        private static object DimensionGrids(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = ProView(uiApp, doc, args);
            if (!(view is ViewPlan)) throw new InvalidOperationException("Cotas de eixos exigem uma vista de planta.");
            double offset = MToFt(GetD(args, "offset_m", 1.5));
            bool overall = ProBool(args, "overall", true);
            var grids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => g.Curve is Line).ToList();
            if (grids.Count < 2) throw new InvalidOperationException("São necessários ao menos 2 eixos retos visíveis na vista.");

            // Agrupa por direção: eixos paralelos recebem uma corrente de cotas.
            var groups = new List<List<Grid>>();
            foreach (var g in grids)
            {
                var dir = ((Line)g.Curve).Direction;
                var group = groups.FirstOrDefault(gr => Math.Abs(((Line)gr[0].Curve).Direction.CrossProduct(dir).Z) < 1e-6);
                if (group == null) groups.Add(new List<Grid> { g }); else group.Add(g);
            }
            var criadas = new List<object>();
            using (var tx = ProTx(doc, "Cotas de eixos"))
            {
                foreach (var group in groups.Where(gr => gr.Count >= 2))
                {
                    var line0 = (Line)group[0].Curve;
                    var dir = line0.Direction.Normalize();
                    var normal = new XYZ(-dir.Y, dir.X, 0);
                    var ordered = group.OrderBy(g => ((Line)g.Curve).Origin.DotProduct(normal)).ToList();
                    // A cota passa além da extremidade dos eixos (lado "fim" de cada um).
                    double along = ordered.Max(g => Math.Max(((Line)g.Curve).GetEndPoint(0).DotProduct(dir), ((Line)g.Curve).GetEndPoint(1).DotProduct(dir))) - offset;
                    double minN = ordered.First().Curve.GetEndPoint(0).DotProduct(normal);
                    double maxN = ordered.Last().Curve.GetEndPoint(0).DotProduct(normal);
                    XYZ P(double n, double a) => normal * n + dir * a + new XYZ(0, 0, view.GenLevel?.Elevation ?? 0);
                    var refs = new ReferenceArray();
                    foreach (var g in ordered) refs.Append(new Reference(g));
                    var chain = doc.Create.NewDimension(view, Line.CreateBound(P(minN - 1, along), P(maxN + 1, along)), refs);
                    criadas.Add(new { id = chain.Id.Value, tipo = "corrente", eixos = ordered.Select(g => g.Name).ToList(), valores_m = ArchDimValues(chain) });
                    if (overall && ordered.Count > 2)
                    {
                        var ends = new ReferenceArray();
                        ends.Append(new Reference(ordered.First()));
                        ends.Append(new Reference(ordered.Last()));
                        var total = doc.Create.NewDimension(view, Line.CreateBound(P(minN - 1, along - offset * 0.6), P(maxN + 1, along - offset * 0.6)), ends);
                        criadas.Add(new { id = total.Id.Value, tipo = "total", eixos = new[] { ordered.First().Name, ordered.Last().Name }, valores_m = ArchDimValues(total) });
                    }
                }
                tx.Commit();
            }
            return new { vista = view.Name, cotas = criadas };
        }

        private static List<double> ArchDimValues(Dimension d)
        {
            var values = new List<double>();
            try
            {
                if (d.NumberOfSegments == 0) { if (d.Value.HasValue) values.Add(Math.Round(FtToM(d.Value.Value), 3)); }
                else foreach (DimensionSegment seg in d.Segments) if (seg.Value.HasValue) values.Add(Math.Round(FtToM(seg.Value.Value), 3));
            }
            catch { }
            return values;
        }

        private static object PinElements(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var ids = ProIds(args, "ids");
            bool pinned = ProBool(args, "pinned", true);
            var cats = ProCategories(args, "categories");
            var elements = ids.Select(doc.GetElement).Where(e => e != null).ToList();
            foreach (var bic in cats)
                elements.AddRange(new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements());
            if (elements.Count == 0) throw new ArgumentException("Informe ids ou categories (ex.: [\"Grids\", \"Levels\"]).");
            int changed = 0;
            var falhas = new List<object>();
            using (var tx = ProTx(doc, pinned ? "Fixar elementos" : "Liberar elementos"))
            {
                foreach (var e in elements)
                {
                    try { if (e.Pinned != pinned) { e.Pinned = pinned; changed++; } }
                    catch (Exception ex) { falhas.Add(new { id = e.Id.Value, motivo = ex.Message }); }
                }
                tx.Commit();
            }
            return new { fixado = pinned, elementos = elements.Count, alterados = changed, ja_estavam = elements.Count - changed - falhas.Count, falhas };
        }

        private static object SetSelection(UIApplication uiApp, JsonElement args)
        {
            var uidoc = uiApp.ActiveUIDocument ?? throw new Exception("Nenhum documento ativo.");
            var ids = ProIds(args, "ids").Where(id => uidoc.Document.GetElement(id) != null).ToList();
            uidoc.Selection.SetElementIds(ids);
            if (ids.Count > 0 && ProBool(args, "zoom", true)) uidoc.ShowElements(ids);
            return new { selecionados = ids.Count, ids = ids.Select(i => i.Value).ToList() };
        }

        private static object GetElementRelationships(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "element_id");
            var e = doc.GetElement(id) ?? throw new ArgumentException($"Elemento {id.Value} não existe.");
            object Brief(Element x) => x == null ? null : new { id = x.Id.Value, categoria = x.Category?.Name, nome = x.Name, tipo = ProTypeName(doc, x) };

            var fi = e as FamilyInstance;
            List<object> hospedados = null;
            if (e is HostObject host)
            {
                try { hospedados = host.FindInserts(true, true, true, true).Select(i => Brief(doc.GetElement(i))).ToList(); } catch { }
            }
            List<object> unidos = null;
            try { unidos = JoinGeometryUtils.GetJoinedElements(doc, e).Select(i => Brief(doc.GetElement(i))).ToList(); } catch { }

            object ambientes = null;
            if (fi != null)
            {
                try
                {
                    ambientes = new
                    {
                        ambiente = fi.Room == null ? null : new { id = fi.Room.Id.Value, numero = fi.Room.Number, nome = ArchParamText(fi.Room, BuiltInParameter.ROOM_NAME) },
                        de = fi.FromRoom == null ? null : new { id = fi.FromRoom.Id.Value, numero = fi.FromRoom.Number, nome = ArchParamText(fi.FromRoom, BuiltInParameter.ROOM_NAME) },
                        para = fi.ToRoom == null ? null : new { id = fi.ToRoom.Id.Value, numero = fi.ToRoom.Number, nome = ArchParamText(fi.ToRoom, BuiltInParameter.ROOM_NAME) }
                    };
                }
                catch { }
            }
            else
            {
                var pt = ProElementPoint(e);
                var room = pt == null ? null : doc.GetRoomAtPoint(pt);
                if (room != null) ambientes = new { ambiente = new { id = room.Id.Value, numero = room.Number, nome = ArchParamText(room, BuiltInParameter.ROOM_NAME) } };
            }

            int dependentes = 0;
            try { dependentes = e.GetDependentElements(null).Count - 1; } catch { }
            string workset = null;
            try { if (doc.IsWorkshared) workset = doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name; } catch { }
            string faseCriacao = null, faseDemolicao = null;
            try
            {
                if (e.CreatedPhaseId != ElementId.InvalidElementId) faseCriacao = doc.GetElement(e.CreatedPhaseId)?.Name;
                if (e.DemolishedPhaseId != ElementId.InvalidElementId) faseDemolicao = doc.GetElement(e.DemolishedPhaseId)?.Name;
            }
            catch { }

            return new
            {
                elemento = ChangeTracker.Describe(doc, e),
                hospedeiro = Brief(fi?.Host),
                hospedados,
                unidos,
                ambientes,
                grupo = e.GroupId != ElementId.InvalidElementId ? Brief(doc.GetElement(e.GroupId)) : null,
                fixado = e.Pinned,
                workset,
                fase_criacao = faseCriacao,
                fase_demolicao = faseDemolicao,
                opcao_projeto = e.DesignOption?.Name,
                elementos_dependentes = Math.Max(0, dependentes),
                observacao = dependentes > 0 ? "Excluir este elemento também remove os dependentes (use preview_delete)." : null
            };
        }

        private static object ListViewTemplates(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var uso = views.Where(v => !v.IsTemplate && v.ViewTemplateId != ElementId.InvalidElementId)
                .GroupBy(v => v.ViewTemplateId.Value).ToDictionary(g => g.Key, g => g.Count());
            return views.Where(v => v.IsTemplate).OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name)
                .Select(v => new
                {
                    id = v.Id.Value,
                    nome = v.Name,
                    tipo_vista = v.ViewType.ToString(),
                    escala = v.Scale,
                    nivel_detalhe = v.DetailLevel.ToString(),
                    vistas_usando = uso.TryGetValue(v.Id.Value, out var n) ? n : 0
                }).ToList();
        }

        private static object CreateViewTemplate(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = ProView(uiApp, doc, args);
            if (view.IsTemplate) throw new ArgumentException("A vista informada já é um template.");
            if (!view.CanViewBeDuplicated(ViewDuplicateOption.Duplicate) && view.ViewType == ViewType.Schedule)
                throw new InvalidOperationException("Este tipo de vista não gera template.");
            using (var tx = ProTx(doc, "Criar template de vista"))
            {
                var template = view.CreateViewTemplate();
                var name = GetS(args, "name");
                if (!string.IsNullOrWhiteSpace(name)) template.Name = name;
                if (ProBool(args, "apply_to_source", false)) view.ViewTemplateId = template.Id;
                tx.Commit();
                return new { id = template.Id.Value, nome = template.Name, origem = view.Name, tipo_vista = template.ViewType.ToString(), aplicado_na_origem = ProBool(args, "apply_to_source", false) };
            }
        }

        private static object ListDesignOptions(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var active = DesignOption.GetActiveDesignOptionId(doc);
            var opcoes = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>()
                .Select(o =>
                {
                    string set = null;
                    try { var p = o.get_Parameter(BuiltInParameter.OPTION_SET_ID); if (p != null) set = doc.GetElement(p.AsElementId())?.Name; } catch { }
                    return new { id = o.Id.Value, nome = o.Name, conjunto = set, principal = o.IsPrimary, ativa = o.Id == active };
                }).OrderBy(o => o.conjunto).ThenBy(o => o.nome).ToList();
            return new { quantidade = opcoes.Count, opcoes, observacao = opcoes.Count == 0 ? "O projeto não usa opções de projeto." : null };
        }

        // =====================================================================
        // MODELAGEM
        // =====================================================================

        private static object FlipInstances(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var ids = ProIds(args, "ids", required: true);
            bool hand = ProBool(args, "hand", false), facing = ProBool(args, "facing", false);
            if (!hand && !facing) throw new ArgumentException("Informe hand=true (lado da dobradiça/abertura) e/ou facing=true (sentido interno/externo).");
            var resultado = new List<object>();
            using (var tx = ProTx(doc, "Inverter instâncias"))
            {
                foreach (var id in ids)
                {
                    var fi = doc.GetElement(id) as FamilyInstance;
                    string erro = null;
                    if (fi == null) erro = "não é instância de família";
                    else
                    {
                        try
                        {
                            if (hand) { if (fi.CanFlipHand) fi.flipHand(); else erro = "não permite inverter o lado"; }
                            if (facing && erro == null) { if (fi.CanFlipFacing) fi.flipFacing(); else erro = "não permite inverter o sentido"; }
                        }
                        catch (Exception ex) { erro = ex.Message; }
                    }
                    resultado.Add(new { id = id.Value, ok = erro == null, invertida_lado = fi?.HandFlipped, invertida_sentido = fi?.FacingFlipped, motivo = erro });
                }
                tx.Commit();
            }
            return new { elementos = resultado };
        }

        private static object CreateFloorOpening(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var hostId = GetElementIdReq(args, "host_id");
            var host = doc.GetElement(hostId) ?? throw new ArgumentException($"Elemento {hostId.Value} não existe.");
            if (!(host is Floor || host is Ceiling || host is RoofBase))
                throw new ArgumentException("host_id deve ser um piso, forro ou cobertura (para paredes use create_opening).");
            var bb = host.get_BoundingBox(null);
            var pts = ProPoints(args, "points", bb?.Max.Z ?? 0);
            if (pts.Count < 3) throw new ArgumentException("O contorno da abertura precisa de ao menos 3 pontos.");
            var profile = new CurveArray();
            for (int i = 0; i < pts.Count; i++) profile.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
            using (var tx = ProTx(doc, "Abertura em laje"))
            {
                var opening = doc.Create.NewOpening(host, profile, ProBool(args, "perpendicular", true));
                tx.Commit();
                double area = 0;
                for (int i = 0; i < pts.Count; i++) area += pts[i].X * pts[(i + 1) % pts.Count].Y - pts[(i + 1) % pts.Count].X * pts[i].Y;
                return new { id = opening.Id.Value, hospedeiro = new { id = host.Id.Value, categoria = host.Category?.Name, tipo = ProTypeName(doc, host) }, area_m2 = Math.Round(Math.Abs(area) / 2 * 0.3048 * 0.3048, 3) };
            }
        }

        private static object JoinGeometry(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string mode = (GetS(args, "mode", "join") ?? "join").ToLowerInvariant();
            var pairs = new List<(ElementId, ElementId)>();
            if (ProHas(args, "pairs", out var parr) && parr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in parr.EnumerateArray())
                {
                    var ab = p.EnumerateArray().Select(v => new ElementId(v.GetInt64())).ToList();
                    if (ab.Count != 2) throw new ArgumentException("Cada par precisa de 2 IDs.");
                    pairs.Add((ab[0], ab[1]));
                }
            }
            else
            {
                // Modo automático: une cada elemento de ids_a aos de ids_b que o tocam.
                var idsA = ProIds(args, "ids_a", required: true);
                var catB = ProCategories(args, "categories_b");
                if (catB.Count == 0) throw new ArgumentException("Informe pairs, ou ids_a + categories_b (ex.: [\"Walls\"]).");
                foreach (var idA in idsA)
                {
                    var a = doc.GetElement(idA); var bb = a?.get_BoundingBox(null);
                    if (bb == null) continue;
                    foreach (var b in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(catB)).WhereElementIsNotElementType()
                                 .WherePasses(new BoundingBoxIntersectsFilter(new Outline(bb.Min, bb.Max))).ToElements())
                        if (b.Id != idA) pairs.Add((idA, b.Id));
                }
            }
            var resultado = new List<object>();
            int ok = 0;
            using (var tx = ProTx(doc, "Unir geometria"))
            {
                foreach (var (ia, ib) in pairs)
                {
                    var a = doc.GetElement(ia); var b = doc.GetElement(ib);
                    string erro = null;
                    try
                    {
                        if (a == null || b == null) erro = "elemento inexistente";
                        else if (mode == "unjoin") { if (JoinGeometryUtils.AreElementsJoined(doc, a, b)) JoinGeometryUtils.UnjoinGeometry(doc, a, b); }
                        else if (mode == "switch") JoinGeometryUtils.SwitchJoinOrder(doc, a, b);
                        else if (!JoinGeometryUtils.AreElementsJoined(doc, a, b)) JoinGeometryUtils.JoinGeometry(doc, a, b);
                    }
                    catch (Exception ex) { erro = ex.Message; }
                    if (erro == null) ok++;
                    if (resultado.Count < 200) resultado.Add(new { a = ia.Value, b = ib.Value, ok = erro == null, motivo = erro });
                }
                tx.Commit();
            }
            return new { modo = mode, pares = pairs.Count, sucesso = ok, falhas = pairs.Count - ok, detalhes = resultado };
        }

        private static object AttachWalls(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var wallIds = ProIds(args, "wall_ids", required: true);
            var targetId = GetElementIdReq(args, "target_id");
            var target = doc.GetElement(targetId) ?? throw new ArgumentException($"Elemento {targetId.Value} não existe.");
            var location = string.Equals(GetS(args, "location", "top"), "base", StringComparison.OrdinalIgnoreCase)
                ? AttachmentLocation.Base : AttachmentLocation.Top;
            bool detach = ProBool(args, "detach", false);
            var resultado = new List<object>();
            using (var tx = ProTx(doc, detach ? "Desanexar paredes" : "Anexar paredes"))
            {
                foreach (var id in wallIds)
                {
                    var wall = doc.GetElement(id) as Wall;
                    string erro = null;
                    try
                    {
                        if (wall == null) erro = "não é parede";
                        else if (detach) wall.RemoveAttachment(targetId);
                        else if (!Wall.IsValidTargetAttachment(doc, targetId)) erro = $"{target.Category?.Name} não é alvo válido para esta parede";
                        else wall.AddAttachment(targetId, location);
                    }
                    catch (Exception ex) { erro = ex.Message; }
                    resultado.Add(new { id = id.Value, ok = erro == null, motivo = erro });
                }
                tx.Commit();
            }
            return new { alvo = new { id = target.Id.Value, categoria = target.Category?.Name }, posicao = location.ToString(), desanexar = detach, paredes = resultado };
        }

        private static object SetTrueNorth(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            double angle = GetDReq(args, "angle_deg");
            var location = doc.ActiveProjectLocation;
            var current = location.GetProjectPosition(XYZ.Zero);
            using (var tx = ProTx(doc, "Norte verdadeiro"))
            {
                location.SetProjectPosition(XYZ.Zero, new ProjectPosition(current.EastWest, current.NorthSouth, current.Elevation, angle * Math.PI / 180.0));
                tx.Commit();
            }
            return new
            {
                angulo_anterior_graus = Math.Round(current.Angle * 180.0 / Math.PI, 4),
                angulo_novo_graus = angle,
                observacao = "Ângulo entre o norte do projeto e o norte verdadeiro (positivo = anti-horário). Afeta estudos de insolação e vistas orientadas ao norte verdadeiro."
            };
        }

        // =====================================================================
        // PRODUÇÃO
        // =====================================================================

        /// <summary>
        /// Numera elementos em sequência (Marca de portas/janelas, Número de
        /// ambientes...). Ordem: nível e depois posição (y desc, x asc = leitura
        /// de planta de cima para baixo, esquerda para direita) ou pelo ambiente.
        /// </summary>
        private static object RenumberElements(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var bic = ProCategory(GetS(args, "category"));
            string prefix = GetS(args, "prefix", "");
            int start = (int)GetD(args, "start", 1);
            int digits = Math.Clamp((int)GetD(args, "digits", 2), 1, 6);
            string order = (GetS(args, "order", "position") ?? "position").ToLowerInvariant();
            bool perLevel = ProBool(args, "restart_per_level", false);
            string levelFilter = GetS(args, "level");
            bool dryRun = ProBool(args, "dry_run", false);

            var elements = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements()
                .Where(e => levelFilter == null || Normalizar(ProLevelName(doc, e) ?? "") == Normalizar(levelFilter))
                .Where(e => !(e is SpatialElement se) || se.Area > 0)
                .ToList();
            if (elements.Count == 0) throw new ArgumentException("Nenhum elemento a numerar com esses filtros.");
            double LevelElev(Element e) => (doc.GetElement(e.LevelId) as Level)?.Elevation ?? 0;
            string RoomNumber(Element e)
            {
                try { var fi = e as FamilyInstance; var r = fi?.ToRoom ?? fi?.FromRoom ?? fi?.Room; return r?.Number; } catch { return null; }
            }
            Parameter Target(Element e)
            {
                if (e is SpatialElement) return e.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                var name = GetS(args, "parameter");
                return name != null ? ProLookup(doc, e, name, includeType: false) : e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            }

            var ordered = elements
                .OrderBy(e => LevelElev(e))
                .ThenBy(e => order == "room" ? (RoomNumber(e) ?? "~") : "")
                .ThenByDescending(e => Math.Round(ProElementPoint(e)?.Y ?? 0, 1))
                .ThenBy(e => ProElementPoint(e)?.X ?? 0)
                .ToList();

            var plano = new List<(Element e, string valor)>();
            int counter = start;
            long currentLevel = long.MinValue;
            var porSala = new Dictionary<string, int>();
            foreach (var e in ordered)
            {
                if (perLevel && e.LevelId.Value != currentLevel) { counter = start; currentLevel = e.LevelId.Value; }
                string value;
                if (order == "room")
                {
                    // Portas: número do ambiente + sufixo (101a, 101b...).
                    var room = RoomNumber(e) ?? "SA";
                    porSala[room] = porSala.TryGetValue(room, out var n) ? n + 1 : 0;
                    value = prefix + room + (porSala[room] == 0 ? "" : ((char)('a' + porSala[room] - 1)).ToString());
                }
                else value = prefix + counter++.ToString().PadLeft(digits, '0');
                plano.Add((e, value));
            }

            var resultado = plano.Select(x => new { id = x.e.Id.Value, nivel = ProLevelName(doc, x.e), anterior = ProParamText(doc, Target(x.e)), novo = x.valor }).ToList();
            if (dryRun) return new { simulacao = true, elementos = resultado };

            var falhas = new List<object>();
            using (var tx = ProTx(doc, "Renumerar"))
            {
                // Valores temporários evitam conflito de números duplicados durante a troca (ambientes).
                if (bic == BuiltInCategory.OST_Rooms || bic == BuiltInCategory.OST_MEPSpaces || bic == BuiltInCategory.OST_Areas)
                    foreach (var (e, _) in plano) Target(e)?.Set("tmp_" + e.Id.Value);
                foreach (var (e, value) in plano)
                {
                    var p = Target(e);
                    string erro = p == null ? "parâmetro de numeração não encontrado" : p.IsReadOnly ? "somente leitura" : null;
                    if (erro == null) { try { if (!p.Set(value)) erro = "valor rejeitado"; } catch (Exception ex) { erro = ex.Message; } }
                    if (erro != null) falhas.Add(new { id = e.Id.Value, motivo = erro });
                }
                tx.Commit();
            }
            return new { numerados = plano.Count - falhas.Count, falhas, elementos = resultado };
        }

        private static object ExportImages(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var ids = ProIds(args, "view_ids");
            if (ids.Count == 0) ids.Add(uiApp.ActiveUIDocument.ActiveView.Id);
            var views = ids.Select(id => doc.GetElement(id) as View).Where(v => v != null && !v.IsTemplate && v.CanBePrinted).ToList();
            if (views.Count == 0) throw new ArgumentException("Nenhuma vista imprimível entre os IDs informados.");
            string folder = GetS(args, "folder") ?? System.IO.Path.GetTempPath();
            System.IO.Directory.CreateDirectory(folder);
            int pixels = Math.Clamp((int)GetD(args, "pixels", 2400), 256, 15000);
            var format = (GetS(args, "format", "png") ?? "png").ToLowerInvariant() == "jpg" ? ImageFileType.JPEGLossless : ImageFileType.PNG;
            string prefix = GetS(args, "prefix", doc.Title);
            var before = new HashSet<string>(System.IO.Directory.GetFiles(folder));
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = System.IO.Path.Combine(folder, prefix),
                FitDirection = FitDirectionType.Horizontal,
                PixelSize = pixels,
                HLRandWFViewsFileType = format,
                ShadowViewsFileType = format,
                ImageResolution = ImageResolution.DPI_300,
                ZoomType = ZoomFitType.FitToPage
            };
            options.SetViewsAndSheets(views.Select(v => v.Id).ToList());
            doc.ExportImage(options);
            var arquivos = System.IO.Directory.GetFiles(folder).Where(f => !before.Contains(f))
                .Select(f => new { arquivo = f, tamanho_kb = new System.IO.FileInfo(f).Length / 1024 }).ToList();
            return new { pasta = folder, largura_px = pixels, vistas = views.Select(v => v.Name).ToList(), arquivos };
        }
    }
}
