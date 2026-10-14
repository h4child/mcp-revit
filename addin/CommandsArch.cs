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
    }
}
