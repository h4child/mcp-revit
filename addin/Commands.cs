// ============================================================================
// Commands.cs — Todos os comandos do Revit expostos via MCP
// ============================================================================
//
// Cada método estático corresponde a um comando que pode ser chamado pelo
// servidor Python MCP. Todos recebem UIApplication (acesso ao Revit) e
// JsonElement (argumentos do comando).
//
// CONCEITOS IMPORTANTES DO REVIT:
//
//   • Document (doc) — o arquivo .rvt aberto. Todo elemento vive dentro
//     de um Document.
//
//   • Transaction — toda modificação no modelo precisa estar dentro de
//     uma Transaction. Sem ela, o Revit rejeita a operação.
//     Exemplo: using (var tx = new Transaction(doc, "nome")) { tx.Start(); ... tx.Commit(); }
//
//   • Level (Nível) — plano horizontal que define andares.
//     Paredes, pisos e tetos são associados a um Level.
//
//   • Wall (Parede) — criada a partir de uma linha (Line) e um Level.
//     Tem parâmetro de altura (WALL_USER_HEIGHT_PARAM).
//
//   • FamilySymbol — tipo de família (ex: "Porta 80cm", "Janela 120x100").
//     Portas e janelas são instâncias de FamilySymbol colocadas em paredes.
//
//   • DirectShape — geometria livre, sem família. Usado aqui para criar
//     caixas representando móveis (sofá, mesa, etc).
//
//   • Room (Cômodo) — elemento que identifica áreas fechadas por paredes.
//     O Revit calcula automaticamente a área e o perímetro.
//
// UNIDADES:
//   O Revit trabalha internamente em pés (feet).
//   Todas as ferramentas MCP aceitam e retornam METROS.
//   A conversão é feita por MToFt() e FtToM().
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace RevitMcp
{
    public static class Commands
    {
        // =====================================================================
        // Dispatch — roteador central de comandos
        // =====================================================================

        /// <summary>
        /// Recebe um RpcRequest e executa o comando correspondente.
        /// Comandos que não precisam da thread principal (como "ping")
        /// são executados diretamente. Os demais são enfileirados via
        /// App.Handler.Run() para rodar na thread principal do Revit.
        /// </summary>
        public static object Dispatch(RpcRequest req)
        {
            if (req == null || string.IsNullOrEmpty(req.cmd))
                throw new ArgumentException("Campo 'cmd' ausente na requisição");

            // Ping não precisa da thread principal do Revit
            if (req.cmd == "ping")
                return new { pong = true, time = DateTime.Now.ToString("s") };

            // Todos os outros comandos precisam da thread principal
            return App.Handler.Run(uiApp =>
            {
                // Snapshot de avisos ANTES do comando: pra detectar novos avisos causados por ele.
                var doc = uiApp?.ActiveUIDocument?.Document;
                var avisosAntes = doc != null
                    ? new HashSet<string>(doc.GetWarnings().Select(w => ChaveAviso(w)))
                    : new HashSet<string>();

                object resultado = ExecutarComando(uiApp, req);

                if (doc != null && IsAuditableWrite(req.cmd))
                    AppendAuditEntry(doc, uiApp.Application.Username, req.cmd, "concluído");

                // Compara com estado pós-comando; se surgiram avisos novos, anexa ao retorno.
                if (doc != null)
                {
                    var novos = doc.GetWarnings()
                        .Where(w => !avisosAntes.Contains(ChaveAviso(w)))
                        .Select(w => new
                        {
                            severidade = w.GetSeverity().ToString(),
                            descricao  = w.GetDescriptionText(),
                            ids        = w.GetFailingElements().Select(i => i.Value).ToList()
                        })
                        .ToList();
                    if (novos.Count > 0)
                        return new { resultado, avisos = novos };
                }
                return resultado;
            });
        }

        /// <summary>Chave estável pra deduplicar avisos entre snapshots.</summary>
        private static string ChaveAviso(FailureMessage w)
        {
            var ids = string.Join(",", w.GetFailingElements().OrderBy(i => i.Value).Select(i => i.Value));
            return w.GetDescriptionText() + "|" + ids;
        }

        /// <summary>Executa o comando propriamente dito — separado do Dispatch para permitir a checagem de avisos.</summary>
        private static object ExecutarComando(UIApplication uiApp, RpcRequest req)
        {
                switch (req.cmd)
                {
                    // --- Informações ---
                    case "get_info":        return GetInfo(uiApp);
                    case "list_model_warnings": return ListModelWarnings(uiApp);
                    case "list_categories": return ListCategories(uiApp);
                    case "list_levels":     return ListLevels(uiApp);
                    case "list_walls":      return ListWalls(uiApp);
                    case "list_windows":    return ListWindows(uiApp);
                    case "list_wall_types": return ListWallTypes(uiApp);
                    case "list_floor_types":return ListFloorTypes(uiApp);
                    case "list_families":   return ListFamilies(uiApp, req.args);

                    // --- Introspecção genérica ---
                    case "list_elements":   return ListElements(uiApp, req.args);
                    case "get_element":     return GetElement(uiApp, req.args);
                    case "list_views":      return ListViews(uiApp);
                    case "get_active_view": return GetActiveView(uiApp);
                    case "get_selection":   return GetSelection(uiApp);

                    // --- Projeto ---
                    case "new_project":     return NewProject(uiApp, req.args);
                    case "open_document":   return OpenDocument(uiApp, req.args);
                    case "save_document":   return SaveDocument(uiApp);
                    case "save_as":         return SaveAs(uiApp, req.args);
                    case "delete_element":  return DeleteElement(uiApp, req.args);
                    case "preview_delete":  return PreviewDelete(uiApp, req.args);
                    case "backup_document": return BackupDocument(uiApp, req.args);
                    case "plan_batch":      return PlanBatch(uiApp, req.args);
                    case "execute_batch":   return ExecuteBatch(uiApp, req.args);

                    // --- Criação de elementos ---
                    case "create_level":    return CreateLevel(uiApp, req.args);
                    case "create_wall":     return CreateWall(uiApp, req.args);
                    case "create_walls":    return CreateWalls(uiApp, req.args);
                    case "create_plan_layout": return CreatePlanLayout(uiApp, req.args);
                    case "create_box":      return CreateBox(uiApp, req.args);
                    case "create_door":     return CreateDoor(uiApp, req.args);
                    case "create_window":   return CreateWindow(uiApp, req.args);
                    case "resize_window":   return ResizeWindow(uiApp, req.args);
                    case "place_rooms":     return PlaceRooms(uiApp);
                    case "place_rooms_at":  return PlaceRoomsAt(uiApp, req.args);
                    case "place_furniture": return PlaceFurniture(uiApp, req.args);
                    case "place_blocks":    return PlaceFurnitureBlocks(uiApp, req.args);
                    case "load_family":     return LoadFamily(uiApp, req.args);
                    case "create_furniture_family": return CreateFurnitureFamily(uiApp, req.args);
                    case "place_family_instance": return PlaceFamilyInstance(uiApp, req.args);
                    case "place_component": return PlaceComponent(uiApp, req.args);
                    case "create_floor":    return CreateFloor(uiApp, req.args);
                    case "create_roof":     return CreateRoof(uiApp, req.args);
                    case "create_ceiling":  return CreateCeiling(uiApp, req.args);
                    case "create_stairs":   return CreateStairs(uiApp, req.args);
                    case "create_railing":  return CreateRailing(uiApp, req.args);
                    case "create_storey":   return CreateStorey(uiApp, req.args);
                    case "create_column":   return CreateColumn(uiApp, req.args);
                    case "create_dimension":return CreateDimension(uiApp, req.args);
                    case "tag_rooms":       return TagRooms(uiApp, req.args);

                    // --- Materiais ---
                    case "list_materials":  return ListMaterials(uiApp, req.args);
                    case "set_material":    return SetMaterial(uiApp, req.args);
                    case "create_material": return CreateMaterial(uiApp, req.args);
                    case "create_rectangular_prism": return CreateRectangularPrism(uiApp, req.args);

                    // --- Visualização ---
                    case "zoom_fit":        return ZoomFit(uiApp);
                    case "focus_element":   return FocusElement(uiApp, req.args);
                    case "set_view_3d":     return SetView3D(uiApp);
                    case "set_view_shaded": return SetViewShaded(uiApp);
                    case "set_view_plan":   return SetViewPlan(uiApp, req.args);
                    case "create_floor_plan": return CreateFloorPlan(uiApp, req.args);
                    case "set_plan_crop":   return SetPlanCrop(uiApp, req.args);
                    case "set_section_box": return SetSectionBox(uiApp, req.args);
                    case "set_visual_style":return SetVisualStyle(uiApp, req.args);
                    case "set_camera":      return SetCamera(uiApp, req.args);
                    case "screenshot":      return Screenshot(uiApp, req.args);

                    // --- Manipulação (Onda 1) ---
                    case "move_element":    return MoveElement(uiApp, req.args);
                    case "copy_element":    return CopyElement(uiApp, req.args);
                    case "rotate_element":  return RotateElement(uiApp, req.args);
                    case "mirror_element":  return MirrorElement(uiApp, req.args);
                    case "modify_parameters": return ModifyParameters(uiApp, req.args);
                    case "align_elements":  return AlignElements(uiApp, req.args);

                    // --- Anotação (Onda 2) ---
                    case "create_text":         return CreateText(uiApp, req.args);
                    case "delete_text_notes":   return DeleteTextNotes(uiApp, req.args);
                    case "create_section_mark": return CreateSectionMark(uiApp, req.args);
                    case "create_spot_elevation": return CreateSpotElevation(uiApp, req.args);
                    case "create_detail_line":  return CreateDetailLine(uiApp, req.args);
                    case "create_detail_lines": return CreateDetailLines(uiApp, req.args);
                    case "create_detail_rectangle": return CreateDetailRectangle(uiApp, req.args);
                    case "create_filled_region":return CreateFilledRegion(uiApp, req.args);
                    case "create_callout":      return CreateCallout(uiApp, req.args);

                    // --- Modelagem avançada (Onda 3) ---
                    case "create_section_view": return CreateSectionView(uiApp, req.args);
                    case "create_grid":         return CreateGrid(uiApp, req.args);
                    case "create_opening":      return CreateOpening(uiApp, req.args);
                    case "create_curtain_wall": return CreateCurtainWall(uiApp, req.args);
                    case "create_topography":   return CreateTopography(uiApp, req.args);
                    case "list_toposolid_types": return ListToposolidTypes(uiApp);
                    case "create_toposolid":     return CreateToposolid(uiApp, req.args);
                    case "create_building_pad": return CreateBuildingPad(uiApp, req.args);
                    case "create_slab_edge":    return CreateSlabEdge(uiApp, req.args);
                    case "create_ceiling_plan": return CreateCeilingPlan(uiApp, req.args);
                    case "modify_roof":         return ModifyRoof(uiApp, req.args);

                    // --- Componentes (Onda 4) ---
                    case "place_plumbing_fixture": return PlaceByCategory(uiApp, req.args, BuiltInCategory.OST_PlumbingFixtures);
                    case "place_kitchen_counter":  return PlaceKitchenCounter(uiApp, req.args);
                    case "place_vegetation":       return PlaceByCategory(uiApp, req.args, BuiltInCategory.OST_Planting);
                    case "place_car":              return PlaceByCategory(uiApp, req.args, BuiltInCategory.OST_Parking);
                    case "place_pool":             return PlacePool(uiApp, req.args);
                    case "place_appliance":        return PlaceByCategory(uiApp, req.args, BuiltInCategory.OST_SpecialityEquipment);

                    // --- Pranchas (Onda 5) ---
                    case "create_sheet":         return CreateSheet(uiApp, req.args);
                    case "place_view_on_sheet":  return PlaceViewOnSheet(uiApp, req.args);
                    case "create_schedule":      return CreateSchedule(uiApp, req.args);
                    case "create_legend":        return CreateLegend(uiApp, req.args);
                    case "create_color_fill":    return CreateColorFill(uiApp, req.args);
                    case "create_north_arrow":   return CreateNorthArrow(uiApp, req.args);

                    // --- Exportação (Onda 6) ---
                    case "export_pdf":  return ExportPdf(uiApp, req.args);
                    case "export_dwg":  return ExportDwg(uiApp, req.args);
                    case "export_ifc":  return ExportIfc(uiApp, req.args);

                    // --- Configuração (Onda 7) ---
                    case "set_view_template": return SetViewTemplate(uiApp, req.args);
                    case "set_wall_join":     return SetWallJoin(uiApp, req.args);
                    case "set_line_style":    return SetLineStyle(uiApp, req.args);
                    case "import_cad":        return ImportCad(uiApp, req.args);
                    case "validate_document": return ValidateDocument(uiApp);
                    case "analyze_bim_quality": return AnalyzeBimQuality(uiApp);
                    case "validate_room_program": return ValidateRoomProgram(uiApp, req.args);
                    case "audit_documentation": return AuditDocumentation(uiApp);
                    case "get_worksharing_status": return GetWorksharingStatus(uiApp);
                    case "get_element_worksharing_info": return GetElementWorksharingInfo(uiApp, req.args);
                    case "list_audit_log": return ListAuditLog(uiApp, req.args);

                    default:
                        throw new Exception($"Comando desconhecido: {req.cmd}");
                }
        }

        private static bool IsAuditableWrite(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return false;
            return !(cmd.StartsWith("list_", StringComparison.OrdinalIgnoreCase)
                || cmd.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
                || cmd.StartsWith("validate_", StringComparison.OrdinalIgnoreCase)
                || cmd.StartsWith("analyze_", StringComparison.OrdinalIgnoreCase)
                || cmd.StartsWith("preview_", StringComparison.OrdinalIgnoreCase)
                || cmd == "ping" || cmd == "bridge_health" || cmd == "save_document"
                || cmd == "backup_document" || cmd.StartsWith("export_", StringComparison.OrdinalIgnoreCase));
        }

        private static readonly Guid AuditSchemaGuid = new Guid("9AD434C7-8A4A-4B66-AE0B-5A7D8AC835E1");

        private static Schema AuditSchema()
        {
            var existing = Schema.Lookup(AuditSchemaGuid);
            if (existing != null) return existing;
            var builder = new SchemaBuilder(AuditSchemaGuid);
            builder.SetSchemaName("RevitMcpAuditV1");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("timestamp_utc", typeof(string));
            builder.AddSimpleField("user", typeof(string));
            builder.AddSimpleField("command", typeof(string));
            builder.AddSimpleField("status", typeof(string));
            return builder.Finish();
        }

        private static void AppendAuditEntry(Document doc, string user, string command, string status)
        {
            var schema = AuditSchema();
            using (var tx = new Transaction(doc, "MCP: Auditoria"))
            {
                tx.Start();
                var storage = DataStorage.Create(doc);
                storage.Name = "MCP Audit " + DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
                var entity = new Entity(schema);
                entity.Set(schema.GetField("timestamp_utc"), DateTime.UtcNow.ToString("o"));
                entity.Set(schema.GetField("user"), user ?? "desconhecido");
                entity.Set(schema.GetField("command"), command);
                entity.Set(schema.GetField("status"), status);
                storage.SetEntity(entity);
                tx.Commit();
            }
        }

        private static object ListAuditLog(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var schema = Schema.Lookup(AuditSchemaGuid);
            if (schema == null) return new { quantidade = 0, entradas = Array.Empty<object>() };
            int limit = Math.Clamp((int)GetD(args, "limit", 50), 1, 500);
            var entries = new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .Select(ds => new { ds, entity = ds.GetEntity(schema) })
                .Where(x => x.entity.IsValid())
                .Select(x => new {
                    id = x.ds.Id.Value,
                    instante_utc = x.entity.Get<string>(schema.GetField("timestamp_utc")),
                    usuario = x.entity.Get<string>(schema.GetField("user")),
                    comando = x.entity.Get<string>(schema.GetField("command")),
                    status = x.entity.Get<string>(schema.GetField("status"))
                })
                .OrderByDescending(x => x.instante_utc).Take(limit).ToList();
            return new { quantidade = entries.Count, entradas = entries };
        }

        // =====================================================================
        // Utilitários de conversão de unidades e leitura de argumentos
        // =====================================================================

        // Fator de conversão: 1 metro = 3.28084 pés
        private const double FT_PER_M = 1.0 / 0.3048;

        /// <summary>Converte metros para pés (unidade interna do Revit).</summary>
        private static double MToFt(double metros) => metros * FT_PER_M;

        /// <summary>Converte pés (unidade interna do Revit) para metros.</summary>
        private static double FtToM(double feet) => feet * 0.3048;

        /// <summary>Converte pés quadrados (unidade interna de área do Revit, ex: Room.Area) para m².</summary>
        private static double SqFtToM2(double sqFeet) => sqFeet * 0.3048 * 0.3048;

        /// <summary>
        /// Obtém o Document ativo ou lança exceção com mensagem amigável.
        /// </summary>
        private static Document RequireDoc(UIApplication uiApp)
        {
            var doc = uiApp?.ActiveUIDocument?.Document;
            if (doc == null)
                throw new Exception(
                    "Nenhum documento aberto. Abra ou crie um projeto (.rvt) no Revit primeiro.");
            return doc;
        }

        /// <summary>Lê um double do JSON, com valor padrão se ausente.</summary>
        private static double GetD(JsonElement args, string name, double fallback)
        {
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
            return fallback;
        }

        /// <summary>Lê um double obrigatório do JSON. Lança exceção se ausente.</summary>
        private static double GetDReq(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
                throw new ArgumentException($"Argumento obrigatório ausente: {name}");
            return v.GetDouble();
        }

        /// <summary>Lê uma string do JSON, com valor padrão se ausente.</summary>
        private static string GetS(JsonElement args, string name, string fallback = null)
        {
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return fallback;
        }

        /// <summary>Lê um double de um sub-objeto JSON (para arrays de itens).</summary>
        private static double GetItemD(JsonElement item, string key, double fallback)
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty(key, out var v) &&
                v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
            return fallback;
        }

        /// <summary>Lê um bool de um sub-objeto JSON (para arrays de itens).</summary>
        private static bool GetItemBool(JsonElement item, string key, bool fallback)
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty(key, out var v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                return v.GetBoolean();
            return fallback;
        }

        /// <summary>
        /// Remove acentos e coloca em minúsculas, para comparação de texto
        /// tolerante (ex: "Ceramica" casa com "Cerâmica"). Usado na busca de
        /// materiais, onde a IA pode digitar nomes sem acentuação.
        /// </summary>
        private static string Normalizar(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return string.Empty;
            var decomposto = texto.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(decomposto.Length);
            foreach (var c in decomposto)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                    != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().ToLowerInvariant();
        }

        // =====================================================================
        // Busca de nível (Level)
        // =====================================================================

        /// <summary>
        /// Encontra um Level pelo nome, ou retorna o nível mais adequado.
        /// Prioridade quando nome não é informado:
        ///   1. "Nível 1" (template brasileiro)
        ///   2. "Level 1" (template inglês)
        ///   3. Nível com elevação mais próxima de 0 (térreo)
        /// </summary>
        private static Level FindLevel(Document doc, string name)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();

            // Se um nome foi especificado: tenta match exato, depois normalizado (sem acento).
            if (!string.IsNullOrEmpty(name))
            {
                var exato = levels.FirstOrDefault(l =>
                    l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (exato != null) return exato;
                var alvo = Normalizar(name);
                return levels.FirstOrDefault(l => Normalizar(l.Name) == alvo)
                    ?? levels.FirstOrDefault(l => Normalizar(l.Name).Contains(alvo));
            }

            // Tenta nomes comuns de térreo
            var terreo = levels.FirstOrDefault(l =>
                l.Name.Equals("Nível 1", StringComparison.OrdinalIgnoreCase) ||
                l.Name.Equals("Nivel 1", StringComparison.OrdinalIgnoreCase) ||
                l.Name.Equals("Level 1", StringComparison.OrdinalIgnoreCase));
            if (terreo != null) return terreo;

            // Último recurso: nível mais próximo da elevação 0
            return levels
                .OrderBy(l => Math.Abs(l.Elevation))
                .ThenBy(l => l.Elevation)
                .FirstOrDefault();
        }

        /// <summary>
        /// Encontra um WallType (tipo de parede básica) por nome parcial,
        /// ignorando maiúsculas e acentos. Retorna null se não houver
        /// correspondência (ou se o nome for vazio).
        /// </summary>
        private static WallType FindWallType(Document doc, string nomeContem)
        {
            if (string.IsNullOrEmpty(nomeContem)) return null;

            var tipos = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType)).Cast<WallType>()
                .Where(t => t.Kind == WallKind.Basic)
                .ToList();

            var alvo = Normalizar(nomeContem);
            return tipos.FirstOrDefault(t => Normalizar(t.Name) == alvo)
                ?? tipos.FirstOrDefault(t => Normalizar(t.Name).Contains(alvo));
        }

        // =====================================================================
        // INFORMAÇÕES
        // =====================================================================

        /// <summary>
        /// Retorna informações sobre o Revit e o documento/vista ativos.
        /// Útil para verificar conexão e estado atual.
        /// </summary>
        private static object GetInfo(UIApplication uiApp)
        {
            var app = uiApp.Application;
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            return new
            {
                versao = app.VersionName,
                build = app.VersionBuild,
                usuario = app.Username,
                documento = doc?.Title,
                caminho = doc?.PathName,
                vista_ativa = uidoc?.ActiveView?.Name,
                tipo_vista = uidoc?.ActiveView?.ViewType.ToString()
            };
        }

        /// <summary>
        /// Lista as 40 categorias com mais elementos no documento.
        /// Exemplo: "Paredes: 12", "Portas: 3", "Mobiliário: 5"
        /// </summary>
        /// <summary>
        /// Lista avisos persistentes do modelo antes de uma automação continuar.
        /// Não fecha caixas de diálogo: revela a causa e os ElementId envolvidos.
        /// </summary>
        private static object ListModelWarnings(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var warnings = doc.GetWarnings()
                .Select(w => new
                {
                    severidade = w.GetSeverity().ToString(),
                    descricao = w.GetDescriptionText(),
                    elementos = w.GetFailingElements().Select(id => id.Value).ToList(),
                    adicionais = w.GetAdditionalElements().Select(id => id.Value).ToList()
                })
                .ToList();
            return new { quantidade = warnings.Count, avisos = warnings };
        }

        private static object ListCategories(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e.Category != null && !string.IsNullOrEmpty(e.Category.Name))
                .GroupBy(e => e.Category.Name)
                .OrderByDescending(g => g.Count())
                .Take(40)
                .Select(g => new { categoria = g.Key, quantidade = g.Count() })
                .ToList();
        }

        /// <summary>
        /// Lista todos os níveis (andares) do projeto, com elevação em metros.
        /// </summary>
        private static object ListLevels(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new
                {
                    id = l.Id.Value,
                    nome = l.Name,
                    elevacao_m = Math.Round(FtToM(l.Elevation), 3)
                })
                .ToList();
        }

        /// <summary>
        /// Lista todas as paredes do projeto, com comprimento em metros.
        /// </summary>
        private static object ListWalls(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Select(w => new
                {
                    id = w.Id.Value,
                    nome = w.Name,
                    comprimento_m = (w.Location is LocationCurve lc)
                        ? Math.Round(FtToM(lc.Curve.Length), 3)
                        : 0.0
                })
                .ToList();
        }

        /// <summary>Lista instâncias de janela e suas dimensões atuais.</summary>
        private static object ListWindows(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Windows)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Select(w =>
                {
                    var type = w.Symbol;
                    var width = type?.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble();
                    var height = type?.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble();
                    var sill = w.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble();
                    return new
                    {
                        id = w.Id.Value,
                        familia = type?.Family?.Name,
                        tipo = type?.Name,
                        nivel = (doc.GetElement(w.LevelId) as Level)?.Name,
                        largura_m = width.HasValue ? Math.Round(FtToM(width.Value), 3) : (double?)null,
                        altura_m = height.HasValue ? Math.Round(FtToM(height.Value), 3) : (double?)null,
                        peitoril_m = sill.HasValue ? Math.Round(FtToM(sill.Value), 3) : (double?)null
                    };
                })
                .OrderBy(w => w.nivel).ThenBy(w => w.id)
                .ToList();
        }

        /// <summary>
        /// Lista os tipos de parede básica disponíveis (nome e espessura),
        /// para uso no parâmetro wall_type de create_wall / create_walls.
        /// </summary>
        private static object ListWallTypes(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc)
                .OfClass(typeof(WallType)).Cast<WallType>()
                .Where(t => t.Kind == WallKind.Basic)
                .OrderBy(t => t.Width)
                .Select(t => new
                {
                    id = t.Id.Value,
                    nome = t.Name,
                    espessura_m = Math.Round(FtToM(t.Width), 3)
                })
                .ToList();
        }

        /// <summary>
        /// Lista os tipos de piso disponíveis (nome e espessura), excluindo
        /// lajes de fundação. Use o nome em floor_type de create_floor.
        /// </summary>
        private static object ListFloorTypes(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType)).Cast<FloorType>()
                .Where(ft => !ft.IsFoundationSlab)
                .OrderBy(ft => ft.Name)
                .Select(ft => new
                {
                    id = ft.Id.Value,
                    nome = ft.Name,
                    espessura_m = Math.Round(FtToM(ft.GetCompoundStructure()?.GetWidth() ?? 0), 3)
                })
                .ToList();
        }

        /// <summary>
        /// Lista famílias (FamilySymbol) carregadas no projeto.
        /// Filtra por categoria se informada (ex: "Doors", "Windows", "Furniture").
        /// </summary>
        private static object ListFamilies(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string catStr = GetS(args, "category");

            var query = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>();

            // Filtra por BuiltInCategory se nome foi informado
            if (!string.IsNullOrEmpty(catStr) &&
                (TryResolveComponentCategory(catStr, out var bic) ||
                 Enum.TryParse<BuiltInCategory>("OST_" + catStr, true, out bic)))
            {
                query = query.Where(s =>
                    s.Category != null && s.Category.Id.Value == (long)bic);
            }

            return query
                .Select(s => new
                {
                    id = s.Id.Value,
                    categoria = s.Category?.Name,
                    familia = s.Family.Name,
                    tipo = s.Name
                })
                .OrderBy(x => x.categoria)
                .ThenBy(x => x.familia)
                .Take(200)
                .ToList();
        }

        /// <summary>
        /// Resolve uma string de categoria (ex.: "Walls", "OST_Walls", "Doors")
        /// para a BuiltInCategory correspondente. Tenta primeiro os aliases de
        /// componentes (furniture, plumbing, etc.) e depois o nome direto do
        /// enum, com e sem o prefixo "OST_".
        /// </summary>
        private static bool TryResolveAnyCategory(string category, out BuiltInCategory result)
        {
            if (TryResolveComponentCategory(category, out result)) return true;
            if (Enum.TryParse(category, true, out result)) return true;
            if (Enum.TryParse("OST_" + category, true, out result)) return true;
            result = BuiltInCategory.INVALID;
            return false;
        }

        /// <summary>
        /// Lista elementos de qualquer categoria do documento (ex.: "Walls",
        /// "Doors", "Furniture"). Para instâncias de família, inclui família,
        /// tipo e nível; para outros elementos, apenas id/nome/categoria.
        /// </summary>
        private static object ListElements(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string catStr = GetS(args, "category")
                ?? throw new ArgumentException("Argumento obrigatório ausente: category");
            if (!TryResolveAnyCategory(catStr, out var bic))
                throw new ArgumentException($"Categoria desconhecida: {catStr}");
            int limit = Math.Clamp((int)GetD(args, "limit", 100), 1, 1000);

            var elementos = new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .Take(limit)
                .Select(e =>
                {
                    var fi = e as FamilyInstance;
                    var levelId = e.LevelId;
                    return new
                    {
                        id = e.Id.Value,
                        nome = e.Name,
                        categoria = e.Category?.Name,
                        familia = fi?.Symbol?.Family?.Name,
                        tipo = fi?.Symbol?.Name,
                        nivel = (levelId != ElementId.InvalidElementId)
                            ? (doc.GetElement(levelId) as Level)?.Name
                            : null
                    };
                })
                .ToList();
            return new { quantidade = elementos.Count, elementos };
        }

        /// <summary>
        /// Retorna os detalhes completos (categoria, tipo, nível, parâmetros)
        /// de um elemento qualquer pelo ID.
        /// </summary>
        private static object GetElement(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "element_id");
            var el = doc.GetElement(id) ?? throw new Exception($"Elemento {id.Value} não existe.");
            var fi = el as FamilyInstance;
            var levelId = el.LevelId;

            var parametros = el.Parameters.Cast<Parameter>()
                .Where(p => p.Definition != null)
                .Select(p => new
                {
                    nome = p.Definition.Name,
                    valor = p.AsValueString() ?? (p.StorageType == StorageType.String ? p.AsString() : null),
                    tipo_armazenamento = p.StorageType.ToString(),
                    somente_leitura = p.IsReadOnly
                })
                .OrderBy(p => p.nome)
                .ToList();

            return new
            {
                id = el.Id.Value,
                nome = el.Name,
                categoria = el.Category?.Name,
                familia = fi?.Symbol?.Family?.Name,
                tipo = fi?.Symbol?.Name,
                nivel = (levelId != ElementId.InvalidElementId)
                    ? (doc.GetElement(levelId) as Level)?.Name
                    : null,
                parametros
            };
        }

        /// <summary>Lista todas as vistas do projeto (id, nome, tipo, template aplicado).</summary>
        private static object ListViews(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .Select(v => new
                {
                    id = v.Id.Value,
                    nome = v.Name,
                    tipo = v.ViewType.ToString(),
                    template = (v.ViewTemplateId != ElementId.InvalidElementId)
                        ? doc.GetElement(v.ViewTemplateId)?.Name
                        : null
                })
                .OrderBy(v => v.tipo).ThenBy(v => v.nome)
                .ToList();
        }

        /// <summary>Retorna informações da vista atualmente ativa na UI do Revit.</summary>
        private static object GetActiveView(UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument ?? throw new Exception("Nenhum documento ativo.");
            var view = uidoc.ActiveView;
            return new
            {
                id = view.Id.Value,
                nome = view.Name,
                tipo = view.ViewType.ToString(),
                template = (view.ViewTemplateId != ElementId.InvalidElementId)
                    ? uidoc.Document.GetElement(view.ViewTemplateId)?.Name
                    : null
            };
        }

        /// <summary>Retorna os elementos atualmente selecionados na UI do Revit.</summary>
        private static object GetSelection(UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument ?? throw new Exception("Nenhum documento ativo.");
            var doc = uidoc.Document;
            var elementos = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e != null)
                .Select(e => new
                {
                    id = e.Id.Value,
                    nome = e.Name,
                    categoria = e.Category?.Name
                })
                .ToList();
            return new { quantidade = elementos.Count, elementos };
        }

        // =====================================================================
        // PROJETO — criar, abrir, salvar
        // =====================================================================

        /// <summary>
        /// Cria um novo projeto Revit.
        /// Se template_path for informado, usa esse template.
        /// Senão, usa o template padrão do Revit.
        /// Se nenhum template existir, cria projeto métrico básico.
        /// </summary>
        private static object NewProject(UIApplication uiApp, JsonElement args)
        {
            var app = uiApp.Application;
            string template = GetS(args, "template_path");

            if (string.IsNullOrEmpty(template))
                template = app.DefaultProjectTemplate;

            Document doc;
            if (!string.IsNullOrEmpty(template) && File.Exists(template))
                doc = app.NewProjectDocument(template);
            else
                doc = app.NewProjectDocument(UnitSystem.Metric);

            // Salva temporariamente para poder abrir como documento ativo
            var tmp = Path.Combine(Path.GetTempPath(), $"mcp_novo_{Guid.NewGuid():N}.rvt");
            doc.SaveAs(tmp, new SaveAsOptions { OverwriteExistingFile = true });
            doc.Close(false);
            var uidoc = uiApp.OpenAndActivateDocument(tmp);

            return new
            {
                titulo = uidoc.Document.Title,
                caminho = uidoc.Document.PathName,
                vista_ativa = uidoc.ActiveView?.Name
            };
        }

        /// <summary>Abre um arquivo .rvt existente.</summary>
        private static object OpenDocument(UIApplication uiApp, JsonElement args)
        {
            string path = GetS(args, "path")
                ?? throw new ArgumentException("Argumento 'path' é obrigatório");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Arquivo não encontrado: {path}");

            var uidoc = uiApp.OpenAndActivateDocument(path);
            return new { titulo = uidoc.Document.Title, caminho = uidoc.Document.PathName };
        }

        /// <summary>
        /// Salva o documento ativo no seu caminho atual. Esta operação é
        /// deliberadamente separada de SaveAs para não trocar o arquivo aberto
        /// nem apagar backups enquanto uma automação está apenas persistindo
        /// alterações já validadas.
        /// </summary>
        private static object SaveDocument(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            if (doc.IsReadOnly)
                throw new InvalidOperationException("O documento ativo é somente leitura e não pode ser salvo.");
            if (string.IsNullOrWhiteSpace(doc.PathName))
                throw new InvalidOperationException("O documento ainda não possui caminho. Use save_as primeiro.");

            doc.Save();
            return new { salvo = doc.PathName, tamanho_bytes = new FileInfo(doc.PathName).Length };
        }

        /// <summary>
        /// Salva o documento ativo no caminho especificado.
        /// Remove backups antigos que possam bloquear a operação.
        /// </summary>
        private static object SaveAs(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string path = GetS(args, "path")
                ?? throw new ArgumentException("Argumento 'path' é obrigatório");

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Remove backups que podem bloquear SaveAs
            foreach (var side in new[] { path, path + ".backup", path + ".rvt.backup" })
            {
                try { if (File.Exists(side)) File.Delete(side); } catch { }
            }

            // Remove backups numerados do Revit (nome.0001.rvt)
            try
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                var d = string.IsNullOrEmpty(dir) ? "." : dir;
                foreach (var f in Directory.GetFiles(d, stem + ".*.rvt"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }

            doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true, Compact = true });
            return new { salvo = path, tamanho_bytes = new FileInfo(path).Length };
        }

        /// <summary>
        /// Deleta um elemento pelo ID. Retorna a quantidade de elementos
        /// removidos (pode ser > 1 se o elemento tinha dependências).
        /// </summary>
        private static object DeleteElement(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            long id = (long)GetDReq(args, "id");
            bool confirmed = args.TryGetProperty("confirm", out var confirmValue) && confirmValue.ValueKind == JsonValueKind.True;
            if (!confirmed)
                throw new InvalidOperationException("Exclusão bloqueada. Execute preview_delete e repita delete_element com confirm=true.");

            using (var tx = new Transaction(doc, "MCP: Excluir elemento"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                var removed = doc.Delete(new ElementId(id));
                tx.Commit();
                return new { excluidos = removed.Count };
            }
        }

        /// <summary>Mostra os impactos de uma exclusão sem modificar o modelo.</summary>
        private static object PreviewDelete(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var element = doc.GetElement(GetElementIdReq(args, "id"))
                ?? throw new Exception("Elemento não encontrado.");
            var dependents = element.GetDependentElements(null).Where(id => id != element.Id).Select(id => {
                var dependent = doc.GetElement(id);
                return new { id = id.Value, nome = dependent?.Name, categoria = dependent?.Category?.Name };
            }).ToList();
            return new {
                elemento = new { id = element.Id.Value, nome = element.Name, categoria = element.Category?.Name },
                dependentes = dependents,
                total_estimado = dependents.Count + 1,
                proximo_passo = "Revise os impactos e execute delete_element com confirm=true somente se a exclusão for desejada."
            };
        }

        /// <summary>
        /// Salva o RVT e copia um ponto de restauração para a pasta indicada.
        /// Não usa SaveAs: o documento ativo permanece no mesmo caminho.
        /// Não é permitido para modelos workshared, que exigem fluxo central/local.
        /// </summary>
        private static object BackupDocument(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (doc.IsWorkshared)
                throw new InvalidOperationException("Backup por cópia é bloqueado para modelo workshared. Use o fluxo de central/local da equipe.");
            if (string.IsNullOrWhiteSpace(doc.PathName))
                throw new InvalidOperationException("O documento ainda não possui caminho. Use save_as primeiro.");
            var folder = GetS(args, "folder");
            if (string.IsNullOrWhiteSpace(folder))
                folder = Path.Combine(Path.GetDirectoryName(doc.PathName), "mcp-backups");
            Directory.CreateDirectory(folder);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var name = Path.GetFileNameWithoutExtension(doc.PathName) + "_" + stamp + ".rvt";
            var target = Path.Combine(folder, name);
            doc.Save();
            File.Copy(doc.PathName, target, false);
            return new { backup = target, tamanho_bytes = new FileInfo(target).Length, origem = doc.PathName };
        }

        private static readonly HashSet<string> BatchCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "create_level", "create_wall", "create_walls", "create_plan_layout", "create_box",
            "create_floor", "create_roof", "create_ceiling", "create_column", "create_grid",
            "create_detail_line", "create_detail_lines", "create_detail_rectangle", "create_text",
            "create_material", "create_rectangular_prism", "place_kitchen_counter", "place_pool",
            "place_rooms_at", "set_plan_crop", "set_wall_join", "set_line_style"
        };

        /// <summary>Valida e explica um lote antes de ele tocar no modelo.</summary>
        private static object PlanBatch(UIApplication uiApp, JsonElement args)
        {
            RequireDoc(uiApp);
            if (!args.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Array 'operations' é obrigatório.");
            var plan = new List<object>();
            bool allAllowed = true;
            int index = 0;
            foreach (var item in operations.EnumerateArray())
            {
                string cmd = GetS(item, "cmd");
                bool allowed = !string.IsNullOrWhiteSpace(cmd) && BatchCommands.Contains(cmd);
                allAllowed &= allowed;
                plan.Add(new { indice = index++, comando = cmd, permitido = allowed,
                    risco = allowed ? "modelagem controlada" : "bloqueado",
                    motivo = allowed ? "Pode ser executado dentro de TransactionGroup." : "Comando não permitido em lote; execute-o isoladamente." });
            }
            return new { simulacao_padrao = true, requer_commit_explicito = true, operacoes = plan,
                permitido = allAllowed };
        }

        /// <summary>
        /// Executa operações em TransactionGroup. Por padrão faz dry-run e
        /// rollback; use commit=true somente após analisar plan_batch.
        /// </summary>
        private static object ExecuteBatch(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!args.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() == 0)
                throw new ArgumentException("Array 'operations' não vazio é obrigatório.");
            bool commit = args.TryGetProperty("commit", out var cv) && cv.ValueKind == JsonValueKind.True;
            var results = new List<object>();
            using (var group = new TransactionGroup(doc, "MCP: Lote atômico"))
            {
                group.Start();
                try
                {
                    int index = 0;
                    foreach (var item in operations.EnumerateArray())
                    {
                        string cmd = GetS(item, "cmd");
                        if (string.IsNullOrWhiteSpace(cmd) || !BatchCommands.Contains(cmd))
                            throw new ArgumentException($"Operação {index}: comando '{cmd ?? "(vazio)"}' não permitido em lote.");
                        var itemArgs = item.TryGetProperty("args", out var av) && av.ValueKind == JsonValueKind.Object
                            ? av : JsonDocument.Parse("{}").RootElement.Clone();
                        var result = ExecutarComando(uiApp, new RpcRequest { cmd = cmd, args = itemArgs });
                        results.Add(new { indice = index, comando = cmd, resultado = result });
                        index++;
                    }
                    if (commit) group.Assimilate(); else group.RollBack();
                }
                catch
                {
                    if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                    throw;
                }
            }
            return new { commit, simulacao = !commit, operacoes = results,
                observacao = commit ? "Lote confirmado como uma única etapa de desfazer." : "Dry-run concluído; todas as alterações foram revertidas." };
        }

        // =====================================================================
        // CRIAÇÃO DE ELEMENTOS — níveis, paredes, portas, janelas
        // =====================================================================

        /// <summary>
        /// Cria um novo nível (Level) na elevação especificada.
        /// Elevação em metros. Nome é opcional.
        /// </summary>
        private static object CreateLevel(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            double elev = GetD(args, "elevation_m", 3.0);
            string name = GetS(args, "name");

            using (var tx = new Transaction(doc, "MCP: Criar nível"))
            {
                tx.Start();
                var lvl = Level.Create(doc, MToFt(elev));
                if (!string.IsNullOrEmpty(name))
                {
                    try { lvl.Name = name; } catch { /* nome duplicado */ }
                }
                tx.Commit();
                return new
                {
                    id = lvl.Id.Value,
                    nome = lvl.Name,
                    elevacao_m = Math.Round(FtToM(lvl.Elevation), 3)
                };
            }
        }

        /// <summary>
        /// Cria uma parede reta entre dois pontos (x1,y1) e (x2,y2) em metros.
        /// Altura em metros, nível pelo nome (padrão: térreo).
        /// </summary>
        private static object CreateWall(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            double x1 = GetDReq(args, "x1_m");
            double y1 = GetDReq(args, "y1_m");
            double x2 = GetDReq(args, "x2_m");
            double y2 = GetDReq(args, "y2_m");
            double height = GetD(args, "height_m", 3.0);
            string levelName = GetS(args, "level");
            string wallTypeName = GetS(args, "wall_type");

            var level = FindLevel(doc, levelName)
                ?? throw new Exception("Nenhum nível encontrado. Crie um primeiro com create_level.");

            // Resolve o tipo de parede, se informado (senão usa o tipo padrão)
            WallType wt = null;
            if (!string.IsNullOrEmpty(wallTypeName))
                wt = FindWallType(doc, wallTypeName)
                    ?? throw new Exception(
                        $"Tipo de parede '{wallTypeName}' não encontrado. Use list_wall_types.");

            var p1 = new XYZ(MToFt(x1), MToFt(y1), level.Elevation);
            var p2 = new XYZ(MToFt(x2), MToFt(y2), level.Elevation);

            if (p1.DistanceTo(p2) < 1e-6)
                throw new Exception("Os pontos da parede são idênticos.");

            var line = Line.CreateBound(p1, p2);

            using (var tx = new Transaction(doc, "MCP: Criar parede"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                Wall wall = wt != null
                    ? Wall.Create(doc, line, wt.Id, level.Id, MToFt(height), 0.0, false, false)
                    : Wall.Create(doc, line, level.Id, false);
                if (wt == null)
                    wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(MToFt(height));
                tx.Commit();

                return new
                {
                    id = wall.Id.Value,
                    nivel = level.Name,
                    tipo = (doc.GetElement(wall.GetTypeId()) as WallType)?.Name,
                    comprimento_m = Math.Round(FtToM(line.Length), 3)
                };
            }
        }

        /// <summary>
        /// Cria várias paredes em uma única transação.
        /// Recebe array "walls" com objetos { x1_m, y1_m, x2_m, y2_m, height_m }.
        /// Mais eficiente que chamar create_wall várias vezes.
        /// </summary>
        private static object CreateWalls(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!args.TryGetProperty("walls", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Array 'walls' é obrigatório");

            string levelName = GetS(args, "level");
            var level = FindLevel(doc, levelName)
                ?? throw new Exception("Nenhum nível encontrado.");

            // Tipo de parede global (aplicado a todas, salvo override por-parede)
            string wallTypeGlobal = GetS(args, "wall_type");
            WallType wtGlobal = null;
            if (!string.IsNullOrEmpty(wallTypeGlobal))
                wtGlobal = FindWallType(doc, wallTypeGlobal)
                    ?? throw new Exception(
                        $"Tipo de parede '{wallTypeGlobal}' não encontrado. Use list_wall_types.");

            var created = new List<object>();
            using (var tx = new Transaction(doc, "MCP: Criar paredes"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                foreach (var item in arr.EnumerateArray())
                {
                    double x1 = item.GetProperty("x1_m").GetDouble();
                    double y1 = item.GetProperty("y1_m").GetDouble();
                    double x2 = item.GetProperty("x2_m").GetDouble();
                    double y2 = item.GetProperty("y2_m").GetDouble();
                    double h = item.TryGetProperty("height_m", out var hv) ? hv.GetDouble() : 3.0;

                    // Override de tipo por-parede (opcional)
                    WallType wt = wtGlobal;
                    if (item.TryGetProperty("wall_type", out var wtv) &&
                        wtv.ValueKind == JsonValueKind.String)
                        wt = FindWallType(doc, wtv.GetString()) ?? wtGlobal;

                    var line = Line.CreateBound(
                        new XYZ(MToFt(x1), MToFt(y1), level.Elevation),
                        new XYZ(MToFt(x2), MToFt(y2), level.Elevation));

                    Wall w = wt != null
                        ? Wall.Create(doc, line, wt.Id, level.Id, MToFt(h), 0.0, false, false)
                        : Wall.Create(doc, line, level.Id, false);
                    if (wt == null)
                        w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(MToFt(h));

                    created.Add(new
                    {
                        id = w.Id.Value,
                        tipo = (doc.GetElement(w.GetTypeId()) as WallType)?.Name,
                        comprimento_m = Math.Round(FtToM(line.Length), 3),
                        altura_m = h
                    });
                }
                tx.Commit();
            }
            return new { quantidade = created.Count, paredes = created, nivel = level.Name };
        }

        /// <summary>
        /// Cria paredes, portas e janelas em uma única transação. As aberturas
        /// referenciam a chave da parede (wall_key), sem depender de IDs entre RPCs.
        /// </summary>
        private static object CreatePlanLayout(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!args.TryGetProperty("walls", out var wallItems) || wallItems.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Array 'walls' é obrigatório.");

            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nenhum nível encontrado.");
            var wallTypeGlobal = FindWallType(doc, GetS(args, "wall_type"));
            var walls = new Dictionary<string, Wall>();
            var doorIds = new List<long>();
            var windowIds = new List<long>();

            using (var tx = new Transaction(doc, "MCP: Criar layout de planta"))
            {
                tx.Start();
                // Em automações não pode haver uma caixa modal esperando um clique.
                // Avisos (como paredes sobrepostas) são removidos do commit e continuam
                // sendo relatados pela validação de geometria do comando chamador.
                var failureOptions = tx.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(new McpWarningPreprocessor());
                failureOptions.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(failureOptions);
                int index = 0;
                foreach (var item in wallItems.EnumerateArray())
                {
                    string key = GetS(item, "key", index.ToString());
                    if (walls.ContainsKey(key)) throw new ArgumentException($"Chave de parede duplicada: {key}");
                    double x1 = GetItemD(item, "x1_m", double.NaN);
                    double y1 = GetItemD(item, "y1_m", double.NaN);
                    double x2 = GetItemD(item, "x2_m", double.NaN);
                    double y2 = GetItemD(item, "y2_m", double.NaN);
                    if (double.IsNaN(x1) || double.IsNaN(y1) || double.IsNaN(x2) || double.IsNaN(y2))
                        throw new ArgumentException($"Coordenadas inválidas na parede '{key}'.");
                    var line = Line.CreateBound(new XYZ(MToFt(x1), MToFt(y1), level.Elevation),
                                                new XYZ(MToFt(x2), MToFt(y2), level.Elevation));
                    if (line.Length < 1e-6) throw new ArgumentException($"Parede '{key}' tem comprimento zero.");
                    var type = FindWallType(doc, GetS(item, "wall_type")) ?? wallTypeGlobal;
                    double height = GetItemD(item, "height_m", 3.0);
                    var wall = type != null
                        ? Wall.Create(doc, line, type.Id, level.Id, MToFt(height), 0, false, false)
                        : Wall.Create(doc, line, level.Id, false);
                    if (type == null) wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(MToFt(height));
                    walls.Add(key, wall);
                    index++;
                }
                doc.Regenerate();
                CreateLayoutHostedInstances(args, "doors", walls, level, doc, BuiltInCategory.OST_Doors, double.NaN, doorIds);
                CreateLayoutHostedInstances(args, "windows", walls, level, doc, BuiltInCategory.OST_Windows, 1.0, windowIds);
                tx.Commit();
            }
            return new { nivel = level.Name, paredes = walls.ToDictionary(p => p.Key, p => p.Value.Id.Value), portas = doorIds, janelas = windowIds };
        }

        private static void CreateLayoutHostedInstances(JsonElement args, string property,
            Dictionary<string, Wall> walls, Level level, Document doc, BuiltInCategory category,
            double defaultSill, List<long> createdIds)
        {
            if (!args.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array) return;
            foreach (var item in items.EnumerateArray())
            {
                string key = GetS(item, "wall_key");
                if (string.IsNullOrEmpty(key) || !walls.TryGetValue(key, out var wall))
                    throw new ArgumentException($"{property}: wall_key '{key}' não encontrado.");
                string kind = GetS(item, "kind");
                double width = GetItemD(item, "width_m", double.NaN);
                double height = GetItemD(item, "height_m", double.NaN);
                double sill = GetItemD(item, "sill_m", defaultSill);
                string preset = category == BuiltInCategory.OST_Windows
                    ? ApplyWindowPreset(kind, ref width, ref height, ref sill) : null;
                var symbol = category == BuiltInCategory.OST_Windows
                    ? FindWindowSymbol(doc, GetS(item, "family"), GetS(item, "type"), kind)
                    : FindSymbol(doc, category, GetS(item, "family"), GetS(item, "type"));
                if (symbol == null) throw new Exception($"Nenhuma família carregada para {property}.");
                if (category == BuiltInCategory.OST_Windows)
                    symbol = ResolveWindowSize(doc, symbol, width, height, preset, out _);
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                var curve = (wall.Location as LocationCurve)?.Curve ?? throw new Exception("Parede sem curva de localização.");
                var point = curve.Evaluate(Math.Clamp(GetItemD(item, "position", 0.5), 0, 1), true);
                var instance = doc.Create.NewFamilyInstance(point, symbol, wall, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                if (!double.IsNaN(defaultSill))
                    instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(MToFt(sill));
                createdIds.Add(instance.Id.Value);
            }
        }

        /// <summary>
        /// Atalho: cria 4 paredes formando um retângulo centrado em (cx, cy).
        /// Útil para criar rapidamente um cômodo ou uma caixa de teste.
        /// </summary>
        private static object CreateBox(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            double cx = GetD(args, "cx_m", 0);
            double cy = GetD(args, "cy_m", 0);
            double w = GetD(args, "width_m", 5.0);
            double d = GetD(args, "depth_m", 4.0);
            double h = GetD(args, "height_m", 3.0);
            string levelName = GetS(args, "level");

            var level = FindLevel(doc, levelName)
                ?? throw new Exception("Nenhum nível encontrado. Crie um primeiro.");

            double x0 = cx - w / 2, x1 = cx + w / 2;
            double y0 = cy - d / 2, y1 = cy + d / 2;

            // Quatro cantos do retângulo, em sentido horário
            var edges = new (double, double, double, double)[]
            {
                (x0, y0, x1, y0),  // parede inferior
                (x1, y0, x1, y1),  // parede direita
                (x1, y1, x0, y1),  // parede superior
                (x0, y1, x0, y0),  // parede esquerda
            };

            var ids = new List<long>();
            using (var tx = new Transaction(doc, "MCP: Criar caixa (4 paredes)"))
            {
                tx.Start();
                foreach (var (ax, ay, bx, by) in edges)
                {
                    var line = Line.CreateBound(
                        new XYZ(MToFt(ax), MToFt(ay), level.Elevation),
                        new XYZ(MToFt(bx), MToFt(by), level.Elevation));
                    var wall = Wall.Create(doc, line, level.Id, false);
                    wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(MToFt(h));
                    ids.Add(wall.Id.Value);
                }
                tx.Commit();
            }
            return new { ids_paredes = ids, nivel = level.Name };
        }

        /// <summary>
        /// Cria uma porta em uma parede existente.
        /// O Revit projeta automaticamente o ponto (x,y) sobre a parede.
        /// </summary>
        private static object CreateDoor(UIApplication uiApp, JsonElement args)
            => CreateHostedInstance(uiApp, args, BuiltInCategory.OST_Doors, "MCP: Criar porta");

        /// <summary>
        /// Cria uma janela em uma parede existente.
        /// Aceita sill_m (altura do peitoril, padrão 1m), width_m e height_m.
        /// As dimensões criam ou reutilizam um tipo MCP sem alterar os tipos originais.
        /// </summary>
        private static object CreateWindow(UIApplication uiApp, JsonElement args)
            => CreateHostedInstance(uiApp, args, BuiltInCategory.OST_Windows, "MCP: Criar janela");

        /// <summary>
        /// Redimensiona uma janela existente trocando apenas a sua instância
        /// para um tipo MCP dedicado. Não modifica as demais janelas do projeto.
        /// </summary>
        private static object ResizeWindow(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "window_id");
            var window = doc.GetElement(id) as FamilyInstance;
            if (window?.Category?.Id.Value != (long)BuiltInCategory.OST_Windows)
                throw new ArgumentException($"Elemento {id.Value} não é uma janela.");

            double width = GetD(args, "width_m", double.NaN);
            double height = GetD(args, "height_m", double.NaN);
            double sill = GetD(args, "sill_m", double.NaN);
            string kind = GetS(args, "kind", "generica");
            string requestedFamily = GetS(args, "family");
            string requestedType = GetS(args, "type");
            bool useExistingType = !string.IsNullOrEmpty(requestedType);
            string preset = useExistingType ? null : ApplyWindowPreset(kind, ref width, ref height, ref sill);
            if (!useExistingType && double.IsNaN(width) && double.IsNaN(height))
                throw new ArgumentException("Informe width_m, height_m ou um perfil kind válido.");

            object result;
            using (var tx = new Transaction(doc, "MCP: Redimensionar janela"))
            {
                tx.Start();
                var options = tx.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new McpWarningPreprocessor());
                options.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(options);

                FamilySymbol symbol;
                string sizing;
                if (useExistingType)
                {
                    string family = string.IsNullOrEmpty(requestedFamily) ? window.Symbol.Family.Name : requestedFamily;
                    symbol = FindSymbol(doc, BuiltInCategory.OST_Windows, family, requestedType)
                        ?? throw new ArgumentException($"Tipo de janela '{requestedType}' não encontrado na família '{family}'.");
                    sizing = "tipo de janela existente aplicado";
                }
                else
                    symbol = ResolveWindowSize(doc, window.Symbol, width, height, preset, out sizing);
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                if (window.GetTypeId() != symbol.Id) window.ChangeTypeId(symbol.Id);
                if (!double.IsNaN(sill))
                    window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(MToFt(sill));

                double? finalWidth = symbol.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble();
                double? finalHeight = symbol.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble();
                double? finalSill = window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble();

                result = new
                {
                    id = window.Id.Value,
                    familia = symbol.Family.Name,
                    tipo = symbol.Name,
                    perfil = preset,
                    largura_m = finalWidth.HasValue ? Math.Round(FtToM(finalWidth.Value), 3) : (double?)null,
                    altura_m = finalHeight.HasValue ? Math.Round(FtToM(finalHeight.Value), 3) : (double?)null,
                    peitoril_m = finalSill.HasValue ? Math.Round(FtToM(finalSill.Value), 3) : (double?)null,
                    dimensoes = sizing
                };
                tx.Commit();
            }
            return result;
        }

        /// <summary>
        /// Método compartilhado para criar portas e janelas.
        /// Ambas são "hosted family instances" — instâncias hospedadas em paredes.
        /// </summary>
        private static object CreateHostedInstance(
            UIApplication uiApp, JsonElement args,
            BuiltInCategory cat, string txName)
        {
            var doc = RequireDoc(uiApp);
            long wallId = (long)GetDReq(args, "wall_id");
            double sill = GetD(args, "sill_m", double.NaN);
            string family = GetS(args, "family");
            string type = GetS(args, "type");
            string kind = GetS(args, "kind");
            double width = GetD(args, "width_m", double.NaN);
            double height = GetD(args, "height_m", double.NaN);
            string preset = cat == BuiltInCategory.OST_Windows
                ? ApplyWindowPreset(kind, ref width, ref height, ref sill) : null;
            if (cat == BuiltInCategory.OST_Windows && double.IsNaN(sill)) sill = 1.0;

            var wall = doc.GetElement(new ElementId(wallId)) as Wall
                ?? throw new Exception($"Parede com ID {wallId} não encontrada.");

            // Aceita duas formas de posicionamento:
            //   1. position = fração [0..1] ao longo da parede (ex: 0.5 = meio)
            //   2. x_m, y_m = coordenadas absolutas em metros
            double x, y;
            double pos = GetD(args, "position", double.NaN);
            if (!double.IsNaN(pos))
            {
                var curva = (wall.Location as LocationCurve)?.Curve
                    ?? throw new Exception("Parede sem curva de localização.");
                var p = curva.Evaluate(Math.Clamp(pos, 0, 1), true);
                x = FtToM(p.X); y = FtToM(p.Y);
            }
            else
            {
                x = GetDReq(args, "x_m");
                y = GetDReq(args, "y_m");
            }

            var sym = (cat == BuiltInCategory.OST_Windows
                    ? FindWindowSymbol(doc, family, type, kind)
                    : FindSymbol(doc, cat, family, type))
                ?? throw new Exception(
                    $"Nenhuma família de {(cat == BuiltInCategory.OST_Doors ? "portas" : "janelas")} " +
                    "carregada neste projeto. Use Inserir > Carregar Família no Revit.");

            object result;
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                var failureOptions = tx.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(new McpWarningPreprocessor());
                failureOptions.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(failureOptions);

                string sizing = null;
                if (cat == BuiltInCategory.OST_Windows)
                    sym = ResolveWindowSize(doc, sym, width, height, preset, out sizing);

                // Ativa o tipo se necessário (exigido pelo Revit)
                if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }

                var level = doc.GetElement(wall.LevelId) as Level;
                var pt = new XYZ(MToFt(x), MToFt(y), level?.Elevation ?? 0);

                var inst = doc.Create.NewFamilyInstance(
                    pt, sym, wall, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                // Define altura do peitoril (só para janelas)
                if (!double.IsNaN(sill))
                {
                    inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)
                        ?.Set(MToFt(sill));
                }

                result = new
                {
                    id = inst.Id.Value,
                    familia = sym.Family.Name,
                    tipo = sym.Name,
                    largura_m = double.IsNaN(width) ? (double?)null : width,
                    altura_m = double.IsNaN(height) ? (double?)null : height,
                    perfil = preset,
                    peitoril_m = double.IsNaN(sill) ? (double?)null : sill,
                    dimensoes = sizing
                };
                tx.Commit();
            }
            return result;
        }

        /// <summary>
        /// Encontra um FamilySymbol por categoria e nome parcial da família/tipo.
        /// Se nome não for informado, retorna o primeiro disponível.
        /// </summary>
        private static FamilySymbol FindSymbol(
            Document doc, BuiltInCategory cat, string familyContains, string typeContains = null)
        {
            var all = new FilteredElementCollector(doc)
                .OfCategory(cat)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            if (all.Count == 0) return null;

            if (!string.IsNullOrEmpty(familyContains))
            {
                all = all.Where(s =>
                    s.Family.Name.IndexOf(familyContains, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (all.Count == 0) return null;
            }

            if (!string.IsNullOrEmpty(typeContains))
            {
                var hit = all.FirstOrDefault(s =>
                    s.Name.IndexOf(typeContains, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) return hit;
            }

            return all.First();
        }

        /// <summary>
        /// Define medidas usuais por ambiente. Valores enviados explicitamente
        /// pelo cliente sempre têm prioridade sobre o perfil.
        /// </summary>
        private static string ApplyWindowPreset(string kind, ref double widthM, ref double heightM, ref double sillM)
        {
            string normalized = (kind ?? "").Trim().ToLowerInvariant();
            double defaultWidth = double.NaN, defaultHeight = double.NaN, defaultSill = double.NaN;
            string label = null;
            switch (normalized)
            {
                case "generic": case "generica": case "genérica": case "custom":
                    label = "generica"; defaultWidth = 1.20; defaultHeight = 1.20; defaultSill = 1.00; break;
                case "bedroom": case "quarto": case "dormitorio": case "dormitório":
                    label = "quarto"; defaultWidth = 1.50; defaultHeight = 1.20; defaultSill = 1.10; break;
                case "bathroom": case "banheiro": case "lavabo": case "wc":
                    label = "banheiro"; defaultWidth = 0.60; defaultHeight = 0.60; defaultSill = 1.60; break;
                case "living": case "sala": case "estar":
                    label = "sala"; defaultWidth = 2.40; defaultHeight = 1.50; defaultSill = 0.60; break;
                case "kitchen": case "cozinha":
                    label = "cozinha"; defaultWidth = 1.20; defaultHeight = 1.00; defaultSill = 1.20; break;
                case "laundry": case "lavanderia": case "lavandaria":
                    label = "lavanderia"; defaultWidth = 0.80; defaultHeight = 0.80; defaultSill = 1.50; break;
                case "closet":
                    label = "closet"; defaultWidth = 0.80; defaultHeight = 0.80; defaultSill = 1.50; break;
            }
            if (label == null) return null;
            if (double.IsNaN(widthM)) widthM = defaultWidth;
            if (double.IsNaN(heightM)) heightM = defaultHeight;
            if (double.IsNaN(sillM)) sillM = defaultSill;
            return label;
        }

        /// <summary>
        /// Quando existirem famílias adequadas carregadas, prefere uma tipologia
        /// compatível (por exemplo basculante para banheiro). Sem uma família
        /// específica, a geometria e o peitoril do perfil continuam sendo usados.
        /// </summary>
        private static FamilySymbol FindWindowSymbol(Document doc, string familyContains, string typeContains, string kind)
        {
            if (!string.IsNullOrEmpty(familyContains) || !string.IsNullOrEmpty(typeContains))
                return FindSymbol(doc, BuiltInCategory.OST_Windows, familyContains, typeContains);

            string normalized = (kind ?? "").Trim().ToLowerInvariant();
            string[] hints = normalized == "bathroom" || normalized == "banheiro" || normalized == "lavabo" || normalized == "wc"
                ? new[] { "basculante", "maxim", "awning", "bath" }
                : normalized == "bedroom" || normalized == "quarto" || normalized == "dormitorio" || normalized == "dormitório"
                    ? new[] { "veneziana", "casement", "sliding", "quarto" }
                    : normalized == "living" || normalized == "sala" || normalized == "estar"
                        ? new[] { "sliding", "fixed", "picture", "panoramic" }
                        : Array.Empty<string>();
            if (hints.Length > 0)
            {
                var match = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows).OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s => hints.Any(h => (s.Family.Name + " " + s.Name)
                        .IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0));
                if (match != null) return match;
            }
            return FindSymbol(doc, BuiltInCategory.OST_Windows, null, null);
        }

        /// <summary>
        /// Cria (ou reutiliza) um tipo de janela com a dimensão solicitada. Os
        /// parâmetros são de tipo na maioria das famílias, portanto duplicamos o
        /// tipo base para não redimensionar janelas já existentes no projeto.
        /// </summary>
        private static FamilySymbol ResolveWindowSize(Document doc, FamilySymbol baseSymbol,
            double widthM, double heightM, string preset, out string result)
        {
            result = null;
            if (double.IsNaN(widthM) && double.IsNaN(heightM)) return baseSymbol;
            if (!double.IsNaN(widthM) && widthM <= 0)
                throw new ArgumentException("width_m deve ser maior que zero.");
            if (!double.IsNaN(heightM) && heightM <= 0)
                throw new ArgumentException("height_m deve ser maior que zero.");

            string widthLabel = double.IsNaN(widthM) ? "auto" : Math.Round(widthM * 1000).ToString("0") + "mm";
            string heightLabel = double.IsNaN(heightM) ? "auto" : Math.Round(heightM * 1000).ToString("0") + "mm";
            string prefix = string.IsNullOrEmpty(preset) ? "" : " - " + preset.ToUpperInvariant();
            string typeName = $"{baseSymbol.Name}{prefix} - MCP {widthLabel} x {heightLabel}";

            var existing = baseSymbol.Family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id) as FamilySymbol)
                .FirstOrDefault(s => s != null && s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                result = "tipo MCP existente reutilizado";
                return existing;
            }

            var sized = baseSymbol.Duplicate(typeName) as FamilySymbol;
            if (sized == null)
                throw new Exception("Não foi possível duplicar o tipo de janela.");

            bool widthApplied = double.IsNaN(widthM) || TrySetWindowLength(sized,
                BuiltInParameter.WINDOW_WIDTH, new[] { "Width", "Largura" }, MToFt(widthM));
            bool heightApplied = double.IsNaN(heightM) || TrySetWindowLength(sized,
                BuiltInParameter.WINDOW_HEIGHT, new[] { "Height", "Altura" }, MToFt(heightM));

            if (!widthApplied && !heightApplied)
            {
                doc.Delete(sized.Id);
                result = "a família não expõe parâmetros editáveis de largura/altura; usado o tipo original";
                return baseSymbol;
            }

            var pending = new List<string>();
            if (!widthApplied) pending.Add("largura");
            if (!heightApplied) pending.Add("altura");
            result = pending.Count == 0
                ? "dimensões aplicadas em tipo MCP dedicado"
                : "tipo MCP criado; parâmetro sem edição: " + string.Join(", ", pending);
            return sized;
        }

        private static bool TrySetWindowLength(FamilySymbol symbol, BuiltInParameter builtIn,
            IEnumerable<string> displayNames, double valueFt)
        {
            var candidates = new List<Parameter> { symbol.get_Parameter(builtIn) };
            candidates.AddRange(displayNames.Select(symbol.LookupParameter));
            foreach (var parameter in candidates.Where(p => p != null).Distinct())
            {
                if (parameter.IsReadOnly || parameter.StorageType != StorageType.Double) continue;
                if (parameter.Set(valueFt)) return true;
            }
            return false;
        }

        // =====================================================================
        // FAMÍLIAS — carregar .rfa e posicionar instâncias reais
        // =====================================================================

        /// <summary>
        /// Carrega uma família (.rfa) do disco para dentro do projeto.
        /// Necessário porque o Revit 2025 não instala mobiliário localmente —
        /// o arquiteto baixa famílias e as carrega com este comando.
        ///
        /// Parâmetros:
        ///   path — caminho completo do arquivo .rfa (obrigatório)
        ///
        /// Retorna a família carregada e a lista de seus tipos (símbolos),
        /// para uso posterior em place_family_instance.
        /// </summary>
        private static object LoadFamily(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string path = GetS(args, "path");
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Argumento obrigatório ausente: path");
            if (!File.Exists(path))
                throw new Exception($"Arquivo não encontrado: {path}");

            Family family = null;
            using (var tx = new Transaction(doc, "MCP: Carregar família"))
            {
                tx.Start();
                if (!doc.LoadFamily(path, out family))
                {
                    // Já carregada — tenta localizar pela raiz do nome do arquivo
                    var nome = Path.GetFileNameWithoutExtension(path);
                    family = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f =>
                            f.Name.Equals(nome, StringComparison.OrdinalIgnoreCase));
                }
                tx.Commit();
            }

            if (family == null)
                throw new Exception(
                    "Falha ao carregar a família. Verifique se o .rfa é compatível com o Revit 2025.");

            // Lista os tipos (símbolos) da família carregada
            var tipos = family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id) as FamilySymbol)
                .Where(s => s != null)
                .Select(s => s.Name)
                .ToList();

            return new
            {
                id = family.Id.Value,
                familia = family.Name,
                categoria = family.FamilyCategory?.Name,
                tipos = tipos
            };
        }

        /// <summary>
        /// Cria uma familia RFA editavel a partir do template de mobiliario
        /// metrico instalado com o Revit, salva-a e a carrega no documento
        /// aberto. E uma alternativa local e reproduzivel quando a biblioteca
        /// comercial de objetos ainda nao foi instalada.
        /// </summary>
        private static object CreateFurnitureFamily(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string name = GetS(args, "name") ?? throw new ArgumentException("Argumento 'name' obrigatorio.");
            double width = GetDReq(args, "width_m");
            double depth = GetDReq(args, "depth_m");
            double height = GetDReq(args, "height_m");
            if (width <= 0 || depth <= 0 || height <= 0)
                throw new ArgumentException("width_m, depth_m e height_m devem ser maiores que zero.");

            string template = "C:\\ProgramData\\Autodesk\\RVT 2025\\Family Templates\\Portuguese\\Mobili\u00e1rio m\u00e9trico.rft";
            if (!File.Exists(template))
                throw new FileNotFoundException("Template de mobiliario metrico do Revit nao encontrado.", template);
            string output = GetS(args, "path");
            if (string.IsNullOrWhiteSpace(output))
                output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "families", name + ".rfa");
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            var familyDoc = uiApp.Application.NewFamilyDocument(template);
            try
            {
                using (var familyTx = new Transaction(familyDoc, "MCP: Criar familia de mobiliario"))
                {
                    familyTx.Start();
                    familyDoc.FamilyManager.NewType(name);
                    var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    var sketch = SketchPlane.Create(familyDoc, plane);
                    var curves = new CurveArray();
                    double w = MToFt(width), d = MToFt(depth);
                    var p0 = XYZ.Zero;
                    var p1 = new XYZ(w, 0, 0);
                    var p2 = new XYZ(w, d, 0);
                    var p3 = new XYZ(0, d, 0);
                    curves.Append(Line.CreateBound(p0, p1));
                    curves.Append(Line.CreateBound(p1, p2));
                    curves.Append(Line.CreateBound(p2, p3));
                    curves.Append(Line.CreateBound(p3, p0));
                    var profiles = new CurveArrArray();
                    profiles.Append(curves);
                    familyDoc.FamilyCreate.NewExtrusion(true, profiles, sketch, MToFt(height));
                    familyTx.Commit();
                }
                familyDoc.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally
            {
                familyDoc.Close(false);
            }

            Family family = null;
            using (var loadTx = new Transaction(doc, "MCP: Carregar familia de mobiliario"))
            {
                loadTx.Start();
                ConfigureAutomaticWarningHandling(loadTx);
                if (!doc.LoadFamily(output, out family))
                    family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                        .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                loadTx.Commit();
            }
            if (family == null) throw new Exception("A familia foi criada, mas nao foi carregada no projeto.");
            var types = family.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol)
                .Where(s => s != null).Select(s => s.Name).ToList();
            return new { familia = family.Name, tipos = types, caminho = output, largura_m = width, profundidade_m = depth, altura_m = height };
        }

        /// <summary>
        /// Posiciona uma instância de uma família JÁ carregada no projeto,
        /// num ponto (x, y) sobre um nível, com rotação opcional.
        /// Diferente de place_furniture (caixas), aqui são famílias reais
        /// (sofás, mesas, colunas, etc).
        ///
        /// Parâmetros (metros e graus):
        ///   family     — nome (parcial) da família a posicionar (obrigatório)
        ///   type       — nome (parcial) do tipo/símbolo (opcional)
        ///   x_m, y_m   — posição no plano (obrigatórios)
        ///   z_m        — altura acima do nível (opcional, padrão 0)
        ///   rotation   — rotação em graus no eixo Z (opcional, padrão 0)
        ///   level      — nome do nível (opcional, usa térreo)
        /// </summary>
        private static object PlaceFamilyInstance(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string familyName = GetS(args, "family");
            if (string.IsNullOrEmpty(familyName))
                throw new ArgumentException("Argumento obrigatório ausente: family");
            string typeName = GetS(args, "type");
            double x = GetDReq(args, "x_m");
            double y = GetDReq(args, "y_m");
            double z = GetD(args, "z_m", 0);
            double rotDeg = GetD(args, "rotation", 0);

            var level = FindLevel(doc, GetS(args, "level"))
                ?? throw new Exception("Nenhum nível encontrado.");

            // Busca o símbolo por família (parcial) e, se informado, tipo (parcial)
            var candidatos = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family.Name.IndexOf(
                    familyName, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (candidatos.Count == 0)
                throw new Exception(
                    $"Família '{familyName}' não encontrada. Carregue-a antes com load_family, " +
                    "ou verifique o nome com list_families.");

            var sym = candidatos.FirstOrDefault(s =>
                    string.IsNullOrEmpty(typeName) ||
                    s.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? candidatos.First();

            object result;
            using (var tx = new Transaction(doc, "MCP: Posicionar família"))
            {
                tx.Start();

                if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }

                var pt = new XYZ(MToFt(x), MToFt(y), level.Elevation + MToFt(z));
                var inst = doc.Create.NewFamilyInstance(
                    pt, sym, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                // Aplica rotação no eixo Z ao redor do ponto de inserção
                if (Math.Abs(rotDeg) > 1e-6)
                {
                    var eixo = Line.CreateBound(pt, pt + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(
                        doc, inst.Id, eixo, rotDeg * Math.PI / 180.0);
                }

                result = new
                {
                    id = inst.Id.Value,
                    familia = sym.Family.Name,
                    tipo = sym.Name,
                    rotacao_graus = rotDeg
                };
                tx.Commit();
            }
            return result;
        }

        // =====================================================================
        // CÔMODOS e MOBILIÁRIO
        // =====================================================================

        /// <summary>
        /// Coloca Rooms (cômodos) em regiões fechadas por paredes.
        /// Os pontos são fixos para a planta padrão de 3 cômodos
        /// (Sala, Cozinha, Banheiro).
        /// </summary>
        private static object PlaceRooms(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var level = FindLevel(doc, null)
                ?? throw new Exception("Nenhum nível encontrado.");

            // Pontos internos dos cômodos da planta padrão
            var roomPoints = new (double x, double y, string nome)[]
            {
                (2.5, 2.5, "Sala"),
                (6.5, 1.25, "Cozinha"),
                (6.5, 3.75, "Banheiro"),
            };

            var placed = new List<object>();
            var errors = new List<string>();

            using (var tx = new Transaction(doc, "MCP: Criar cômodos"))
            {
                tx.Start();
                foreach (var (x, y, nome) in roomPoints)
                {
                    try
                    {
                        var uv = new UV(MToFt(x), MToFt(y));
                        var room = doc.Create.NewRoom(level, uv);
                        if (room != null)
                        {
                            try { room.Name = nome; } catch { }
                            placed.Add(new
                            {
                                id = room.Id.Value,
                                nome = room.Name,
                                x_m = x,
                                y_m = y
                            });
                        }
                        else
                        {
                            errors.Add($"Cômodo não criado em ({x},{y})");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"({x},{y}) {nome}: {ex.Message}");
                    }
                }
                tx.Commit();
            }
            return new { quantidade = placed.Count, comodos = placed, erros = errors };
        }

        /// <summary>
        /// Coloca mobiliario como simbolos 2D na vista ativa.
        /// Cada item: { name, x, y, w, d, h, rotation } em metros.
        /// O desenho usa apenas linhas de detalhe para ficar visivel em planta
        /// sem depender de familias externas carregadas.
        /// </summary>
        private static object PlaceFurnitureBlocks(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument?.ActiveView ?? throw new Exception("Nenhuma vista ativa.");
            if (!args.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Array 'items' e obrigatorio");

            var placed = new List<object>();
            var errors = new List<string>();

            using (var tx = new Transaction(doc, "MCP: Blocos de mobiliario"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                int index = 0;
                foreach (var item in arr.EnumerateArray())
                {
                    try
                    {
                        string name = GetS(item, "name", "Mobiliario");
                        double x = GetItemD(item, "x", 0);
                        double y = GetItemD(item, "y", 0);
                        double w = Math.Max(GetItemD(item, "w", 1), 0.1);
                        double d = Math.Max(GetItemD(item, "d", 1), 0.1);
                        double rotation = GetItemD(item, "rotation", 0);
                        bool label = GetItemBool(item, "label", false);

                        var ids = CreateFurnitureBlock(doc, view, name, x, y, w, d, rotation, label);
                        placed.Add(new { nome = name, ids });
                    }
                    catch (Exception ex)
                    {
                        string itemName = $"item {index}";
                        if (item.TryGetProperty("name", out var nv))
                            itemName = nv.GetString() ?? itemName;
                        errors.Add($"{itemName}: {ex.Message}");
                    }
                    index++;
                }
                tx.Commit();
            }

            return new { quantidade = placed.Count, blocos = placed, erros = errors };
        }

        /// <summary>
        /// Mantem a assinatura antiga, mas agora desenha blocos 2D em planta.
        /// </summary>
        /// <summary>Insere uma família já carregada por categoria, nome e tipo opcionais.</summary>
        private static object PlaceComponent(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string categoryName = GetS(args, "category") ?? throw new ArgumentException("Argumento 'category' obrigatorio.");
            if (!TryResolveComponentCategory(categoryName, out var category))
                throw new ArgumentException("Categoria nao suportada. Use Furniture, PlumbingFixtures, Casework, Planting, Parking, SpecialityEquipment ou GenericModel.");
            double x = GetDReq(args, "x_m"), y = GetDReq(args, "y_m"), rotation = GetD(args, "rotation", 0);
            string familyName = GetS(args, "family_name"), typeName = GetS(args, "type_name");
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nenhum nivel encontrado.");
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && s.Category.Id.Value == (long)category).ToList();
            if (!string.IsNullOrWhiteSpace(familyName)) symbols = symbols.Where(s => s.Family.Name.IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (!string.IsNullOrWhiteSpace(typeName)) symbols = symbols.Where(s => s.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var symbol = symbols.FirstOrDefault() ?? throw new Exception("Nenhuma familia compativel carregada. Use load_family ou create_furniture_family.");
            FamilyInstance instance;
            using (var tx = new Transaction(doc, "MCP: Inserir componente"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                var point = new XYZ(MToFt(x), MToFt(y), level.Elevation);
                instance = doc.Create.NewFamilyInstance(point, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                if (Math.Abs(rotation) > 1e-6) ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(point, point + XYZ.BasisZ), rotation * Math.PI / 180.0);
                tx.Commit();
            }
            return new { id = instance.Id.Value, categoria = symbol.Category.Name, familia = symbol.Family.Name, tipo = symbol.Name, rotacao_graus = rotation };
        }

        private static bool TryResolveComponentCategory(string category, out BuiltInCategory result)
        {
            switch (Normalizar(category ?? ""))
            {
                case "furniture": case "mobiliario": result = BuiltInCategory.OST_Furniture; return true;
                case "plumbingfixtures": case "plumbing": case "hidraulica": result = BuiltInCategory.OST_PlumbingFixtures; return true;
                case "casework": case "marcenaria": result = BuiltInCategory.OST_Casework; return true;
                case "planting": case "vegetacao": result = BuiltInCategory.OST_Planting; return true;
                case "parking": case "estacionamento": result = BuiltInCategory.OST_Parking; return true;
                case "specialityequipment": case "equipment": case "equipamento": result = BuiltInCategory.OST_SpecialityEquipment; return true;
                case "genericmodel": case "modelogenerico": case "modelo generico": result = BuiltInCategory.OST_GenericModel; return true;
                default: result = BuiltInCategory.INVALID; return false;
            }
        }

        private static object PlaceFurniture(UIApplication uiApp, JsonElement args)
        {
            return PlaceFurnitureBlocks(uiApp, args);
        }

        /// <summary>
        /// Cria Rooms em pontos explícitos, com nome e número opcionais. Evita
        /// a heurística limitada de place_rooms e permite documentar projetos reais.
        /// </summary>
        private static object PlaceRoomsAt(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!args.TryGetProperty("rooms", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Array 'rooms' é obrigatório.");
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nível não encontrado.");
            var placed = new List<object>();
            var errors = new List<string>();
            using (var tx = new Transaction(doc, "MCP: Criar ambientes nomeados"))
            {
                tx.Start();
                foreach (var item in items.EnumerateArray())
                {
                    string name = GetS(item, "name", "Ambiente");
                    try
                    {
                        double x = GetItemD(item, "x_m", double.NaN);
                        double y = GetItemD(item, "y_m", double.NaN);
                        if (double.IsNaN(x) || double.IsNaN(y)) throw new ArgumentException("x_m e y_m são obrigatórios.");
                        var room = doc.Create.NewRoom(level, new UV(MToFt(x), MToFt(y)));
                        if (room == null) throw new Exception("O ponto não pertence a uma região fechada.");
                        room.Name = name;
                        string number = GetS(item, "number");
                        if (!string.IsNullOrEmpty(number)) room.Number = number;
                        placed.Add(new { id = room.Id.Value, nome = room.Name, numero = room.Number, x_m = x, y_m = y });
                    }
                    catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                }
                tx.Commit();
            }
            return new { quantidade = placed.Count, ambientes = placed, erros = errors };
        }

        /// <summary>
        /// Cria um bloco 2D moderno em planta baixa usando apenas linhas de detalhe.
        /// A geometria e rotacionada no proprio desenho, sem depender de familia externa.
        /// </summary>
        private static IReadOnlyList<long> CreateFurnitureBlock(
            Document doc,
            View view,
            string name,
            double xM, double yM, double wM, double dM,
            double rotationDeg,
            bool label)
        {
            double theta = rotationDeg * Math.PI / 180.0;
            XYZ center = new XYZ(MToFt(xM + wM / 2.0), MToFt(yM + dM / 2.0), 0);

            XYZ RotatePt(double px, double py)
            {
                double x = MToFt(px);
                double y = MToFt(py);
                double dx = x - center.X;
                double dy = y - center.Y;
                return new XYZ(
                    center.X + dx * Math.Cos(theta) - dy * Math.Sin(theta),
                    center.Y + dx * Math.Sin(theta) + dy * Math.Cos(theta),
                    0);
            }

            var ids = new List<long>();
            var pts = new[]
            {
                RotatePt(xM, yM),
                RotatePt(xM + wM, yM),
                RotatePt(xM + wM, yM + dM),
                RotatePt(xM, yM + dM),
            };

            void AddLine(XYZ a, XYZ b)
            {
                if (a.IsAlmostEqualTo(b)) return;
                ids.Add(doc.Create.NewDetailCurve(view, Line.CreateBound(a, b)).Id.Value);
            }

            // Outline
            AddLine(pts[0], pts[1]);
            AddLine(pts[1], pts[2]);
            AddLine(pts[2], pts[3]);
            AddLine(pts[3], pts[0]);

            string key = Normalizar(name ?? "");
            double inset = Math.Max(Math.Min(wM, dM) * 0.12, 0.08);

            if (key.Contains("sofa") || key.Contains("sof") || key.Contains("couch"))
            {
                AddLine(RotatePt(xM + inset, yM + dM - inset), RotatePt(xM + wM - inset, yM + dM - inset));
                AddLine(RotatePt(xM + inset, yM + inset), RotatePt(xM + inset, yM + dM - inset));
                AddLine(RotatePt(xM + wM - inset, yM + inset), RotatePt(xM + wM - inset, yM + dM - inset));
                AddLine(RotatePt(xM + wM * 0.33, yM + inset), RotatePt(xM + wM * 0.33, yM + dM - inset));
                AddLine(RotatePt(xM + wM * 0.66, yM + inset), RotatePt(xM + wM * 0.66, yM + dM - inset));
            }
            else if (key.Contains("mesa de jantar") || key.Contains("dining") || key.Contains("table"))
            {
                AddLine(RotatePt(xM + inset, yM + inset), RotatePt(xM + wM - inset, yM + dM - inset));
                AddLine(RotatePt(xM + inset, yM + dM - inset), RotatePt(xM + wM - inset, yM + inset));
                AddLine(RotatePt(xM + wM * 0.5, yM + inset), RotatePt(xM + wM * 0.5, yM + dM - inset));
                AddLine(RotatePt(xM + inset, yM + dM * 0.5), RotatePt(xM + wM - inset, yM + dM * 0.5));
            }
            else if (key.Contains("escritorio") || key.Contains("desk") || key.Contains("office"))
            {
                AddLine(RotatePt(xM + inset, yM + inset), RotatePt(xM + wM - inset, yM + inset));
                AddLine(RotatePt(xM + inset, yM + dM - inset), RotatePt(xM + wM * 0.72, yM + dM - inset));
                AddLine(RotatePt(xM + wM * 0.72, yM + inset), RotatePt(xM + wM * 0.72, yM + dM - inset));
                AddLine(RotatePt(xM + wM * 0.82, yM + inset), RotatePt(xM + wM * 0.82, yM + dM - inset));
            }
            else if (key.Contains("cama") || key.Contains("bed"))
            {
                AddLine(RotatePt(xM + inset, yM + dM * 0.22), RotatePt(xM + wM - inset, yM + dM * 0.22));
                AddLine(RotatePt(xM + inset, yM + dM * 0.78), RotatePt(xM + wM - inset, yM + dM * 0.78));
                AddLine(RotatePt(xM + inset, yM + dM * 0.22), RotatePt(xM + inset, yM + dM * 0.78));
                AddLine(RotatePt(xM + wM - inset, yM + dM * 0.22), RotatePt(xM + wM - inset, yM + dM * 0.78));
            }
            else
            {
                AddLine(RotatePt(xM + inset, yM + inset), RotatePt(xM + wM - inset, yM + dM - inset));
            }

            if (label)
            {
                try
                {
                    string txt = name ?? "Mobiliario";
                    var textType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId();
                    var pos = new XYZ(center.X, center.Y, 0);
                    ids.Add(TextNote.Create(doc, view.Id, pos, txt, textType).Id.Value);
                }
                catch
                {
                }
            }

            return ids;
        }

        // =====================================================================
        // PISO, TELHADO e FORRO
        // =====================================================================

        /// <summary>
        /// Cria um piso (Floor) a partir de um contorno retangular ou polígono.
        /// Aceita "points" (lista de {x,y} em metros) ou "x_min,y_min,x_max,y_max"
        /// para retângulo simples. O piso é criado no nível especificado.
        /// </summary>
        private static object CreateFloor(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string levelName = GetS(args, "level");
            var level = FindLevel(doc, levelName)
                ?? throw new Exception("Nenhum nível encontrado.");

            // Todos os tipos de piso que não sejam laje de fundação
            var floorTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType)).Cast<FloorType>()
                .Where(ft => !ft.IsFoundationSlab)
                .ToList();
            if (floorTypes.Count == 0)
                throw new Exception("Nenhum tipo de piso disponível no template.");

            // Seleção por nome parcial (acento-insensível), senão o primeiro
            FloorType floorType = floorTypes.First();
            string floorTypeName = GetS(args, "floor_type");
            if (!string.IsNullOrEmpty(floorTypeName))
            {
                var alvo = Normalizar(floorTypeName);
                floorType = floorTypes.FirstOrDefault(ft => Normalizar(ft.Name) == alvo)
                    ?? floorTypes.FirstOrDefault(ft => Normalizar(ft.Name).Contains(alvo))
                    ?? throw new Exception(
                        $"Tipo de piso '{floorTypeName}' não encontrado. Use list_floor_types.");
            }

            // Monta o contorno (CurveLoop)
            CurveLoop loop;
            if (args.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Array)
            {
                // Polígono livre: [ {x:0, y:0}, {x:5, y:0}, {x:5, y:4}, {x:0, y:4} ]
                var points = new List<XYZ>();
                foreach (var p in pts.EnumerateArray())
                {
                    double px = p.GetProperty("x").GetDouble();
                    double py = p.GetProperty("y").GetDouble();
                    points.Add(new XYZ(MToFt(px), MToFt(py), 0));
                }
                if (points.Count < 3)
                    throw new Exception("Mínimo 3 pontos para criar um piso.");
                // Fecha o contorno
                var curves = new List<Curve>();
                for (int i = 0; i < points.Count; i++)
                    curves.Add(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
                loop = CurveLoop.Create(curves);
            }
            else
            {
                // Retângulo: x_min, y_min, x_max, y_max
                double x0 = GetD(args, "x_min_m", 0);
                double y0 = GetD(args, "y_min_m", 0);
                double x1 = GetD(args, "x_max_m", 8);
                double y1 = GetD(args, "y_max_m", 5);
                var curves = new List<Curve>
                {
                    Line.CreateBound(new XYZ(MToFt(x0), MToFt(y0), 0), new XYZ(MToFt(x1), MToFt(y0), 0)),
                    Line.CreateBound(new XYZ(MToFt(x1), MToFt(y0), 0), new XYZ(MToFt(x1), MToFt(y1), 0)),
                    Line.CreateBound(new XYZ(MToFt(x1), MToFt(y1), 0), new XYZ(MToFt(x0), MToFt(y1), 0)),
                    Line.CreateBound(new XYZ(MToFt(x0), MToFt(y1), 0), new XYZ(MToFt(x0), MToFt(y0), 0)),
                };
                loop = CurveLoop.Create(curves);
            }

            // Material de acabamento opcional (pinta a face do piso)
            string materialName = GetS(args, "material");
            Material material = null;
            if (!string.IsNullOrEmpty(materialName))
            {
                var mats = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material)).Cast<Material>().ToList();
                var alvo = Normalizar(materialName);
                material = mats.FirstOrDefault(m => Normalizar(m.Name) == alvo)
                    ?? mats.FirstOrDefault(m => Normalizar(m.Name).Contains(alvo))
                    ?? throw new Exception(
                        $"Material '{materialName}' não encontrado. Use list_materials.");
            }

            object result;
            using (var tx = new Transaction(doc, "MCP: Criar piso"))
            {
                tx.Start();
                var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorType.Id, level.Id);

                // Pinta o acabamento, se um material foi informado
                int facesPintadas = 0;
                if (material != null)
                {
                    doc.Regenerate(); // garante geometria disponível para pintar
                    var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                    var geom = floor.get_Geometry(opt);
                    if (geom != null)
                        foreach (var gObj in geom)
                            facesPintadas += PaintFaces(doc, floor.Id, gObj, material.Id);
                }

                result = new
                {
                    id = floor.Id.Value,
                    tipo = floorType.Name,
                    nivel = level.Name,
                    material = material?.Name,
                    faces_pintadas = facesPintadas
                };
                tx.Commit();
            }
            return result;
        }

        /// <summary>
        /// Cria um telhado (FootPrintRoof) com inclinação sobre o contorno da casa.
        /// O FootPrintRoof é definido pelo contorno em planta + inclinação nas bordas.
        /// Cada borda recebe a mesma inclinação, formando um telhado de quatro águas.
        /// Para duas águas, as bordas norte e sul têm inclinação e leste/oeste são empenas.
        /// </summary>
        private static object CreateRoof(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string levelName = GetS(args, "level");
            var level = FindLevel(doc, levelName)
                ?? throw new Exception("Nenhum nível encontrado.");

            double x0 = GetD(args, "x_min_m", 0);
            double y0 = GetD(args, "y_min_m", 0);
            double x1 = GetD(args, "x_max_m", 8);
            double y1 = GetD(args, "y_max_m", 5);
            double slopeDeg = GetD(args, "slope", 30);
            double overhang = GetD(args, "overhang_m", 0.5);
            double baseHeight = GetD(args, "base_height_m", 3.0);

            // Encontra um RoofType opaco (evita vidraça/glass)
            var allRoofTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RoofType))
                .Cast<RoofType>()
                .ToList();

            if (allRoofTypes.Count == 0)
                throw new Exception("Nenhum tipo de telhado disponível no template.");

            // Palavras que indicam telhado de vidro — evitar
            var glassKeywords = new[] { "vidraça", "vidro", "glass", "curtain", "skylight" };

            // Prefere tipos opacos: exclui vidraça/glass, depois tenta nomes genéricos
            var roofType = allRoofTypes
                .Where(rt => !glassKeywords.Any(kw =>
                    rt.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0))
                .FirstOrDefault()
                ?? allRoofTypes.First();

            // Se o usuário passou 'level' explicitamente, usa-o direto como nível do telhado.
            // Se não, calcula base+altura e procura (ou cria) um nível apropriado.
            Level roofLevel;
            double targetElev;
            if (!string.IsNullOrEmpty(levelName))
            {
                roofLevel = level;
                targetElev = FtToM(level.Elevation);
            }
            else
            {
                targetElev = FtToM(level.Elevation) + baseHeight;
                roofLevel = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .Where(l => Math.Abs(FtToM(l.Elevation) - targetElev) < 0.3)
                    .FirstOrDefault();
            }

            // Contorno do telhado com beiral (overhang)
            double ox0 = MToFt(x0 - overhang);
            double oy0 = MToFt(y0 - overhang);
            double ox1 = MToFt(x1 + overhang);
            double oy1 = MToFt(y1 + overhang);

            var footprint = new CurveArray();
            // Sul (com inclinação)
            footprint.Append(Line.CreateBound(new XYZ(ox0, oy0, 0), new XYZ(ox1, oy0, 0)));
            // Leste (empena — sem inclinação para duas-águas)
            footprint.Append(Line.CreateBound(new XYZ(ox1, oy0, 0), new XYZ(ox1, oy1, 0)));
            // Norte (com inclinação)
            footprint.Append(Line.CreateBound(new XYZ(ox1, oy1, 0), new XYZ(ox0, oy1, 0)));
            // Oeste (empena)
            footprint.Append(Line.CreateBound(new XYZ(ox0, oy1, 0), new XYZ(ox0, oy0, 0)));

            object result;
            using (var tx = new Transaction(doc, "MCP: Criar telhado"))
            {
                tx.Start();

                // Cria nível do telhado se não existir
                if (roofLevel == null)
                {
                    roofLevel = Level.Create(doc, MToFt(targetElev));
                    try { roofLevel.Name = "Telhado"; } catch { }
                }

                var modelCurves = new ModelCurveArray();
                var roof = doc.Create.NewFootPrintRoof(
                    footprint, roofLevel, roofType, out modelCurves);

                // SlopeAngle do Revit não é radianos nem graus: é a razão rise/run
                // (a tangente do ângulo). Ex.: 30° → tan(30°) ≈ 0.577, não 0.5236 rad.
                // https://blog.autodesk.io/slope-is-slope-not-radians/
                double slopeRatio = Math.Tan(slopeDeg * Math.PI / 180.0);
                foreach (ModelCurve mc in modelCurves)
                {
                    roof.set_DefinesSlope(mc, true);
                    roof.set_SlopeAngle(mc, slopeRatio);
                }

                // Ajusta offset da base do telhado
                var baseOffsetParam = roof.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM);
                if (baseOffsetParam != null)
                    baseOffsetParam.Set(roofLevel.Id);

                result = new
                {
                    id = roof.Id.Value,
                    tipo = roofType.Name,
                    inclinacao = slopeDeg,
                    beiral_m = overhang,
                    nivel_base = roofLevel.Name
                };
                tx.Commit();
            }
            return result;
        }

        /// <summary>
        /// Cria um forro (Ceiling) no nível especificado.
        /// Contorno retangular definido por x_min_m, y_min_m, x_max_m, y_max_m.
        /// height_offset_m controla a altura do forro acima do nível (padrão: 2.7m).
        /// </summary>
        private static object CreateCeiling(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string levelName = GetS(args, "level");
            var level = FindLevel(doc, levelName)
                ?? throw new Exception("Nenhum nível encontrado.");

            double x0 = GetD(args, "x_min_m", 0);
            double y0 = GetD(args, "y_min_m", 0);
            double x1 = GetD(args, "x_max_m", 8);
            double y1 = GetD(args, "y_max_m", 5);
            double heightOffset = GetD(args, "height_offset_m", 2.7);

            // Encontra um CeilingType
            var ceilingType = new FilteredElementCollector(doc)
                .OfClass(typeof(CeilingType))
                .Cast<CeilingType>()
                .FirstOrDefault()
                ?? throw new Exception("Nenhum tipo de forro disponível no template.");

            var curves = new List<Curve>
            {
                Line.CreateBound(new XYZ(MToFt(x0), MToFt(y0), 0), new XYZ(MToFt(x1), MToFt(y0), 0)),
                Line.CreateBound(new XYZ(MToFt(x1), MToFt(y0), 0), new XYZ(MToFt(x1), MToFt(y1), 0)),
                Line.CreateBound(new XYZ(MToFt(x1), MToFt(y1), 0), new XYZ(MToFt(x0), MToFt(y1), 0)),
                Line.CreateBound(new XYZ(MToFt(x0), MToFt(y1), 0), new XYZ(MToFt(x0), MToFt(y0), 0)),
            };
            var loop = CurveLoop.Create(curves);

            object result;
            using (var tx = new Transaction(doc, "MCP: Criar forro"))
            {
                tx.Start();
                var ceiling = Ceiling.Create(doc, new List<CurveLoop> { loop }, ceilingType.Id, level.Id);
                // Ajusta a altura do forro
                var offsetParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                offsetParam?.Set(MToFt(heightOffset));
                result = new
                {
                    id = ceiling.Id.Value,
                    tipo = ceilingType.Name,
                    nivel = level.Name,
                    altura_m = heightOffset
                };
                tx.Commit();
            }
            return result;
        }

        // =====================================================================
        // ESCADAS — lance reto entre dois níveis (StairsEditScope)
        // =====================================================================

        /// <summary>
        /// Cria uma escada de lance reto entre dois níveis.
        /// Usa StairsEditScope, um modo de edição especial do Revit exigido
        /// para criar/editar escadas (não é uma Transaction comum).
        ///
        /// Parâmetros (metros e graus):
        ///   x_m, y_m    — ponto inicial do lance (base da escada)
        ///   width_m     — largura da escada (padrão 1.0)
        ///   direction   — direção da subida no plano XY, em graus (0 = eixo +X)
        ///   base_level  — nome do nível inferior (opcional, usa térreo)
        ///   top_level   — nome do nível superior (opcional, usa o próximo acima)
        ///
        /// O número de degraus é calculado automaticamente a partir da altura
        /// entre os níveis e da altura máxima de espelho do tipo de escada.
        /// </summary>
        private static object CreateStairs(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var uidoc = uiApp.ActiveUIDocument;

            double x = GetD(args, "x_m", 0);
            double y = GetD(args, "y_m", 0);
            double width = GetD(args, "width_m", 1.0);
            double dirDeg = GetD(args, "direction", 0);

            var baseLevel = FindLevel(doc, GetS(args, "base_level"))
                ?? throw new Exception("Nível base não encontrado.");

            // Nível superior: informado, ou o próximo nível acima do base
            string topName = GetS(args, "top_level");
            Level topLevel = !string.IsNullOrEmpty(topName)
                ? FindLevel(doc, topName)
                    ?? throw new Exception($"Nível superior '{topName}' não encontrado.")
                : new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .Where(l => l.Elevation > baseLevel.Elevation + 1e-6)
                    .OrderBy(l => l.Elevation)
                    .FirstOrDefault()
                    ?? throw new Exception(
                        "Nenhum nível acima do base. Crie um nível superior antes (create_level).");

            var stairsType = new FilteredElementCollector(doc)
                .OfClass(typeof(StairsType)).Cast<StairsType>()
                .FirstOrDefault()
                ?? throw new Exception("Nenhum tipo de escada disponível no template.");

            // --- Calcula geometria do lance a partir do tipo de escada ---
            double alturaTotal = topLevel.Elevation - baseLevel.Elevation;      // pés
            double maxEspelho = stairsType.MaxRiserHeight;                       // pés
            if (maxEspelho <= 0) maxEspelho = MToFt(0.18);
            int nEspelhos = (int)Math.Ceiling(alturaTotal / maxEspelho);
            if (nEspelhos < 2) nEspelhos = 2;

            double minPiso = stairsType.MinTreadDepth;                          // pés
            if (minPiso <= 0) minPiso = MToFt(0.28);
            // Comprimento generoso: o Revit para no nível de topo automaticamente
            double comprimento = nEspelhos * minPiso;

            // StairsEditScope exige uma vista de planta ou 3D ativa.
            // Ativa a planta do nível base para maior estabilidade.
            var planta = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.GenLevel != null
                    && v.GenLevel.Id == baseLevel.Id);
            if (planta != null) uidoc.ActiveView = planta;

            // Linha do lance na direção informada
            double rad = dirDeg * Math.PI / 180.0;
            var inicio = new XYZ(MToFt(x), MToFt(y), baseLevel.Elevation);
            var direcao = new XYZ(Math.Cos(rad), Math.Sin(rad), 0);
            var fim = inicio + direcao.Multiply(comprimento);
            var linhaLance = Line.CreateBound(inicio, fim);

            // --- Cria a escada dentro do StairsEditScope ---
            var scope = new StairsEditScope(doc, "MCP: Criar escada");
            ElementId stairsId = scope.Start(baseLevel.Id, topLevel.Id);

            using (var tx = new Transaction(doc, "MCP: Adicionar lance"))
            {
                tx.Start();
                var run = StairsRun.CreateStraightRun(
                    doc, stairsId, linhaLance, StairsRunJustification.Center);
                run.ActualRunWidth = MToFt(width);
                tx.Commit();
            }

            scope.Commit(new StairsFailurePreprocessor());

            var stairs = doc.GetElement(stairsId) as Stairs;
            return new
            {
                id = stairsId.Value,
                tipo = stairsType.Name,
                degraus = stairs?.ActualRisersNumber ?? nEspelhos,
                nivel_base = baseLevel.Name,
                nivel_topo = topLevel.Name,
                largura_m = width
            };
        }

        // =====================================================================
        // COLUNAS / PILARES — estruturais ou arquitetônicos
        // =====================================================================

        /// <summary>
        /// Cria colunas (pilares) verticais, uma ou várias de uma vez.
        /// A coluna é vinculada a um nível de base e sobe até o nível de topo
        /// (ou até a altura informada).
        ///
        /// Parâmetros (metros):
        ///   x_m, y_m     — posição de uma coluna, OU
        ///   points       — lista de posições [x, y] ou { x_m, y_m } para várias
        ///   base_level   — nível da base (opcional, usa térreo)
        ///   top_level    — nível do topo (opcional; se omitido usa height_m)
        ///   height_m     — altura quando não há top_level (padrão 3.0)
        ///   family, type — família e tipo (opcionais, busca parcial)
        ///   structural   — true = pilar estrutural, false = coluna
        ///                  arquitetônica (padrão: true, com fallback automático)
        /// </summary>
        private static object CreateColumn(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);

            var baseLevel = FindLevel(doc, GetS(args, "base_level"))
                ?? throw new Exception("Nível base não encontrado.");

            string topName = GetS(args, "top_level");
            Level topLevel = null;
            if (!string.IsNullOrEmpty(topName))
                topLevel = FindLevel(doc, topName)
                    ?? throw new Exception($"Nível de topo '{topName}' não encontrado.");

            double height = GetD(args, "height_m", 3.0);

            bool structural = true;
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("structural", out var sv) &&
                (sv.ValueKind == JsonValueKind.True || sv.ValueKind == JsonValueKind.False))
                structural = sv.GetBoolean();

            // Posições: points (lista) ou x_m/y_m (única)
            var posicoes = new List<XYZ>();
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("points", out var pts) &&
                pts.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pts.EnumerateArray())
                {
                    double px, py;
                    if (p.ValueKind == JsonValueKind.Array)
                    {
                        var a = p.EnumerateArray().ToList();
                        px = a[0].GetDouble(); py = a[1].GetDouble();
                    }
                    else
                    {
                        px = p.GetProperty("x_m").GetDouble();
                        py = p.GetProperty("y_m").GetDouble();
                    }
                    posicoes.Add(new XYZ(MToFt(px), MToFt(py), baseLevel.Elevation));
                }
            }
            else
            {
                posicoes.Add(new XYZ(
                    MToFt(GetDReq(args, "x_m")),
                    MToFt(GetDReq(args, "y_m")),
                    baseLevel.Elevation));
            }
            if (posicoes.Count == 0)
                throw new Exception("Informe x_m/y_m ou uma lista 'points'.");

            // Busca o símbolo: pilar estrutural primeiro, com fallback para
            // coluna arquitetônica (e vice-versa) — nem todo template tem os dois.
            var catPreferida = structural
                ? BuiltInCategory.OST_StructuralColumns
                : BuiltInCategory.OST_Columns;
            var catAlternativa = structural
                ? BuiltInCategory.OST_Columns
                : BuiltInCategory.OST_StructuralColumns;

            string familyName = GetS(args, "family");
            string typeName = GetS(args, "type");

            var sym = BuscarSimboloColuna(doc, catPreferida, familyName, typeName)
                ?? BuscarSimboloColuna(doc, catAlternativa, familyName, typeName)
                ?? throw new Exception(
                    "Nenhuma família de coluna/pilar carregada. " +
                    "Verifique com list_families ou carregue uma com load_family.");

            bool ehEstrutural = sym.Category != null &&
                sym.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;

            var criadas = new List<object>();
            using (var tx = new Transaction(doc, "MCP: Criar colunas"))
            {
                tx.Start();
                if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }

                // O tipo estrutural PRECISA casar com a categoria da família.
                // Passar StructuralType.Column para uma coluna arquitetônica
                // deixa o elemento inconsistente e pode derrubar o Revit.
                var tipoEstrutural = ehEstrutural
                    ? Autodesk.Revit.DB.Structure.StructuralType.Column
                    : Autodesk.Revit.DB.Structure.StructuralType.NonStructural;

                foreach (var pt in posicoes)
                {
                    var inst = doc.Create.NewFamilyInstance(
                        pt, sym, baseLevel, tipoEstrutural);

                    // Base e topo: vincula a níveis quando possível.
                    // Parâmetros ausentes ou somente-leitura são ignorados —
                    // as famílias variam no que expõem.
                    DefinirParametro(inst, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, baseLevel.Id);

                    if (topLevel != null)
                    {
                        DefinirParametro(inst, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLevel.Id);
                        DefinirParametro(inst, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, 0.0);
                    }
                    else
                    {
                        // Sem nível de topo: mesmo nível da base + deslocamento
                        DefinirParametro(inst, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, baseLevel.Id);
                        DefinirParametro(inst, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, MToFt(height));
                    }

                    criadas.Add(new
                    {
                        id = inst.Id.Value,
                        x_m = Math.Round(FtToM(pt.X), 3),
                        y_m = Math.Round(FtToM(pt.Y), 3)
                    });
                }
                tx.Commit();
            }

            return new
            {
                quantidade = criadas.Count,
                colunas = criadas,
                familia = sym.Family.Name,
                tipo = sym.Name,
                estrutural = ehEstrutural,
                nivel_base = baseLevel.Name,
                nivel_topo = topLevel?.Name,
                altura_m = topLevel == null ? (double?)height : null
            };
        }

        /// <summary>
        /// Define um parâmetro apenas se ele existir e for editável.
        /// Famílias diferentes expõem conjuntos diferentes de parâmetros;
        /// escrever num parâmetro somente-leitura lança exceção.
        /// </summary>
        private static void DefinirParametro(Element e, BuiltInParameter bip, ElementId valor)
        {
            var p = e.get_Parameter(bip);
            if (p != null && !p.IsReadOnly)
            {
                try { p.Set(valor); } catch { }
            }
        }

        private static void DefinirParametro(Element e, BuiltInParameter bip, double valor)
        {
            var p = e.get_Parameter(bip);
            if (p != null && !p.IsReadOnly)
            {
                try { p.Set(valor); } catch { }
            }
        }

        /// <summary>
        /// Busca um FamilySymbol de coluna numa categoria, filtrando por nome
        /// de família e tipo (parciais, acento-insensíveis). Null se não achar.
        /// </summary>
        private static FamilySymbol BuscarSimboloColuna(
            Document doc, BuiltInCategory cat, string familia, string tipo)
        {
            var todos = new FilteredElementCollector(doc)
                .OfCategory(cat)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();
            if (todos.Count == 0) return null;

            var candidatos = todos;
            if (!string.IsNullOrEmpty(familia))
            {
                var alvo = Normalizar(familia);
                var filtrados = todos.Where(s => Normalizar(s.Family.Name).Contains(alvo)).ToList();
                if (filtrados.Count == 0) return null;   // família pedida não existe aqui
                candidatos = filtrados;
            }

            if (!string.IsNullOrEmpty(tipo))
            {
                var alvo = Normalizar(tipo);
                var porTipo = candidatos.FirstOrDefault(s => Normalizar(s.Name).Contains(alvo));
                if (porTipo != null) return porTipo;
            }

            return candidatos.First();
        }

        // =====================================================================
        // COTAGEM e ANOTAÇÕES — documentação da planta
        // =====================================================================

        /// <summary>
        /// Cria cotas (Dimension) do comprimento de paredes, em uma vista de planta.
        /// A cota é desenhada paralela à parede, deslocada perpendicularmente.
        ///
        /// Parâmetros:
        ///   wall_id   — ID de uma parede, OU
        ///   wall_ids  — lista de IDs (uma cota por parede)
        ///   offset_m  — distância da cota até a parede (padrão 1.0m; pode ser
        ///               negativo para desenhar do outro lado)
        ///   view      — nome da vista (opcional; usa a planta do nível da parede)
        ///
        /// Observação técnica: as referências vêm da linha de eixo da parede,
        /// exposta pela geometria com IncludeNonVisibleObjects.
        /// </summary>
        private static object CreateDimension(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var uidoc = uiApp.ActiveUIDocument;
            double offset = GetD(args, "offset_m", 1.0);

            // Aceita wall_id (único) ou wall_ids (lista)
            var wallIds = new List<long>();
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("wall_ids", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in arr.EnumerateArray())
                    wallIds.Add((long)v.GetDouble());
            }
            else
            {
                wallIds.Add((long)GetDReq(args, "wall_id"));
            }
            if (wallIds.Count == 0)
                throw new ArgumentException("Informe wall_id ou wall_ids (lista).");

            // Vista: a informada, ou a planta do nível da primeira parede
            var primeira = doc.GetElement(new ElementId(wallIds[0])) as Wall
                ?? throw new Exception($"Parede com ID {wallIds[0]} não encontrada.");

            string viewName = GetS(args, "view");
            View vista;
            if (!string.IsNullOrEmpty(viewName))
            {
                var alvo = Normalizar(viewName);
                vista = new FilteredElementCollector(doc)
                    .OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && Normalizar(v.Name).Contains(alvo))
                    ?? throw new Exception($"Vista '{viewName}' não encontrada.");
            }
            else
            {
                vista = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && v.GenLevel != null
                        && v.GenLevel.Id == primeira.LevelId)
                    ?? throw new Exception(
                        "Nenhuma planta encontrada para o nível da parede. Informe 'view'.");
            }

            // Cotas só aparecem na vista onde foram criadas: ativa-a
            if (uidoc.ActiveView.Id != vista.Id)
            {
                try { uidoc.ActiveView = vista; } catch { }
            }

            var criadas = new List<object>();
            var erros = new List<string>();

            using (var tx = new Transaction(doc, "MCP: Criar cotas"))
            {
                tx.Start();
                foreach (var wid in wallIds)
                {
                    try
                    {
                        var parede = doc.GetElement(new ElementId(wid)) as Wall
                            ?? throw new Exception($"Parede {wid} não encontrada.");

                        var lc = parede.Location as LocationCurve
                            ?? throw new Exception("Parede sem linha de locação.");
                        var p1 = lc.Curve.GetEndPoint(0);
                        var p2 = lc.Curve.GetEndPoint(1);
                        var dir = (p2 - p1).Normalize();

                        var refs2 = ObterReferenciasParede(parede, dir, out string diag);
                        if (refs2 == null)
                            throw new Exception(
                                "Não foi possível obter referências para cotar. " + diag);
                        Reference r1 = refs2.Item1, r2 = refs2.Item2;
                        var perp = dir.CrossProduct(XYZ.BasisZ).Normalize();
                        var desloc = perp.Multiply(MToFt(offset));
                        var linhaCota = Line.CreateBound(p1 + desloc, p2 + desloc);

                        var refs = new ReferenceArray();
                        refs.Append(r1);
                        refs.Append(r2);

                        var cota = doc.Create.NewDimension(vista, linhaCota, refs);
                        criadas.Add(new
                        {
                            id = cota.Id.Value,
                            parede_id = wid,
                            valor_m = Math.Round(FtToM(lc.Curve.Length), 3)
                        });
                    }
                    catch (Exception ex)
                    {
                        erros.Add($"Parede {wid}: {ex.Message}");
                    }
                }
                tx.Commit();
            }

            if (criadas.Count == 0)
                throw new Exception("Nenhuma cota criada. " + string.Join(" | ", erros));

            return new
            {
                quantidade = criadas.Count,
                cotas = criadas,
                vista = vista.Name,
                erros = erros.Count > 0 ? erros : null
            };
        }

        /// <summary>
        /// Obtém duas Reference nos extremos de uma parede, para cotar seu
        /// comprimento. Tenta duas estratégias, pois nem todo tipo de parede
        /// expõe a linha de eixo como geometria:
        ///   1. Extremos da linha de eixo (geometria não visível)
        ///   2. Faces planas de extremidade (normal paralela à direção da parede)
        /// Devolve null e preenche 'diag' quando nenhuma funciona.
        /// </summary>
        private static Tuple<Reference, Reference> ObterReferenciasParede(
            Wall parede, XYZ direcao, out string diag)
        {
            diag = "";
            var tipos = new List<string>();

            var opt = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Fine
            };
            var geom = parede.get_Geometry(opt);
            if (geom == null)
            {
                diag = "Elemento sem geometria.";
                return null;
            }

            // --- Estratégia 1: linha de eixo ---
            foreach (var gObj in geom)
            {
                tipos.Add(gObj.GetType().Name);
                if (gObj is Line eixo)
                {
                    var a = eixo.GetEndPointReference(0);
                    var b = eixo.GetEndPointReference(1);
                    if (a != null && b != null)
                        return Tuple.Create(a, b);
                }
            }

            // --- Estratégia 2: faces de extremidade do sólido ---
            // As faces de extremidade têm normal paralela à direção da parede.
            foreach (var gObj in geom)
            {
                if (!(gObj is Solid solido) || solido.Faces.Size == 0) continue;

                var extremidades = new List<Tuple<double, Reference>>();
                foreach (Face face in solido.Faces)
                {
                    if (!(face is PlanarFace pf) || pf.Reference == null) continue;

                    // |normal · direção| ≈ 1 → face perpendicular ao eixo da parede
                    if (Math.Abs(pf.FaceNormal.DotProduct(direcao)) > 0.99)
                    {
                        double posicao = pf.Origin.DotProduct(direcao);
                        extremidades.Add(Tuple.Create(posicao, pf.Reference));
                    }
                }

                if (extremidades.Count >= 2)
                {
                    var ordenadas = extremidades.OrderBy(t => t.Item1).ToList();
                    return Tuple.Create(ordenadas.First().Item2, ordenadas.Last().Item2);
                }
            }

            diag = $"Geometria encontrada: [{string.Join(", ", tipos.Distinct())}]; " +
                   "sem linha de eixo referenciável nem faces de extremidade.";
            return null;
        }

        /// <summary>
        /// Insere identificadores (tags) em todos os cômodos de uma vista,
        /// mostrando nome e área. Requer cômodos criados antes (place_rooms).
        ///
        /// Parâmetros:
        ///   view — nome da vista (opcional; usa a planta ativa ou a primeira)
        /// </summary>
        private static object TagRooms(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var uidoc = uiApp.ActiveUIDocument;

            var comodos = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<SpatialElement>()
                .Where(r => r.Area > 0)
                .ToList();

            if (comodos.Count == 0)
                throw new Exception(
                    "Nenhum cômodo encontrado. Crie os cômodos antes com place_rooms.");

            // Vista de planta onde os identificadores serão inseridos
            string viewName = GetS(args, "view");
            ViewPlan vista;
            if (!string.IsNullOrEmpty(viewName))
            {
                var alvo = Normalizar(viewName);
                vista = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && Normalizar(v.Name).Contains(alvo))
                    ?? throw new Exception($"Planta '{viewName}' não encontrada.");
            }
            else
            {
                vista = uidoc.ActiveView as ViewPlan
                    ?? new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                        .FirstOrDefault(v => !v.IsTemplate && v.GenLevel != null)
                    ?? throw new Exception("Nenhuma planta disponível. Informe 'view'.");
            }

            if (uidoc.ActiveView.Id != vista.Id)
            {
                try { uidoc.ActiveView = vista; } catch { }
            }

            var criadas = new List<object>();
            var erros = new List<string>();

            using (var tx = new Transaction(doc, "MCP: Identificar cômodos"))
            {
                tx.Start();
                foreach (var comodo in comodos)
                {
                    try
                    {
                        if (!(comodo.Location is LocationPoint lp))
                        {
                            erros.Add($"Cômodo {comodo.Id.Value}: sem ponto de locação.");
                            continue;
                        }

                        var uv = new UV(lp.Point.X, lp.Point.Y);
                        var tag = doc.Create.NewRoomTag(
                            new LinkElementId(comodo.Id), uv, vista.Id);

                        criadas.Add(new
                        {
                            id = tag.Id.Value,
                            comodo = comodo.Name,
                            area_m2 = Math.Round(SqFtToM2(comodo.Area), 2)
                        });
                    }
                    catch (Exception ex)
                    {
                        erros.Add($"Cômodo {comodo.Id.Value}: {ex.Message}");
                    }
                }
                tx.Commit();
            }

            if (criadas.Count == 0)
                throw new Exception(
                    "Nenhum identificador criado. " + string.Join(" | ", erros));

            return new
            {
                quantidade = criadas.Count,
                identificadores = criadas,
                vista = vista.Name,
                erros = erros.Count > 0 ? erros : null
            };
        }

        // =====================================================================
        // PAVIMENTOS — cria um andar completo (nível + laje + paredes)
        // =====================================================================

        /// <summary>
        /// Cria um pavimento completo em uma única operação: nível, laje de piso
        /// e as 4 paredes perimetrais. Atalho para edifícios de vários andares —
        /// evita orquestrar create_level + create_floor + create_walls à mão.
        ///
        /// Parâmetros (metros):
        ///   elevation_m   — elevação do novo nível (obrigatório)
        ///   name          — nome do nível (opcional; o Revit gera se omitido)
        ///   x_min_m, y_min_m, x_max_m, y_max_m — contorno em planta
        ///   wall_height_m — altura das paredes (padrão 3.0)
        ///   wall_type     — tipo de parede (opcional)
        ///   with_floor    — criar a laje de piso (padrão true)
        ///   floor_type    — tipo de piso (opcional)
        ///   floor_material— acabamento do piso (opcional)
        /// </summary>
        private static object CreateStorey(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            double elev = GetDReq(args, "elevation_m");
            string name = GetS(args, "name");

            double x0 = GetD(args, "x_min_m", 0);
            double y0 = GetD(args, "y_min_m", 0);
            double x1 = GetD(args, "x_max_m", 8);
            double y1 = GetD(args, "y_max_m", 5);
            double wallH = GetD(args, "wall_height_m", 3.0);

            bool withFloor = true;
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("with_floor", out var wf) &&
                (wf.ValueKind == JsonValueKind.True || wf.ValueKind == JsonValueKind.False))
                withFloor = wf.GetBoolean();

            if (Math.Abs(x1 - x0) < 1e-6 || Math.Abs(y1 - y0) < 1e-6)
                throw new Exception("O contorno do pavimento tem largura ou profundidade zero.");

            // Resolve tipos ANTES da transação, para falhar cedo com erro claro
            WallType wt = null;
            string wallTypeName = GetS(args, "wall_type");
            if (!string.IsNullOrEmpty(wallTypeName))
                wt = FindWallType(doc, wallTypeName)
                    ?? throw new Exception(
                        $"Tipo de parede '{wallTypeName}' não encontrado. Use list_wall_types.");

            FloorType ft = null;
            string floorTypeName = GetS(args, "floor_type");
            if (withFloor)
            {
                var floorTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType)).Cast<FloorType>()
                    .Where(f => !f.IsFoundationSlab)
                    .ToList();
                if (floorTypes.Count == 0)
                    throw new Exception("Nenhum tipo de piso disponível no template.");

                if (!string.IsNullOrEmpty(floorTypeName))
                {
                    var alvo = Normalizar(floorTypeName);
                    ft = floorTypes.FirstOrDefault(f => Normalizar(f.Name) == alvo)
                        ?? floorTypes.FirstOrDefault(f => Normalizar(f.Name).Contains(alvo))
                        ?? throw new Exception(
                            $"Tipo de piso '{floorTypeName}' não encontrado. Use list_floor_types.");
                }
                else ft = floorTypes.First();
            }

            Material floorMat = null;
            string matName = GetS(args, "floor_material");
            if (withFloor && !string.IsNullOrEmpty(matName))
            {
                var mats = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material)).Cast<Material>().ToList();
                var alvo = Normalizar(matName);
                floorMat = mats.FirstOrDefault(m => Normalizar(m.Name) == alvo)
                    ?? mats.FirstOrDefault(m => Normalizar(m.Name).Contains(alvo))
                    ?? throw new Exception(
                        $"Material '{matName}' não encontrado. Use list_materials.");
            }

            // As 4 arestas do perímetro, em sentido anti-horário
            var arestas = new (double ax, double ay, double bx, double by)[]
            {
                (x0, y0, x1, y0),
                (x1, y0, x1, y1),
                (x1, y1, x0, y1),
                (x0, y1, x0, y0),
            };

            object result;
            using (var tx = new Transaction(doc, "MCP: Criar pavimento"))
            {
                tx.Start();

                // 1) Nível — se um nome foi passado e já existe, reaproveita em vez de criar novo.
                Level level = null;
                if (!string.IsNullOrEmpty(name)) level = FindLevel(doc, name);
                if (level == null)
                {
                    level = Level.Create(doc, MToFt(elev));
                    if (!string.IsNullOrEmpty(name))
                        try { level.Name = name; } catch { /* nome duplicado */ }
                }

                // 2) Laje de piso
                long? floorId = null;
                int facesPintadas = 0;
                if (withFloor)
                {
                    var curvasPiso = new List<Curve>();
                    foreach (var (ax, ay, bx, by) in arestas)
                        curvasPiso.Add(Line.CreateBound(
                            new XYZ(MToFt(ax), MToFt(ay), 0),
                            new XYZ(MToFt(bx), MToFt(by), 0)));

                    var piso = Floor.Create(doc,
                        new List<CurveLoop> { CurveLoop.Create(curvasPiso) },
                        ft.Id, level.Id);
                    floorId = piso.Id.Value;

                    if (floorMat != null)
                    {
                        doc.Regenerate();
                        var opt = new Options
                        {
                            ComputeReferences = true,
                            DetailLevel = ViewDetailLevel.Fine
                        };
                        var geom = piso.get_Geometry(opt);
                        if (geom != null)
                            foreach (var gObj in geom)
                                facesPintadas += PaintFaces(doc, piso.Id, gObj, floorMat.Id);
                    }
                }

                // 3) Paredes perimetrais
                var idsParedes = new List<long>();
                foreach (var (ax, ay, bx, by) in arestas)
                {
                    var linha = Line.CreateBound(
                        new XYZ(MToFt(ax), MToFt(ay), level.Elevation),
                        new XYZ(MToFt(bx), MToFt(by), level.Elevation));

                    Wall parede = wt != null
                        ? Wall.Create(doc, linha, wt.Id, level.Id, MToFt(wallH), 0.0, false, false)
                        : Wall.Create(doc, linha, level.Id, false);
                    if (wt == null)
                        parede.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)
                            ?.Set(MToFt(wallH));

                    idsParedes.Add(parede.Id.Value);
                }

                result = new
                {
                    nivel = new
                    {
                        id = level.Id.Value,
                        nome = level.Name,
                        elevacao_m = elev
                    },
                    piso = floorId == null ? null : new
                    {
                        id = floorId.Value,
                        tipo = ft.Name,
                        material = floorMat?.Name,
                        faces_pintadas = facesPintadas
                    },
                    paredes = new
                    {
                        ids = idsParedes,
                        quantidade = idsParedes.Count,
                        tipo = wt?.Name,
                        altura_m = wallH
                    }
                };
                tx.Commit();
            }
            return result;
        }

        // =====================================================================
        // GUARDA-CORPOS — railing independente ao longo de um caminho
        // =====================================================================

        /// <summary>
        /// Cria um guarda-corpo (Railing) independente ao longo de um caminho.
        /// Útil para mezaninos, varandas e bordas de laje (a escada já cria o
        /// seu próprio corrimão automaticamente).
        ///
        /// Parâmetros (metros):
        ///   points          — lista de pontos do caminho. Cada ponto pode ser
        ///                     [x, y] ou { "x_m": .., "y_m": .. }. Mínimo 2 pontos.
        ///   level           — nível de base (opcional, usa térreo)
        ///   height_offset_m — deslocamento vertical acima do nível (opcional)
        ///   railing_type    — nome (parcial) do tipo de guarda-corpo (opcional)
        /// </summary>
        private static object CreateRailing(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var level = FindLevel(doc, GetS(args, "level"))
                ?? throw new Exception("Nenhum nível encontrado.");
            double heightOffset = GetD(args, "height_offset_m", 0.0);

            if (!args.TryGetProperty("points", out var pts) ||
                pts.ValueKind != JsonValueKind.Array)
                throw new ArgumentException(
                    "Array 'points' é obrigatório (mínimo 2 pontos [x, y]).");

            double z = level.Elevation + MToFt(heightOffset);
            var vertices = new List<XYZ>();
            foreach (var p in pts.EnumerateArray())
            {
                double px, py;
                if (p.ValueKind == JsonValueKind.Array)
                {
                    var a = p.EnumerateArray().ToList();
                    px = a[0].GetDouble(); py = a[1].GetDouble();
                }
                else
                {
                    px = p.GetProperty("x_m").GetDouble();
                    py = p.GetProperty("y_m").GetDouble();
                }
                vertices.Add(new XYZ(MToFt(px), MToFt(py), z));
            }

            if (vertices.Count < 2)
                throw new Exception("O caminho do guarda-corpo precisa de ao menos 2 pontos.");

            // Monta o caminho (CurveLoop aberto) ligando pontos consecutivos
            var curves = new List<Curve>();
            for (int i = 0; i < vertices.Count - 1; i++)
            {
                if (vertices[i].DistanceTo(vertices[i + 1]) < 1e-6) continue;
                curves.Add(Line.CreateBound(vertices[i], vertices[i + 1]));
            }
            if (curves.Count == 0)
                throw new Exception("Pontos coincidentes: nenhum segmento válido.");
            var loop = CurveLoop.Create(curves);

            // Tipo de guarda-corpo (por nome parcial, ou o primeiro disponível)
            var tipos = new FilteredElementCollector(doc)
                .OfClass(typeof(RailingType)).Cast<RailingType>().ToList();
            if (tipos.Count == 0)
                throw new Exception("Nenhum tipo de guarda-corpo disponível no template.");

            string typeName = GetS(args, "railing_type");
            RailingType railingType = tipos.First();
            if (!string.IsNullOrEmpty(typeName))
            {
                var alvo = Normalizar(typeName);
                railingType = tipos.FirstOrDefault(t => Normalizar(t.Name) == alvo)
                    ?? tipos.FirstOrDefault(t => Normalizar(t.Name).Contains(alvo))
                    ?? tipos.First();
            }

            object result;
            using (var tx = new Transaction(doc, "MCP: Criar guarda-corpo"))
            {
                tx.Start();
                var railing = Railing.Create(doc, loop, railingType.Id, level.Id);
                result = new
                {
                    id = railing.Id.Value,
                    tipo = railingType.Name,
                    nivel = level.Name,
                    segmentos = curves.Count
                };
                tx.Commit();
            }
            return result;
        }

        // =====================================================================
        // MATERIAIS — listar e aplicar acabamentos (piso, azulejo, madeira...)
        // =====================================================================

        /// <summary>
        /// Lista os materiais disponíveis no documento, com ID, nome e cor RGB.
        /// Aceita filtro opcional "filtro" (string) para busca parcial no nome.
        /// Exemplo de uso: encontrar "Madeira", "Cerâmica", "Concreto" antes
        /// de aplicar com set_material.
        /// </summary>
        private static object ListMaterials(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string filtro = GetS(args, "filtro");

            var materiais = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>();

            if (!string.IsNullOrEmpty(filtro))
            {
                var alvo = Normalizar(filtro);
                materiais = materiais.Where(m => Normalizar(m.Name).Contains(alvo));
            }

            return materiais
                .OrderBy(m => m.Name)
                .Select(m => new
                {
                    id = m.Id.Value,
                    nome = m.Name,
                    cor = $"{m.Color.Red},{m.Color.Green},{m.Color.Blue}"
                })
                .ToList();
        }

        /// <summary>Cria ou atualiza um material e, quando informado, vincula uma textura bitmap ao ativo de aparencia.</summary>
        private static object CreateMaterial(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string name = GetS(args, "name") ?? throw new ArgumentException("Argumento 'name' obrigatorio.");
            var color = ParseMcpColor(GetS(args, "color", "#B0B0B0"));
            int transparency = (int)Math.Max(0, Math.Min(100, GetD(args, "transparency", 0)));
            string texturePath = GetS(args, "texture_path");
            if (!string.IsNullOrWhiteSpace(texturePath) && !File.Exists(texturePath))
                throw new FileNotFoundException("Arquivo de textura nao encontrado.", texturePath);

            var material = FindMaterialByName(doc, name);
            bool created = material == null;
            ElementId assetId = ElementId.InvalidElementId;
            using (var tx = new Transaction(doc, "MCP: Criar material"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                if (material == null)
                    material = doc.GetElement(Material.Create(doc, name)) as Material;
                material.Color = color;
                material.Transparency = transparency;

                // Um material novo nao possui necessariamente ativo de aparencia.
                // Duplica um ativo existente para que textura e cor nao alterem outro material.
                if (!string.IsNullOrWhiteSpace(texturePath) && material.AppearanceAssetId == ElementId.InvalidElementId)
                {
                    var source = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                        .FirstOrDefault(m => m.Id != material.Id && m.AppearanceAssetId != ElementId.InvalidElementId);
                    var sourceAsset = source == null ? null : doc.GetElement(source.AppearanceAssetId) as AppearanceAssetElement;
                    if (sourceAsset != null)
                        material.AppearanceAssetId = sourceAsset.Duplicate("MCP Aparencia " + name).Id;
                }
                assetId = material.AppearanceAssetId;
                tx.Commit();
            }

            bool textureApplied = false;
            if (!string.IsNullOrWhiteSpace(texturePath) && assetId != ElementId.InvalidElementId)
            {
                using (var tx = new Transaction(doc, "MCP: Aplicar textura"))
                {
                    tx.Start();
                    ConfigureAutomaticWarningHandling(tx);
                    var scope = new AppearanceAssetEditScope(doc);
                    try
                    {
                        var asset = scope.Start(assetId);
                        var diffuse = asset.FindByName("generic_diffuse");
                        if (diffuse == null) throw new Exception("O ativo de aparencia nao possui canal diffuse editavel.");
                        var bitmapAsset = diffuse.GetSingleConnectedAsset();
                        if (bitmapAsset == null)
                        {
                            diffuse.AddConnectedAsset("UnifiedBitmap");
                            bitmapAsset = diffuse.GetSingleConnectedAsset();
                        }
                        var bitmap = bitmapAsset?.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
                        if (bitmap == null || !bitmap.IsValidValue(texturePath))
                            throw new Exception("A textura nao e valida para o ativo de aparencia selecionado.");
                        bitmap.Value = texturePath;
                        scope.Commit(true);
                        textureApplied = true;
                    }
                    catch
                    {
                        if (scope.IsActive) scope.Cancel();
                        throw;
                    }
                    finally { scope.Dispose(); }
                    tx.Commit();
                }
            }
            return new { id = material.Id.Value, nome = material.Name, criado = created,
                cor = $"{color.Red},{color.Green},{color.Blue}", transparencia = transparency,
                textura_aplicada = textureApplied, textura = texturePath };
        }

        /// <summary>Cria geometria retangular 3D em categoria de modelo com material opcional.</summary>
        private static object CreateRectangularPrism(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            double x = GetDReq(args, "x_m"), y = GetDReq(args, "y_m");
            double w = GetDReq(args, "width_m"), d = GetDReq(args, "depth_m"), h = GetD(args, "height_m", 0.1);
            if (w <= 0 || d <= 0 || h <= 0) throw new ArgumentException("width_m, depth_m e height_m devem ser positivos.");
            string categoryName = GetS(args, "category", "GenericModel");
            string name = GetS(args, "name", "Bloco MCP");
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nenhum nivel encontrado.");
            string materialName = GetS(args, "material");
            var material = string.IsNullOrWhiteSpace(materialName) ? null : FindMaterialByName(doc, materialName);
            if (!string.IsNullOrWhiteSpace(materialName) && material == null)
                throw new Exception("Material nao encontrado. Use create_material ou list_materials.");

            double xf = MToFt(x), yf = MToFt(y), wf = MToFt(w), df = MToFt(d);
            var p0 = new XYZ(xf, yf, level.Elevation);
            var p1 = new XYZ(xf + wf, yf, level.Elevation);
            var p2 = new XYZ(xf + wf, yf + df, level.Elevation);
            var p3 = new XYZ(xf, yf + df, level.Elevation);
            var loop = CurveLoop.Create(new List<Curve> {
                Line.CreateBound(p0,p1), Line.CreateBound(p1,p2), Line.CreateBound(p2,p3), Line.CreateBound(p3,p0) });
            var options = new SolidOptions(material?.Id ?? ElementId.InvalidElementId, ElementId.InvalidElementId);
            var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, MToFt(h), options);
            DirectShape shape;
            using (var tx = new Transaction(doc, "MCP: Criar prisma retangular"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                shape = DirectShape.CreateElement(doc, new ElementId(ResolveMcpShapeCategory(categoryName)));
                shape.ApplicationId = "RevitMcp";
                shape.ApplicationDataId = Guid.NewGuid().ToString("N");
                shape.Name = name;
                shape.SetShape(new GeometryObject[] { solid });
                tx.Commit();
            }
            return new { id = shape.Id.Value, nome = shape.Name, categoria = categoryName, material = material?.Name,
                x_m = x, y_m = y, largura_m = w, profundidade_m = d, altura_m = h };
        }

        private static Material FindMaterialByName(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var target = Normalizar(name);
            var all = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
            return all.FirstOrDefault(m => Normalizar(m.Name) == target)
                ?? all.FirstOrDefault(m => Normalizar(m.Name).Contains(target));
        }

        private static Color ParseMcpColor(string value)
        {
            value = (value ?? "").Trim();
            if (value.StartsWith("#")) value = value.Substring(1);
            try
            {
                if (value.Length == 6)
                    return new Color(Convert.ToByte(value.Substring(0, 2), 16), Convert.ToByte(value.Substring(2, 2), 16), Convert.ToByte(value.Substring(4, 2), 16));
                var values = value.Split(',').Select(v => Convert.ToByte(v.Trim())).ToArray();
                if (values.Length == 3) return new Color(values[0], values[1], values[2]);
            }
            catch { }
            throw new ArgumentException("Cor invalida. Use #RRGGBB ou R,G,B.");
        }

        private static BuiltInCategory ResolveMcpShapeCategory(string category)
        {
            switch (Normalizar(category ?? ""))
            {
                case "furniture": case "mobiliario": return BuiltInCategory.OST_Furniture;
                case "casework": case "marcenaria": return BuiltInCategory.OST_Casework;
                case "planting": case "vegetacao": return BuiltInCategory.OST_Planting;
                case "parking": case "estacionamento": return BuiltInCategory.OST_Parking;
                case "genericmodel": case "modelo generico": case "modelogenerico": return BuiltInCategory.OST_GenericModel;
                default: throw new ArgumentException("Categoria nao suportada. Use GenericModel, Furniture, Casework, Planting ou Parking.");
            }
        }

        /// <summary>
        /// Aplica um material a um elemento existente pintando suas faces.
        /// Funciona para paredes, pisos, telhados, forros e móveis (DirectShape).
        ///
        /// Parâmetros:
        ///   id       — ID do elemento (obrigatório)
        ///   material — nome do material, busca parcial, ignorando maiúsculas
        ///              e acentos (ex: "Madeira", "Ceramica", "Concreto", "Vidro")
        ///
        /// A pintura muda a aparência visual em vistas sombreadas/realistas
        /// sem alterar a estrutura do tipo (não afeta outros elementos).
        /// </summary>
        private static object SetMaterial(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            long id = (long)GetDReq(args, "id");
            string matName = GetS(args, "material");
            if (string.IsNullOrEmpty(matName))
                throw new ArgumentException("Argumento obrigatório ausente: material");

            var elem = doc.GetElement(new ElementId(id));
            if (elem == null)
                throw new Exception($"Elemento com ID {id} não encontrado.");

            // Busca material: exato primeiro, depois parcial (case-insensitive)
            var materiais = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .ToList();

            var alvo = Normalizar(matName);
            var mat = materiais.FirstOrDefault(m => Normalizar(m.Name) == alvo)
                ?? materiais.FirstOrDefault(m => Normalizar(m.Name).Contains(alvo));

            if (mat == null)
                throw new Exception(
                    $"Material '{matName}' não encontrado. " +
                    "Use list_materials para ver os materiais disponíveis.");

            // Calcula geometria com referências (necessário para Paint)
            var opt = new Options
            {
                ComputeReferences = true,
                DetailLevel = ViewDetailLevel.Fine
            };
            var geom = elem.get_Geometry(opt)
                ?? throw new Exception("Elemento não possui geometria para aplicar material.");

            int faces = 0;
            using (var tx = new Transaction(doc, "MCP: Aplicar material"))
            {
                tx.Start();
                foreach (var gObj in geom)
                    faces += PaintFaces(doc, elem.Id, gObj, mat.Id);
                tx.Commit();
            }

            if (faces == 0)
                throw new Exception(
                    "Nenhuma face foi pintada. O elemento pode não ter geometria sólida " +
                    "acessível (tente aplicar a paredes, pisos, telhados ou móveis).");

            return new
            {
                id = elem.Id.Value,
                material = mat.Name,
                faces_pintadas = faces
            };
        }

        /// <summary>
        /// Pinta recursivamente todas as faces sólidas de um GeometryObject.
        /// Trata sólidos diretos e GeometryInstance (famílias aninhadas).
        /// Retorna o número de faces efetivamente pintadas.
        /// </summary>
        private static int PaintFaces(Document doc, ElementId elemId, GeometryObject gObj, ElementId matId)
        {
            int count = 0;

            if (gObj is Solid solid && solid.Faces.Size > 0)
            {
                foreach (Face face in solid.Faces)
                {
                    try { doc.Paint(elemId, face, matId); count++; }
                    catch { /* face não pintável — ignora */ }
                }
            }
            else if (gObj is GeometryInstance gi)
            {
                foreach (var sub in gi.GetInstanceGeometry())
                    count += PaintFaces(doc, elemId, sub, matId);
            }

            return count;
        }

        // =====================================================================
        // VISUALIZAÇÃO — vistas, zoom, section box, screenshot
        // =====================================================================

        /// <summary>
        /// Define um Section Box na vista 3D ativa para criar um corte isométrico.
        /// O Section Box recorta a geometria, mostrando o interior da casa
        /// como na imagem de referência "Ground Floor".
        ///
        /// Parâmetros (todos em metros):
        ///   x_min, y_min, z_min — canto inferior do box
        ///   x_max, y_max, z_max — canto superior do box
        ///   enabled — true/false para ativar/desativar (padrão: true)
        /// </summary>
        private static object SetSectionBox(UIApplication uiApp, JsonElement args)
        {
            var uidoc = uiApp.ActiveUIDocument
                ?? throw new Exception("Nenhum documento ativo.");
            var doc = uidoc.Document;

            // Garante que estamos em uma vista 3D
            var view3d = uidoc.ActiveView as View3D;
            if (view3d == null)
            {
                SetView3D(uiApp);
                view3d = uidoc.ActiveView as View3D;
                if (view3d == null)
                    throw new Exception("Não foi possível ativar a vista 3D.");
            }

            bool enabled = true;
            if (args.TryGetProperty("enabled", out var enVal))
            {
                if (enVal.ValueKind == JsonValueKind.False) enabled = false;
            }

            using (var tx = new Transaction(doc, "MCP: Section Box"))
            {
                tx.Start();

                if (!enabled)
                {
                    view3d.IsSectionBoxActive = false;
                    tx.Commit();
                    return new { section_box = false, vista = view3d.Name };
                }

                // Limites do section box em metros → pés
                double xMin = GetD(args, "x_min_m", -1);
                double yMin = GetD(args, "y_min_m", -1);
                double zMin = GetD(args, "z_min_m", -0.5);
                double xMax = GetD(args, "x_max_m", 9);
                double yMax = GetD(args, "y_max_m", 6);
                double zMax = GetD(args, "z_max_m", 4);

                var box = new BoundingBoxXYZ
                {
                    Min = new XYZ(MToFt(xMin), MToFt(yMin), MToFt(zMin)),
                    Max = new XYZ(MToFt(xMax), MToFt(yMax), MToFt(zMax))
                };

                view3d.IsSectionBoxActive = true;
                view3d.SetSectionBox(box);
                tx.Commit();
            }

            // Reorienta a câmera para ângulo isométrico
            try
            {
                double cx = (GetD(args, "x_min_m", -1) + GetD(args, "x_max_m", 9)) / 2.0;
                double cy = (GetD(args, "y_min_m", -1) + GetD(args, "y_max_m", 6)) / 2.0;
                double cz = (GetD(args, "z_min_m", -0.5) + GetD(args, "z_max_m", 4)) / 2.0;
                var target = new XYZ(MToFt(cx), MToFt(cy), MToFt(cz));
                var eye = new XYZ(MToFt(cx + 15), MToFt(cy + 15), MToFt(cz + 10));
                var forward = target - eye;
                view3d.SetOrientation(new ViewOrientation3D(eye, XYZ.BasisZ, forward));
            }
            catch { }

            try
            {
                foreach (var uv in uidoc.GetOpenUIViews())
                    if (uv.ViewId == view3d.Id) uv.ZoomToFit();
            }
            catch { }

            return new { section_box = true, vista = view3d.Name };
        }

        /// <summary>
        /// Altera o estilo visual da vista ativa.
        /// Estilos disponíveis: wireframe, hidden, shading, realistic, raytrace
        /// O modo "realistic" mostra materiais e texturas.
        /// O modo "shading" mostra cores sólidas com sombreamento.
        /// </summary>
        private static object SetVisualStyle(UIApplication uiApp, JsonElement args)
        {
            var uidoc = uiApp.ActiveUIDocument
                ?? throw new Exception("Nenhum documento ativo.");
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            string style = GetS(args, "style", "shading");

            DisplayStyle ds;
            switch (style.ToLowerInvariant())
            {
                case "wireframe":  ds = DisplayStyle.Wireframe; break;
                case "hidden":     ds = DisplayStyle.HLR; break;
                case "shading":    ds = DisplayStyle.Shading; break;
                case "realistic":  ds = DisplayStyle.Realistic; break;
                default:
                    throw new Exception(
                        $"Estilo '{style}' não reconhecido. " +
                        "Use: wireframe, hidden, shading, realistic");
            }

            using (var tx = new Transaction(doc, "MCP: Estilo visual"))
            {
                tx.Start();
                view.DisplayStyle = ds;
                tx.Commit();
            }

            return new { vista = view.Name, estilo = style };
        }

        /// <summary>
        /// Orienta a câmera 3D com controle preciso — apresentações estilo
        /// "Ground Floor" (isométrico rebaixado, olhando um canto específico
        /// do modelo). Usa coordenadas esféricas ao redor de um alvo:
        ///
        ///   • azimuth   — giro horizontal em graus (0 = +X, 90 = +Y, 225 = SW)
        ///   • elevation — inclinação vertical em graus (0 = horizonte,
        ///                 30 = isométrico típico, 90 = topo direto)
        ///   • distance_m— distância da câmera ao alvo (padrão 20m)
        ///   • target_*  — ponto que a câmera olha (opcional; usa o centro
        ///                 do modelo se omitido)
        /// </summary>
        private static object SetCamera(UIApplication uiApp, JsonElement args)
        {
            var uidoc = uiApp.ActiveUIDocument
                ?? throw new Exception("Nenhum documento ativo.");
            var doc = uidoc.Document;

            var view3d = uidoc.ActiveView as View3D
                ?? new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D)).Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate)
                ?? throw new Exception("Nenhuma vista 3D disponível.");

            if (uidoc.ActiveView.Id != view3d.Id)
                uidoc.ActiveView = view3d;

            double azimute = GetD(args, "azimuth", 225);   // sudoeste, padrão isométrico
            double elevacao = GetD(args, "elevation", 30);  // 30° acima do horizonte
            double distancia = GetD(args, "distance_m", 20);

            // Alvo: informado, ou centro do modelo (via bounding box do que está visível)
            XYZ alvo;
            bool alvoInformado = args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("target_x_m", out _);
            if (alvoInformado)
            {
                alvo = new XYZ(
                    MToFt(GetD(args, "target_x_m", 0)),
                    MToFt(GetD(args, "target_y_m", 0)),
                    MToFt(GetD(args, "target_z_m", 1.5)));
            }
            else
            {
                var bb = view3d.get_BoundingBox(null);
                alvo = bb != null
                    ? (bb.Min + bb.Max) / 2.0
                    : new XYZ(MToFt(4), MToFt(2.5), MToFt(1.5));
            }

            // Coordenadas esféricas → posição da câmera
            double azRad = azimute * Math.PI / 180.0;
            double elRad = elevacao * Math.PI / 180.0;
            double dFt = MToFt(distancia);

            double dx = dFt * Math.Cos(elRad) * Math.Cos(azRad);
            double dy = dFt * Math.Cos(elRad) * Math.Sin(azRad);
            double dz = dFt * Math.Sin(elRad);
            var olho = new XYZ(alvo.X + dx, alvo.Y + dy, alvo.Z + dz);

            var frente = (alvo - olho).Normalize();

            // "Cima" precisa ser EXATAMENTE perpendicular a "frente".
            // Estratégia: pega Z global e remove sua projeção na direção "frente",
            // depois normaliza. Se "frente" já é vertical (câmera olhando para
            // baixo), usa um "cima" horizontal alinhado ao azimute.
            XYZ cima;
            if (Math.Abs(frente.DotProduct(XYZ.BasisZ)) > 0.9999)
            {
                cima = new XYZ(-Math.Cos(azRad), -Math.Sin(azRad), 0).Normalize();
            }
            else
            {
                var projecao = frente.Multiply(XYZ.BasisZ.DotProduct(frente));
                cima = (XYZ.BasisZ - projecao).Normalize();
            }

            using (var tx = new Transaction(doc, "MCP: Orientar câmera"))
            {
                tx.Start();
                view3d.SetOrientation(new ViewOrientation3D(olho, cima, frente));
                tx.Commit();
            }

            // Zoom para caber o modelo na tela
            foreach (var uv in uidoc.GetOpenUIViews())
                if (uv.ViewId == view3d.Id) uv.ZoomToFit();

            return new
            {
                vista = view3d.Name,
                azimute_graus = azimute,
                elevacao_graus = elevacao,
                distancia_m = distancia,
                alvo_m = new
                {
                    x = Math.Round(FtToM(alvo.X), 3),
                    y = Math.Round(FtToM(alvo.Y), 3),
                    z = Math.Round(FtToM(alvo.Z), 3)
                }
            };
        }

        /// <summary>Ajusta o zoom para mostrar todo o conteúdo da vista ativa.</summary>
        private static object ZoomFit(UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument
                ?? throw new Exception("Nenhum documento ativo.");
            var view = uidoc.ActiveView;

            foreach (var uv in uidoc.GetOpenUIViews())
            {
                if (uv.ViewId == view.Id)
                {
                    uv.ZoomToFit();
                    return new { zoom_ok = true, vista = view.Name };
                }
            }
            return new { zoom_ok = false };
        }

        /// <summary>Seleciona e enquadra um elemento na vista ativa.</summary>
        private static object FocusElement(UIApplication uiApp, JsonElement args)
        {
            var uidoc = uiApp.ActiveUIDocument
                ?? throw new Exception("Nenhum documento ativo.");
            var id = GetElementIdReq(args, "element_id");
            var element = uidoc.Document.GetElement(id)
                ?? throw new ArgumentException($"Elemento {id.Value} não existe.");
            uidoc.Selection.SetElementIds(new List<ElementId> { id });
            uidoc.ShowElements(id);
            return new { id = id.Value, nome = element.Name, vista = uidoc.ActiveView.Name, focado = true };
        }

        /// <summary>
        /// Ativa a vista 3D padrão ({3D}). Cria uma se não existir.
        /// Aplica zoom-to-fit automaticamente.
        /// </summary>
        private static object SetView3D(UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument
                ?? throw new Exception("Nenhum documento ativo.");
            var doc = uidoc.Document;

            // Procura uma vista 3D existente (não-template)
            var view3d = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);

            // Cria uma nova se não encontrar
            if (view3d == null)
            {
                var vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                    ?? throw new Exception("Tipo de vista 3D não encontrado no template.");

                using (var tx = new Transaction(doc, "MCP: Criar vista 3D"))
                {
                    tx.Start();
                    view3d = View3D.CreateIsometric(doc, vft.Id);
                    tx.Commit();
                }
            }

            uidoc.ActiveView = view3d;
            foreach (var uv in uidoc.GetOpenUIViews())
                if (uv.ViewId == view3d.Id) uv.ZoomToFit();

            return new { vista = view3d.Name, id = view3d.Id.Value };
        }

        /// <summary>
        /// Ativa a vista 3D com ângulo isométrico (perspectiva elevada).
        /// Bom para visualizar a casa completa com móveis.
        /// </summary>
        private static object SetViewShaded(UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument
                ?? throw new Exception("Nenhum documento ativo.");

            // Garante que estamos em uma vista 3D
            var view = uidoc.ActiveView as View3D;
            if (view == null)
            {
                // Ativa a vista 3D primeiro
                SetView3D(uiApp);
                view = uidoc.ActiveView as View3D;
                if (view == null)
                    throw new Exception("Não foi possível ativar a vista 3D.");
            }

            try
            {
                // Posiciona a câmera em ângulo isométrico elevado
                var target = new XYZ(4, 2.5, 1.5);   // centro aproximado da casa
                var eyePos = new XYZ(12, 12, 8);      // posição elevada
                var forward = target - eyePos;
                view.SetOrientation(new ViewOrientation3D(eyePos, XYZ.BasisZ, forward));
            }
            catch { }

            try
            {
                foreach (var uv in uidoc.GetOpenUIViews())
                    if (uv.ViewId == view.Id) uv.ZoomToFit();
            }
            catch { }

            return new { vista = view.Name, angulo = "isométrico" };
        }

        /// <summary>
        /// Ativa a planta de piso (ViewPlan) do nível especificado.
        /// Se nível não for informado, usa o térreo.
        /// </summary>
        private static object SetViewPlan(UIApplication uiApp, JsonElement args)
        {
            var uidoc = uiApp.ActiveUIDocument
                ?? throw new Exception("Nenhum documento ativo.");
            var doc = uidoc.Document;
            string levelName = GetS(args, "level");

            var level = FindLevel(doc, levelName)
                ?? throw new Exception("Nível não encontrado.");

            // Procura a ViewPlan associada ao nível
            var plan = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate &&
                    v.GenLevel != null && v.GenLevel.Id == level.Id);

            if (plan == null)
                throw new Exception($"Nenhuma planta de piso para o nível '{level.Name}'.");

            uidoc.ActiveView = plan;
            foreach (var uv in uidoc.GetOpenUIViews())
                if (uv.ViewId == plan.Id) uv.ZoomToFit();

            return new { vista = plan.Name, id = plan.Id.Value, nivel = level.Name };
        }

        /// <summary>
        /// Cria uma vista de planta para um nível que não recebeu uma vista do
        /// template. É essencial ao automatizar pavimentos criados pela API.
        /// Se já existir, retorna a vista existente sem duplicá-la.
        /// </summary>
        private static object CreateFloorPlan(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var level = FindLevel(doc, GetS(args, "level"))
                ?? throw new Exception("Nível não encontrado.");
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id);
            if (existing != null)
                return new { id = existing.Id.Value, vista = existing.Name, nivel = level.Name, criada = false };

            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan)
                ?? throw new Exception("Nenhum tipo de vista de planta disponível no projeto.");
            ViewPlan plan;
            using (var tx = new Transaction(doc, "MCP: Criar vista de planta"))
            {
                tx.Start();
                plan = ViewPlan.Create(doc, type.Id, level.Id);
                tx.Commit();
            }
            return new { id = plan.Id.Value, vista = plan.Name, nivel = level.Name, criada = true };
        }

        /// <summary>
        /// Define o recorte de uma vista de planta em metros. Remove a área
        /// vazia da imagem exportada e padroniza pranchas de apresentação.
        /// </summary>
        private static object SetPlanCrop(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            if (!(view is ViewPlan)) throw new Exception("A vista ativa deve ser uma planta de piso.");
            double x0 = GetDReq(args, "x_min_m");
            double y0 = GetDReq(args, "y_min_m");
            double x1 = GetDReq(args, "x_max_m");
            double y1 = GetDReq(args, "y_max_m");
            if (x1 <= x0 || y1 <= y0) throw new ArgumentException("O retângulo de recorte é inválido.");
            using (var tx = new Transaction(doc, "MCP: Ajustar recorte de planta"))
            {
                tx.Start();
                view.CropBox = new BoundingBoxXYZ
                {
                    Min = new XYZ(MToFt(x0), MToFt(y0), -100),
                    Max = new XYZ(MToFt(x1), MToFt(y1), 100)
                };
                view.CropBoxActive = true;
                view.CropBoxVisible = false;
                tx.Commit();
            }
            return new { vista = view.Name, x_min_m = x0, y_min_m = y0, x_max_m = x1, y_max_m = y1 };
        }

        /// <summary>
        /// Exporta a vista ativa como imagem PNG.
        /// Se path não for informado, salva em %TEMP%.
        /// Retorna o caminho do arquivo gerado.
        /// </summary>
        private static object Screenshot(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string requested = GetS(args, "path");
            int pixels = (int)GetD(args, "pixels", 1600);

            string basePath;
            if (string.IsNullOrEmpty(requested))
            {
                basePath = Path.Combine(Path.GetTempPath(),
                    $"revit_mcp_{DateTime.Now:yyyyMMdd_HHmmss}");
            }
            else
            {
                var dir = Path.GetDirectoryName(requested);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                basePath = Path.Combine(
                    dir ?? "",
                    Path.GetFileNameWithoutExtension(requested));
            }

            var opts = new ImageExportOptions
            {
                FilePath = basePath,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ExportRange = ExportRange.CurrentView,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixels,
                ImageResolution = ImageResolution.DPI_150,
                FitDirection = FitDirectionType.Horizontal
            };
            doc.ExportImage(opts);

            // O Revit adiciona sufixo ao nome do arquivo (ex: " - Planta - Nível 1.png")
            // Precisamos encontrar o arquivo gerado
            var searchDir = Path.GetDirectoryName(basePath);
            if (string.IsNullOrEmpty(searchDir))
                searchDir = Directory.GetCurrentDirectory();

            var stem = Path.GetFileName(basePath);
            var candidates = Directory.GetFiles(searchDir, stem + "*.png");

            var picked = candidates
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();

            return new
            {
                caminho = picked ?? (basePath + ".png"),
                arquivos_encontrados = candidates
            };
        }

        // =====================================================================
        // ONDA 1 — MANIPULAÇÃO DE ELEMENTOS
        // =====================================================================

        /// <summary>Obtém um ElementId a partir de argumento numérico.</summary>
        private static ElementId GetElementIdReq(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
                throw new ArgumentException($"Argumento obrigatório ausente: {name}");
            return new ElementId(v.GetInt64());
        }

        /// <summary>Move elemento por (dx, dy, dz) em metros.</summary>
        private static object MoveElement(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "element_id");
            var vec = new XYZ(MToFt(GetD(args, "dx_m", 0)),
                              MToFt(GetD(args, "dy_m", 0)),
                              MToFt(GetD(args, "dz_m", 0)));
            using (var tx = new Transaction(doc, "MCP: Mover"))
            {
                tx.Start();
                ElementTransformUtils.MoveElement(doc, id, vec);
                tx.Commit();
            }
            return new { id = id.Value, movido_m = new { dx = GetD(args, "dx_m", 0), dy = GetD(args, "dy_m", 0), dz = GetD(args, "dz_m", 0) } };
        }

        /// <summary>Copia elemento com deslocamento.</summary>
        private static object CopyElement(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "element_id");
            var vec = new XYZ(MToFt(GetD(args, "dx_m", 0)),
                              MToFt(GetD(args, "dy_m", 0)),
                              MToFt(GetD(args, "dz_m", 0)));
            ICollection<ElementId> ids;
            using (var tx = new Transaction(doc, "MCP: Copiar"))
            {
                tx.Start();
                ids = ElementTransformUtils.CopyElement(doc, id, vec);
                tx.Commit();
            }
            return new { ids_criados = ids.Select(i => i.Value).ToList() };
        }

        /// <summary>Rotaciona elemento em torno de eixo vertical passando por (x,y).</summary>
        private static object RotateElement(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "element_id");
            double angGraus = GetD(args, "angle_deg", 90);
            double cx = GetD(args, "center_x_m", 0);
            double cy = GetD(args, "center_y_m", 0);
            var eixo = Line.CreateBound(
                new XYZ(MToFt(cx), MToFt(cy), 0),
                new XYZ(MToFt(cx), MToFt(cy), 10));
            using (var tx = new Transaction(doc, "MCP: Rotacionar"))
            {
                tx.Start();
                ElementTransformUtils.RotateElement(doc, id, eixo, angGraus * Math.PI / 180.0);
                tx.Commit();
            }
            return new { id = id.Value, angulo_graus = angGraus };
        }

        /// <summary>Espelha elemento em plano vertical XZ passando por linha horizontal.</summary>
        private static object MirrorElement(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "element_id");
            double x1 = GetDReq(args, "x1_m"), y1 = GetDReq(args, "y1_m");
            double x2 = GetDReq(args, "x2_m"), y2 = GetDReq(args, "y2_m");
            var p1 = new XYZ(MToFt(x1), MToFt(y1), 0);
            var p2 = new XYZ(MToFt(x2), MToFt(y2), 0);
            var dir = (p2 - p1).Normalize();
            var normal = new XYZ(-dir.Y, dir.X, 0);
            var plane = Plane.CreateByNormalAndOrigin(normal, p1);
            ICollection<ElementId> ids;
            using (var tx = new Transaction(doc, "MCP: Espelhar"))
            {
                tx.Start();
                ids = ElementTransformUtils.MirrorElements(doc, new[] { id }, plane, true);
                tx.Commit();
            }
            return new { ids_criados = ids.Select(i => i.Value).ToList() };
        }

        /// <summary>Modifica parâmetros de um elemento por nome. Aceita string ou número.</summary>
        private static object ModifyParameters(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "element_id");
            var el = doc.GetElement(id) ?? throw new Exception($"Elemento {id.Value} não existe.");
            if (!args.TryGetProperty("params", out var pmap) || pmap.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Objeto 'params' com pares nome->valor é obrigatório");

            var aplicados = new List<object>();
            using (var tx = new Transaction(doc, "MCP: Modificar parâmetros"))
            {
                tx.Start();
                foreach (var prop in pmap.EnumerateObject())
                {
                    var par = el.LookupParameter(prop.Name);
                    if (par == null || par.IsReadOnly) { aplicados.Add(new { nome = prop.Name, ok = false, motivo = par == null ? "não encontrado" : "só-leitura" }); continue; }
                    try
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number)
                        {
                            double val = prop.Value.GetDouble();
                            if (par.StorageType == StorageType.Double) par.Set(MToFt(val));
                            else if (par.StorageType == StorageType.Integer) par.Set((int)val);
                            else par.Set(val.ToString());
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.String) par.Set(prop.Value.GetString());
                        else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False) par.Set(prop.Value.GetBoolean() ? 1 : 0);
                        aplicados.Add(new { nome = prop.Name, ok = true });
                    }
                    catch (Exception ex) { aplicados.Add(new { nome = prop.Name, ok = false, motivo = ex.Message }); }
                }
                tx.Commit();
            }
            return new { id = id.Value, parametros = aplicados };
        }

        /// <summary>Alinha elementos: move ids alinhando a coordenada especificada (x/y/z) à do primeiro.</summary>
        private static object AlignElements(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!args.TryGetProperty("ids", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 2)
                throw new ArgumentException("Array 'ids' com pelo menos 2 IDs é obrigatório");
            string eixo = (GetS(args, "axis", "x") ?? "x").ToLowerInvariant();
            var ids = arr.EnumerateArray().Select(v => new ElementId(v.GetInt64())).ToList();
            var refEl = doc.GetElement(ids[0]) ?? throw new Exception("Primeiro elemento não existe.");
            var refPt = (refEl.Location as LocationPoint)?.Point
                     ?? (refEl.Location as LocationCurve)?.Curve.GetEndPoint(0)
                     ?? throw new Exception("Elemento de referência sem posição.");
            var movidos = new List<object>();
            using (var tx = new Transaction(doc, "MCP: Alinhar"))
            {
                tx.Start();
                for (int i = 1; i < ids.Count; i++)
                {
                    var el = doc.GetElement(ids[i]); if (el == null) continue;
                    var p = (el.Location as LocationPoint)?.Point
                         ?? (el.Location as LocationCurve)?.Curve.GetEndPoint(0);
                    if (p == null) continue;
                    var d = eixo == "x" ? new XYZ(refPt.X - p.X, 0, 0)
                          : eixo == "y" ? new XYZ(0, refPt.Y - p.Y, 0)
                                        : new XYZ(0, 0, refPt.Z - p.Z);
                    ElementTransformUtils.MoveElement(doc, ids[i], d);
                    movidos.Add(new { id = ids[i].Value });
                }
                tx.Commit();
            }
            return new { alinhados = movidos, eixo };
        }

        // =====================================================================
        // ONDA 2 — ANOTAÇÃO
        // =====================================================================

        /// <summary>Cria texto na vista ativa em (x,y) metros.</summary>
        private static object CreateText(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            string texto = GetS(args, "text") ?? throw new ArgumentException("Argumento 'text' obrigatório");
            var pos = new XYZ(MToFt(GetD(args, "x_m", 0)), MToFt(GetD(args, "y_m", 0)), 0);
            var typeId = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId();
            double sizeMm = GetD(args, "text_size_mm", double.NaN);
            using (var tx = new Transaction(doc, "MCP: Texto"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                if (!double.IsNaN(sizeMm) && sizeMm > 0)
                {
                    var baseType = doc.GetElement(typeId) as TextNoteType;
                    string name = $"MCP Texto {sizeMm:0.##}mm";
                    var custom = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                        .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (custom == null)
                    {
                        custom = baseType.Duplicate(name) as TextNoteType;
                        custom.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(MToFt(sizeMm / 1000.0));
                    }
                    typeId = custom.Id;
                }
                var tn = TextNote.Create(doc, view.Id, pos, texto, typeId);
                tx.Commit();
                return new { id = tn.Id.Value, texto, tamanho_mm = double.IsNaN(sizeMm) ? (double?)null : sizeMm };
            }
        }

        /// <summary>
        /// Remove anotações de texto somente da vista ativa. Pode filtrar por
        /// trechos de texto, permitindo corrigir pranchas sem tocar em outras vistas.
        /// </summary>
        private static object DeleteTextNotes(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var filters = new List<string>();
            if (args.TryGetProperty("contains", out var values) && values.ValueKind == JsonValueKind.Array)
                filters = values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (filters.Count == 0) throw new ArgumentException("Array 'contains' com ao menos um texto é obrigatório.");
            var ids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
                .Where(t => filters.Any(f => t.Text.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(t => t.Id).ToList();
            using (var tx = new Transaction(doc, "MCP: Remover textos da vista"))
            {
                tx.Start();
                foreach (var id in ids) doc.Delete(id);
                tx.Commit();
            }
            return new { removidos = ids.Count, vista = view.Name };
        }

        /// <summary>Cria vista de corte (secção) numa caixa em torno de linha do modelo.</summary>
        private static object CreateSectionMark(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            double x1 = GetDReq(args, "x1_m"), y1 = GetDReq(args, "y1_m");
            double x2 = GetDReq(args, "x2_m"), y2 = GetDReq(args, "y2_m");
            double alturaBaixo = GetD(args, "bottom_m", 0);
            double alturaCima  = GetD(args, "top_m", 6);
            double profundidade = GetD(args, "depth_m", 5);

            var p1 = new XYZ(MToFt(x1), MToFt(y1), 0);
            var p2 = new XYZ(MToFt(x2), MToFt(y2), 0);
            var dirCorte = (p2 - p1).Normalize();
            var normal = new XYZ(-dirCorte.Y, dirCorte.X, 0);
            var t = Transform.Identity;
            t.Origin = (p1 + p2) * 0.5;
            t.BasisX = dirCorte;
            t.BasisY = XYZ.BasisZ;
            t.BasisZ = normal;
            var bb = new BoundingBoxXYZ { Transform = t };
            double meia = p1.DistanceTo(p2) / 2.0;
            bb.Min = new XYZ(-meia, MToFt(alturaBaixo), 0);
            bb.Max = new XYZ( meia, MToFt(alturaCima), MToFt(profundidade));

            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Section)
                ?? throw new Exception("Nenhum ViewFamilyType de Section disponível.");
            using (var tx = new Transaction(doc, "MCP: Section mark"))
            {
                tx.Start();
                var vs = ViewSection.CreateSection(doc, vft.Id, bb);
                tx.Commit();
                return new { id = vs.Id.Value, nome = vs.Name };
            }
        }

        /// <summary>Cria cota de nível (spot elevation) num ponto.</summary>
        private static object CreateSpotElevation(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var id = GetElementIdReq(args, "element_id");
            var el = doc.GetElement(id) ?? throw new Exception($"Elemento {id.Value} não existe.");

            // Pega uma face horizontal (normal +Z ou -Z) — spot elevation precisa dela.
            // Preferência: face de topo do sólido do elemento.
            var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, DetailLevel = ViewDetailLevel.Fine };
            Reference refFace = null;
            XYZ pontoFace = null;
            foreach (var gObj in el.get_Geometry(opt))
            {
                if (!(gObj is Solid solido) || solido.Faces.Size == 0) continue;
                PlanarFace melhor = null;
                foreach (Face f in solido.Faces)
                {
                    if (f is PlanarFace pf && pf.Reference != null &&
                        Math.Abs(pf.FaceNormal.Z) > 0.99)
                    {
                        if (melhor == null || pf.Origin.Z > melhor.Origin.Z) melhor = pf;
                    }
                }
                if (melhor != null) { refFace = melhor.Reference; pontoFace = melhor.Origin; break; }
            }
            if (refFace == null)
                throw new Exception("Elemento não expõe face horizontal — spot elevation exige piso, laje, teto, viga ou topo de parede.");

            // Usa a origem da face — garantido que está NA face (o Revit exige isso).
            // Coordenadas x/y do usuário são ignoradas pra evitar "ponto fora da referência".
            var pOrig = pontoFace;
            var bend = new XYZ(pOrig.X + MToFt(0.5), pOrig.Y + MToFt(0.5), pOrig.Z);
            var end  = new XYZ(pOrig.X + MToFt(1.0), pOrig.Y + MToFt(1.0), pOrig.Z);
            using (var tx = new Transaction(doc, "MCP: Spot elevation"))
            {
                tx.Start();
                var se = doc.Create.NewSpotElevation(view, refFace, pOrig, bend, end, pOrig, false);
                tx.Commit();
                return new { id = se.Id.Value, elevacao_m = Math.Round(FtToM(pontoFace.Z), 3) };
            }
        }

        /// <summary>Cria linha de detalhe 2D na vista ativa.</summary>
        private static object CreateDetailLine(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var p1 = new XYZ(MToFt(GetDReq(args, "x1_m")), MToFt(GetDReq(args, "y1_m")), 0);
            var p2 = new XYZ(MToFt(GetDReq(args, "x2_m")), MToFt(GetDReq(args, "y2_m")), 0);
            using (var tx = new Transaction(doc, "MCP: Detail line"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                var dc = doc.Create.NewDetailCurve(view, Line.CreateBound(p1, p2));
                tx.Commit();
                return new { id = dc.Id.Value };
            }
        }

        /// <summary>
        /// Cria uma coleção de linhas de detalhe na mesma transação. Cada item
        /// aceita x1_m, y1_m, x2_m e y2_m. Erros de itens individuais são
        /// reportados sem descartar as outras linhas do desenho.
        /// </summary>
        private static object CreateDetailLines(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            if (!args.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Array 'lines' é obrigatório.");

            var created = new List<long>();
            var errors = new List<string>();
            using (var tx = new Transaction(doc, "MCP: Linhas de detalhe em lote"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);

                int index = 0;
                foreach (var item in lines.EnumerateArray())
                {
                    try
                    {
                        var p1 = new XYZ(MToFt(GetDReq(item, "x1_m")), MToFt(GetDReq(item, "y1_m")), 0);
                        var p2 = new XYZ(MToFt(GetDReq(item, "x2_m")), MToFt(GetDReq(item, "y2_m")), 0);
                        if (p1.IsAlmostEqualTo(p2)) throw new ArgumentException("A linha não pode ter comprimento zero.");
                        var dc = doc.Create.NewDetailCurve(view, Line.CreateBound(p1, p2));
                        created.Add(dc.Id.Value);
                    }
                    catch (Exception ex) { errors.Add($"linha {index}: {ex.Message}"); }
                    index++;
                }
                tx.Commit();
            }
            return new { quantidade = created.Count, ids = created, erros = errors };
        }

        /// <summary>Cria um retangulo 2D, com borda e preenchimento opcional, na vista ativa.</summary>
        private static object CreateDetailRectangle(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            double x = GetDReq(args, "x_m"), y = GetDReq(args, "y_m");
            double w = GetDReq(args, "width_m"), h = GetDReq(args, "height_m");
            bool filled = args.TryGetProperty("filled", out var filledValue) && filledValue.ValueKind == JsonValueKind.True;
            if (w <= 0 || h <= 0) throw new ArgumentException("width_m e height_m devem ser positivos.");
            var points = new[] {
                new XYZ(MToFt(x), MToFt(y), 0), new XYZ(MToFt(x+w), MToFt(y), 0),
                new XYZ(MToFt(x+w), MToFt(y+h), 0), new XYZ(MToFt(x), MToFt(y+h), 0) };
            var ids = new List<long>();
            long? regionId = null;
            using (var tx = new Transaction(doc, "MCP: Retangulo de detalhe"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                for (int i = 0; i < 4; i++)
                    ids.Add(doc.Create.NewDetailCurve(view, Line.CreateBound(points[i], points[(i + 1) % 4])).Id.Value);
                if (filled)
                {
                    var typeId = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).FirstElementId();
                    if (typeId == ElementId.InvalidElementId) throw new Exception("Nenhum tipo de regiao preenchida disponivel.");
                    var loop = new CurveLoop();
                    for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % 4]));
                    regionId = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop }).Id.Value;
                }
                tx.Commit();
            }
            return new { ids_borda = ids, id_regiao = regionId, preenchido = filled };
        }

        /// <summary>Cria região preenchida (hachura) num retângulo na vista ativa.</summary>
        private static object CreateFilledRegion(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            double x1 = GetDReq(args, "x1_m"), y1 = GetDReq(args, "y1_m");
            double x2 = GetDReq(args, "x2_m"), y2 = GetDReq(args, "y2_m");
            var pts = new List<XYZ> {
                new XYZ(MToFt(x1), MToFt(y1), 0),
                new XYZ(MToFt(x2), MToFt(y1), 0),
                new XYZ(MToFt(x2), MToFt(y2), 0),
                new XYZ(MToFt(x1), MToFt(y2), 0)
            };
            var loop = new CurveLoop();
            for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
            var typeId = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).FirstElementId();
            using (var tx = new Transaction(doc, "MCP: Filled region"))
            {
                tx.Start();
                var fr = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });
                tx.Commit();
                return new { id = fr.Id.Value };
            }
        }

        /// <summary>Cria callout (detalhe ampliado) num retângulo da vista ativa.</summary>
        private static object CreateCallout(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var p1 = new XYZ(MToFt(GetDReq(args, "x1_m")), MToFt(GetDReq(args, "y1_m")), 0);
            var p2 = new XYZ(MToFt(GetDReq(args, "x2_m")), MToFt(GetDReq(args, "y2_m")), 0);
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Detail)
                ?? throw new Exception("Nenhum ViewFamilyType de Detail disponível.");
            using (var tx = new Transaction(doc, "MCP: Callout"))
            {
                tx.Start();
                var co = ViewSection.CreateCallout(doc, view.Id, vft.Id, p1, p2);
                tx.Commit();
                return new { id = co.Id.Value, nome = co.Name };
            }
        }

        // =====================================================================
        // ONDA 3 — MODELAGEM AVANÇADA
        // =====================================================================

        /// <summary>Cria vista de corte 3D orientada a partir de dois pontos (mesmo que section_mark).</summary>
        private static object CreateSectionView(UIApplication uiApp, JsonElement args)
        {
            return CreateSectionMark(uiApp, args);
        }

        /// <summary>Cria eixo estrutural (Grid) entre dois pontos.</summary>
        private static object CreateGrid(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var p1 = new XYZ(MToFt(GetDReq(args, "x1_m")), MToFt(GetDReq(args, "y1_m")), 0);
            var p2 = new XYZ(MToFt(GetDReq(args, "x2_m")), MToFt(GetDReq(args, "y2_m")), 0);
            string nome = GetS(args, "name");
            using (var tx = new Transaction(doc, "MCP: Grid"))
            {
                tx.Start();
                var g = Grid.Create(doc, Line.CreateBound(p1, p2));
                if (!string.IsNullOrEmpty(nome)) { try { g.Name = nome; } catch { } }
                tx.Commit();
                return new { id = g.Id.Value, nome = g.Name };
            }
        }

        /// <summary>Cria abertura retangular numa parede (por ID) ou verticalmente entre dois pontos.</summary>
        private static object CreateOpening(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "wall_id");
            var host = doc.GetElement(id) as Wall ?? throw new Exception("wall_id não é uma parede.");
            double bottom = GetD(args, "bottom_m", 0);
            double top    = GetD(args, "top_m", 2.1);
            double x1 = GetDReq(args, "x1_m"), y1 = GetDReq(args, "y1_m");
            double x2 = GetDReq(args, "x2_m"), y2 = GetDReq(args, "y2_m");
            var pA = new XYZ(MToFt(x1), MToFt(y1), MToFt(bottom));
            var pB = new XYZ(MToFt(x2), MToFt(y2), MToFt(top));
            using (var tx = new Transaction(doc, "MCP: Opening"))
            {
                tx.Start();
                var op = doc.Create.NewOpening(host, pA, pB);
                tx.Commit();
                return new { id = op.Id.Value };
            }
        }

        /// <summary>Cria parede cortina (curtain wall) entre dois pontos.</summary>
        private static object CreateCurtainWall(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nenhum nível encontrado.");
            var p1 = new XYZ(MToFt(GetDReq(args, "x1_m")), MToFt(GetDReq(args, "y1_m")), level.Elevation);
            var p2 = new XYZ(MToFt(GetDReq(args, "x2_m")), MToFt(GetDReq(args, "y2_m")), level.Elevation);
            double h = GetD(args, "height_m", 3.0);
            var cw = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .FirstOrDefault(t => t.Kind == WallKind.Curtain)
                ?? throw new Exception("Nenhum tipo de curtain wall disponível.");
            using (var tx = new Transaction(doc, "MCP: Curtain wall"))
            {
                tx.Start();
                var w = Wall.Create(doc, Line.CreateBound(p1, p2), cw.Id, level.Id, MToFt(h), 0.0, false, false);
                tx.Commit();
                return new { id = w.Id.Value, tipo = cw.Name };
            }
        }

        /// <summary>Cria superfície topográfica a partir de pontos [{x,y,z}, ...].</summary>
        private static object CreateTopography(UIApplication uiApp, JsonElement args)
        {
            // Mantem compatibilidade de nome, mas usa a API atual Toposolid.
            return CreateToposolid(uiApp, args);
        }

        /// <summary>Lista tipos de terreno moderno Toposolid disponíveis no documento.</summary>
        private static object ListToposolidTypes(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            return new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>()
                .OrderBy(t => t.Name).Select(t => new { id = t.Id.Value, nome = t.Name }).ToList();
        }

        /// <summary>Cria Toposolid com a API moderna do Revit 2025.</summary>
        private static object CreateToposolid(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!args.TryGetProperty("points", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Array 'points' com objetos {x_m,y_m,z_m} obrigatorio.");
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nenhum nivel encontrado.");
            // z_m é relativo ao nível solicitado, como em pisos, paredes e demais ferramentas MCP.
            var points = arr.EnumerateArray().Select(p => new XYZ(
                MToFt(GetDReq(p, "x_m")), MToFt(GetDReq(p, "y_m")), level.Elevation + MToFt(GetD(p, "z_m", 0)))).ToList();
            if (points.Count < 3) throw new ArgumentException("Toposolid requer ao menos 3 pontos.");
            string typeName = GetS(args, "toposolid_type");
            var types = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().ToList();
            var type = string.IsNullOrWhiteSpace(typeName) ? types.FirstOrDefault() :
                types.FirstOrDefault(t => t.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);
            if (type == null) throw new Exception("Nenhum ToposolidType disponivel. Carregue ou crie um tipo de terreno no template.");
            Toposolid result;
            using (var tx = new Transaction(doc, "MCP: Criar Toposolid"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                result = Toposolid.Create(doc, points, type.Id, level.Id);
                tx.Commit();
            }
            return new { id = result.Id.Value, tipo = type.Name, nivel = level.Name, pontos = points.Count };
        }

        /// <summary>Cria platô (building pad) sobre topografia num retângulo.</summary>
        private static object CreateBuildingPad(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nenhum nível encontrado.");
            double x1 = GetDReq(args, "x1_m"), y1 = GetDReq(args, "y1_m");
            double x2 = GetDReq(args, "x2_m"), y2 = GetDReq(args, "y2_m");
            var loop = new CurveLoop();
            var pts = new[] {
                new XYZ(MToFt(x1), MToFt(y1), level.Elevation),
                new XYZ(MToFt(x2), MToFt(y1), level.Elevation),
                new XYZ(MToFt(x2), MToFt(y2), level.Elevation),
                new XYZ(MToFt(x1), MToFt(y2), level.Elevation)
            };
            for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
            var padType = new FilteredElementCollector(doc).OfClass(typeof(BuildingPadType)).FirstElementId();
            if (padType == ElementId.InvalidElementId) throw new Exception("Nenhum tipo de BuildingPad disponível.");
            using (var tx = new Transaction(doc, "MCP: Building pad"))
            {
                tx.Start();
                var pad = BuildingPad.Create(doc, padType, level.Id, new List<CurveLoop> { loop });
                tx.Commit();
                return new { id = pad.Id.Value };
            }
        }

        /// <summary>Cria SlabEdge (rodapé de laje) em torno de aresta de piso — atalho: cria piso fino.</summary>
        private static object CreateSlabEdge(UIApplication uiApp, JsonElement args)
        {
            // API SlabEdge exige host edges — implementação simplificada: cria uma linha decorativa.
            return CreateDetailLine(uiApp, args);
        }

        /// <summary>Cria forro (mesmo que create_ceiling, existente); wrapper para alinhamento com roadmap.</summary>
        private static object CreateCeilingPlan(UIApplication uiApp, JsonElement args)
        {
            return CreateCeiling(uiApp, args);
        }

        /// <summary>Ajusta parâmetros de um telhado existente (ex: inclinação).</summary>
        private static object ModifyRoof(UIApplication uiApp, JsonElement args)
        {
            return ModifyParameters(uiApp, args);
        }

        // =====================================================================
        // ONDA 4 — COMPONENTES
        // =====================================================================

        /// <summary>Posiciona instância de família filtrada por categoria (louças, plantas, carros, etc).</summary>
        private static object PlaceByCategory(UIApplication uiApp, JsonElement args, BuiltInCategory cat)
        {
            var doc = RequireDoc(uiApp);
            string nomeContem = GetS(args, "family_name");
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nenhum nível encontrado.");
            var pos = new XYZ(MToFt(GetD(args, "x_m", 0)),
                              MToFt(GetD(args, "y_m", 0)),
                              level.Elevation + MToFt(GetD(args, "z_m", 0)));

            var candidatos = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category?.Id.Value == (long)cat).ToList();
            if (candidatos.Count == 0) throw new Exception($"Nenhuma família da categoria {cat} carregada. Use load_family primeiro.");

            FamilySymbol sym;
            if (!string.IsNullOrEmpty(nomeContem))
            {
                var alvo = Normalizar(nomeContem);
                sym = candidatos.FirstOrDefault(s => Normalizar(s.Family.Name + " " + s.Name).Contains(alvo)) ?? candidatos.First();
            }
            else sym = candidatos.First();

            using (var tx = new Transaction(doc, "MCP: Place " + cat))
            {
                tx.Start();
                if (!sym.IsActive) sym.Activate();
                var inst = doc.Create.NewFamilyInstance(pos, sym, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                tx.Commit();
                return new { id = inst.Id.Value, familia = sym.Family.Name, tipo = sym.Name };
            }
        }

        /// <summary>Bancada de cozinha simplificada: caixa DirectShape sobre nível.</summary>
        private static object PlaceKitchenCounter(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nenhum nível.");
            double x = GetD(args, "x_m", 0), y = GetD(args, "y_m", 0);
            double compr = GetD(args, "length_m", 2.4), prof = GetD(args, "depth_m", 0.6), alt = GetD(args, "height_m", 0.9);
            var min = new XYZ(MToFt(x), MToFt(y), level.Elevation);
            var max = new XYZ(MToFt(x + compr), MToFt(y + prof), level.Elevation + MToFt(alt));
            using (var tx = new Transaction(doc, "MCP: Bancada"))
            {
                tx.Start();
                var geom = CriarSolidoCaixa(min, max);
                var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_Casework));
                ds.SetShape(new List<GeometryObject> { geom });
                tx.Commit();
                return new { id = ds.Id.Value };
            }
        }

        /// <summary>Piscina simplificada: piso rebaixado + 4 paredes finas ao redor.</summary>
        private static object PlacePool(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var level = FindLevel(doc, GetS(args, "level")) ?? throw new Exception("Nenhum nível.");
            double x = GetD(args, "x_m", 0), y = GetD(args, "y_m", 0);
            double w = GetD(args, "width_m", 4), l = GetD(args, "length_m", 8), d = GetD(args, "depth_m", 1.5);
            var minZ = level.Elevation - MToFt(d);
            var min = new XYZ(MToFt(x), MToFt(y), minZ);
            var max = new XYZ(MToFt(x + l), MToFt(y + w), level.Elevation);
            using (var tx = new Transaction(doc, "MCP: Piscina"))
            {
                tx.Start();
                var geom = CriarSolidoCaixa(min, max);
                var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                ds.SetShape(new List<GeometryObject> { geom });
                try { ds.Name = "Piscina"; } catch { }
                tx.Commit();
                return new { id = ds.Id.Value, largura_m = w, comprimento_m = l, profundidade_m = d };
            }
        }

        /// <summary>Helper: cria sólido em forma de caixa entre dois pontos opostos.</summary>
        private static Solid CriarSolidoCaixa(XYZ min, XYZ max)
        {
            var profile = new CurveLoop();
            var p0 = new XYZ(min.X, min.Y, min.Z);
            var p1 = new XYZ(max.X, min.Y, min.Z);
            var p2 = new XYZ(max.X, max.Y, min.Z);
            var p3 = new XYZ(min.X, max.Y, min.Z);
            profile.Append(Line.CreateBound(p0, p1));
            profile.Append(Line.CreateBound(p1, p2));
            profile.Append(Line.CreateBound(p2, p3));
            profile.Append(Line.CreateBound(p3, p0));
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { profile }, XYZ.BasisZ, max.Z - min.Z);
        }

        // =====================================================================
        // ONDA 5 — PRANCHAS
        // =====================================================================

        /// <summary>Cria uma nova prancha (folha) com selo padrão se disponível.</summary>
        private static object CreateSheet(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string numero = GetS(args, "number", "A-001");
            string nome   = GetS(args, "name", "Planta Geral");
            var tb = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>().FirstOrDefault(s => s.Category?.Id.Value == (long)BuiltInCategory.OST_TitleBlocks);
            using (var tx = new Transaction(doc, "MCP: Prancha"))
            {
                tx.Start();
                if (tb != null && !tb.IsActive) tb.Activate();
                var sheet = tb != null ? ViewSheet.Create(doc, tb.Id) : ViewSheet.Create(doc, ElementId.InvalidElementId);
                try { sheet.SheetNumber = numero; } catch { }
                try { sheet.Name = nome; } catch { }
                tx.Commit();
                return new { id = sheet.Id.Value, numero = sheet.SheetNumber, nome = sheet.Name };
            }
        }

        /// <summary>Posiciona uma vista existente numa prancha.</summary>
        private static object PlaceViewOnSheet(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var sheetId = GetElementIdReq(args, "sheet_id");
            var viewId  = GetElementIdReq(args, "view_id");
            var p = new XYZ(MToFt(GetD(args, "x_m", 0.2)), MToFt(GetD(args, "y_m", 0.2)), 0);
            using (var tx = new Transaction(doc, "MCP: Viewport"))
            {
                tx.Start();
                var vp = Viewport.Create(doc, sheetId, viewId, p);
                tx.Commit();
                return new { id = vp.Id.Value };
            }
        }

        /// <summary>Cria tabela (schedule) para uma categoria (ex: OST_Doors, OST_Windows, OST_Rooms).</summary>
        private static object CreateSchedule(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string catStr = GetS(args, "category") ?? throw new ArgumentException("Argumento 'category' obrigatório (ex: Doors, Windows, Rooms).");
            if (!Enum.TryParse<BuiltInCategory>("OST_" + catStr, true, out var bic))
                throw new Exception($"Categoria desconhecida: {catStr}");
            using (var tx = new Transaction(doc, "MCP: Schedule"))
            {
                tx.Start();
                var sched = ViewSchedule.CreateSchedule(doc, new ElementId(bic));
                tx.Commit();
                return new { id = sched.Id.Value, nome = sched.Name };
            }
        }

        /// <summary>Cria vista Legenda (duplica primeira legenda existente).</summary>
        private static object CreateLegend(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var leg = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.ViewType == ViewType.Legend);
            if (leg == null) throw new Exception("Este template não tem legenda base para duplicar.");
            using (var tx = new Transaction(doc, "MCP: Legenda"))
            {
                tx.Start();
                var novo = doc.GetElement(leg.Duplicate(ViewDuplicateOption.Duplicate));
                try { novo.Name = GetS(args, "name", "Legenda MCP"); } catch { }
                tx.Commit();
                return new { id = novo.Id.Value };
            }
        }

        /// <summary>Aplica esquema de cores em ambientes (Color Fill) — usa primeiro esquema disponível.</summary>
        private static object CreateColorFill(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var esq = new FilteredElementCollector(doc).OfClass(typeof(ColorFillScheme))
                .Cast<ColorFillScheme>()
                .FirstOrDefault(c => c.CategoryId.Value == (long)BuiltInCategory.OST_Rooms);
            if (esq == null) throw new Exception("Nenhum ColorFillScheme para Ambientes disponível.");
            using (var tx = new Transaction(doc, "MCP: Color fill"))
            {
                tx.Start();
                view.SetColorFillSchemeId(new ElementId(BuiltInCategory.OST_Rooms), esq.Id);
                tx.Commit();
                return new { esquema = esq.Name };
            }
        }

        /// <summary>Insere símbolo de norte (Símbolo Genérico de Anotação) na vista ativa.</summary>
        private static object CreateNorthArrow(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var sym = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => Normalizar(s.Family.Name + s.Name).Contains("norte") ||
                                     Normalizar(s.Family.Name + s.Name).Contains("north"));
            if (sym == null) throw new Exception("Nenhuma família de Norte carregada. Use load_family para carregar 'Simbolo de Norte'.");
            var p = new XYZ(MToFt(GetD(args, "x_m", 0)), MToFt(GetD(args, "y_m", 0)), 0);
            using (var tx = new Transaction(doc, "MCP: Norte"))
            {
                tx.Start();
                if (!sym.IsActive) sym.Activate();
                var inst = doc.Create.NewFamilyInstance(p, sym, view);
                tx.Commit();
                return new { id = inst.Id.Value };
            }
        }

        // =====================================================================
        // ONDA 6 — EXPORTAÇÃO
        // =====================================================================

        /// <summary>Exporta a vista ativa (ou lista de views) em PDF.</summary>
        private static object ExportPdf(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string folder = GetS(args, "folder") ?? Path.GetTempPath();
            string name   = GetS(args, "name", "export_mcp");
            var viewIds = new List<ElementId> { uiApp.ActiveUIDocument.ActiveView.Id };
            var opts = new PDFExportOptions { FileName = name, Combine = true };
            doc.Export(folder, viewIds, opts);
            return new { pasta = folder, nome = name + ".pdf" };
        }

        /// <summary>Exporta a vista ativa em DWG.</summary>
        private static object ExportDwg(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string folder = GetS(args, "folder") ?? Path.GetTempPath();
            string name   = GetS(args, "name", "export_mcp.dwg");
            var viewIds = new List<ElementId> { uiApp.ActiveUIDocument.ActiveView.Id };
            var opts = new DWGExportOptions();
            doc.Export(folder, name, viewIds, opts);
            return new { pasta = folder, nome = name };
        }

        /// <summary>Exporta o modelo em IFC com perfil explícito de interoperabilidade.</summary>
        private static object ExportIfc(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string folder = GetS(args, "folder") ?? Path.GetTempPath();
            string name   = GetS(args, "name", "export_mcp.ifc");
            var opts = new IFCExportOptions();
            string profile = (GetS(args, "profile", "coordination") ?? "coordination").ToLowerInvariant();
            bool quantities = args.TryGetProperty("base_quantities", out var qv) ? qv.ValueKind == JsonValueKind.True : true;
            int boundaries = (int)GetD(args, "space_boundaries", profile == "coordination" ? 0 : 2);
            if (boundaries < 0 || boundaries > 2) throw new ArgumentException("space_boundaries deve ser 0, 1 ou 2.");
            if (profile != "coordination" && profile != "architecture" && profile != "quantities")
                throw new ArgumentException("profile deve ser coordination, architecture ou quantities.");
            Directory.CreateDirectory(folder);
            opts.AddOption("ExportBaseQuantities", quantities ? "true" : "false");
            opts.AddOption("SpaceBoundaries", boundaries.ToString());
            opts.AddOption("ExportInternalRevitPropertySets", "true");
            opts.AddOption("ExportIFCCommonPropertySets", "true");
            opts.AddOption("Export2DElements", profile == "architecture" ? "true" : "false");
            opts.AddOption("SplitWallsAndColumns", "false");
            using (var tx = new Transaction(doc, "MCP: IFC"))
            {
                tx.Start();
                doc.Export(folder, name, opts);
                tx.Commit();
            }
            return new { pasta = folder, nome = name, profile, base_quantities = quantities, space_boundaries = boundaries };
        }

        // =====================================================================
        // ONDA 7 — CONFIGURAÇÃO
        // =====================================================================

        /// <summary>Aplica template de vista à vista ativa (por ID de template).</summary>
        private static object SetViewTemplate(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var tplId = GetElementIdReq(args, "template_id");
            using (var tx = new Transaction(doc, "MCP: View template"))
            {
                tx.Start();
                view.ViewTemplateId = tplId;
                tx.Commit();
            }
            return new { view = view.Name, template_id = tplId.Value };
        }

        /// <summary>Controla junção de parede (allow/disallow) na extremidade 0 ou 1.</summary>
        private static object SetWallJoin(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var wall = doc.GetElement(GetElementIdReq(args, "wall_id")) as Wall
                ?? throw new Exception("wall_id não é uma parede.");
            int end = (int)GetD(args, "end", 0);
            bool allow = GetS(args, "mode", "allow") == "allow";
            using (var tx = new Transaction(doc, "MCP: Wall join"))
            {
                tx.Start();
                if (allow) WallUtils.AllowWallJoinAtEnd(wall, end);
                else       WallUtils.DisallowWallJoinAtEnd(wall, end);
                tx.Commit();
            }
            return new { wall = wall.Id.Value, end, modo = allow ? "allow" : "disallow" };
        }

        /// <summary>Altera a espessura de linha (line weight) de uma categoria — projeção.</summary>
        private static object SetLineStyle(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string catStr = GetS(args, "category") ?? throw new ArgumentException("Argumento 'category' obrigatório (ex: Walls)");
            int weight = (int)GetD(args, "weight", 3);
            if (!Enum.TryParse<BuiltInCategory>("OST_" + catStr, true, out var bic))
                throw new Exception($"Categoria desconhecida: {catStr}");
            var cat = doc.Settings.Categories.get_Item(bic);
            using (var tx = new Transaction(doc, "MCP: Line style"))
            {
                tx.Start();
                cat.SetLineWeight(weight, GraphicsStyleType.Projection);
                tx.Commit();
            }
            return new { categoria = catStr, weight };
        }

        /// <summary>Importa arquivo CAD (DWG/DXF) na vista ativa.</summary>
        private static object ImportCad(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            string path = GetS(args, "path") ?? throw new ArgumentException("Argumento 'path' obrigatório");
            if (!File.Exists(path)) throw new Exception($"Arquivo não existe: {path}");
            var opts = new DWGImportOptions
            {
                Placement = ImportPlacement.Origin,
                ColorMode = ImportColorMode.Preserved,
                Unit = ImportUnit.Meter
            };
            ElementId idOut;
            using (var tx = new Transaction(doc, "MCP: Import CAD"))
            {
                tx.Start();
                doc.Import(path, opts, view, out idOut);
                tx.Commit();
            }
            return new { id = idOut.Value, caminho = path };
        }

        /// <summary>Revisao somente leitura para uso antes de salvar ou exportar.</summary>
        private static object ValidateDocument(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var warnings = doc.GetWarnings().Select(w => new {
                severidade = w.GetSeverity().ToString(), descricao = w.GetDescriptionText(),
                ids = w.GetFailingElements().Select(i => i.Value).ToList() }).ToList();
            int levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();
            int walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();
            int doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().GetElementCount();
            int windows = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().GetElementCount();
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Cast<Room>().ToList();
            var issues = new List<object>();
            bool hasFatal = levels == 0;
            if (hasFatal) issues.Add(new { severidade = "erro", codigo = "SEM_NIVEL", descricao = "O documento nao possui niveis." });
            if (walls == 0) issues.Add(new { severidade = "aviso", codigo = "SEM_PAREDES", descricao = "Nao ha paredes no documento." });
            if (rooms.Any(r => r.Area <= 0)) issues.Add(new { severidade = "aviso", codigo = "AMBIENTE_NAO_POSICIONADO", descricao = "Ha ambientes nao posicionados ou sem fechamento.", quantidade = rooms.Count(r => r.Area <= 0) });
            if (warnings.Count > 0) issues.Add(new { severidade = "aviso", codigo = "AVISOS_REVIT", descricao = "O Revit reportou avisos para revisao.", quantidade = warnings.Count });
            return new {
                status = hasFatal ? "erro" : (issues.Count == 0 ? "aprovado" : "revisar"),
                resumo = new { niveis = levels, paredes = walls, portas = doors, janelas = windows, ambientes = rooms.Count },
                avisos_revit = warnings, problemas = issues
            };
        }

        /// <summary>
        /// Auditoria BIM de leitura: complementa validate_document com sinais de
        /// qualidade que um arquiteto precisa revisar antes de emitir pranchas.
        /// Não interpreta norma local; aponta dados objetivos do modelo.
        /// </summary>
        private static object AnalyzeBimQuality(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().Cast<Room>().ToList();
            var roomTags = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsNotElementType().GetElementCount();
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().ToList();
            var duplicateRoomNumbers = rooms.Where(r => !string.IsNullOrWhiteSpace(r.Number))
                .GroupBy(r => r.Number).Where(g => g.Count() > 1).Select(g => new { numero = g.Key, ids = g.Select(r => r.Id.Value).ToList() }).ToList();
            var duplicateSheetNumbers = sheets.Where(s => !string.IsNullOrWhiteSpace(s.SheetNumber))
                .GroupBy(s => s.SheetNumber).Where(g => g.Count() > 1).Select(g => new { numero = g.Key, ids = g.Select(s => s.Id.Value).ToList() }).ToList();
            var wallKeys = new Dictionary<string, List<long>>();
            foreach (var wall in walls)
            {
                var line = (wall.Location as LocationCurve)?.Curve as Line;
                if (line == null) continue;
                var a = line.GetEndPoint(0); var b = line.GetEndPoint(1);
                string PointKey(XYZ p) => $"{Math.Round(FtToM(p.X), 3):F3},{Math.Round(FtToM(p.Y), 3):F3}";
                var p1 = PointKey(a); var p2 = PointKey(b);
                var key = string.CompareOrdinal(p1, p2) <= 0 ? p1 + "|" + p2 : p2 + "|" + p1;
                if (!wallKeys.TryGetValue(key, out var ids)) wallKeys[key] = ids = new List<long>();
                ids.Add(wall.Id.Value);
            }
            var duplicatedWalls = wallKeys.Where(kv => kv.Value.Count > 1)
                .Select(kv => new { ids = kv.Value, geometria = kv.Key }).ToList();
            var issues = new List<object>();
            bool hasError = duplicateRoomNumbers.Count > 0 || duplicateSheetNumbers.Count > 0;
            if (rooms.Any(r => r.Area <= 0)) issues.Add(new { codigo = "AMBIENTE_SEM_AREA", severidade = "aviso", quantidade = rooms.Count(r => r.Area <= 0) });
            if (duplicateRoomNumbers.Count > 0) issues.Add(new { codigo = "NUMERO_AMBIENTE_DUPLICADO", severidade = "erro", itens = duplicateRoomNumbers });
            if (duplicateSheetNumbers.Count > 0) issues.Add(new { codigo = "NUMERO_PRANCHA_DUPLICADO", severidade = "erro", itens = duplicateSheetNumbers });
            if (duplicatedWalls.Count > 0) issues.Add(new { codigo = "PAREDE_GEOMETRIA_DUPLICADA", severidade = "aviso", itens = duplicatedWalls });
            if (rooms.Count > roomTags) issues.Add(new { codigo = "AMBIENTES_SEM_TAG_POTENCIAL", severidade = "aviso", quantidade = rooms.Count - roomTags });
            return new {
                status = hasError ? "reprovar" : (issues.Count == 0 ? "aprovado" : "revisar"),
                resumo = new { ambientes = rooms.Count, tags_ambiente = roomTags, pranchas = sheets.Count, paredes = walls.Count },
                problemas = issues,
                escopo = "Qualidade BIM objetiva; normas municipais e NBR exigem regra configurada e revisão técnica."
            };
        }

        /// <summary>Expõe o estado de colaboração sem tentar sincronizar ou tomar posse de elementos.</summary>
        /// <summary>
        /// Confere o programa de ambientes contra regras fornecidas pelo usuário.
        /// Não presume requisitos de código local ou NBR: cada regra aceita
        /// name_contains, count_min e min_area_m2.
        /// </summary>
        private static object ValidateRoomProgram(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            if (!args.TryGetProperty("requirements", out var requirements) || requirements.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Informe requirements como lista de regras de ambiente.");

            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().Cast<Room>().ToList();
            var checks = new List<object>();
            var issues = new List<object>();
            int index = 0;
            foreach (var rule in requirements.EnumerateArray())
            {
                index++;
                if (rule.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException($"requirements[{index - 1}] deve ser um objeto.");
                var nameContains = GetS(rule, "name_contains", "");
                var label = GetS(rule, "label", string.IsNullOrWhiteSpace(nameContains) ? $"regra_{index}" : nameContains);
                int countMin = Math.Max(0, (int)GetD(rule, "count_min", 1));
                double minArea = Math.Max(0, GetD(rule, "min_area_m2", 0));
                if (string.IsNullOrWhiteSpace(nameContains))
                    throw new ArgumentException($"requirements[{index - 1}].name_contains é obrigatório.");

                var matches = rooms.Where(r => Normalizar(r.Name ?? "").Contains(Normalizar(nameContains))).ToList();
                var qualified = matches.Where(r => r.Area > 0 && SqFtToM2(r.Area) >= minArea).ToList();
                var matchInfo = matches.Select(r => new {
                    id = r.Id.Value, nome = r.Name, numero = r.Number,
                    area_m2 = Math.Round(SqFtToM2(r.Area), 2), posicionado = r.Area > 0
                }).ToList();
                bool countOk = matches.Count >= countMin;
                bool areaOk = qualified.Count >= countMin;
                if (!countOk) issues.Add(new { codigo = "AMBIENTE_AUSENTE", regra = label, esperado = countMin, encontrado = matches.Count });
                else if (!areaOk) issues.Add(new { codigo = "AREA_MINIMA_NAO_ATENDIDA", regra = label, area_minima_m2 = minArea, ambientes = matchInfo });
                checks.Add(new {
                    regra = label, contem_nome = nameContains, quantidade_minima = countMin,
                    area_minima_m2 = minArea, encontrado = matches.Count,
                    conforme = countOk && areaOk, ambientes = matchInfo
                });
            }
            return new {
                status = issues.Count == 0 ? "aprovado" : "revisar",
                regras = checks, problemas = issues,
                escopo = "Regras configuradas pelo usuário; não substitui verificação de legislação local ou responsabilidade técnica."
            };
        }

        /// <summary>
        /// Confere a prontidão documental a partir de vistas, pranchas e viewports.
        /// Não cria pranchas nem altera a publicação.
        /// </summary>
        private static object AuditDocumentation(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            var placedViewIds = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Viewport))
                .Cast<Viewport>().Select(vp => vp.ViewId.Value));
            var printableViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && !(v is ViewSheet) && v.CanBePrinted)
                .ToList();
            var unplaced = printableViews.Where(v => !placedViewIds.Contains(v.Id.Value))
                .Select(v => new { id = v.Id.Value, nome = v.Name, tipo = v.ViewType.ToString() })
                .OrderBy(v => v.tipo).ThenBy(v => v.nome).Take(100).ToList();
            var duplicatedSheets = sheets.GroupBy(s => s.SheetNumber).Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
                .Select(g => new { numero = g.Key, ids = g.Select(s => s.Id.Value).ToList() }).ToList();
            var issues = new List<object>();
            if (sheets.Count == 0) issues.Add(new { codigo = "SEM_PRANCHA", severidade = "aviso", descricao = "Não há pranchas para emissão." });
            if (duplicatedSheets.Count > 0) issues.Add(new { codigo = "NUMERO_PRANCHA_DUPLICADO", severidade = "erro", itens = duplicatedSheets });
            if (unplaced.Count > 0) issues.Add(new { codigo = "VISTAS_NAO_POSICIONADAS", severidade = "aviso", quantidade = unplaced.Count });
            return new {
                status = duplicatedSheets.Count > 0 ? "reprovar" : (issues.Count == 0 ? "aprovado" : "revisar"),
                resumo = new { pranchas = sheets.Count, vistas_imprimiveis = printableViews.Count, vistas_em_prancha = placedViewIds.Count, vistas_nao_posicionadas = unplaced.Count },
                vistas_nao_posicionadas = unplaced, problemas = issues,
                escopo = "Auditoria de emissão objetiva; a composição gráfica e os padrões do escritório ainda exigem revisão humana."
            };
        }

        private static object GetWorksharingStatus(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            if (!doc.IsWorkshared)
                return new { workshared = false, caminho = doc.PathName, recomendacao = "Modelo individual: use backup_document antes de lotes e habilite worksharing no Revit se houver equipe." };
            var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets()
                .Select(w => new { id = w.Id.IntegerValue, nome = w.Name, visivel_padrao = w.IsVisibleByDefault }).ToList();
            return new { workshared = true, caminho = doc.PathName, worksets, recomendacao = "Não sincronize nem tome posse automaticamente; execute alterações somente com a política de worksets da equipe." };
        }

        /// <summary>
        /// Configuração única para operações MCP que podem gerar avisos do
        /// Revit. Avisos são registrados no log do documento e descartados para
        /// que uma caixa modal não interrompa uma automação; erros reais seguem
        /// a política normal da transação e retornam à chamada MCP.
        /// </summary>
        /// <summary>
        /// Consulta responsável, criador e último autor de um elemento workshared.
        /// Jamais tenta reservar, editar ou sincronizar o elemento.
        /// </summary>
        private static object GetElementWorksharingInfo(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "element_id");
            var element = doc.GetElement(id) ?? throw new ArgumentException($"Elemento {id.Value} não encontrado.");
            if (!doc.IsWorkshared)
                return new { workshared = false, id = id.Value, nome = element.Name, recomendacao = "Modelo individual: não há proprietário de worksharing." };
            var info = WorksharingUtils.GetWorksharingTooltipInfo(doc, id);
            return new {
                workshared = true, id = id.Value, nome = element.Name, categoria = element.Category?.Name,
                criador = info.Creator, proprietario = info.Owner, alterado_por_ultimo = info.LastChangedBy,
                recomendacao = "Informação somente leitura. Não tome posse automaticamente; respeite o fluxo da equipe."
            };
        }

        private static void ConfigureAutomaticWarningHandling(Transaction tx)
        {
            var options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new McpWarningPreprocessor());
            options.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(options);
        }
    }

    // =========================================================================
    // Tratador de falhas do StairsEditScope
    // =========================================================================

    /// <summary>
    /// O StairsEditScope.Commit exige um IFailuresPreprocessor. Este apenas
    /// deixa o Revit prosseguir com avisos não-críticos (ex: escada fora do
    /// alinhamento ideal), evitando que o commit falhe por advertências.
    /// </summary>
    public class StairsFailurePreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            return FailureProcessingResult.Continue;
        }
    }

    /// <summary>
    /// Evita que advertências do Revit bloqueiem chamadas MCP com caixas modais.
    /// Erros continuam cancelando a transação: somente FailureSeverity.Warning é descartado.
    /// </summary>
    public class McpWarningPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    accessor.DeleteWarning(failure);
            }
            return FailureProcessingResult.Continue;
        }
    }
}
