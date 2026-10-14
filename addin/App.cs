// ============================================================================
// App.cs — Ponto de entrada do add-in RevitMcp
// ============================================================================
//
// Este arquivo registra o add-in no Revit. Quando o Revit inicia:
//   1. Cria um ExternalEvent (mecanismo do Revit para executar código
//      na thread principal a partir de threads externas)
//   2. Inicia o servidor HTTP local na porta 14733
//
// O ExternalEvent é essencial porque a API do Revit só pode ser chamada
// da thread principal. Qualquer chamada de fora (como do nosso servidor
// HTTP) precisa ser enfileirada via ExternalEvent.
//
// Ciclo de vida:
//   Revit inicia → OnStartup() → Bridge HTTP sobe na porta 14733
//   Revit fecha  → OnShutdown() → Bridge HTTP para
// ============================================================================

using System;
using System.IO;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitMcp
{
    public class App : IExternalApplication
    {
        // Referências estáticas acessíveis por toda a aplicação.
        // O Revit garante que só existe uma instância de App.
        public static UIControlledApplication UiCtrlApp;
        public static UIApplication UiApp;
        public static CommandHandler Handler;
        public static ExternalEvent Event;
        public static HttpBridge Bridge;

        /// <summary>
        /// Chamado pelo Revit quando o add-in é carregado.
        /// Configura o handler de comandos e inicia o servidor HTTP.
        /// </summary>
        public Result OnStartup(UIControlledApplication a)
        {
            try
            {
                StartupLog("OnStartup iniciado.");
                UiCtrlApp = a;

                // O Handler recebe lambdas e as executa na thread principal
                Handler = new CommandHandler();
                Event = ExternalEvent.Create(Handler);
                StartupLog("ExternalEvent criado.");

                // Captura a UIApplication (só disponível via evento Idling)
                a.Idling += OnIdling;

                // Relatório de alterações anexado às respostas de escrita.
                a.ControlledApplication.DocumentChanged += ChangeTracker.OnDocumentChanged;

                // Inicia o servidor HTTP que recebe comandos do MCP Python
                Bridge = new HttpBridge(HttpBridge.DefaultPort);
                Bridge.Start();
                StartupLog($"Bridge HTTP ativo em 127.0.0.1:{Bridge.Port}.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                StartupLog("Falha no OnStartup: " + ex);
                try { Bridge?.Stop(); } catch { }
                Bridge = null;
                // O Revit deve continuar utilizável mesmo sem o bridge; a mensagem
                // indica a causa e onde está o log completo.
                TaskDialog.Show("RevitMcp",
                    "O bridge MCP não pôde ser iniciado: " + ex.Message +
                    "\n\nLog: %LOCALAPPDATA%\\RevitMcp\\logs\\revit_mcp_startup.log");
                return Result.Succeeded;
            }
        }

        /// <summary>
        /// Evento Idling: chamado repetidamente quando o Revit está ocioso.
        /// Usado apenas uma vez para capturar a UIApplication.
        /// </summary>
        private void OnIdling(object sender, IdlingEventArgs e)
        {
            if (UiApp == null && sender is UIApplication u)
            {
                UiApp = u;
                // Só é necessário uma vez; manter a assinatura custaria uma
                // chamada a cada ciclo ocioso do Revit.
                try { UiCtrlApp.Idling -= OnIdling; } catch { }
            }
        }

        /// <summary>
        /// Chamado pelo Revit quando o add-in é descarregado.
        /// Para o servidor HTTP de forma limpa.
        /// </summary>
        public Result OnShutdown(UIControlledApplication a)
        {
            try { a.Idling -= OnIdling; } catch { }
            try { a.ControlledApplication.DocumentChanged -= ChangeTracker.OnDocumentChanged; } catch { }
            try { Bridge?.Stop(); StartupLog("OnShutdown concluído."); } catch { }
            return Result.Succeeded;
        }

        /// <summary>Registro mínimo de inicialização para diagnóstico sem depender da interface.</summary>
        /// <remarks>Grava em %LOCALAPPDATA%\RevitMcp\logs e, por compatibilidade, em %TEMP%.</remarks>
        private static void StartupLog(string message)
        {
            var line = DateTime.Now.ToString("s") + " " + message + Environment.NewLine;
            try { File.AppendAllText(Path.Combine(HttpBridge.LogDirectory(), "revit_mcp_startup.log"), line); } catch { }
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "revit_mcp_startup.log"), line); } catch { }
        }
    }
}
