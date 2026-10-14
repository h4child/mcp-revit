// ============================================================================
// ChangeTracker.cs — Relatório detalhado do que cada comando alterou
// ============================================================================
//
// Toda resposta de comando de escrita inclui um bloco "alteracoes" com os
// elementos criados (categoria, tipo, nível, medidas), modificados e
// excluídos. Assim a IA confirma o efeito real no modelo sem precisar de
// uma segunda consulta, e o usuário vê exatamente o que mudou.
//
// Fonte: evento Application.DocumentChanged, que o Revit dispara após cada
// transação confirmada (ou desfeita) na thread principal — a mesma thread
// em que os comandos rodam via ExternalEvent, então não há concorrência.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace RevitMcp
{
    public static class ChangeTracker
    {
        private const int MaxDetailed = 100;
        private const int MaxIds = 500;

        private static bool _active;
        private static Document _doc;
        private static readonly HashSet<long> _added = new HashSet<long>();
        private static readonly HashSet<long> _modified = new HashSet<long>();
        private static readonly HashSet<long> _deleted = new HashSet<long>();
        private static readonly List<string> _transactions = new List<string>();
        private static readonly Stopwatch _clock = new Stopwatch();

        /// <summary>Registrado em App.OnStartup.</summary>
        public static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (!_active) return;
            try
            {
                var doc = e.GetDocument();
                if (_doc == null) _doc = doc;
                else if (!_doc.Equals(doc)) return;

                foreach (var id in e.GetAddedElementIds()) { _added.Add(id.Value); _deleted.Remove(id.Value); }
                foreach (var id in e.GetModifiedElementIds()) _modified.Add(id.Value);
                foreach (var id in e.GetDeletedElementIds())
                {
                    // Criado e desfeito no mesmo comando (ex.: simulação de lote) não conta.
                    if (!_added.Remove(id.Value)) _deleted.Add(id.Value);
                    _modified.Remove(id.Value);
                }
                foreach (var name in e.GetTransactionNames())
                    if (_transactions.Count < 50) _transactions.Add(name);
            }
            catch (Exception ex) { HttpBridge.LogException("ChangeTracker", ex); }
        }

        public static void Begin(Document doc)
        {
            _added.Clear(); _modified.Clear(); _deleted.Clear(); _transactions.Clear();
            _doc = doc;
            _active = true;
            _clock.Restart();
        }

        /// <summary>
        /// Encerra a coleta e devolve o resumo, ou null quando o comando não
        /// alterou o modelo (leituras continuam com a resposta original).
        /// </summary>
        public static object End()
        {
            _active = false;
            _clock.Stop();
            var doc = _doc;
            _doc = null;
            if (doc == null || !doc.IsValidObject) return null;
            if (_added.Count == 0 && _modified.Count == 0 && _deleted.Count == 0) return null;

            var created = new List<object>();
            int internos = 0;
            foreach (var value in _added.OrderBy(v => v))
            {
                var e = doc.GetElement(new ElementId(value));
                if (e == null) continue;
                // Elementos sem categoria (planos de esboço, armazenamento interno)
                // são contados mas não detalhados.
                if (e.Category == null) { internos++; continue; }
                if (created.Count < MaxDetailed) created.Add(Describe(doc, e));
                else break;
            }

            var modifiedIds = _modified.Where(v => !_added.Contains(v)).ToList();
            var porCategoria = new Dictionary<string, int>();
            foreach (var value in modifiedIds)
            {
                var cat = doc.GetElement(new ElementId(value))?.Category?.Name;
                if (cat == null) continue;
                porCategoria[cat] = porCategoria.TryGetValue(cat, out var n) ? n + 1 : 1;
            }

            int criadosVisiveis = _added.Count - internos;
            return new
            {
                documento = doc.Title,
                duracao_ms = _clock.ElapsedMilliseconds,
                transacoes = _transactions.Distinct().ToList(),
                criados_total = Math.Max(0, criadosVisiveis),
                criados = created,
                criados_internos = internos,
                modificados_total = modifiedIds.Count,
                modificados_por_categoria = porCategoria,
                modificados_ids = modifiedIds.Take(MaxIds).ToList(),
                excluidos_total = _deleted.Count,
                excluidos_ids = _deleted.Take(MaxIds).ToList(),
                pode_desfazer = "Ctrl+Z no Revit desfaz cada transação listada."
            };
        }

        /// <summary>Descrição compacta e útil de um elemento recém-criado.</summary>
        public static object Describe(Document doc, Element e)
        {
            string tipo = null, familia = null, nivel = null;
            try
            {
                var type = doc.GetElement(e.GetTypeId()) as ElementType;
                tipo = type?.Name;
                familia = (type as FamilySymbol)?.FamilyName ?? type?.FamilyName;
            }
            catch { }
            try
            {
                var levelId = e.LevelId;
                if (levelId == null || levelId == ElementId.InvalidElementId)
                    levelId = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId()
                        ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)?.AsElementId();
                if (levelId != null && levelId != ElementId.InvalidElementId) nivel = doc.GetElement(levelId)?.Name;
            }
            catch { }

            object posicao = null;
            double? comprimento = null;
            try
            {
                if (e.Location is LocationPoint lp)
                    posicao = new { x_m = R(lp.Point.X * 0.3048), y_m = R(lp.Point.Y * 0.3048), z_m = R(lp.Point.Z * 0.3048) };
                else if (e.Location is LocationCurve lc)
                {
                    var a = lc.Curve.GetEndPoint(0); var b = lc.Curve.GetEndPoint(1);
                    posicao = new
                    {
                        inicio = new { x_m = R(a.X * 0.3048), y_m = R(a.Y * 0.3048), z_m = R(a.Z * 0.3048) },
                        fim = new { x_m = R(b.X * 0.3048), y_m = R(b.Y * 0.3048), z_m = R(b.Z * 0.3048) }
                    };
                    comprimento = R(lc.Curve.Length * 0.3048);
                }
            }
            catch { }

            return new
            {
                id = e.Id.Value,
                categoria = e.Category?.Name,
                nome = SafeName(e),
                familia,
                tipo,
                nivel,
                posicao,
                comprimento_m = comprimento,
                area_m2 = Measure(e, BuiltInParameter.HOST_AREA_COMPUTED, UnitTypeId.SquareMeters)
                          ?? Measure(e, BuiltInParameter.ROOM_AREA, UnitTypeId.SquareMeters),
                volume_m3 = Measure(e, BuiltInParameter.HOST_VOLUME_COMPUTED, UnitTypeId.CubicMeters),
                altura_m = Measure(e, BuiltInParameter.WALL_USER_HEIGHT_PARAM, UnitTypeId.Meters)
            };
        }

        private static double R(double v) => Math.Round(v, 4);

        private static string SafeName(Element e)
        {
            try { return e.Name; } catch { return null; }
        }

        private static double? Measure(Element e, BuiltInParameter bip, ForgeTypeId unit)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                var v = UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit);
                return Math.Abs(v) < 1e-9 ? (double?)null : Math.Round(v, 4);
            }
            catch { return null; }
        }
    }
}
