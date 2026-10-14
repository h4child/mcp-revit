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
    }
}
