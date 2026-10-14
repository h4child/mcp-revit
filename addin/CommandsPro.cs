// ============================================================================
// CommandsPro.cs — Comandos para fluxos profissionais de qualquer disciplina
// ============================================================================
//
// Complementa Commands.cs com o que um projeto real precisa além da
// modelagem básica:
//
//   • Dados do projeto: informações, localização, worksets e
//     consultas/edições em massa por parâmetro.
//   • Documentação: vistas (duplicar, elevações, 3D, drafting, filtros,
//     sobreposições), etiquetas, pranchas, revisões, tabelas e exportação.
//   • Coordenação: detecção de interferências, quantitativos, levantamento
//     de materiais, vínculos e limpeza de itens não usados.
//   • Estrutura: vigas e fundações.
//   • MEP: dutos, tubos, eletrocalhas, eletrodutos e espaços.
//   • Modelagem geral: cópias em série e separadores de ambiente.
//
// Parâmetros de projeto, fases, grupos e filtros por categoria ficam em
// Commands.cs (create_project_parameter, set_element_phase, create_group...).
//
// Convenções iguais às de Commands.cs: entradas/saídas em metros, m², m³ e
// graus; toda escrita dentro de Transaction; erros de validação como
// ArgumentException com mensagem acionável.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace RevitMcp
{
    public static partial class Commands
    {
        private static object ExecutarComandoPro(UIApplication uiApp, RpcRequest req)
        {
            var a = req.args;
            switch (req.cmd)
            {
                // --- Dados do projeto ---
                case "get_project_info":        return GetProjectInfo(uiApp);
                case "set_project_info":        return SetProjectInfo(uiApp, a);
                case "get_project_location":    return GetProjectLocation(uiApp);
                case "create_workset":          return CreateWorkset(uiApp, a);
                case "set_element_workset":     return SetElementWorkset(uiApp, a);
                case "find_elements":           return FindElements(uiApp, a);
                case "set_parameters_bulk":     return SetParametersBulk(uiApp, a);

                // --- Vistas ---
                case "duplicate_view":          return DuplicateView(uiApp, a);
                case "set_view_properties":     return SetViewProperties(uiApp, a);
                case "create_elevations":       return CreateElevations(uiApp, a);
                case "create_3d_view":          return Create3DView(uiApp, a);
                case "create_drafting_view":    return CreateDraftingView(uiApp, a);
                case "create_rule_filter":      return CreateRuleFilter(uiApp, a);
                case "override_elements_in_view": return OverrideElementsInView(uiApp, a);
                case "set_category_visibility": return SetCategoryVisibility(uiApp, a);
                case "tag_elements":            return TagElements(uiApp, a);

                // --- Pranchas, revisões, tabelas e exportação ---
                case "list_sheets":             return ListSheets(uiApp);
                case "list_titleblocks":        return ListTitleblocks(uiApp);
                case "create_sheets":           return CreateSheets(uiApp, a);
                case "list_revisions":          return ListRevisions(uiApp);
                case "create_revision":         return CreateRevision(uiApp, a);
                case "add_revision_to_sheets":  return AddRevisionToSheets(uiApp, a);
                case "list_schedules":          return ListSchedules(uiApp);
                case "create_schedule_advanced": return CreateScheduleAdvanced(uiApp, a);
                case "get_schedule_data":       return GetScheduleData(uiApp, a);
                case "export_schedule_csv":     return ExportScheduleCsv(uiApp, a);
                case "export_sheets_pdf":       return ExportSheetsPdf(uiApp, a);
                case "export_sheets_dwg":       return ExportSheetsDwg(uiApp, a);

                // --- Coordenação e qualidade ---
                case "analyze_clashes":         return AnalyzeClashes(uiApp, a);
                case "analyze_quantities":      return AnalyzeQuantities(uiApp, a);
                case "analyze_material_takeoff": return AnalyzeMaterialTakeoff(uiApp, a);
                case "preview_purge_unused":    return PreviewPurgeUnused(uiApp);
                case "purge_unused":            return PurgeUnused(uiApp, a);
                case "list_links":              return ListLinks(uiApp);
                case "load_revit_link":         return LoadRevitLink(uiApp, a);
                case "reload_link":             return ReloadLink(uiApp, a);

                // --- Estrutura ---
                case "create_beam":             return CreateBeam(uiApp, a);
                case "create_foundation":       return CreateFoundation(uiApp, a);

                // --- MEP ---
                case "list_mep_types":          return ListMepTypes(uiApp);
                case "create_mep_run":          return CreateMepRun(uiApp, a);
                case "create_spaces_from_rooms": return CreateSpacesFromRooms(uiApp, a);

                // --- Modelagem geral ---
                case "array_elements":          return ArrayElements(uiApp, a);
                case "create_room_separators":  return CreateRoomSeparators(uiApp, a);

                default:
                    throw new ArgumentException($"Comando desconhecido: {req.cmd}");
            }
        }

        // =====================================================================
        // Utilitários compartilhados
        // =====================================================================

        private static bool ProHas(JsonElement args, string name, out JsonElement value)
        {
            value = default;
            return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out value)
                && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined;
        }

        private static bool ProBool(JsonElement args, string name, bool fallback)
        {
            if (ProHas(args, name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                return v.GetBoolean();
            return fallback;
        }

        private static List<ElementId> ProIds(JsonElement args, string name, bool required = false)
        {
            if (!ProHas(args, name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                if (required) throw new ArgumentException($"Array '{name}' com IDs de elementos é obrigatório.");
                return new List<ElementId>();
            }
            var ids = arr.EnumerateArray().Select(v => new ElementId(v.GetInt64())).Distinct().ToList();
            if (required && ids.Count == 0) throw new ArgumentException($"Array '{name}' não pode ser vazio.");
            return ids;
        }

        private static List<string> ProStrings(JsonElement args, string name)
        {
            if (!ProHas(args, name, out var v)) return new List<string>();
            if (v.ValueKind == JsonValueKind.String) return new List<string> { v.GetString() };
            if (v.ValueKind != JsonValueKind.Array) return new List<string>();
            return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList();
        }

        private static BuiltInCategory ProCategory(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Categoria obrigatória (ex.: Walls, Doors, Ducts, StructuralFraming).");
            if (!TryResolveAnyCategory(name.Replace(" ", ""), out var bic) || bic == BuiltInCategory.INVALID)
                throw new ArgumentException($"Categoria desconhecida: {name}. Use o nome da BuiltInCategory sem 'OST_' (ex.: Walls, Floors, StructuralColumns, PipeCurves).");
            return bic;
        }

        private static List<BuiltInCategory> ProCategories(JsonElement args, string name)
            => ProStrings(args, name).Select(ProCategory).Distinct().ToList();

        private static T ProFindType<T>(Document doc, string contains, Func<T, bool> predicate = null) where T : Element
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
                .Where(t => predicate == null || predicate(t)).ToList();
            if (string.IsNullOrWhiteSpace(contains)) return all.OrderBy(t => t.Name).FirstOrDefault();
            var alvo = Normalizar(contains);
            return all.FirstOrDefault(t => Normalizar(t.Name) == alvo) ?? all.FirstOrDefault(t => Normalizar(t.Name).Contains(alvo));
        }

        /// <summary>FamilySymbol de uma categoria por nome de família/tipo, ativado para uso.</summary>
        private static FamilySymbol ProSymbol(Document doc, BuiltInCategory bic, string family, string type)
        {
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(bic)
                .Cast<FamilySymbol>().ToList();
            if (!string.IsNullOrWhiteSpace(family))
            {
                var f = Normalizar(family);
                symbols = symbols.Where(s => Normalizar(s.Family.Name).Contains(f)).ToList();
            }
            if (!string.IsNullOrWhiteSpace(type))
            {
                var t = Normalizar(type);
                symbols = symbols.Where(s => Normalizar(s.Name).Contains(t)).ToList();
            }
            var symbol = symbols.OrderBy(s => s.Family.Name).ThenBy(s => s.Name).FirstOrDefault();
            if (symbol == null)
                throw new ArgumentException($"Nenhuma família carregada em {bic}" +
                    (string.IsNullOrWhiteSpace(family) && string.IsNullOrWhiteSpace(type) ? "" : $" com família '{family}' / tipo '{type}'") +
                    ". Carregue uma com load_family.");
            if (!symbol.IsActive) symbol.Activate();
            return symbol;
        }

        private static View ProView(UIApplication uiApp, Document doc, JsonElement args, string name = "view_id")
        {
            if (ProHas(args, name, out var v) && v.ValueKind == JsonValueKind.Number)
                return doc.GetElement(new ElementId(v.GetInt64())) as View
                    ?? throw new ArgumentException($"{name} {v.GetInt64()} não é uma vista.");
            return uiApp.ActiveUIDocument?.ActiveView ?? throw new Exception("Nenhuma vista ativa.");
        }

        private static string ProLevelName(Document doc, Element e)
        {
            var id = e.LevelId;
            if (id == null || id == ElementId.InvalidElementId)
            {
                var p = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                id = p?.AsElementId();
            }
            return id != null && id != ElementId.InvalidElementId ? doc.GetElement(id)?.Name : null;
        }

        private static string ProTypeName(Document doc, Element e)
        {
            var typeId = e.GetTypeId();
            return typeId != ElementId.InvalidElementId ? doc.GetElement(typeId)?.Name : null;
        }

        private static readonly HashSet<string> LengthUnits = new HashSet<string>
        {
            UnitTypeId.Millimeters.TypeId, UnitTypeId.Centimeters.TypeId, UnitTypeId.Decimeters.TypeId,
            UnitTypeId.Meters.TypeId, UnitTypeId.MetersCentimeters.TypeId, UnitTypeId.Feet.TypeId,
            UnitTypeId.Inches.TypeId, UnitTypeId.FeetFractionalInches.TypeId, UnitTypeId.FractionalInches.TypeId
        };

        /// <summary>
        /// Unidade SI usada pelo MCP para uma grandeza: comprimentos em m, áreas em m²,
        /// volumes em m³, ângulos em graus; demais grandezas na unidade de exibição do projeto.
        /// </summary>
        private static ForgeTypeId ProMcpUnit(Document doc, ForgeTypeId spec)
        {
            if (spec == null || !UnitUtils.IsMeasurableSpec(spec)) return null;
            if (spec == SpecTypeId.Area) return UnitTypeId.SquareMeters;
            if (spec == SpecTypeId.Volume) return UnitTypeId.CubicMeters;
            if (spec == SpecTypeId.Angle) return UnitTypeId.Degrees;
            var display = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
            if (spec == SpecTypeId.Length || LengthUnits.Contains(display.TypeId)) return UnitTypeId.Meters;
            return display;
        }

        private static ForgeTypeId ProSpec(Parameter p)
        {
            try { return p.Definition.GetDataType(); } catch { return null; }
        }

        /// <summary>Valor do parâmetro em unidades MCP (número), texto ou nome do elemento referenciado.</summary>
        private static object ProParamValue(Document doc, Parameter p)
        {
            if (p == null || !p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.Integer:
                    return ProSpec(p) == SpecTypeId.Boolean.YesNo ? (object)(p.AsInteger() == 1) : p.AsInteger();
                case StorageType.Double:
                    var unit = ProMcpUnit(doc, ProSpec(p));
                    var raw = p.AsDouble();
                    return Math.Round(unit != null ? UnitUtils.ConvertFromInternalUnits(raw, unit) : raw, 6);
                case StorageType.ElementId:
                    var id = p.AsElementId();
                    if (id == ElementId.InvalidElementId) return null;
                    return doc.GetElement(id)?.Name ?? (object)id.Value;
                default: return p.AsValueString();
            }
        }

        private static double? ProParamNumber(Document doc, Parameter p)
        {
            var v = ProParamValue(doc, p);
            if (v is double d) return d;
            if (v is int i) return i;
            if (v is bool b) return b ? 1 : 0;
            if (v is string s && double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return null;
        }

        private static string ProParamText(Document doc, Parameter p)
        {
            if (p == null || !p.HasValue) return null;
            return p.StorageType == StorageType.String ? p.AsString() : (p.AsValueString() ?? ProParamValue(doc, p)?.ToString());
        }

        /// <summary>Define um parâmetro a partir de JSON. Retorna null se OK ou o motivo da falha.</summary>
        private static string ProSetParam(Document doc, Parameter p, JsonElement value)
        {
            if (p == null) return "não encontrado";
            if (p.IsReadOnly) return "somente leitura";
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        return p.Set(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()) ? null : "valor rejeitado";
                    case StorageType.Integer:
                        int iv;
                        if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) iv = value.GetBoolean() ? 1 : 0;
                        else if (value.ValueKind == JsonValueKind.Number) iv = (int)Math.Round(value.GetDouble());
                        else if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)) iv = parsed;
                        else if (value.ValueKind == JsonValueKind.String) return p.SetValueString(value.GetString()) ? null : "valor rejeitado";
                        else return "esperado número inteiro ou booleano";
                        return p.Set(iv) ? null : "valor rejeitado";
                    case StorageType.Double:
                        if (value.ValueKind == JsonValueKind.String)
                            return p.SetValueString(value.GetString()) ? null : "texto não reconhecido nas unidades do projeto";
                        if (value.ValueKind != JsonValueKind.Number) return "esperado número";
                        var unit = ProMcpUnit(doc, ProSpec(p));
                        var dv = value.GetDouble();
                        return p.Set(unit != null ? UnitUtils.ConvertToInternalUnits(dv, unit) : dv) ? null : "valor rejeitado";
                    case StorageType.ElementId:
                        if (value.ValueKind == JsonValueKind.Number) return p.Set(new ElementId(value.GetInt64())) ? null : "ID rejeitado";
                        if (value.ValueKind == JsonValueKind.String) return p.SetValueString(value.GetString()) ? null : "nome não reconhecido; informe o ID";
                        return "esperado ID de elemento";
                    default:
                        return "tipo de parâmetro não suportado";
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static Parameter ProLookup(Document doc, Element e, string name, bool includeType = true)
        {
            var p = e.LookupParameter(name);
            if (p == null && Enum.TryParse<BuiltInParameter>(name, true, out var bip)) p = e.get_Parameter(bip);
            if (p == null && includeType)
            {
                var type = doc.GetElement(e.GetTypeId());
                if (type != null)
                {
                    p = type.LookupParameter(name);
                    if (p == null && Enum.TryParse<BuiltInParameter>(name, true, out var tbip)) p = type.get_Parameter(tbip);
                }
            }
            return p;
        }

        private static Color ProColor(JsonElement args, string name)
        {
            var s = GetS(args, name);
            return string.IsNullOrWhiteSpace(s) ? null : ParseMcpColor(s);
        }

        private static ElementId ProSolidFill(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id ?? ElementId.InvalidElementId;
        }

        private static OverrideGraphicSettings ProOverrides(Document doc, JsonElement args)
        {
            var ogs = new OverrideGraphicSettings();
            var color = ProColor(args, "color");
            if (color != null)
            {
                ogs.SetProjectionLineColor(color);
                ogs.SetCutLineColor(color);
                if (ProBool(args, "fill", true))
                {
                    var solid = ProSolidFill(doc);
                    if (solid != ElementId.InvalidElementId)
                    {
                        ogs.SetSurfaceForegroundPatternId(solid);
                        ogs.SetSurfaceForegroundPatternColor(color);
                        ogs.SetCutForegroundPatternId(solid);
                        ogs.SetCutForegroundPatternColor(color);
                    }
                }
            }
            if (ProBool(args, "halftone", false)) ogs.SetHalftone(true);
            int transparency = (int)GetD(args, "transparency", -1);
            if (transparency >= 0) ogs.SetSurfaceTransparency(Math.Clamp(transparency, 0, 100));
            int weight = (int)GetD(args, "line_weight", -1);
            if (weight > 0) { ogs.SetProjectionLineWeight(Math.Clamp(weight, 1, 16)); ogs.SetCutLineWeight(Math.Clamp(weight, 1, 16)); }
            return ogs;
        }

        private static XYZ ProElementPoint(Element e, View view = null)
        {
            if (e.Location is LocationPoint lp) return lp.Point;
            if (e.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
            var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
            return bb != null ? (bb.Min + bb.Max) / 2 : null;
        }

        private static object ProPoint(XYZ p) => p == null ? null : new
        {
            x_m = Math.Round(FtToM(p.X), 4),
            y_m = Math.Round(FtToM(p.Y), 4),
            z_m = Math.Round(FtToM(p.Z), 4)
        };

        private static List<XYZ> ProPoints(JsonElement args, string name, double z)
        {
            if (!ProHas(args, name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"Array '{name}' de pontos {{x_m, y_m}} é obrigatório.");
            var pts = new List<XYZ>();
            foreach (var p in arr.EnumerateArray())
            {
                double x, y, pz = double.NaN;
                if (p.ValueKind == JsonValueKind.Array)
                {
                    var c = p.EnumerateArray().ToList();
                    if (c.Count < 2) throw new ArgumentException("Cada ponto precisa de x e y.");
                    x = c[0].GetDouble(); y = c[1].GetDouble(); if (c.Count > 2) pz = c[2].GetDouble();
                }
                else
                {
                    x = GetItemD(p, "x_m", double.NaN); y = GetItemD(p, "y_m", double.NaN); pz = GetItemD(p, "z_m", double.NaN);
                    if (double.IsNaN(x) || double.IsNaN(y)) throw new ArgumentException("Cada ponto precisa de x_m e y_m.");
                }
                pts.Add(new XYZ(MToFt(x), MToFt(y), double.IsNaN(pz) ? z : MToFt(pz)));
            }
            return pts;
        }

        private static Transaction ProTx(Document doc, string name)
        {
            var tx = new Transaction(doc, "MCP: " + name);
            tx.Start();
            var options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new McpWarningPreprocessor());
            tx.SetFailureHandlingOptions(options);
            return tx;
        }

        // =====================================================================
        // DADOS DO PROJETO
        // =====================================================================

        private static object GetProjectInfo(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var pi = doc.ProjectInformation;
            var extras = pi.Parameters.Cast<Parameter>()
                .Where(p => p.Definition != null && p.HasValue)
                .GroupBy(p => p.Definition.Name)
                .ToDictionary(g => g.Key, g => ProParamValue(doc, g.First()));
            return new
            {
                nome = pi.Name,
                numero = pi.Number,
                cliente = pi.ClientName,
                endereco = pi.Address,
                status = pi.Status,
                data_emissao = pi.IssueDate,
                edificio = pi.BuildingName,
                autor = pi.Author,
                organizacao = pi.OrganizationName,
                arquivo = doc.PathName,
                workshared = doc.IsWorkshared,
                parametros = extras
            };
        }

        private static object SetProjectInfo(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var pi = doc.ProjectInformation;
            var known = new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = v => pi.Name = v, ["number"] = v => pi.Number = v, ["client"] = v => pi.ClientName = v,
                ["address"] = v => pi.Address = v, ["status"] = v => pi.Status = v, ["issue_date"] = v => pi.IssueDate = v,
                ["building_name"] = v => pi.BuildingName = v, ["author"] = v => pi.Author = v,
                ["organization"] = v => pi.OrganizationName = v
            };
            var resultado = new List<object>();
            using (var tx = ProTx(doc, "Informações do projeto"))
            {
                foreach (var key in known.Keys.ToList())
                {
                    var s = GetS(args, key);
                    if (s == null) continue;
                    known[key](s);
                    resultado.Add(new { campo = key, ok = true });
                }
                if (ProHas(args, "params", out var pmap) && pmap.ValueKind == JsonValueKind.Object)
                    foreach (var prop in pmap.EnumerateObject())
                    {
                        var erro = ProSetParam(doc, pi.LookupParameter(prop.Name), prop.Value);
                        resultado.Add(new { campo = prop.Name, ok = erro == null, motivo = erro });
                    }
                if (resultado.Count == 0)
                    throw new ArgumentException("Nada a alterar. Informe name, number, client, address, status, issue_date, building_name, author, organization ou params.");
                tx.Commit();
            }
            return new { alterados = resultado };
        }

        private static object GetProjectLocation(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var pbp = BasePoint.GetProjectBasePoint(doc);
            var sp = BasePoint.GetSurveyPoint(doc);
            var position = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            var site = doc.SiteLocation;
            return new
            {
                ponto_base_projeto = new { interno = ProPoint(pbp?.Position), compartilhado = ProPoint(pbp?.SharedPosition) },
                ponto_levantamento = new { interno = ProPoint(sp?.Position), compartilhado = ProPoint(sp?.SharedPosition) },
                posicao_compartilhada = new
                {
                    leste_oeste_m = Math.Round(FtToM(position.EastWest), 4),
                    norte_sul_m = Math.Round(FtToM(position.NorthSouth), 4),
                    elevacao_m = Math.Round(FtToM(position.Elevation), 4),
                    angulo_norte_verdadeiro_graus = Math.Round(position.Angle * 180.0 / Math.PI, 4)
                },
                local = site == null ? null : new
                {
                    nome = site.PlaceName,
                    latitude_graus = Math.Round(site.Latitude * 180.0 / Math.PI, 6),
                    longitude_graus = Math.Round(site.Longitude * 180.0 / Math.PI, 6),
                    fuso_horario = site.TimeZone
                },
                localizacao_ativa = doc.ActiveProjectLocation.Name
            };
        }


        private static Phase ProPhase(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var alvo = Normalizar(name);
            return doc.Phases.Cast<Phase>().FirstOrDefault(p => Normalizar(p.Name) == alvo)
                ?? doc.Phases.Cast<Phase>().FirstOrDefault(p => Normalizar(p.Name).Contains(alvo))
                ?? throw new ArgumentException($"Fase '{name}' não encontrada. Use list_phases.");
        }


        private static object CreateWorkset(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!doc.IsWorkshared) throw new InvalidOperationException("O modelo não é workshared. Habilite o worksharing no Revit antes de criar worksets.");
            var names = ProStrings(args, "names");
            if (names.Count == 0 && GetS(args, "name") != null) names.Add(GetS(args, "name"));
            if (names.Count == 0) throw new ArgumentException("Informe name ou names.");
            var resultado = new List<object>();
            using (var tx = ProTx(doc, "Criar worksets"))
            {
                foreach (var name in names)
                {
                    if (!WorksetTable.IsWorksetNameUnique(doc, name)) { resultado.Add(new { nome = name, ok = false, motivo = "já existe" }); continue; }
                    var ws = Workset.Create(doc, name);
                    resultado.Add(new { nome = name, ok = true, id = ws.Id.IntegerValue });
                }
                tx.Commit();
            }
            return new { worksets = resultado };
        }

        private static object SetElementWorkset(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!doc.IsWorkshared) throw new InvalidOperationException("O modelo não é workshared.");
            var ids = ProIds(args, "ids", required: true);
            string name = GetS(args, "workset") ?? throw new ArgumentException("Argumento 'workset' obrigatório.");
            var alvo = Normalizar(name);
            var ws = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets()
                .FirstOrDefault(w => Normalizar(w.Name) == alvo)
                ?? throw new ArgumentException($"Workset '{name}' não encontrado. Use get_worksharing_status ou create_workset.");
            var resultado = new List<object>();
            using (var tx = ProTx(doc, "Definir workset"))
            {
                foreach (var id in ids)
                {
                    var p = doc.GetElement(id)?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    string erro = p == null ? "elemento sem workset editável" : p.IsReadOnly ? "somente leitura" : null;
                    if (erro == null)
                    {
                        try { if (!p.Set(ws.Id.IntegerValue)) erro = "valor rejeitado"; }
                        catch (Exception ex) { erro = ex.Message; }
                    }
                    resultado.Add(new { id = id.Value, ok = erro == null, motivo = erro });
                }
                tx.Commit();
            }
            return new { workset = ws.Name, elementos = resultado };
        }






        // ---------------------------------------------------------------------
        // Consulta e edição em massa por parâmetro
        // ---------------------------------------------------------------------

        private static bool ProMatches(Document doc, Element e, JsonElement cond)
        {
            string pname = GetS(cond, "parameter") ?? throw new ArgumentException("Cada condição precisa de 'parameter'.");
            string op = (GetS(cond, "op", "equals") ?? "equals").ToLowerInvariant();
            var p = ProLookup(doc, e, pname);
            if (op == "empty") return p == null || !p.HasValue || string.IsNullOrEmpty(ProParamText(doc, p));
            if (op == "not_empty") return p != null && p.HasValue && !string.IsNullOrEmpty(ProParamText(doc, p));
            if (p == null) return op == "not_equals" || op == "not_contains";
            if (!cond.TryGetProperty("value", out var value)) throw new ArgumentException($"Condição '{op}' em '{pname}' precisa de 'value'.");

            if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            {
                double target = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : (value.GetBoolean() ? 1 : 0);
                var actual = ProParamNumber(doc, p);
                if (actual == null) return op == "not_equals";
                const double eps = 1e-6;
                switch (op)
                {
                    case "equals": return Math.Abs(actual.Value - target) < eps;
                    case "not_equals": return Math.Abs(actual.Value - target) >= eps;
                    case "gt": return actual.Value > target + eps;
                    case "gte": return actual.Value >= target - eps;
                    case "lt": return actual.Value < target - eps;
                    case "lte": return actual.Value <= target + eps;
                    default: throw new ArgumentException($"Operador '{op}' não se aplica a números.");
                }
            }

            string text = Normalizar(ProParamText(doc, p) ?? "");
            string wanted = Normalizar(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString());
            switch (op)
            {
                case "equals": return text == wanted;
                case "not_equals": return text != wanted;
                case "contains": return text.Contains(wanted);
                case "not_contains": return !text.Contains(wanted);
                case "starts_with": return text.StartsWith(wanted);
                default: throw new ArgumentException($"Operador desconhecido: {op}. Use equals, not_equals, contains, not_contains, starts_with, gt, gte, lt, lte, empty, not_empty.");
            }
        }

        private static List<Element> ProQuery(Document doc, JsonElement args)
        {
            var categories = ProCategories(args, "categories");
            var single = GetS(args, "category");
            if (single != null) categories.Add(ProCategory(single));
            if (categories.Count == 0) throw new ArgumentException("Informe 'category' ou 'categories'.");

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            collector = categories.Count == 1
                ? collector.OfCategory(categories[0])
                : collector.WherePasses(new ElementMulticategoryFilter(categories));
            IEnumerable<Element> query = collector.ToElements();

            string level = GetS(args, "level");
            if (level != null)
            {
                var alvo = Normalizar(level);
                query = query.Where(e => Normalizar(ProLevelName(doc, e) ?? "") == alvo);
            }
            string type = GetS(args, "type_contains");
            if (type != null)
            {
                var alvo = Normalizar(type);
                query = query.Where(e => Normalizar(ProTypeName(doc, e) ?? "").Contains(alvo));
            }
            if (ProHas(args, "where", out var where) && where.ValueKind == JsonValueKind.Array)
            {
                var conds = where.EnumerateArray().ToList();
                query = query.Where(e => conds.All(c => ProMatches(doc, e, c)));
            }
            return query.ToList();
        }

        private static object FindElements(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            int limit = Math.Clamp((int)GetD(args, "limit", 200), 1, 5000);
            var fields = ProStrings(args, "return_parameters");
            var all = ProQuery(doc, args);
            var elementos = all.Take(limit).Select(e => new
            {
                id = e.Id.Value,
                nome = e.Name,
                categoria = e.Category?.Name,
                tipo = ProTypeName(doc, e),
                nivel = ProLevelName(doc, e),
                parametros = fields.Count == 0 ? null : fields.ToDictionary(f => f, f => ProParamValue(doc, ProLookup(doc, e, f)))
            }).ToList();
            return new { total = all.Count, retornados = elementos.Count, elementos };
        }

        private static object SetParametersBulk(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!ProHas(args, "params", out var pmap) || pmap.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Objeto 'params' {nome: valor} é obrigatório.");
            bool onType = string.Equals(GetS(args, "target", "instance"), "type", StringComparison.OrdinalIgnoreCase);
            List<Element> elements;
            var ids = ProIds(args, "ids");
            if (ids.Count > 0) elements = ids.Select(doc.GetElement).Where(e => e != null).ToList();
            else if (ProHas(args, "filter", out var filter) && filter.ValueKind == JsonValueKind.Object) elements = ProQuery(doc, filter);
            else throw new ArgumentException("Informe 'ids' ou 'filter' (mesmos campos de find_elements).");

            if (onType)
                elements = elements.Select(e => doc.GetElement(e.GetTypeId())).Where(t => t != null)
                    .GroupBy(t => t.Id.Value).Select(g => g.First()).ToList();
            if (elements.Count == 0) return new { alterados = 0, falhas = Array.Empty<object>() };

            int ok = 0;
            var falhas = new List<object>();
            using (var tx = ProTx(doc, "Parâmetros em massa"))
            {
                foreach (var e in elements)
                {
                    bool all = true;
                    foreach (var prop in pmap.EnumerateObject())
                    {
                        var erro = ProSetParam(doc, ProLookup(doc, e, prop.Name, includeType: false), prop.Value);
                        if (erro != null) { all = false; if (falhas.Count < 100) falhas.Add(new { id = e.Id.Value, parametro = prop.Name, motivo = erro }); }
                    }
                    if (all) ok++;
                }
                tx.Commit();
            }
            return new { alvo = onType ? "tipo" : "instancia", elementos = elements.Count, alterados = ok, falhas };
        }

        // =====================================================================
        // VISTAS
        // =====================================================================

        private static object DuplicateView(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = ProView(uiApp, doc, args);
            var option = (GetS(args, "mode", "with_detailing") ?? "").ToLowerInvariant() switch
            {
                "duplicate" => ViewDuplicateOption.Duplicate,
                "dependent" => ViewDuplicateOption.AsDependent,
                _ => ViewDuplicateOption.WithDetailing
            };
            if (!view.CanViewBeDuplicated(option)) throw new InvalidOperationException($"A vista '{view.Name}' não pode ser duplicada no modo {option}.");
            int copies = Math.Clamp((int)GetD(args, "copies", 1), 1, 50);
            var names = ProStrings(args, "names");
            var criadas = new List<object>();
            using (var tx = ProTx(doc, "Duplicar vista"))
            {
                for (int i = 0; i < copies; i++)
                {
                    var newView = (View)doc.GetElement(view.Duplicate(option));
                    string name = i < names.Count ? names[i] : (i == 0 ? GetS(args, "name") : null);
                    if (!string.IsNullOrWhiteSpace(name)) newView.Name = name;
                    criadas.Add(new { id = newView.Id.Value, nome = newView.Name });
                }
                tx.Commit();
            }
            return new { origem = view.Name, modo = option.ToString(), vistas = criadas };
        }

        private static object SetViewProperties(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = ProView(uiApp, doc, args);
            var alteracoes = new List<string>();
            using (var tx = ProTx(doc, "Propriedades da vista"))
            {
                var name = GetS(args, "name");
                if (name != null) { view.Name = name; alteracoes.Add("nome"); }
                int scale = (int)GetD(args, "scale", 0);
                if (scale > 0) { view.Scale = scale; alteracoes.Add("escala"); }
                var detail = GetS(args, "detail_level");
                if (detail != null)
                {
                    view.DetailLevel = detail.ToLowerInvariant() switch
                    {
                        "coarse" or "baixo" => ViewDetailLevel.Coarse,
                        "fine" or "alto" => ViewDetailLevel.Fine,
                        _ => ViewDetailLevel.Medium
                    };
                    alteracoes.Add("nivel_detalhe");
                }
                var discipline = GetS(args, "discipline");
                if (discipline != null)
                {
                    if (!Enum.TryParse<ViewDiscipline>(discipline, true, out var d))
                        throw new ArgumentException("discipline deve ser Architectural, Structural, Mechanical, Electrical, Plumbing ou Coordination.");
                    view.Discipline = d; alteracoes.Add("disciplina");
                }
                var phase = GetS(args, "phase");
                if (phase != null)
                {
                    var p = view.get_Parameter(BuiltInParameter.VIEW_PHASE) ?? throw new InvalidOperationException("A vista não tem parâmetro de fase.");
                    p.Set(ProPhase(doc, phase).Id); alteracoes.Add("fase");
                }
                if (ProHas(args, "crop", out var crop) && (crop.ValueKind == JsonValueKind.True || crop.ValueKind == JsonValueKind.False))
                { view.CropBoxActive = crop.GetBoolean(); alteracoes.Add("recorte"); }
                if (ProHas(args, "crop_visible", out var cv) && (cv.ValueKind == JsonValueKind.True || cv.ValueKind == JsonValueKind.False))
                { view.CropBoxVisible = cv.GetBoolean(); alteracoes.Add("recorte_visivel"); }
                if (ProHas(args, "template_id", out var tv) && tv.ValueKind == JsonValueKind.Number)
                { view.ViewTemplateId = new ElementId(tv.GetInt64()); alteracoes.Add("template"); }
                if (alteracoes.Count == 0) throw new ArgumentException("Nada a alterar. Use name, scale, detail_level, discipline, phase, crop, crop_visible ou template_id.");
                tx.Commit();
            }
            return new { id = view.Id.Value, nome = view.Name, alteracoes };
        }

        private static object CreateElevations(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var plan = ProView(uiApp, doc, args, "plan_view_id") as ViewPlan
                ?? throw new InvalidOperationException("Elevações precisam de uma vista de planta (ative uma planta ou informe plan_view_id).");
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(v => v.ViewFamily == ViewFamily.Elevation)
                .OrderBy(v => Normalizar(v.Name).Contains("interior") == ProBool(args, "interior", false) ? 0 : 1)
                .FirstOrDefault() ?? throw new InvalidOperationException("Nenhum tipo de vista de elevação no projeto.");
            var center = new XYZ(MToFt(GetD(args, "x_m", 0)), MToFt(GetD(args, "y_m", 0)), plan.GenLevel?.Elevation ?? 0);
            int scale = (int)GetD(args, "scale", 100);
            string prefix = GetS(args, "name_prefix", ProBool(args, "interior", false) ? "Elevação interna" : "Elevação");
            var wanted = ProStrings(args, "directions").Select(Normalizar).ToList();
            var criadas = new List<object>();
            using (var tx = ProTx(doc, "Criar elevações"))
            {
                var marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, center, scale);
                for (int i = 0; i < 4; i++)
                {
                    if (!marker.IsAvailableIndex(i)) continue;
                    var view = marker.CreateElevation(doc, plan.Id, i);
                    // ViewDirection aponta para o observador; a direção de visada é o oposto.
                    var look = view.ViewDirection.Negate();
                    string dir = Math.Abs(look.X) > Math.Abs(look.Y) ? (look.X > 0 ? "Leste" : "Oeste") : (look.Y > 0 ? "Norte" : "Sul");
                    if (wanted.Count > 0 && !wanted.Contains(Normalizar(dir)))
                    {
                        doc.Delete(view.Id);
                        continue;
                    }
                    try { view.Name = $"{prefix} {dir}"; } catch { /* nome em uso: mantém o automático */ }
                    criadas.Add(new { id = view.Id.Value, nome = view.Name, olhando_para = dir });
                }
                tx.Commit();
                return new { marcador = marker.Id.Value, vistas = criadas };
            }
        }

        private static BoundingBoxXYZ ProUnionBox(Document doc, IEnumerable<ElementId> ids, double marginFt)
        {
            XYZ min = null, max = null;
            foreach (var id in ids)
            {
                var bb = doc.GetElement(id)?.get_BoundingBox(null);
                if (bb == null) continue;
                min = min == null ? bb.Min : new XYZ(Math.Min(min.X, bb.Min.X), Math.Min(min.Y, bb.Min.Y), Math.Min(min.Z, bb.Min.Z));
                max = max == null ? bb.Max : new XYZ(Math.Max(max.X, bb.Max.X), Math.Max(max.Y, bb.Max.Y), Math.Max(max.Z, bb.Max.Z));
            }
            if (min == null) return null;
            var m = new XYZ(marginFt, marginFt, marginFt);
            return new BoundingBoxXYZ { Min = min - m, Max = max + m };
        }

        private static object Create3DView(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional)
                ?? throw new InvalidOperationException("Nenhum tipo de vista 3D no projeto.");
            View3D view;
            using (var tx = ProTx(doc, "Criar vista 3D"))
            {
                view = View3D.CreateIsometric(doc, vft.Id);
                var name = GetS(args, "name");
                if (!string.IsNullOrWhiteSpace(name)) view.Name = name;
                var ids = ProIds(args, "element_ids");
                BoundingBoxXYZ box = null;
                if (ids.Count > 0) box = ProUnionBox(doc, ids, MToFt(GetD(args, "margin_m", 1.0)));
                else if (ProHas(args, "x_min_m", out _))
                    box = new BoundingBoxXYZ
                    {
                        Min = new XYZ(MToFt(GetD(args, "x_min_m", 0)), MToFt(GetD(args, "y_min_m", 0)), MToFt(GetD(args, "z_min_m", -1))),
                        Max = new XYZ(MToFt(GetD(args, "x_max_m", 10)), MToFt(GetD(args, "y_max_m", 10)), MToFt(GetD(args, "z_max_m", 10)))
                    };
                if (box != null) { view.SetSectionBox(box); view.IsSectionBoxActive = true; }
                var hide = ProCategories(args, "hide_categories");
                foreach (var bic in hide)
                {
                    var cat = Category.GetCategory(doc, bic);
                    if (cat != null && view.CanCategoryBeHidden(cat.Id)) view.SetCategoryHidden(cat.Id, true);
                }
                tx.Commit();
            }
            if (ProBool(args, "activate", true)) uiApp.ActiveUIDocument.ActiveView = view;
            return new { id = view.Id.Value, nome = view.Name, caixa_de_corte = view.IsSectionBoxActive };
        }

        private static object CreateDraftingView(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting)
                ?? throw new InvalidOperationException("Nenhum tipo de vista de desenho no projeto.");
            using (var tx = ProTx(doc, "Criar vista de desenho"))
            {
                var view = ViewDrafting.Create(doc, vft.Id);
                var name = GetS(args, "name");
                if (!string.IsNullOrWhiteSpace(name)) view.Name = name;
                view.Scale = Math.Max(1, (int)GetD(args, "scale", 20));
                tx.Commit();
                return new { id = view.Id.Value, nome = view.Name, escala = view.Scale };
            }
        }

        private static ElementId ProFindParameterId(Document doc, IList<BuiltInCategory> categories, string name)
        {
            if (Enum.TryParse<BuiltInParameter>(name, true, out var bip)) return new ElementId(bip);
            foreach (var bic in categories)
            {
                foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().Take(20))
                {
                    var p = ProLookup(doc, e, name);
                    if (p != null) return p.Id;
                }
                foreach (var t in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType().Take(20))
                {
                    var p = t.LookupParameter(name);
                    if (p != null) return p.Id;
                }
            }
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
                if (string.Equals(it.Key.Name, name, StringComparison.OrdinalIgnoreCase) && it.Key is InternalDefinition def)
                    return def.Id;
            throw new ArgumentException($"Parâmetro '{name}' não encontrado nas categorias informadas.");
        }

        private static FilterRule ProFilterRule(Document doc, IList<BuiltInCategory> categories, JsonElement rule)
        {
            string pname = GetS(rule, "parameter") ?? throw new ArgumentException("Regra precisa de 'parameter'.");
            string op = (GetS(rule, "op", "equals") ?? "equals").ToLowerInvariant();
            var pid = ProFindParameterId(doc, categories, pname);
            if (op == "has_value") return ParameterFilterRuleFactory.CreateHasValueParameterRule(pid);
            if (op == "has_no_value") return ParameterFilterRuleFactory.CreateHasNoValueParameterRule(pid);
            if (!rule.TryGetProperty("value", out var value)) throw new ArgumentException($"Regra '{op}' precisa de 'value'.");
            if (value.ValueKind == JsonValueKind.String)
            {
                var s = value.GetString();
                return op switch
                {
                    "equals" => ParameterFilterRuleFactory.CreateEqualsRule(pid, s),
                    "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(pid, s),
                    "contains" => ParameterFilterRuleFactory.CreateContainsRule(pid, s),
                    "not_contains" => ParameterFilterRuleFactory.CreateNotContainsRule(pid, s),
                    "starts_with" => ParameterFilterRuleFactory.CreateBeginsWithRule(pid, s),
                    _ => throw new ArgumentException($"Operador '{op}' inválido para texto.")
                };
            }
            if (value.ValueKind != JsonValueKind.Number) throw new ArgumentException("value deve ser texto ou número.");
            // Números em unidades MCP (m, m², m³, graus) convertidos pela grandeza do parâmetro.
            double v = value.GetDouble();
            double internalValue = v, eps = 1e-6;
            ForgeTypeId spec = null;
            foreach (var bic in categories)
            {
                var sample = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().FirstElement();
                var p = sample == null ? null : ProLookup(doc, sample, pname);
                if (p != null)
                {
                    if (p.StorageType == StorageType.Integer)
                    {
                        int iv = (int)Math.Round(v);
                        return op switch
                        {
                            "equals" => ParameterFilterRuleFactory.CreateEqualsRule(pid, iv),
                            "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(pid, iv),
                            "gt" => ParameterFilterRuleFactory.CreateGreaterRule(pid, iv),
                            "gte" => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, iv),
                            "lt" => ParameterFilterRuleFactory.CreateLessRule(pid, iv),
                            "lte" => ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, iv),
                            _ => throw new ArgumentException($"Operador '{op}' inválido para inteiro.")
                        };
                    }
                    spec = ProSpec(p);
                    break;
                }
            }
            var unit = ProMcpUnit(doc, spec);
            if (unit != null) { internalValue = UnitUtils.ConvertToInternalUnits(v, unit); eps = Math.Max(1e-9, Math.Abs(internalValue) * 1e-6); }
            return op switch
            {
                "equals" => ParameterFilterRuleFactory.CreateEqualsRule(pid, internalValue, eps),
                "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(pid, internalValue, eps),
                "gt" => ParameterFilterRuleFactory.CreateGreaterRule(pid, internalValue, eps),
                "gte" => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, internalValue, eps),
                "lt" => ParameterFilterRuleFactory.CreateLessRule(pid, internalValue, eps),
                "lte" => ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, internalValue, eps),
                _ => throw new ArgumentException($"Operador '{op}' inválido para número.")
            };
        }

        private static object CreateRuleFilter(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string name = GetS(args, "name") ?? throw new ArgumentException("Argumento 'name' obrigatório.");
            var categories = ProCategories(args, "categories");
            if (categories.Count == 0) throw new ArgumentException("Array 'categories' obrigatório.");
            var catIds = categories.Select(c => new ElementId(c)).ToList();

            var rules = new List<FilterRule>();
            if (ProHas(args, "rules", out var rarr) && rarr.ValueKind == JsonValueKind.Array)
                foreach (var r in rarr.EnumerateArray()) rules.Add(ProFilterRule(doc, categories, r));
            bool matchAny = string.Equals(GetS(args, "logic", "and"), "or", StringComparison.OrdinalIgnoreCase);

            var view = ProBool(args, "apply", true) ? ProView(uiApp, doc, args) : null;
            if (view != null && view.ViewTemplateId != ElementId.InvalidElementId)
                throw new InvalidOperationException($"A vista '{view.Name}' é controlada pelo template '{doc.GetElement(view.ViewTemplateId)?.Name}'. Aplique o filtro ao template (view_id do template) ou remova o template.");

            using (var tx = ProTx(doc, "Filtro de vista"))
            {
                var existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                    .Cast<ParameterFilterElement>().FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                ParameterFilterElement filter;
                ElementFilter elementFilter = null;
                if (rules.Count > 0)
                {
                    var parts = rules.Select(r => (ElementFilter)new ElementParameterFilter(r)).ToList();
                    elementFilter = parts.Count == 1 ? parts[0] : matchAny ? new LogicalOrFilter(parts) : (ElementFilter)new LogicalAndFilter(parts);
                }
                if (existing != null)
                {
                    filter = existing;
                    filter.SetCategories(catIds);
                    if (elementFilter != null) filter.SetElementFilter(elementFilter);
                }
                else
                {
                    filter = elementFilter != null
                        ? ParameterFilterElement.Create(doc, name, catIds, elementFilter)
                        : ParameterFilterElement.Create(doc, name, catIds);
                }

                if (view != null)
                {
                    if (!view.IsFilterApplied(filter.Id)) view.AddFilter(filter.Id);
                    view.SetFilterOverrides(filter.Id, ProOverrides(doc, args));
                    view.SetFilterVisibility(filter.Id, ProBool(args, "visible", true));
                }
                tx.Commit();
                return new { id = filter.Id.Value, nome = filter.Name, regras = rules.Count, aplicado_em = view?.Name, atualizado = existing != null };
            }
        }

        private static object OverrideElementsInView(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = ProView(uiApp, doc, args);
            var ids = ProIds(args, "ids", required: true);
            string mode = (GetS(args, "mode", "override") ?? "override").ToLowerInvariant();
            using (var tx = ProTx(doc, "Sobrepor gráficos"))
            {
                switch (mode)
                {
                    case "hide": view.HideElements(ids); break;
                    case "unhide": view.UnhideElements(ids); break;
                    case "isolate": view.IsolateElementsTemporary(ids); break;
                    case "reset":
                        foreach (var id in ids) view.SetElementOverrides(id, new OverrideGraphicSettings());
                        break;
                    default:
                        var ogs = ProOverrides(doc, args);
                        foreach (var id in ids) view.SetElementOverrides(id, ogs);
                        break;
                }
                tx.Commit();
            }
            return new { vista = view.Name, modo = mode, elementos = ids.Count };
        }

        private static object SetCategoryVisibility(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = ProView(uiApp, doc, args);
            var categories = ProCategories(args, "categories");
            if (categories.Count == 0) throw new ArgumentException("Array 'categories' obrigatório.");
            bool visible = ProBool(args, "visible", false);
            var resultado = new List<object>();
            using (var tx = ProTx(doc, "Visibilidade de categorias"))
            {
                foreach (var bic in categories)
                {
                    var cat = Category.GetCategory(doc, bic);
                    if (cat == null || !view.CanCategoryBeHidden(cat.Id)) { resultado.Add(new { categoria = bic.ToString(), ok = false, motivo = "não pode ser alterada nesta vista (template ou categoria)" }); continue; }
                    view.SetCategoryHidden(cat.Id, !visible);
                    resultado.Add(new { categoria = cat.Name, ok = true });
                }
                tx.Commit();
            }
            return new { vista = view.Name, visivel = visible, categorias = resultado };
        }

        private static object TagElements(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = ProView(uiApp, doc, args);
            var bic = ProCategory(GetS(args, "category"));
            bool leader = ProBool(args, "leader", false);
            bool skipTagged = ProBool(args, "skip_tagged", true);
            var offset = new XYZ(MToFt(GetD(args, "offset_x_m", 0)), MToFt(GetD(args, "offset_y_m", 0)), 0);

            var targets = ProIds(args, "ids");
            var elements = targets.Count > 0
                ? targets.Select(doc.GetElement).Where(e => e != null).ToList()
                : new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType().ToElements().ToList();

            var tagged = new HashSet<long>();
            if (skipTagged)
                foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                    foreach (var id in tag.GetTaggedLocalElementIds()) tagged.Add(id.Value);

            var criadas = new List<long>();
            var falhas = new List<object>();
            using (var tx = ProTx(doc, "Etiquetar elementos"))
            {
                foreach (var e in elements)
                {
                    if (tagged.Contains(e.Id.Value)) continue;
                    var pt = ProElementPoint(e, view);
                    if (pt == null) { falhas.Add(new { id = e.Id.Value, motivo = "sem posição" }); continue; }
                    try
                    {
                        var tag = IndependentTag.Create(doc, view.Id, new Reference(e), leader, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, pt + offset);
                        criadas.Add(tag.Id.Value);
                    }
                    catch (Exception ex)
                    {
                        falhas.Add(new { id = e.Id.Value, motivo = ex.Message });
                        // Sem família de etiqueta carregada todas falharão igual.
                        if (falhas.Count >= 5 && criadas.Count == 0) break;
                    }
                }
                tx.Commit();
            }
            return new { vista = view.Name, etiquetas = criadas.Count, ids = criadas, ja_etiquetados = tagged.Count, falhas };
        }

        // =====================================================================
        // PRANCHAS, REVISÕES, TABELAS E EXPORTAÇÃO
        // =====================================================================

        private static object ListSheets(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .OrderBy(s => s.SheetNumber)
                .Select(s => new
                {
                    id = s.Id.Value,
                    numero = s.SheetNumber,
                    nome = s.Name,
                    vistas = s.GetAllPlacedViews().Select(v => doc.GetElement(v)?.Name).ToList(),
                    revisoes = s.GetAllRevisionIds().Select(r => (doc.GetElement(r) as Revision)?.Description).ToList()
                }).ToList();
        }

        private static object ListTitleblocks(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>().Select(s => new { id = s.Id.Value, familia = s.Family.Name, tipo = s.Name })
                .OrderBy(x => x.familia).ThenBy(x => x.tipo).ToList();
        }

        private static object CreateSheets(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!ProHas(args, "sheets", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Array 'sheets' [{number, name}] é obrigatório.");
            var tb = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>().ToList();
            var titleblockName = GetS(args, "titleblock");
            FamilySymbol symbol = null;
            if (!string.IsNullOrWhiteSpace(titleblockName))
            {
                var alvo = Normalizar(titleblockName);
                symbol = tb.FirstOrDefault(s => Normalizar(s.Family.Name + " " + s.Name).Contains(alvo))
                    ?? throw new ArgumentException($"Carimbo '{titleblockName}' não encontrado. Use list_titleblocks.");
            }
            else symbol = tb.FirstOrDefault();
            var existing = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
            var resultado = new List<object>();
            using (var tx = ProTx(doc, "Criar pranchas"))
            {
                if (symbol != null && !symbol.IsActive) symbol.Activate();
                foreach (var item in arr.EnumerateArray())
                {
                    string number = GetS(item, "number");
                    string name = GetS(item, "name", "Sem título");
                    if (string.IsNullOrWhiteSpace(number)) { resultado.Add(new { ok = false, motivo = "number ausente" }); continue; }
                    if (existing.Contains(number)) { resultado.Add(new { numero = number, ok = false, motivo = "número já existe" }); continue; }
                    var sheet = ViewSheet.Create(doc, symbol?.Id ?? ElementId.InvalidElementId);
                    sheet.SheetNumber = number;
                    sheet.Name = name;
                    existing.Add(number);
                    resultado.Add(new { id = sheet.Id.Value, numero = number, nome = name, ok = true });
                }
                tx.Commit();
            }
            return new { carimbo = symbol == null ? null : symbol.Family.Name + " : " + symbol.Name, pranchas = resultado };
        }

        private static object ListRevisions(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return Revision.GetAllRevisionIds(doc).Select(id => doc.GetElement(id) as Revision).Where(r => r != null)
                .Select(r => new
                {
                    id = r.Id.Value,
                    sequencia = r.SequenceNumber,
                    numero = r.RevisionNumber,
                    descricao = r.Description,
                    data = r.RevisionDate,
                    emitida_por = r.IssuedBy,
                    emitida_para = r.IssuedTo,
                    emitida = r.Issued
                }).ToList();
        }

        private static object CreateRevision(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            using (var tx = ProTx(doc, "Criar revisão"))
            {
                var r = Revision.Create(doc);
                r.Description = GetS(args, "description", "Revisão");
                r.RevisionDate = GetS(args, "date", DateTime.Now.ToString("dd/MM/yyyy"));
                if (GetS(args, "issued_by") != null) r.IssuedBy = GetS(args, "issued_by");
                if (GetS(args, "issued_to") != null) r.IssuedTo = GetS(args, "issued_to");
                tx.Commit();
                return new { id = r.Id.Value, numero = r.RevisionNumber, descricao = r.Description, data = r.RevisionDate };
            }
        }

        private static object AddRevisionToSheets(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var revId = GetElementIdReq(args, "revision_id");
            if (!(doc.GetElement(revId) is Revision)) throw new ArgumentException($"{revId.Value} não é uma revisão.");
            var sheetIds = ProIds(args, "sheet_ids");
            var sheets = sheetIds.Count > 0
                ? sheetIds.Select(id => doc.GetElement(id) as ViewSheet).Where(s => s != null).ToList()
                : new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
            using (var tx = ProTx(doc, "Revisão em pranchas"))
            {
                foreach (var s in sheets)
                {
                    var ids = s.GetAdditionalRevisionIds();
                    if (!ids.Contains(revId)) { ids.Add(revId); s.SetAdditionalRevisionIds(ids); }
                }
                tx.Commit();
            }
            return new { revisao = revId.Value, pranchas = sheets.Select(s => s.SheetNumber).ToList() };
        }

        private static object ListSchedules(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                .Select(s => new
                {
                    id = s.Id.Value,
                    nome = s.Name,
                    categoria = Category.GetCategory(doc, s.Definition.CategoryId)?.Name,
                    campos = Enumerable.Range(0, s.Definition.GetFieldCount()).Select(i => s.Definition.GetField(i).GetName()).ToList()
                }).OrderBy(s => s.nome).ToList();
        }

        private static object CreateScheduleAdvanced(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var bic = ProCategory(GetS(args, "category"));
            var fieldNames = ProStrings(args, "fields");
            if (fieldNames.Count == 0) throw new ArgumentException("Array 'fields' com nomes de parâmetros é obrigatório (ex.: [\"Tipo\", \"Largura\", \"Altura\"]).");
            using (var tx = ProTx(doc, "Tabela avançada"))
            {
                var sched = ViewSchedule.CreateSchedule(doc, new ElementId(bic));
                var name = GetS(args, "name");
                if (!string.IsNullOrWhiteSpace(name)) sched.Name = name;
                var def = sched.Definition;
                var available = def.GetSchedulableFields();
                var added = new Dictionary<string, ScheduleField>(StringComparer.OrdinalIgnoreCase);
                var faltando = new List<string>();
                foreach (var fname in fieldNames)
                {
                    var alvo = Normalizar(fname);
                    var sf = available.FirstOrDefault(f => Normalizar(f.GetName(doc)) == alvo)
                          ?? available.FirstOrDefault(f => Normalizar(f.GetName(doc)).Contains(alvo));
                    if (sf == null) { faltando.Add(fname); continue; }
                    added[fname] = def.AddField(sf);
                }
                if (added.Count == 0)
                    throw new ArgumentException("Nenhum campo encontrado. Disponíveis: " + string.Join(", ", available.Select(f => f.GetName(doc)).Distinct().OrderBy(n => n).Take(80)));

                foreach (var sname in ProStrings(args, "sort_by"))
                {
                    if (!added.TryGetValue(sname, out var field)) continue;
                    def.AddSortGroupField(new ScheduleSortGroupField(field.FieldId, ScheduleSortOrder.Ascending));
                }
                if (ProHas(args, "filters", out var farr) && farr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in farr.EnumerateArray())
                    {
                        var fieldName = GetS(f, "field");
                        if (fieldName == null || !added.TryGetValue(fieldName, out var field))
                            throw new ArgumentException($"Filtro sobre campo '{fieldName}' que não está em 'fields'.");
                        var type = (GetS(f, "op", "equals") ?? "equals").ToLowerInvariant() switch
                        {
                            "equals" => ScheduleFilterType.Equal,
                            "not_equals" => ScheduleFilterType.NotEqual,
                            "gt" => ScheduleFilterType.GreaterThan,
                            "gte" => ScheduleFilterType.GreaterThanOrEqual,
                            "lt" => ScheduleFilterType.LessThan,
                            "lte" => ScheduleFilterType.LessThanOrEqual,
                            "contains" => ScheduleFilterType.Contains,
                            "not_contains" => ScheduleFilterType.NotContains,
                            "starts_with" => ScheduleFilterType.BeginsWith,
                            "has_value" => ScheduleFilterType.HasValue,
                            _ => throw new ArgumentException("op de filtro inválido.")
                        };
                        ScheduleFilter filter;
                        if (type == ScheduleFilterType.HasValue) filter = new ScheduleFilter(field.FieldId, type);
                        else if (f.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                        {
                            var spec = field.GetSpecTypeId();
                            if (spec == SpecTypeId.Int.Integer) filter = new ScheduleFilter(field.FieldId, type, (int)Math.Round(v.GetDouble()));
                            else
                            {
                                var unit = ProMcpUnit(doc, spec);
                                filter = new ScheduleFilter(field.FieldId, type, unit != null ? UnitUtils.ConvertToInternalUnits(v.GetDouble(), unit) : v.GetDouble());
                            }
                        }
                        else filter = new ScheduleFilter(field.FieldId, type, GetS(f, "value", ""));
                        def.AddFilter(filter);
                    }
                }
                def.IsItemized = ProBool(args, "itemize", true);
                def.ShowGrandTotal = ProBool(args, "grand_total", false);
                tx.Commit();
                return new { id = sched.Id.Value, nome = sched.Name, campos = added.Keys.ToList(), campos_nao_encontrados = faltando };
            }
        }

        private static ViewSchedule ProSchedule(Document doc, JsonElement args)
        {
            var id = GetElementIdReq(args, "schedule_id");
            return doc.GetElement(id) as ViewSchedule ?? throw new ArgumentException($"{id.Value} não é uma tabela.");
        }

        private static object GetScheduleData(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var sched = ProSchedule(doc, args);
            int maxRows = Math.Clamp((int)GetD(args, "max_rows", 500), 1, 5000);
            var body = sched.GetTableData().GetSectionData(SectionType.Body);
            var rows = new List<List<string>>();
            for (int r = body.FirstRowNumber; r <= body.LastRowNumber && rows.Count < maxRows; r++)
            {
                var row = new List<string>();
                for (int c = body.FirstColumnNumber; c <= body.LastColumnNumber; c++) row.Add(sched.GetCellText(SectionType.Body, r, c));
                rows.Add(row);
            }
            return new { id = sched.Id.Value, nome = sched.Name, linhas_total = body.NumberOfRows, linhas = rows };
        }

        private static object ExportScheduleCsv(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var sched = ProSchedule(doc, args);
            string folder = GetS(args, "folder") ?? Path.GetTempPath();
            Directory.CreateDirectory(folder);
            string name = GetS(args, "name") ?? string.Join("_", sched.Name.Split(Path.GetInvalidFileNameChars())) + ".csv";
            var options = new ViewScheduleExportOptions
            {
                FieldDelimiter = GetS(args, "delimiter", ";"),
                TextQualifier = ExportTextQualifier.DoubleQuote,
                HeadersFootersBlanks = true,
                Title = false
            };
            sched.Export(folder, name, options);
            return new { arquivo = Path.Combine(folder, name) };
        }

        private static List<ViewSheet> ProSheets(Document doc, JsonElement args)
        {
            var ids = ProIds(args, "sheet_ids");
            var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder);
            if (ids.Count > 0) all = ids.Select(id => doc.GetElement(id) as ViewSheet).Where(s => s != null);
            var prefix = GetS(args, "number_prefix");
            if (!string.IsNullOrWhiteSpace(prefix)) all = all.Where(s => s.SheetNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var list = all.OrderBy(s => s.SheetNumber).ToList();
            if (list.Count == 0) throw new ArgumentException("Nenhuma prancha corresponde ao pedido.");
            return list;
        }

        private static object ExportSheetsPdf(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var sheets = ProSheets(doc, args);
            string folder = GetS(args, "folder") ?? Path.GetTempPath();
            Directory.CreateDirectory(folder);
            bool combine = ProBool(args, "combine", true);
            var options = new PDFExportOptions
            {
                Combine = combine,
                FileName = GetS(args, "name", doc.Title + "_pranchas"),
                HideCropBoundaries = true,
                HideScopeBoxes = true,
                HideReferencePlane = true,
                HideUnreferencedViewTags = true,
                ZoomType = ZoomType.Zoom,
                ZoomPercentage = 100
            };
            var ok = doc.Export(folder, sheets.Select(s => s.Id).ToList(), options);
            return new { ok, pasta = folder, combinado = combine, pranchas = sheets.Select(s => s.SheetNumber + " - " + s.Name).ToList() };
        }

        private static object ExportSheetsDwg(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var sheets = ProSheets(doc, args);
            string folder = GetS(args, "folder") ?? Path.GetTempPath();
            Directory.CreateDirectory(folder);
            var options = new DWGExportOptions { MergedViews = ProBool(args, "merged_views", true) };
            var ok = doc.Export(folder, GetS(args, "prefix", ""), sheets.Select(s => s.Id).ToList(), options);
            return new { ok, pasta = folder, pranchas = sheets.Select(s => s.SheetNumber).ToList() };
        }

        // =====================================================================
        // COORDENAÇÃO E QUALIDADE
        // =====================================================================

        private static object AnalyzeClashes(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var catA = ProCategory(GetS(args, "category_a"));
            var catB = ProCategory(GetS(args, "category_b"));
            int limit = Math.Clamp((int)GetD(args, "limit", 200), 1, 2000);
            bool ignoreJoined = ProBool(args, "ignore_joined", true);
            var idsA = ProIds(args, "ids_a");
            var elementsA = idsA.Count > 0
                ? idsA.Select(doc.GetElement).Where(e => e != null).ToList()
                : new FilteredElementCollector(doc).OfCategory(catA).WhereElementIsNotElementType().ToElements().ToList();

            var seen = new HashSet<string>();
            var conflitos = new List<object>();
            int ignorados = 0, analisados = 0;
            foreach (var a in elementsA)
            {
                if (conflitos.Count >= limit) break;
                var bb = a.get_BoundingBox(null);
                if (bb == null) continue;
                ElementIntersectsElementFilter intersects;
                try { intersects = new ElementIntersectsElementFilter(a); }
                catch { ignorados++; continue; }
                analisados++;
                var hits = new FilteredElementCollector(doc).OfCategory(catB).WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(new Outline(bb.Min, bb.Max)))
                    .Excluding(new List<ElementId> { a.Id })
                    .WherePasses(intersects)
                    .ToElements();
                foreach (var b in hits)
                {
                    string key = Math.Min(a.Id.Value, b.Id.Value) + "-" + Math.Max(a.Id.Value, b.Id.Value);
                    if (!seen.Add(key)) continue;
                    // Interferências intencionais: hospedeiro/hospedado e geometrias unidas.
                    if ((a as FamilyInstance)?.Host?.Id == b.Id || (b as FamilyInstance)?.Host?.Id == a.Id) continue;
                    if (ignoreJoined && JoinGeometryUtils.AreElementsJoined(doc, a, b)) continue;
                    var bbB = b.get_BoundingBox(null);
                    XYZ center = bbB == null ? null : new XYZ(
                        (Math.Max(bb.Min.X, bbB.Min.X) + Math.Min(bb.Max.X, bbB.Max.X)) / 2,
                        (Math.Max(bb.Min.Y, bbB.Min.Y) + Math.Min(bb.Max.Y, bbB.Max.Y)) / 2,
                        (Math.Max(bb.Min.Z, bbB.Min.Z) + Math.Min(bb.Max.Z, bbB.Max.Z)) / 2);
                    conflitos.Add(new
                    {
                        id_a = a.Id.Value, categoria_a = a.Category?.Name, tipo_a = ProTypeName(doc, a),
                        id_b = b.Id.Value, categoria_b = b.Category?.Name, tipo_b = ProTypeName(doc, b),
                        ponto = ProPoint(center)
                    });
                    if (conflitos.Count >= limit) break;
                }
            }
            return new
            {
                quantidade = conflitos.Count,
                limite_atingido = conflitos.Count >= limit,
                elementos_analisados = analisados,
                elementos_sem_solido = ignorados,
                conflitos,
                observacao = "Somente o modelo ativo; hospedeiro/hospedado e elementos unidos são desconsiderados."
            };
        }

        private static readonly BuiltInCategory[] DefaultQuantityCategories =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows, BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Stairs,
            BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit
        };

        private static double ProBip(Element e, BuiltInParameter bip, ForgeTypeId unit)
        {
            var p = e.get_Parameter(bip);
            return p != null && p.HasValue && p.StorageType == StorageType.Double ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit) : 0;
        }

        private static object AnalyzeQuantities(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var categories = ProCategories(args, "categories");
            if (categories.Count == 0) categories = DefaultQuantityCategories.ToList();
            string level = GetS(args, "level");
            var linhas = new List<object>();
            foreach (var bic in categories)
            {
                var elements = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements()
                    .Where(e => level == null || Normalizar(ProLevelName(doc, e) ?? "") == Normalizar(level)).ToList();
                foreach (var g in elements.GroupBy(e => ProTypeName(doc, e) ?? "(sem tipo)").OrderBy(g => g.Key))
                {
                    linhas.Add(new
                    {
                        categoria = g.First().Category?.Name ?? bic.ToString(),
                        tipo = g.Key,
                        quantidade = g.Count(),
                        comprimento_m = Math.Round(g.Sum(e => ProBip(e, BuiltInParameter.CURVE_ELEM_LENGTH, UnitTypeId.Meters)), 3),
                        area_m2 = Math.Round(g.Sum(e => ProBip(e, BuiltInParameter.HOST_AREA_COMPUTED, UnitTypeId.SquareMeters)), 3),
                        volume_m3 = Math.Round(g.Sum(e => ProBip(e, BuiltInParameter.HOST_VOLUME_COMPUTED, UnitTypeId.CubicMeters)), 3)
                    });
                }
            }
            return new { nivel = level, linhas, observacao = "Valores calculados pelo Revit (HOST_AREA/VOLUME_COMPUTED, CURVE_ELEM_LENGTH). Conferir critérios de medição do contrato." };
        }

        private static object AnalyzeMaterialTakeoff(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var categories = ProCategories(args, "categories");
            if (categories.Count == 0) categories = DefaultQuantityCategories.Take(10).ToList();
            var totals = new Dictionary<long, (string nome, double area, double volume, int elementos)>();
            foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories))
                         .WhereElementIsNotElementType().ToElements())
            {
                ICollection<ElementId> mats;
                try { mats = e.GetMaterialIds(false); } catch { continue; }
                foreach (var mid in mats)
                {
                    double area = 0, vol = 0;
                    try { area = e.GetMaterialArea(mid, false); vol = e.GetMaterialVolume(mid); } catch { }
                    totals.TryGetValue(mid.Value, out var t);
                    totals[mid.Value] = (doc.GetElement(mid)?.Name ?? "?", t.area + area, t.volume + vol, t.elementos + 1);
                }
            }
            var linhas = totals.Select(kv => new
            {
                material_id = kv.Key,
                material = kv.Value.nome,
                elementos = kv.Value.elementos,
                area_m2 = Math.Round(UnitUtils.ConvertFromInternalUnits(kv.Value.area, UnitTypeId.SquareMeters), 3),
                volume_m3 = Math.Round(UnitUtils.ConvertFromInternalUnits(kv.Value.volume, UnitTypeId.CubicMeters), 3)
            }).OrderByDescending(x => x.volume_m3).ThenBy(x => x.material).ToList();
            return new { categorias = categories.Select(c => c.ToString()).ToList(), materiais = linhas };
        }

        private static ICollection<ElementId> ProUnused(Document doc)
            => doc.GetUnusedElements(new HashSet<ElementId>());

        private static object PreviewPurgeUnused(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var unused = ProUnused(doc);
            var grupos = unused.Select(doc.GetElement).Where(e => e != null)
                .GroupBy(e => e.Category?.Name ?? e.GetType().Name)
                .Select(g => new { grupo = g.Key, quantidade = g.Count(), exemplos = g.Take(5).Select(e => e.Name).ToList() })
                .OrderByDescending(g => g.quantidade).ToList();
            return new { total = unused.Count, grupos, observacao = "Somente leitura. purge_unused(confirm=true) remove estes itens; alguns liberam outros em passadas seguintes." };
        }

        private static object PurgeUnused(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!ProBool(args, "confirm", false))
                throw new ArgumentException("Exclusão permanente. Rode preview_purge_unused e chame com confirm=true.");
            int passes = Math.Clamp((int)GetD(args, "passes", 3), 1, 5);
            int removidos = 0, falhas = 0;
            var porPassada = new List<int>();
            for (int pass = 0; pass < passes; pass++)
            {
                var unused = ProUnused(doc).ToList();
                if (unused.Count == 0) break;
                int antes = removidos;
                using (var tx = ProTx(doc, "Limpar não usados"))
                {
                    try { removidos += doc.Delete(unused).Count > 0 ? unused.Count : 0; }
                    catch
                    {
                        // Algum item não pôde ser excluído em lote: tenta um a um.
                        foreach (var id in unused)
                        {
                            using (var sub = new SubTransaction(doc))
                            {
                                sub.Start();
                                try { doc.Delete(id); sub.Commit(); removidos++; }
                                catch { sub.RollBack(); falhas++; }
                            }
                        }
                    }
                    tx.Commit();
                }
                porPassada.Add(removidos - antes);
                if (removidos == antes) break;
            }
            return new { removidos, falhas, por_passada = porPassada, restantes = ProUnused(doc).Count };
        }

        private static object ListLinks(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var revit = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Select(i =>
                {
                    var type = doc.GetElement(i.GetTypeId()) as RevitLinkType;
                    string path = null;
                    try
                    {
                        var ext = type?.GetExternalFileReference();
                        if (ext != null) path = ModelPathUtils.ConvertModelPathToUserVisiblePath(ext.GetAbsolutePath());
                    }
                    catch { }
                    return new
                    {
                        id = i.Id.Value,
                        tipo_id = type?.Id.Value,
                        nome = i.Name,
                        caminho = path,
                        carregado = type != null && RevitLinkType.IsLoaded(doc, type.Id),
                        documento = i.GetLinkDocument()?.Title
                    };
                }).ToList();
            var cad = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Select(i => new
                {
                    id = i.Id.Value,
                    nome = doc.GetElement(i.GetTypeId())?.Name,
                    vinculado = i.IsLinked,
                    vista_especifica = i.ViewSpecific,
                    vista = i.ViewSpecific ? doc.GetElement(i.OwnerViewId)?.Name : null
                }).ToList();
            return new { revit, cad, observacao = cad.Any(c => !c.vinculado) ? "Há CAD importado (não vinculado): prefira vincular para manter o modelo leve." : null };
        }

        private static object LoadRevitLink(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string path = GetS(args, "path") ?? throw new ArgumentException("Argumento 'path' (.rvt) obrigatório.");
            if (!File.Exists(path)) throw new ArgumentException($"Arquivo não encontrado: {path}");
            bool shared = string.Equals(GetS(args, "positioning", "origin"), "shared", StringComparison.OrdinalIgnoreCase);
            using (var tx = ProTx(doc, "Vincular modelo Revit"))
            {
                var result = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new RevitLinkOptions(false));
                if (result.ElementId == ElementId.InvalidElementId)
                    throw new Exception($"Falha ao vincular: {result.LoadResult}");
                var instance = shared
                    ? RevitLinkInstance.Create(doc, result.ElementId, ImportPlacement.Shared)
                    : RevitLinkInstance.Create(doc, result.ElementId);
                tx.Commit();
                return new { tipo_id = result.ElementId.Value, instancia_id = instance.Id.Value, posicionamento = shared ? "coordenadas compartilhadas" : "origem a origem", resultado = result.LoadResult.ToString() };
            }
        }

        private static object ReloadLink(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "link_type_id");
            var element = doc.GetElement(id);
            var type = element as RevitLinkType;
            if (type == null && element is RevitLinkInstance instance) type = doc.GetElement(instance.GetTypeId()) as RevitLinkType;
            if (type == null) throw new ArgumentException($"{id.Value} não é um vínculo Revit (use tipo_id ou id de list_links).");
            // Reload/Unload não podem ocorrer dentro de transação.
            if (ProBool(args, "unload", false)) { type.Unload(null); return new { vinculo = type.Name, descarregado = true }; }
            var result = type.Reload();
            return new { vinculo = type.Name, resultado = result.LoadResult.ToString() };
        }

        // =====================================================================
        // ESTRUTURA
        // =====================================================================

        private static object CreateBeam(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nível não encontrado.");
            double z = level.Elevation + MToFt(GetD(args, "offset_m", 0));
            var segments = new List<(XYZ, XYZ)>();
            if (ProHas(args, "beams", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in arr.EnumerateArray())
                    segments.Add((new XYZ(MToFt(GetItemD(b, "x1_m", 0)), MToFt(GetItemD(b, "y1_m", 0)), z),
                                  new XYZ(MToFt(GetItemD(b, "x2_m", 0)), MToFt(GetItemD(b, "y2_m", 0)), z)));
            }
            else
            {
                segments.Add((new XYZ(MToFt(GetDReq(args, "x1_m")), MToFt(GetDReq(args, "y1_m")), z),
                              new XYZ(MToFt(GetDReq(args, "x2_m")), MToFt(GetDReq(args, "y2_m")), z)));
            }
            var criadas = new List<long>();
            using (var tx = ProTx(doc, "Criar vigas"))
            {
                var symbol = ProSymbol(doc, BuiltInCategory.OST_StructuralFraming, GetS(args, "family"), GetS(args, "type"));
                foreach (var (p1, p2) in segments)
                {
                    if (p1.DistanceTo(p2) < MToFt(0.05)) throw new ArgumentException("Viga com comprimento menor que 5 cm.");
                    var beam = doc.Create.NewFamilyInstance(Line.CreateBound(p1, p2), symbol, level, StructuralType.Beam);
                    criadas.Add(beam.Id.Value);
                }
                tx.Commit();
                return new { tipo = symbol.Family.Name + " : " + symbol.Name, nivel = level.Name, ids = criadas };
            }
        }

        private static object CreateFoundation(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string kind = (GetS(args, "kind", "isolated") ?? "isolated").ToLowerInvariant();
            var criadas = new List<long>();
            using (var tx = ProTx(doc, "Criar fundações"))
            {
                if (kind == "wall")
                {
                    var wallType = ProFindType<WallFoundationType>(doc, GetS(args, "type"))
                        ?? throw new InvalidOperationException("Nenhum tipo de fundação de parede (WallFoundationType) no projeto.");
                    foreach (var wid in ProIds(args, "wall_ids", required: true))
                        criadas.Add(WallFoundation.Create(doc, wallType.Id, wid).Id.Value);
                    tx.Commit();
                    return new { tipo = wallType.Name, ids = criadas };
                }
                var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nível não encontrado.");
                var symbol = ProSymbol(doc, BuiltInCategory.OST_StructuralFoundation, GetS(args, "family"), GetS(args, "type"));
                var points = ProPoints(args, "points", level.Elevation + MToFt(GetD(args, "offset_m", 0)));
                foreach (var p in points)
                    criadas.Add(doc.Create.NewFamilyInstance(p, symbol, level, StructuralType.Footing).Id.Value);
                tx.Commit();
                return new { tipo = symbol.Family.Name + " : " + symbol.Name, nivel = level.Name, ids = criadas };
            }
        }

        // =====================================================================
        // MEP
        // =====================================================================

        private static object ListMepTypes(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            object Names<T>() where T : Element =>
                new FilteredElementCollector(doc).OfClass(typeof(T)).Select(e => new { id = e.Id.Value, nome = e.Name }).OrderBy(x => x.nome).ToList();
            return new
            {
                sistemas_dutos = Names<MechanicalSystemType>(),
                sistemas_tubulacao = Names<PipingSystemType>(),
                tipos_duto = Names<DuctType>(),
                tipos_tubo = Names<PipeType>(),
                tipos_eletrocalha = Names<CableTrayType>(),
                tipos_eletroduto = Names<ConduitType>()
            };
        }

        private static Connector ProNearestConnector(MEPCurve curve, XYZ point)
        {
            Connector best = null;
            double dist = double.MaxValue;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (c.ConnectorType != ConnectorType.End) continue;
                double d = c.Origin.DistanceTo(point);
                if (d < dist) { dist = d; best = c; }
            }
            return best;
        }

        /// <summary>
        /// Cria um trecho contínuo de duto, tubo, eletrocalha ou eletroduto por uma
        /// polilinha, com dimensões e conexões (curvas) entre segmentos.
        /// </summary>
        private static object CreateMepRun(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string kind = (GetS(args, "kind") ?? throw new ArgumentException("kind obrigatório: duct, pipe, cable_tray ou conduit.")).ToLowerInvariant();
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nível não encontrado.");
            var points = ProPoints(args, "points", level.Elevation + MToFt(GetD(args, "offset_m", 2.7)));
            if (points.Count < 2) throw new ArgumentException("São necessários ao menos 2 pontos.");

            var segments = new List<MEPCurve>();
            var avisos = new List<string>();
            using (var tx = ProTx(doc, "Criar trecho MEP"))
            {
                for (int i = 0; i < points.Count - 1; i++)
                {
                    var p1 = points[i]; var p2 = points[i + 1];
                    MEPCurve curve;
                    switch (kind)
                    {
                        case "duct":
                        {
                            var sys = ProFindType<MechanicalSystemType>(doc, GetS(args, "system_type")) ?? throw new InvalidOperationException("Nenhum sistema de dutos no projeto (use um template mecânico).");
                            var type = ProFindType<DuctType>(doc, GetS(args, "type")) ?? throw new InvalidOperationException("Nenhum tipo de duto no projeto.");
                            curve = Duct.Create(doc, sys.Id, type.Id, level.Id, p1, p2);
                            break;
                        }
                        case "pipe":
                        {
                            var sys = ProFindType<PipingSystemType>(doc, GetS(args, "system_type")) ?? throw new InvalidOperationException("Nenhum sistema de tubulação no projeto.");
                            var type = ProFindType<PipeType>(doc, GetS(args, "type")) ?? throw new InvalidOperationException("Nenhum tipo de tubo no projeto.");
                            curve = Pipe.Create(doc, sys.Id, type.Id, level.Id, p1, p2);
                            break;
                        }
                        case "cable_tray":
                        {
                            var type = ProFindType<CableTrayType>(doc, GetS(args, "type")) ?? throw new InvalidOperationException("Nenhum tipo de eletrocalha no projeto.");
                            curve = CableTray.Create(doc, type.Id, p1, p2, level.Id);
                            break;
                        }
                        case "conduit":
                        {
                            var type = ProFindType<ConduitType>(doc, GetS(args, "type")) ?? throw new InvalidOperationException("Nenhum tipo de eletroduto no projeto.");
                            curve = Conduit.Create(doc, type.Id, p1, p2, level.Id);
                            break;
                        }
                        default: throw new ArgumentException("kind deve ser duct, pipe, cable_tray ou conduit.");
                    }

                    void SetLen(BuiltInParameter bip, string arg)
                    {
                        double v = GetD(args, arg, double.NaN);
                        if (double.IsNaN(v)) return;
                        var p = curve.get_Parameter(bip);
                        if (p == null || p.IsReadOnly) { avisos.Add($"{arg} não se aplica a este tipo."); return; }
                        p.Set(MToFt(v));
                    }
                    if (kind == "duct")
                    {
                        SetLen(BuiltInParameter.RBS_CURVE_WIDTH_PARAM, "width_m");
                        SetLen(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, "height_m");
                        SetLen(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, "diameter_m");
                    }
                    else if (kind == "pipe") SetLen(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, "diameter_m");
                    else if (kind == "cable_tray")
                    {
                        SetLen(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, "width_m");
                        SetLen(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, "height_m");
                    }
                    else SetLen(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, "diameter_m");
                    segments.Add(curve);
                }

                if (ProBool(args, "connect", true))
                {
                    doc.Regenerate();
                    for (int i = 0; i < segments.Count - 1; i++)
                    {
                        var c1 = ProNearestConnector(segments[i], points[i + 1]);
                        var c2 = ProNearestConnector(segments[i + 1], points[i + 1]);
                        if (c1 == null || c2 == null) continue;
                        try { doc.Create.NewElbowFitting(c1, c2); }
                        catch (Exception ex) { avisos.Add($"Curva {i + 1}: {ex.Message} (verifique as preferências de roteamento do tipo)."); }
                    }
                }
                tx.Commit();
            }
            return new { tipo = kind, nivel = level.Name, ids = segments.Select(s => s.Id.Value).ToList(), avisos };
        }

        private static object CreateSpacesFromRooms(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string levelName = GetS(args, "level");
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Cast<Room>().Where(r => r.Area > 0 && r.Location is LocationPoint)
                .Where(r => levelName == null || Normalizar(r.Level?.Name ?? "") == Normalizar(levelName)).ToList();
            var criados = new List<object>();
            int existentes = 0;
            using (var tx = ProTx(doc, "Espaços a partir de ambientes"))
            {
                foreach (var room in rooms)
                {
                    var pt = ((LocationPoint)room.Location).Point;
                    if (doc.GetSpaceAtPoint(pt) != null) { existentes++; continue; }
                    var space = doc.Create.NewSpace(room.Level, new UV(pt.X, pt.Y));
                    space.Name = room.Name.Replace(" " + room.Number, "");
                    space.Number = room.Number;
                    criados.Add(new { id = space.Id.Value, nome = space.Name, numero = space.Number, ambiente = room.Id.Value });
                }
                tx.Commit();
            }
            return new { criados, ja_existiam = existentes };
        }

        // =====================================================================
        // MODELAGEM GERAL
        // =====================================================================

        private static object ArrayElements(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var ids = ProIds(args, "ids", required: true);
            int count = Math.Clamp((int)GetD(args, "count", 2), 2, 500);
            var step = new XYZ(MToFt(GetD(args, "dx_m", 0)), MToFt(GetD(args, "dy_m", 0)), MToFt(GetD(args, "dz_m", 0)));
            if (step.IsZeroLength()) throw new ArgumentException("Informe o espaçamento dx_m/dy_m/dz_m entre cópias.");
            var copias = new List<List<long>>();
            using (var tx = ProTx(doc, "Cópias em série"))
            {
                for (int i = 1; i < count; i++)
                    copias.Add(ElementTransformUtils.CopyElements(doc, ids, step * i).Select(x => x.Value).ToList());
                tx.Commit();
            }
            return new { originais = ids.Select(i => i.Value).ToList(), copias };
        }



        private static object CreateRoomSeparators(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nível não encontrado.");
            var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == level.Id)
                ?? throw new InvalidOperationException($"Nenhuma planta de piso do nível '{level.Name}'. Use create_floor_plan.");
            if (!ProHas(args, "lines", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Array 'lines' [{x1_m, y1_m, x2_m, y2_m}] obrigatório.");
            var curves = new CurveArray();
            foreach (var l in arr.EnumerateArray())
            {
                var p1 = new XYZ(MToFt(GetItemD(l, "x1_m", 0)), MToFt(GetItemD(l, "y1_m", 0)), level.Elevation);
                var p2 = new XYZ(MToFt(GetItemD(l, "x2_m", 0)), MToFt(GetItemD(l, "y2_m", 0)), level.Elevation);
                if (p1.DistanceTo(p2) > doc.Application.ShortCurveTolerance) curves.Append(Line.CreateBound(p1, p2));
            }
            if (curves.IsEmpty) throw new ArgumentException("Nenhuma linha válida.");
            using (var tx = ProTx(doc, "Separadores de ambiente"))
            {
                var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation)));
                var created = doc.Create.NewRoomBoundaryLines(plane, curves, view);
                tx.Commit();
                return new { nivel = level.Name, ids = created.Cast<ModelCurve>().Select(c => c.Id.Value).ToList() };
            }
        }
    }
}
