// ============================================================================
// CommandsExtra.cs — Lacunas de cobertura do catálogo de ações (rodada MCP+1010)
// ============================================================================
//
// Complementa Commands.cs / CommandsPro.cs / CommandsArch.cs com capacidades
// que o catálogo de ações aponta como ausentes e que não duplicam nenhuma
// ferramenta já existente:
//
//   • Geometria 2D: arco, círculo, elipse e polígono de detalhe; linha de
//     modelo (3D, diferente da linha de detalhe 2D); região mascarada.
//   • Transformações: desagrupar instância de grupo; copiar parâmetros entre
//     elementos compatíveis.
//   • Níveis, eixos e coordenadas: renomear nível, alterar elevação, criar
//     plano de referência, mover o Ponto Base do Projeto/Levantamento,
//     auditoria de níveis e eixos (nomes/elevações duplicadas).
//   • Estilos de linha e padrões de preenchimento: listagem.
//   • Parâmetros: leitura por GUID de parâmetro compartilhado.
//   • Links: caminho, status de carregamento e transformação de um vínculo.
//   • Consultas espaciais: bounding box, elementos próximos a um ponto,
//     elementos dentro de uma caixa 3D.
//   • Governança: validação de nomes por regex/caracteres proibidos/tamanho;
//     inventário completo do modelo (contagens por categoria e metadados).
//
// ⚠️ Código novo escrito sem acesso a uma instância real do Revit nesta
// sessão. A sintaxe C# foi revisada linha a linha contra os comandos
// vizinhos, mas precisa de validação funcional em ambiente com Revit antes
// de ir para produção (ver docs/NOVAS_ACOES_IMPLEMENTADAS.md).
//
// Convenções iguais às dos demais arquivos: entradas/saídas em metros, m²,
// m³ e graus; toda escrita dentro de Transaction; coleções antigas da API
// (ParameterSet, Categories, PhaseArray) percorridas com foreach, nunca com
// LINQ encadeado direto (ver nota em CommandsArch.cs).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp
{
    public static partial class Commands
    {
        private static object ExecutarComandoExtra(UIApplication uiApp, RpcRequest req)
        {
            var a = req.args;
            switch (req.cmd)
            {
                // --- Geometria 2D e linha de modelo ---
                case "create_detail_arc":       return CreateDetailArc(uiApp, a);
                case "create_detail_circle":    return CreateDetailCircle(uiApp, a);
                case "create_detail_ellipse":   return CreateDetailEllipse(uiApp, a);
                case "create_detail_polygon":   return CreateDetailPolygon(uiApp, a);
                case "create_model_line":       return CreateModelLine(uiApp, a);
                case "create_masking_region":   return CreateMaskingRegion(uiApp, a);

                // --- Transformações ---
                case "ungroup_elements":        return UngroupElements(uiApp, a);
                case "copy_parameters":         return CopyParameters(uiApp, a);

                // --- Níveis, eixos e coordenadas ---
                case "rename_level":            return RenameLevel(uiApp, a);
                case "set_level_elevation":     return SetLevelElevation(uiApp, a);
                case "create_reference_plane":  return CreateReferencePlane(uiApp, a);
                case "set_project_base_point":  return SetProjectBasePoint(uiApp, a);
                case "audit_levels_grids":      return AuditLevelsGrids(uiApp);

                // --- Estilos de linha e padrões de preenchimento ---
                case "list_line_styles":        return ListLineStyles(uiApp);
                case "list_fill_patterns":      return ListFillPatterns(uiApp);

                // --- Parâmetros ---
                case "read_parameter_by_guid":  return ReadParameterByGuid(uiApp, a);

                // --- Links ---
                case "get_link_info":           return GetLinkInfo(uiApp, a);

                // --- Consultas espaciais e de geometria ---
                case "get_bounding_box":        return GetBoundingBox(uiApp, a);
                case "get_elements_near_point":  return GetElementsNearPoint(uiApp, a);
                case "get_elements_in_box":     return GetElementsInBox(uiApp, a);

                // --- Governança e diagnóstico ---
                case "validate_naming":         return ValidateNaming(uiApp, a);
                case "get_model_inventory":     return GetModelInventory(uiApp);

                default:
                    throw new ArgumentException($"Comando desconhecido: {req.cmd}");
            }
        }

        // =====================================================================
        // GEOMETRIA 2D DE DETALHE E LINHA DE MODELO
        // =====================================================================

        /// <summary>Cria arco de detalhe (2D) na vista ativa por centro, raio e ângulos em graus.</summary>
        private static object CreateDetailArc(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var center = new XYZ(MToFt(GetDReq(args, "center_x_m")), MToFt(GetDReq(args, "center_y_m")), 0);
            double radius = MToFt(GetDReq(args, "radius_m"));
            if (radius <= 0) throw new ArgumentException("radius_m deve ser positivo.");
            double startDeg = GetD(args, "start_angle_deg", 0);
            double endDeg = GetD(args, "end_angle_deg", 180);
            if (Math.Abs(endDeg - startDeg) < 1e-6) throw new ArgumentException("start_angle_deg e end_angle_deg não podem ser iguais.");
            if (endDeg < startDeg) { var tmp = startDeg; startDeg = endDeg; endDeg = tmp; }
            double startRad = startDeg * Math.PI / 180.0;
            double endRad = endDeg * Math.PI / 180.0;
            var arc = Arc.Create(center, radius, startRad, endRad, XYZ.BasisX, XYZ.BasisY);
            using (var tx = new Transaction(doc, "MCP: Arco de detalhe"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                var dc = doc.Create.NewDetailCurve(view, arc);
                tx.Commit();
                return new { id = dc.Id.Value, raio_m = Math.Round(FtToM(radius), 4) };
            }
        }

        /// <summary>Cria círculo de detalhe (2D) completo na vista ativa por centro e raio.</summary>
        private static object CreateDetailCircle(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var center = new XYZ(MToFt(GetDReq(args, "center_x_m")), MToFt(GetDReq(args, "center_y_m")), 0);
            double radius = MToFt(GetDReq(args, "radius_m"));
            if (radius <= 0) throw new ArgumentException("radius_m deve ser positivo.");
            // Arco com 0..2π e mesmo xAxis/yAxis forma uma curva fechada (início == fim),
            // técnica padrão da API do Revit para representar um círculo completo.
            var circle = Arc.Create(center, radius, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY);
            using (var tx = new Transaction(doc, "MCP: Círculo de detalhe"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                var dc = doc.Create.NewDetailCurve(view, circle);
                tx.Commit();
                return new { id = dc.Id.Value, raio_m = Math.Round(FtToM(radius), 4) };
            }
        }

        /// <summary>Cria elipse de detalhe (2D) completa na vista ativa.</summary>
        private static object CreateDetailEllipse(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var center = new XYZ(MToFt(GetDReq(args, "center_x_m")), MToFt(GetDReq(args, "center_y_m")), 0);
            double rx = MToFt(GetDReq(args, "radius_x_m"));
            double ry = MToFt(GetDReq(args, "radius_y_m"));
            if (rx <= 0 || ry <= 0) throw new ArgumentException("radius_x_m e radius_y_m devem ser positivos.");
            double rot = GetD(args, "rotation_deg", 0) * Math.PI / 180.0;
            var xAxis = new XYZ(Math.Cos(rot), Math.Sin(rot), 0);
            var yAxis = new XYZ(-Math.Sin(rot), Math.Cos(rot), 0);
            var ellipse = Ellipse.CreateCurve(center, rx, ry, xAxis, yAxis, 0, 2 * Math.PI);
            using (var tx = new Transaction(doc, "MCP: Elipse de detalhe"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                var dc = doc.Create.NewDetailCurve(view, ellipse);
                tx.Commit();
                return new { id = dc.Id.Value, raio_x_m = Math.Round(FtToM(rx), 4), raio_y_m = Math.Round(FtToM(ry), 4) };
            }
        }

        /// <summary>Cria polígono regular (3 a 24 lados) como linhas de detalhe fechadas na vista ativa.</summary>
        private static object CreateDetailPolygon(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            var center = new XYZ(MToFt(GetDReq(args, "center_x_m")), MToFt(GetDReq(args, "center_y_m")), 0);
            double radius = MToFt(GetDReq(args, "radius_m"));
            int sides = (int)GetD(args, "sides", 6);
            double rot = GetD(args, "rotation_deg", 0) * Math.PI / 180.0;
            if (radius <= 0) throw new ArgumentException("radius_m deve ser positivo.");
            if (sides < 3 || sides > 24) throw new ArgumentException("sides deve estar entre 3 e 24.");

            var pts = new List<XYZ>();
            for (int i = 0; i < sides; i++)
            {
                double ang = rot + 2 * Math.PI * i / sides;
                pts.Add(center + new XYZ(radius * Math.Cos(ang), radius * Math.Sin(ang), 0));
            }

            var ids = new List<long>();
            using (var tx = new Transaction(doc, "MCP: Polígono de detalhe"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                for (int i = 0; i < sides; i++)
                    ids.Add(doc.Create.NewDetailCurve(view, Line.CreateBound(pts[i], pts[(i + 1) % sides])).Id.Value);
                tx.Commit();
            }
            return new { ids, lados = sides, comprimento_lado_m = Math.Round(FtToM(pts[0].DistanceTo(pts[1])), 4) };
        }

        /// <summary>
        /// Cria linha de modelo (geometria 3D real, diferente da linha de detalhe 2D) entre
        /// dois pontos. Escolhe automaticamente um plano de trabalho vertical contendo a
        /// linha (ou, se a linha for vertical, um plano arbitrário perpendicular ao eixo X
        /// que a contém).
        /// </summary>
        private static object CreateModelLine(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var p1 = new XYZ(MToFt(GetDReq(args, "x1_m")), MToFt(GetDReq(args, "y1_m")), MToFt(GetD(args, "z1_m", 0)));
            var p2 = new XYZ(MToFt(GetDReq(args, "x2_m")), MToFt(GetDReq(args, "y2_m")), MToFt(GetD(args, "z2_m", 0)));
            if (p1.IsAlmostEqualTo(p2)) throw new ArgumentException("Os pontos da linha não podem coincidir.");
            var line = Line.CreateBound(p1, p2);
            var dir = (p2 - p1).Normalize();
            XYZ normal = Math.Abs(dir.Z) > 0.999999 ? XYZ.BasisX : dir.CrossProduct(XYZ.BasisZ).Normalize();
            var plane = Plane.CreateByNormalAndOrigin(normal, p1);
            using (var tx = new Transaction(doc, "MCP: Linha de modelo"))
            {
                tx.Start();
                ConfigureAutomaticWarningHandling(tx);
                var sketchPlane = SketchPlane.Create(doc, plane);
                var mc = doc.Create.NewModelCurve(line, sketchPlane);
                tx.Commit();
                return new { id = mc.Id.Value, comprimento_m = Math.Round(FtToM(line.Length), 4) };
            }
        }

        /// <summary>Cria região mascarada (oculta linhas atrás dela) num retângulo da vista ativa.</summary>
        private static object CreateMaskingRegion(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var view = uiApp.ActiveUIDocument.ActiveView;
            double x1 = GetDReq(args, "x1_m"), y1 = GetDReq(args, "y1_m");
            double x2 = GetDReq(args, "x2_m"), y2 = GetDReq(args, "y2_m");
            var pts = new List<XYZ> {
                new XYZ(MToFt(x1), MToFt(y1), 0), new XYZ(MToFt(x2), MToFt(y1), 0),
                new XYZ(MToFt(x2), MToFt(y2), 0), new XYZ(MToFt(x1), MToFt(y2), 0)
            };
            var loop = new CurveLoop();
            for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));

            var tipos = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
            var tipoMascara = tipos.FirstOrDefault(t => t.IsMasking);
            using (var tx = new Transaction(doc, "MCP: Região mascarada"))
            {
                tx.Start();
                if (tipoMascara == null)
                {
                    var tipoBase = tipos.FirstOrDefault()
                        ?? throw new Exception("Nenhum tipo de região preenchida disponível para duplicar.");
                    tipoMascara = tipoBase.Duplicate("Região mascarada MCP") as FilledRegionType;
                    tipoMascara.IsMasking = true;
                }
                var fr = FilledRegion.Create(doc, tipoMascara.Id, view.Id, new List<CurveLoop> { loop });
                tx.Commit();
                return new { id = fr.Id.Value, tipo = tipoMascara.Name };
            }
        }

        // =====================================================================
        // TRANSFORMAÇÕES
        // =====================================================================

        /// <summary>Desagrupa uma instância de grupo de modelo, liberando os elementos membros.</summary>
        private static object UngroupElements(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "group_id");
            var group = doc.GetElement(id) as Autodesk.Revit.DB.Group
                ?? throw new ArgumentException($"{id.Value} não é uma instância de grupo (Group). Use list_groups.");
            using (var tx = new Transaction(doc, "MCP: Desagrupar"))
            {
                tx.Start();
                var membros = group.UngroupMembers();
                tx.Commit();
                return new { ids_membros = membros.Select(m => m.Value).ToList(), quantidade = membros.Count };
            }
        }

        /// <summary>
        /// Copia valores de parâmetro de um elemento de origem para um ou mais elementos
        /// de destino. Sem 'parameters', copia todos os parâmetros de instância com valor
        /// e graváveis na origem, por nome, ignorando silenciosamente os que não existem
        /// no destino.
        /// </summary>
        private static object CopyParameters(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var sourceId = GetElementIdReq(args, "source_id");
            var source = doc.GetElement(sourceId) ?? throw new ArgumentException($"Elemento {sourceId.Value} não existe.");
            var targetIds = ProIds(args, "target_ids", required: true);
            var nomes = ProStrings(args, "parameters");

            // ParameterSet é coleção antiga da API: percorrida com foreach, nunca com LINQ direto.
            var parametrosOrigem = new List<Parameter>();
            if (nomes.Count > 0)
            {
                foreach (var nome in nomes)
                {
                    var p = source.LookupParameter(nome);
                    if (p != null) parametrosOrigem.Add(p);
                }
            }
            else
            {
                // IfcGUID e Número são excluídos do modo "copiar todos": são identificadores
                // que devem ser únicos por elemento (duplicar o IfcGUID quebra a integridade de
                // uma exportação IFC; duplicar o Número de ambiente/porta/etc. dispara o aviso
                // do Revit "elementos têm valores 'Número' duplicados"). Ainda podem ser
                // copiados se pedidos explicitamente por nome em 'parameters'.
                var excluidosPorPadrao = new HashSet<string> { "IfcGUID", "Número" };
                foreach (Parameter p in source.Parameters)
                    if (!p.IsReadOnly && p.HasValue && !excluidosPorPadrao.Contains(p.Definition?.Name))
                        parametrosOrigem.Add(p);
            }

            var resultado = new List<object>();
            using (var tx = new Transaction(doc, "MCP: Copiar parâmetros"))
            {
                tx.Start();
                foreach (var targetId in targetIds)
                {
                    var target = doc.GetElement(targetId);
                    if (target == null) { resultado.Add(new { id = targetId.Value, ok = false, motivo = "elemento não existe" }); continue; }
                    var copiados = new List<string>();
                    var erros = new List<string>();
                    foreach (var sp in parametrosOrigem)
                    {
                        var tp = target.LookupParameter(sp.Definition.Name);
                        if (tp == null) continue; // parâmetro não existe no destino: ignora
                        if (tp.IsReadOnly || tp.StorageType != sp.StorageType) { erros.Add(sp.Definition.Name); continue; }
                        try
                        {
                            switch (sp.StorageType)
                            {
                                case StorageType.String: tp.Set(sp.AsString()); break;
                                case StorageType.Integer: tp.Set(sp.AsInteger()); break;
                                case StorageType.Double: tp.Set(sp.AsDouble()); break;
                                case StorageType.ElementId: tp.Set(sp.AsElementId()); break;
                            }
                            copiados.Add(sp.Definition.Name);
                        }
                        catch (Exception ex) { erros.Add($"{sp.Definition.Name} ({ex.Message})"); }
                    }
                    resultado.Add(new { id = targetId.Value, ok = erros.Count == 0, copiados, erros });
                }
                tx.Commit();
            }
            return new { origem = sourceId.Value, elementos = resultado };
        }

        // =====================================================================
        // NÍVEIS, EIXOS E COORDENADAS
        // =====================================================================

        /// <summary>Resolve um Level por level_id ou level (nome); exige um dos dois quando requireExplicit.</summary>
        private static Level ResolveLevelArg(Document doc, JsonElement args, bool requireExplicit = false)
        {
            if (ProHas(args, "level_id", out var lv) && lv.ValueKind == JsonValueKind.Number)
            {
                var id = new ElementId(lv.GetInt64());
                return doc.GetElement(id) as Level ?? throw new ArgumentException($"level_id {id.Value} não é um nível.");
            }
            string name = GetS(args, "level");
            if (requireExplicit && string.IsNullOrEmpty(name))
                throw new ArgumentException("Informe level_id ou level (nome) para identificar o nível.");
            return FindLevel(doc, name) ?? throw new ArgumentException("Nível não encontrado. Use list_levels.");
        }

        /// <summary>Renomeia um nível, identificado por ID ou pelo nome atual.</summary>
        private static object RenameLevel(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string newName = GetS(args, "new_name") ?? throw new ArgumentException("Argumento 'new_name' obrigatório.");
            var level = ResolveLevelArg(doc, args, requireExplicit: true);
            string nomeAnterior = level.Name;
            using (var tx = new Transaction(doc, "MCP: Renomear nível"))
            {
                tx.Start();
                level.Name = newName;
                tx.Commit();
            }
            return new { id = level.Id.Value, nome_anterior = nomeAnterior, nome = level.Name };
        }

        /// <summary>Altera a elevação de um nível, de forma absoluta (elevation_m) ou relativa (offset_m).</summary>
        private static object SetLevelElevation(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var level = ResolveLevelArg(doc, args, requireExplicit: true);
            bool temAbsoluta = ProHas(args, "elevation_m", out var absVal) && absVal.ValueKind == JsonValueKind.Number;
            bool temRelativa = ProHas(args, "offset_m", out var offVal) && offVal.ValueKind == JsonValueKind.Number;
            if (!temAbsoluta && !temRelativa) throw new ArgumentException("Informe elevation_m (absoluta) ou offset_m (relativa).");
            double elevacaoAnterior = FtToM(level.Elevation);
            using (var tx = new Transaction(doc, "MCP: Elevação do nível"))
            {
                tx.Start();
                if (temAbsoluta) level.Elevation = MToFt(absVal.GetDouble());
                else level.Elevation += MToFt(offVal.GetDouble());
                tx.Commit();
            }
            return new
            {
                id = level.Id.Value,
                nome = level.Name,
                elevacao_anterior_m = Math.Round(elevacaoAnterior, 4),
                elevacao_m = Math.Round(FtToM(level.Elevation), 4)
            };
        }

        /// <summary>Cria um plano de referência entre dois pontos, visível na vista indicada (padrão: vista ativa).</summary>
        private static object CreateReferencePlane(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var p1 = new XYZ(MToFt(GetDReq(args, "x1_m")), MToFt(GetDReq(args, "y1_m")), 0);
            var p2 = new XYZ(MToFt(GetDReq(args, "x2_m")), MToFt(GetDReq(args, "y2_m")), 0);
            if (p1.IsAlmostEqualTo(p2)) throw new ArgumentException("Os pontos do plano de referência não podem coincidir.");

            View view = uiApp.ActiveUIDocument.ActiveView;
            if (ProHas(args, "view_id", out var vv) && vv.ValueKind == JsonValueKind.Number)
                view = doc.GetElement(new ElementId(vv.GetInt64())) as View
                    ?? throw new ArgumentException("view_id não é uma vista válida.");

            string nome = GetS(args, "name");
            var terceiroPonto = p1 + view.ViewDirection;
            using (var tx = new Transaction(doc, "MCP: Plano de referência"))
            {
                tx.Start();
                var plano = doc.Create.NewReferencePlane2(p1, p2, terceiroPonto, view);
                if (!string.IsNullOrEmpty(nome)) { try { plano.Name = nome; } catch { /* nome duplicado */ } }
                tx.Commit();
                return new { id = plano.Id.Value, nome = plano.Name };
            }
        }

        /// <summary>Move o Ponto Base do Projeto (padrão) ou o Ponto de Levantamento (target="survey").</summary>
        private static object SetProjectBasePoint(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            string target = (GetS(args, "target", "project") ?? "project").ToLowerInvariant();
            BasePoint bp = target == "survey" ? BasePoint.GetSurveyPoint(doc) : BasePoint.GetProjectBasePoint(doc);
            if (bp == null) throw new Exception("Ponto base não encontrado no documento.");

            bool temX = ProHas(args, "x_m", out var xv) && xv.ValueKind == JsonValueKind.Number;
            bool temY = ProHas(args, "y_m", out var yv) && yv.ValueKind == JsonValueKind.Number;
            bool temZ = ProHas(args, "z_m", out var zv) && zv.ValueKind == JsonValueKind.Number;
            if (!temX && !temY && !temZ) throw new ArgumentException("Informe ao menos um de x_m, y_m ou z_m.");

            bool estavaFixado = bp.Pinned;
            using (var tx = new Transaction(doc, "MCP: Ponto base"))
            {
                tx.Start();
                // BASEPOINT_EASTWEST_PARAM/NORTHSOUTH/ELEVATION são somente leitura (e não há
                // um Position com setter nesta API): a forma suportada de mover qualquer um dos
                // dois BasePoint é como um Element comum, por translação
                // (ElementTransformUtils.MoveElement). O Ponto Base do Projeto vem fixado
                // (Pinned) por padrão, o que MoveElement recusa; libera antes e refixa depois.
                if (estavaFixado) bp.Pinned = false;
                var atual = bp.Position;
                double x = temX ? MToFt(xv.GetDouble()) : atual.X;
                double y = temY ? MToFt(yv.GetDouble()) : atual.Y;
                double z = temZ ? MToFt(zv.GetDouble()) : atual.Z;
                var translacao = new XYZ(x, y, z) - atual;
                if (!translacao.IsZeroLength())
                    ElementTransformUtils.MoveElement(doc, bp.Id, translacao);
                if (estavaFixado) bp.Pinned = true;
                tx.Commit();
            }
            return new { alvo = target, posicao = ProPoint(bp.Position) };
        }

        /// <summary>Audita níveis e eixos: nomes duplicados, elevações duplicadas e eixos sem nome. Somente leitura.</summary>
        private static object AuditLevelsGrids(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var niveis = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();
            var eixos = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            var problemas = new List<object>();

            foreach (var g in niveis.GroupBy(l => Normalizar(l.Name)).Where(g => g.Count() > 1))
                problemas.Add(new { tipo = "nivel_nome_duplicado", nome = g.First().Name, ids = g.Select(l => l.Id.Value).ToList() });

            foreach (var g in niveis.GroupBy(l => Math.Round(l.Elevation, 3)).Where(g => g.Count() > 1))
                problemas.Add(new { tipo = "nivel_elevacao_duplicada", elevacao_m = Math.Round(FtToM(g.Key), 4), ids = g.Select(l => l.Id.Value).ToList() });

            foreach (var g in eixos)
                if (string.IsNullOrWhiteSpace(g.Name)) problemas.Add(new { tipo = "eixo_sem_nome", id = g.Id.Value });

            foreach (var g in eixos.GroupBy(x => Normalizar(x.Name)).Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1))
                problemas.Add(new { tipo = "eixo_nome_duplicado", nome = g.First().Name, ids = g.Select(x => x.Id.Value).ToList() });

            return new
            {
                niveis = niveis.Select(l => new { id = l.Id.Value, nome = l.Name, elevacao_m = Math.Round(FtToM(l.Elevation), 4) }).ToList(),
                eixos = eixos.Select(g => new { id = g.Id.Value, nome = g.Name }).ToList(),
                problemas,
                quantidade_problemas = problemas.Count
            };
        }

        // =====================================================================
        // ESTILOS DE LINHA E PADRÕES DE PREENCHIMENTO
        // =====================================================================

        /// <summary>Lista os estilos de linha (subcategorias de "Linhas") com peso de projeção e cor.</summary>
        private static object ListLineStyles(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var categoriaLinhas = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var estilos = new List<object>();
            if (categoriaLinhas?.SubCategories != null)
            {
                // Categories/Category são coleções antigas da API: percorridas com foreach.
                foreach (Category sub in categoriaLinhas.SubCategories)
                {
                    Color cor = null;
                    try { cor = sub.LineColor; } catch { }
                    int? peso = null;
                    try { peso = sub.GetLineWeight(GraphicsStyleType.Projection); } catch { }
                    estilos.Add(new
                    {
                        id = sub.Id.Value,
                        nome = sub.Name,
                        peso_projecao = peso,
                        cor = (cor != null && cor.IsValid) ? $"#{cor.Red:X2}{cor.Green:X2}{cor.Blue:X2}" : null
                    });
                }
            }
            return new { quantidade = estilos.Count, estilos };
        }

        /// <summary>Lista os padrões de preenchimento (hachuras) do documento, com destino (desenho/modelo).</summary>
        private static object ListFillPatterns(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);
            var padroes = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .Select(f =>
                {
                    var fp = f.GetFillPattern();
                    return new
                    {
                        id = f.Id.Value,
                        nome = f.Name,
                        destino = fp.Target.ToString(),
                        preenchimento_solido = fp.IsSolidFill
                    };
                })
                .OrderBy(x => x.destino).ThenBy(x => x.nome)
                .ToList();
            return new { quantidade = padroes.Count, padroes };
        }

        // =====================================================================
        // PARÂMETROS
        // =====================================================================

        /// <summary>Lê um parâmetro de um elemento pelo GUID (parâmetro compartilhado), estável entre documentos.</summary>
        private static object ReadParameterByGuid(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "element_id");
            var elemento = doc.GetElement(id) ?? throw new ArgumentException($"Elemento {id.Value} não existe.");
            string guidStr = GetS(args, "guid") ?? throw new ArgumentException("Argumento 'guid' obrigatório.");
            if (!Guid.TryParse(guidStr, out var guid)) throw new ArgumentException($"GUID inválido: {guidStr}");

            var p = elemento.get_Parameter(guid);
            if (p == null) return new { id = id.Value, guid = guidStr, encontrado = false, valor = (object)null };
            return new
            {
                id = id.Value,
                guid = guidStr,
                encontrado = true,
                nome = p.Definition?.Name,
                somente_leitura = p.IsReadOnly,
                valor = ProParamValue(doc, p)
            };
        }

        // =====================================================================
        // LINKS
        // =====================================================================

        /// <summary>Caminho, status de carregamento e transformação (posição/rotação) de um vínculo Revit.</summary>
        private static object GetLinkInfo(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "link_type_id");
            var elemento = doc.GetElement(id);
            var tipo = elemento as RevitLinkType;
            var instancia = elemento as RevitLinkInstance;
            if (tipo == null && instancia != null) tipo = doc.GetElement(instancia.GetTypeId()) as RevitLinkType;
            if (tipo == null) throw new ArgumentException($"{id.Value} não é um vínculo Revit (use tipo_id ou id de list_links).");
            if (instancia == null)
                instancia = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                    .FirstOrDefault(i => i.GetTypeId() == tipo.Id);

            string caminho = null;
            try
            {
                var ext = tipo.GetExternalFileReference();
                if (ext != null) caminho = ModelPathUtils.ConvertModelPathToUserVisiblePath(ext.GetAbsolutePath());
            }
            catch { }

            object transformacao = null;
            if (instancia != null)
            {
                try
                {
                    var t = instancia.GetTotalTransform();
                    transformacao = new
                    {
                        origem_m = ProPoint(t.Origin),
                        rotacao_z_graus = Math.Round(Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180.0 / Math.PI, 4)
                    };
                }
                catch { }
            }

            return new
            {
                tipo_id = tipo.Id.Value,
                instancia_id = instancia?.Id.Value,
                nome = tipo.Name,
                caminho,
                carregado = RevitLinkType.IsLoaded(doc, tipo.Id),
                documento = instancia?.GetLinkDocument()?.Title,
                transformacao
            };
        }

        // =====================================================================
        // CONSULTAS ESPACIAIS E DE GEOMETRIA
        // =====================================================================

        /// <summary>Caixa delimitadora (bounding box) de um elemento: mínimo, máximo, centro e dimensões.</summary>
        private static object GetBoundingBox(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var id = GetElementIdReq(args, "element_id");
            var elemento = doc.GetElement(id) ?? throw new ArgumentException($"Elemento {id.Value} não existe.");
            View view = null;
            if (ProHas(args, "view_id", out var vv) && vv.ValueKind == JsonValueKind.Number)
                view = doc.GetElement(new ElementId(vv.GetInt64())) as View;

            var bb = elemento.get_BoundingBox(view);
            if (bb == null) return new { id = id.Value, encontrado = false };
            return new
            {
                id = id.Value,
                encontrado = true,
                minimo_m = ProPoint(bb.Min),
                maximo_m = ProPoint(bb.Max),
                centro_m = ProPoint((bb.Min + bb.Max) / 2),
                largura_x_m = Math.Round(FtToM(bb.Max.X - bb.Min.X), 4),
                largura_y_m = Math.Round(FtToM(bb.Max.Y - bb.Min.Y), 4),
                altura_z_m = Math.Round(FtToM(bb.Max.Z - bb.Min.Z), 4)
            };
        }

        /// <summary>Lista elementos cuja caixa delimitadora intersecta uma caixa de busca cúbica ao redor de um ponto.</summary>
        private static object GetElementsNearPoint(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var centro = new XYZ(MToFt(GetDReq(args, "x_m")), MToFt(GetDReq(args, "y_m")), MToFt(GetD(args, "z_m", 0)));
            double raio = MToFt(GetD(args, "radius_m", 1.0));
            if (raio <= 0) throw new ArgumentException("radius_m deve ser positivo.");
            int limit = Math.Clamp((int)GetD(args, "limit", 100), 1, 1000);
            var margem = new XYZ(raio, raio, raio);
            var outline = new Outline(centro - margem, centro + margem);

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline));
            var categorias = ProCategories(args, "categories");
            IEnumerable<Element> query = categorias.Count > 0
                ? collector.WherePasses(new ElementMulticategoryFilter(categorias)).ToElements()
                : collector.ToElements();

            var elementos = query.Select(e =>
            {
                var bb = e.get_BoundingBox(null);
                double? distancia = null;
                if (bb != null) distancia = Math.Round(FtToM(((bb.Min + bb.Max) / 2).DistanceTo(centro)), 3);
                return new { id = e.Id.Value, nome = e.Name, categoria = e.Category?.Name, tipo = ProTypeName(doc, e), distancia_m = distancia };
            })
            .OrderBy(x => x.distancia_m ?? double.MaxValue)
            .Take(limit)
            .ToList();

            return new { quantidade = elementos.Count, ponto_m = ProPoint(centro), raio_m = Math.Round(FtToM(raio), 4), elementos };
        }

        /// <summary>Lista elementos cuja caixa delimitadora intersecta uma caixa 3D (coordenadas do modelo, metros).</summary>
        private static object GetElementsInBox(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var min = new XYZ(MToFt(GetDReq(args, "x_min_m")), MToFt(GetDReq(args, "y_min_m")), MToFt(GetDReq(args, "z_min_m")));
            var max = new XYZ(MToFt(GetDReq(args, "x_max_m")), MToFt(GetDReq(args, "y_max_m")), MToFt(GetDReq(args, "z_max_m")));
            if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
                throw new ArgumentException("Os mínimos devem ser menores ou iguais aos máximos.");
            int limit = Math.Clamp((int)GetD(args, "limit", 200), 1, 2000);
            var outline = new Outline(min, max);

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline));
            var categorias = ProCategories(args, "categories");
            IEnumerable<Element> query = categorias.Count > 0
                ? collector.WherePasses(new ElementMulticategoryFilter(categorias)).ToElements()
                : collector.ToElements();

            var elementos = query.Take(limit).Select(e => new
            {
                id = e.Id.Value,
                nome = e.Name,
                categoria = e.Category?.Name,
                tipo = ProTypeName(doc, e),
                nivel = ProLevelName(doc, e)
            }).ToList();

            return new { quantidade = elementos.Count, elementos };
        }

        // =====================================================================
        // GOVERNANÇA E DIAGNÓSTICO
        // =====================================================================

        /// <summary>Valida nomes de vistas, pranchas, famílias, tipos, materiais, filtros, templates e worksets. Somente leitura.</summary>
        private static object ValidateNaming(UIApplication uiApp, JsonElement args)
        {
            var doc = RequireDoc(uiApp);
            var escoposPedidos = ProStrings(args, "scope");
            if (escoposPedidos.Count == 0)
                escoposPedidos = new List<string> { "views", "sheets", "families", "types", "materials", "filters", "templates", "worksets" };

            string patternStr = GetS(args, "pattern");
            Regex regex = null;
            if (!string.IsNullOrEmpty(patternStr))
            {
                try { regex = new Regex(patternStr); }
                catch (Exception ex) { throw new ArgumentException($"Padrão regex inválido: {ex.Message}"); }
            }
            string proibidos = GetS(args, "forbidden_chars");
            int tamanhoMax = (int)GetD(args, "max_length", 0);

            var nomesPorEscopo = new Dictionary<string, List<(long id, string nome)>>();
            foreach (var escopo in escoposPedidos.Select(s => s.ToLowerInvariant()).Distinct())
            {
                List<(long id, string nome)> itens;
                switch (escopo)
                {
                    case "views":
                        // OfClass(View) também retorna ViewSheet (deriva de View); exclui para não duplicar com "sheets".
                        itens = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                            .Where(v => !v.IsTemplate && !(v is ViewSheet))
                            .Select(v => (v.Id.Value, v.Name)).ToList();
                        break;
                    case "templates":
                        itens = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                            .Where(v => v.IsTemplate)
                            .Select(v => (v.Id.Value, v.Name)).ToList();
                        break;
                    case "sheets":
                        itens = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                            .Select(v => (v.Id.Value, v.Name)).ToList();
                        break;
                    case "families":
                        itens = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                            .Select(f => (f.Id.Value, f.Name)).ToList();
                        break;
                    case "types":
                        itens = new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
                            .Select(t => (t.Id.Value, t.Name)).ToList();
                        break;
                    case "materials":
                        itens = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                            .Select(m => (m.Id.Value, m.Name)).ToList();
                        break;
                    case "filters":
                        itens = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
                            .Select(f => (f.Id.Value, f.Name)).ToList();
                        break;
                    case "worksets":
                        itens = doc.IsWorkshared
                            ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets()
                                .Select(w => ((long)w.Id.IntegerValue, w.Name)).ToList()
                            : new List<(long, string)>();
                        break;
                    default:
                        throw new ArgumentException($"scope desconhecido: {escopo}. Use views, sheets, families, types, materials, filters, templates ou worksets.");
                }
                nomesPorEscopo[escopo] = itens;
            }

            var problemas = new List<object>();
            foreach (var par in nomesPorEscopo)
            {
                foreach (var (id, nome) in par.Value)
                {
                    var motivos = new List<string>();
                    if (regex != null && !regex.IsMatch(nome ?? "")) motivos.Add("não casa com o padrão");
                    if (!string.IsNullOrEmpty(proibidos) && nome != null && nome.Any(c => proibidos.Contains(c))) motivos.Add("contém caractere proibido");
                    if (tamanhoMax > 0 && (nome?.Length ?? 0) > tamanhoMax) motivos.Add($"excede {tamanhoMax} caracteres");
                    if (motivos.Count > 0) problemas.Add(new { escopo = par.Key, id, nome, motivos });
                }
            }

            return new
            {
                escopos_verificados = nomesPorEscopo.Keys.ToList(),
                total_itens = nomesPorEscopo.Values.Sum(l => l.Count),
                problemas,
                quantidade_problemas = problemas.Count
            };
        }

        /// <summary>Inventário completo do modelo: contagens por categoria, níveis, vistas, pranchas, famílias, materiais, worksets e vínculos. Somente leitura.</summary>
        private static object GetModelInventory(UIApplication uiApp)
        {
            var doc = RequireDoc(uiApp);

            // Settings.Categories é coleção antiga da API: percorrida com foreach.
            var contagens = new List<(string nome, int quantidade)>();
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                int quantidade;
                try
                {
                    quantidade = new FilteredElementCollector(doc).WherePasses(new ElementCategoryFilter(cat.Id))
                        .WhereElementIsNotElementType().GetElementCount();
                }
                catch { continue; }
                if (quantidade > 0) contagens.Add((cat.Name, quantidade));
            }
            contagens.Sort((x, y) => y.quantidade.CompareTo(x.quantidade));
            var categorias = contagens.Select(c => new { categoria = c.nome, quantidade = c.quantidade }).ToList();

            var worksets = doc.IsWorkshared
                ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Select(w => w.Name).ToList()
                : new List<string>();

            return new
            {
                arquivo = doc.Title,
                caminho = doc.PathName,
                workshared = doc.IsWorkshared,
                niveis = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount(),
                vistas = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Count(v => !v.IsTemplate && !(v is ViewSheet)),
                pranchas = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).GetElementCount(),
                familias = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount(),
                materiais = new FilteredElementCollector(doc).OfClass(typeof(Material)).GetElementCount(),
                vinculos = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount(),
                worksets,
                categorias,
                gerado_em_utc = DateTime.UtcNow.ToString("o")
            };
        }
    }
}
