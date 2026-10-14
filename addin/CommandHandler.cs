// ============================================================================
// CommandHandler.cs — Executa código na thread principal do Revit
// ============================================================================
//
// PROBLEMA:
//   A API do Revit só pode ser chamada da thread principal (UI thread).
//   Mas nosso servidor HTTP recebe requisições em threads do ThreadPool.
//
// SOLUÇÃO:
//   1. O servidor HTTP chama Run() com uma lambda (ex: doc => CriarParede(...))
//   2. Run() guarda a lambda e dispara ExternalEvent.Raise()
//   3. O Revit, quando estiver ocioso, chama Execute() na thread principal
//   4. Execute() roda a lambda e sinaliza que terminou (ManualResetEventSlim)
//   5. Run() retorna o resultado para o servidor HTTP
//
// O lock garante que apenas um comando executa por vez.
// O timeout (2 min, ou 10 min para exportações/auditorias) protege contra
// travamentos (ex: diálogo modal aberto).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.UI;

namespace RevitMcp
{
    /// <summary>
    /// Ponte entre threads HTTP e a thread principal do Revit.
    /// Implementa IExternalEventHandler — interface que o Revit chama
    /// quando um ExternalEvent é disparado.
    /// </summary>
    public class CommandHandler : IExternalEventHandler
    {
        // Trava para serializar comandos (um por vez)
        private readonly object _gate = new object();

        // Lambda que será executada na thread principal
        private sealed class WorkItem
        {
            public Func<UIApplication, object> Work;
            public object Result;
            public Exception Error;
            // Um comando que excedeu o tempo não pode ser executado depois de
            // o cliente receber uma falha. Isso evita alterações tardias e
            // duplicadas em automações de produção.
            public bool Started;
            public bool Cancelled;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }
        private readonly object _queueGate = new object();
        private readonly Queue<WorkItem> _queue = new Queue<WorkItem>();

        // Resultado ou erro da execução

        /// <summary>
        /// Chamado pelo Revit na thread principal quando ExternalEvent.Raise() é disparado.
        /// Executa a lambda armazenada e sinaliza conclusão.
        /// </summary>
        public void Execute(UIApplication app)
        {
            // Processa tudo o que já estiver na fila. Em especial, um item
            // cancelado por timeout não deve consumir o único ExternalEvent e
            // deixar o próximo comando aguardando indefinidamente.
            while (true)
            {
                WorkItem item;
                lock (_queueGate)
                {
                    if (_queue.Count == 0) return;
                    item = _queue.Dequeue();
                    if (!item.Cancelled) item.Started = true;
                }

                if (item.Cancelled)
                {
                    item.Done.Set();
                    continue;
                }

                try
                {
                    item.Result = item.Work?.Invoke(app);
                    item.Error = null;
                }
                catch (Exception ex)
                {
                    item.Result = null;
                    item.Error = ex;
                }
                finally
                {
                    item.Done.Set();
                }
            }
        }

        /// <summary>Nome exibido no log interno do Revit.</summary>
        public string GetName() => "RevitMcp.CommandHandler";

        /// <summary>
        /// Enfileira uma lambda para execução na thread principal do Revit
        /// e aguarda o resultado (bloqueante, com timeout).
        /// </summary>
        /// <param name="work">Lambda que recebe UIApplication e retorna um objeto.</param>
        /// <param name="timeoutMs">Tempo máximo de espera em milissegundos.</param>
        /// <returns>Resultado da lambda.</returns>
        public object Run(Func<UIApplication, object> work, int timeoutMs = 60000)
        {
            lock (_gate)
            {
                if (App.Event == null)
                    throw new InvalidOperationException("RevitMcp ainda não foi inicializado pelo Revit.");

                var item = new WorkItem { Work = work };
                lock (_queueGate) _queue.Enqueue(item);

                // Dispara o evento — o Revit vai chamar Execute() quando puder
                var request = App.Event.Raise();
                if (request == ExternalEventRequest.Denied || request == ExternalEventRequest.TimedOut)
                {
                    lock (_queueGate)
                    {
                        // Pending/Accepted significam que Execute() vai drenar a fila.
                        // Denied/TimedOut: o item não será executado; remove-o.
                        if (!item.Started) item.Cancelled = true;
                    }
                    if (item.Cancelled)
                        throw new InvalidOperationException(
                            $"O Revit recusou o pedido de execução ({request}). Feche diálogos/edições em andamento e tente novamente.");
                }

                // Aguarda a conclusão ou timeout
                if (!item.Done.Wait(timeoutMs))
                {
                    bool cancelled;
                    lock (_queueGate)
                    {
                        cancelled = !item.Started;
                        if (cancelled) item.Cancelled = true;
                    }
                    throw new TimeoutException(cancelled
                        ? $"Comando Revit excedeu {timeoutMs / 1000}s e foi cancelado antes da execução. " +
                          "Verifique se há alguma janela de diálogo aberta no Revit."
                        : $"Comando Revit excedeu {timeoutMs / 1000}s após iniciar e pode ainda estar sendo concluído. " +
                          "Consulte o log antes de repetir a operação.");
                }

                if (item.Error != null)
                    throw item.Error;

                return item.Result;
            }
        }
    }
}
