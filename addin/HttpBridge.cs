// ============================================================================
// HttpBridge.cs — Servidor HTTP embutido no Revit
// ============================================================================
//
// Escuta em http://127.0.0.1:14733 (somente localhost, por segurança).
//
// Endpoints:
//   GET  /health  → { ok: true } — para verificar se o bridge está ativo
//   POST /rpc     → { cmd: "nome_comando", args: {...} } — executa comando
//
// O corpo da resposta é sempre JSON:
//   Sucesso: { ok: true,  result: ... }
//   Erro:    { ok: false, error: "mensagem", type: "...", stack: "..." }
//
// Erros graves são gravados em %TEMP%/revit_mcp.log para diagnóstico.
// ============================================================================

using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RevitMcp
{
    public class HttpBridge
    {
        private readonly HttpListener _listener = new HttpListener();
        private Thread _thread;
        private volatile bool _running;
        private readonly int _port;
        private readonly string _token;
        private readonly bool _developmentMode;
        private const int MaxRequestBytes = 1024 * 1024;

        public HttpBridge(int port)
        {
            _port = port;
            (_token, _developmentMode) = LoadSecurityConfiguration();
            if (string.IsNullOrWhiteSpace(_token))
                throw new InvalidOperationException("Token do bridge ausente. Execute reload-revit-mcp.ps1 para gerar a configuração segura.");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        /// <summary>Inicia o listener HTTP em thread background.</summary>
        public void Start()
        {
            WriteBridgeLog($"Iniciando listener em 127.0.0.1:{_port}.");
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "RevitMcp-HTTP"
            };
            _thread.Start();
            WriteBridgeLog("Listener HTTP iniciado com sucesso.");
        }

        /// <summary>Para o listener HTTP.</summary>
        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private static void WriteBridgeLog(string message)
        {
            try
            {
                var path = Path.Combine(LogDirectory(), "revit_mcp_bridge.log");
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static string LogDirectory()
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMcp", "logs");
            Directory.CreateDirectory(directory);
            return directory;
        }

        // =====================================================================
        // Loop principal — aceita conexões e despacha para threads do pool
        // =====================================================================

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx = null;
                try { ctx = _listener.GetContext(); }
                catch { break; }

                // Cada requisição é tratada em thread separada do pool
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Handle(ctx);
                    }
                    catch (Exception ex)
                    {
                        try { WriteError(ctx, ex.Message); } catch { }
                    }
                });
            }
        }

        // =====================================================================
        // Tratamento de requisições
        // =====================================================================

        private void Handle(HttpListenerContext ctx)
        {
            var requestId = Guid.NewGuid().ToString("N");
            if (!IsAuthorized(ctx.Request))
            {
                WriteJson(ctx, 401, new { ok = false, error = "Não autorizado", request_id = requestId });
                WriteBridgeLog($"[{requestId}] Requisição não autorizada de {ctx.Request.RemoteEndPoint}.");
                return;
            }

            // --- Health check (GET /health) ---
            if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url.AbsolutePath == "/health")
            {
                WriteJson(ctx, 200, new { ok = true, service = "RevitMcp", port = _port, authenticated = true, request_id = requestId });
                return;
            }

            // --- Apenas POST /rpc é aceito para comandos ---
            if (ctx.Request.HttpMethod != "POST" || ctx.Request.Url.AbsolutePath != "/rpc")
            {
                WriteJson(ctx, 404, new { ok = false, error = "Endpoint não encontrado" });
                return;
            }

            // --- Lê o corpo JSON da requisição ---
            if (ctx.Request.ContentLength64 > MaxRequestBytes)
            {
                WriteJson(ctx, 413, new { ok = false, error = "Payload excede o limite de 1 MB", request_id = requestId });
                return;
            }

            string body;
            try { body = ReadBoundedBody(ctx.Request); }
            catch (InvalidDataException ex)
            {
                WriteJson(ctx, 413, new { ok = false, error = ex.Message, request_id = requestId });
                return;
            }

            RpcRequest req;
            try
            {
                req = JsonSerializer.Deserialize<RpcRequest>(body);
            }
            catch (Exception ex)
            {
                WriteJson(ctx, 400, new { ok = false, error = "JSON inválido: " + ex.Message });
                return;
            }

            // --- Despacha o comando para Commands.Dispatch ---
            try
            {
                var result = Commands.Dispatch(req);
                WriteJson(ctx, 200, new { ok = true, result });
            }
            catch (Exception ex)
            {
                LogException($"[{requestId}] Dispatch " + (req?.cmd ?? "?"), ex);

                var payload = new
                {
                    ok = false,
                    error = _developmentMode ? (ex.Message ?? "(sem mensagem)") : "A operação não foi concluída. Consulte o log do RevitMcp com o identificador da requisição.",
                    request_id = requestId,
                    type = _developmentMode ? ex.GetType().FullName : null,
                    stack = _developmentMode ? ex.ToString() : null
                };

                try
                {
                    WriteJson(ctx, 500, payload);
                }
                catch (Exception writeEx)
                {
                    // Fallback: se WriteJson falhar, envia texto puro
                    LogException("WriteJson-500", writeEx);
                    try
                    {
                        var buf = Encoding.UTF8.GetBytes(ex.ToString());
                        ctx.Response.StatusCode = 500;
                        ctx.Response.ContentType = "text/plain; charset=utf-8";
                        ctx.Response.ContentLength64 = buf.Length;
                        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                        ctx.Response.OutputStream.Close();
                    }
                    catch { }
                }
            }
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            var provided = request.Headers["X-Revit-Mcp-Token"];
            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(_token)) return false;
            var expectedBytes = Encoding.UTF8.GetBytes(_token);
            var actualBytes = Encoding.UTF8.GetBytes(provided);
            return expectedBytes.Length == actualBytes.Length &&
                CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        private static string ReadBoundedBody(HttpListenerRequest request)
        {
            var encoding = request.ContentEncoding ?? Encoding.UTF8;
            var buffer = new char[4096];
            var builder = new StringBuilder();
            using (var reader = new StreamReader(request.InputStream, encoding))
            {
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (builder.Length + read > MaxRequestBytes)
                        throw new InvalidDataException("Payload excede o limite de 1 MB");
                    builder.Append(buffer, 0, read);
                }
            }
            return builder.ToString();
        }

        private static (string token, bool developmentMode) LoadSecurityConfiguration()
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMcp", "bridge.json");
                if (!File.Exists(path)) return (null, false);
                using (var json = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    var root = json.RootElement;
                    var token = root.TryGetProperty("token", out var tv) ? tv.GetString() : null;
                    var developmentMode = root.TryGetProperty("development_mode", out var dv) && dv.ValueKind == JsonValueKind.True;
                    return (token, developmentMode);
                }
            }
            catch { return (null, false); }
        }

        // =====================================================================
        // Utilitários de resposta e log
        // =====================================================================

        private static void WriteJson(HttpListenerContext ctx, int status, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var buf = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.OutputStream.Close();
        }

        private static void WriteError(HttpListenerContext ctx, string msg)
        {
            WriteJson(ctx, 500, new { ok = false, error = msg });
        }

        /// <summary>
        /// Grava exceções em arquivo de log para diagnóstico.
        /// Caminho: %TEMP%/revit_mcp.log
        /// </summary>
        private static readonly object _logLock = new object();
        private static void LogException(string where, Exception ex)
        {
            try
            {
                var path = Path.Combine(LogDirectory(), "revit_mcp.log");
                lock (_logLock)
                {
                    File.AppendAllText(path,
                        $"{DateTime.Now:s} [{where}] {ex.GetType().FullName}: {ex.Message}\n" +
                        $"{ex.StackTrace}\n\n");
                }
            }
            catch { }
        }
    }

    // =========================================================================
    // Modelo da requisição RPC recebida via JSON
    // =========================================================================

    /// <summary>
    /// Estrutura do JSON enviado pelo servidor Python MCP.
    /// Exemplo: { "cmd": "create_wall", "args": { "x1_m": 0, ... } }
    /// </summary>
    public class RpcRequest
    {
        /// <summary>Nome do comando (ex: "create_wall", "screenshot")</summary>
        public string cmd { get; set; }

        /// <summary>Argumentos do comando (variam por comando)</summary>
        public JsonElement args { get; set; }
    }
}
