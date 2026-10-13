// ============================================================================
// HttpBridge.cs — Servidor HTTP embutido no Revit
// ============================================================================
//
// Escuta em http://127.0.0.1:14733 (somente loopback, por segurança).
//
// Implementado sobre TcpListener em vez de HttpListener: o HttpListener usa o
// HTTP.sys, que exige reserva de URL (netsh http add urlacl) ou privilégio de
// administrador para prefixos com IP explícito e falha com "Acesso negado" em
// contas comuns. Um socket em 127.0.0.1 não tem essa restrição.
//
// Endpoints:
//   GET  /health  → { ok: true } — para verificar se o bridge está ativo
//   POST /rpc     → { cmd: "nome_comando", args: {...} } — executa comando
//
// O corpo da resposta é sempre JSON:
//   Sucesso: { ok: true,  result: ... }
//   Erro:    { ok: false, error: "mensagem", type: "...", stack: "..." }
//
// Logs ficam em %LOCALAPPDATA%\RevitMcp\logs.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RevitMcp
{
    public class HttpBridge
    {
        public const int DefaultPort = 14733;
        private const int MaxRequestBytes = 1024 * 1024;
        private const int MaxHeaderBytes = 16 * 1024;
        private const int SocketTimeoutMs = 30000;

        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private readonly int _port;
        private readonly string _token;
        private readonly bool _developmentMode;

        public int Port => _port;

        public HttpBridge(int port)
        {
            (_token, _developmentMode, var configuredPort) = LoadSecurityConfiguration();
            _port = configuredPort ?? port;
            if (string.IsNullOrWhiteSpace(_token))
                throw new InvalidOperationException("Token do bridge ausente e não foi possível gerá-lo em %LOCALAPPDATA%\\RevitMcp\\bridge.json.");
        }

        /// <summary>Inicia o listener TCP em thread background.</summary>
        public void Start()
        {
            WriteBridgeLog($"Iniciando listener em 127.0.0.1:{_port}.");
            _listener = new TcpListener(IPAddress.Loopback, _port);
            // Impede que outro processo "roube" a porta com SO_REUSEADDR.
            _listener.ExclusiveAddressUse = true;
            try
            {
                _listener.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new InvalidOperationException(
                    $"A porta {_port} já está em uso. Feche outra instância do Revit com o RevitMcp " +
                    "ou defina \"port\" em %LOCALAPPDATA%\\RevitMcp\\bridge.json (e REVIT_MCP_URL no servidor).", ex);
            }
            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "RevitMcp-HTTP"
            };
            _thread.Start();
            WriteBridgeLog("Listener HTTP iniciado com sucesso.");
        }

        /// <summary>Para o listener.</summary>
        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        internal static void WriteBridgeLog(string message)
        {
            try
            {
                var path = Path.Combine(LogDirectory(), "revit_mcp_bridge.log");
                lock (_logLock)
                    File.AppendAllText(path, DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        internal static string StateDirectory()
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMcp");
            Directory.CreateDirectory(directory);
            return directory;
        }

        internal static string LogDirectory()
        {
            var directory = Path.Combine(StateDirectory(), "logs");
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
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (SocketException) when (!_running) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    LogException("Accept", ex);
                    Thread.Sleep(100);
                    continue;
                }

                // Cada requisição é tratada em thread separada do pool
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    using (client)
                    {
                        try
                        {
                            client.ReceiveTimeout = SocketTimeoutMs;
                            client.SendTimeout = SocketTimeoutMs;
                            client.NoDelay = true;
                            Handle(client.GetStream());
                        }
                        catch (Exception ex)
                        {
                            LogException("Handle", ex);
                        }
                    }
                });
            }
        }

        // =====================================================================
        // Leitura de requisição HTTP/1.1 (mínima, suficiente para o cliente MCP)
        // =====================================================================

        private sealed class Request
        {
            public string Method;
            public string Path;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = Array.Empty<byte>();
        }

        private sealed class HttpError : Exception
        {
            public readonly int Status;
            public HttpError(int status, string message) : base(message) { Status = status; }
        }

        private static Request ReadRequest(NetworkStream stream)
        {
            // Lê byte a byte até o fim dos cabeçalhos para não consumir o corpo.
            var header = new MemoryStream();
            int matched = 0;
            while (matched < 4)
            {
                int b = stream.ReadByte();
                if (b < 0) throw new HttpError(400, "Conexão encerrada antes dos cabeçalhos.");
                header.WriteByte((byte)b);
                if (header.Length > MaxHeaderBytes) throw new HttpError(431, "Cabeçalhos excedem 16 KB.");
                matched = (b == (matched % 2 == 0 ? '\r' : '\n')) ? matched + 1 : (b == '\r' ? 1 : 0);
            }

            var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 3) throw new HttpError(400, "Linha de requisição inválida.");

            var req = new Request { Method = requestLine[0].ToUpperInvariant() };
            var target = requestLine[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) throw new HttpError(400, "Cabeçalho inválido.");
                req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            if (req.Headers.TryGetValue("Transfer-Encoding", out var te) && !string.Equals(te, "identity", StringComparison.OrdinalIgnoreCase))
                throw new HttpError(411, "Transfer-Encoding não suportado; envie Content-Length.");

            long length = 0;
            if (req.Headers.TryGetValue("Content-Length", out var cl) && !long.TryParse(cl, out length))
                throw new HttpError(400, "Content-Length inválido.");
            if (length < 0) throw new HttpError(400, "Content-Length inválido.");
            if (length > MaxRequestBytes) throw new HttpError(413, "Payload excede o limite de 1 MB");

            if (length > 0)
            {
                if (req.Headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    stream.Write(cont, 0, cont.Length);
                }
                req.Body = new byte[length];
                int offset = 0;
                while (offset < length)
                {
                    int read = stream.Read(req.Body, offset, (int)length - offset);
                    if (read <= 0) throw new HttpError(400, "Corpo da requisição incompleto.");
                    offset += read;
                }
            }
            return req;
        }

        // =====================================================================
        // Tratamento de requisições
        // =====================================================================

        private void Handle(NetworkStream stream)
        {
            var requestId = Guid.NewGuid().ToString("N");
            Request req;
            try
            {
                req = ReadRequest(stream);
            }
            catch (HttpError ex)
            {
                WriteJson(stream, ex.Status, new { ok = false, error = ex.Message, request_id = requestId });
                return;
            }
            catch (IOException)
            {
                return; // cliente desconectou ou excedeu o timeout de leitura
            }

            // Proteção contra DNS rebinding: só aceita Host de loopback.
            if (!IsLoopbackHost(req))
            {
                WriteJson(stream, 403, new { ok = false, error = "Host não permitido", request_id = requestId });
                WriteBridgeLog($"[{requestId}] Host rejeitado: {(req.Headers.TryGetValue("Host", out var h) ? h : "(ausente)")}.");
                return;
            }

            if (!IsAuthorized(req))
            {
                WriteJson(stream, 401, new
                {
                    ok = false,
                    error = "Não autorizado: token ausente ou diferente do bridge.json carregado pelo Revit. " +
                            "Execute reload-revit-mcp.ps1 ou reinicie o servidor MCP.",
                    request_id = requestId
                });
                WriteBridgeLog($"[{requestId}] Requisição não autorizada.");
                return;
            }

            // --- Health check (GET /health) ---
            if (req.Method == "GET" && req.Path == "/health")
            {
                WriteJson(stream, 200, new
                {
                    ok = true,
                    service = "RevitMcp",
                    version = typeof(HttpBridge).Assembly.GetName().Version?.ToString(),
                    port = _port,
                    authenticated = true,
                    // Só campos estáticos: a API do Revit não pode ser lida fora da thread principal.
                    revit_ready = App.UiApp != null,
                    request_id = requestId
                });
                return;
            }

            // --- Apenas POST /rpc é aceito para comandos ---
            if (req.Method != "POST" || req.Path != "/rpc")
            {
                WriteJson(stream, 404, new { ok = false, error = "Endpoint não encontrado", request_id = requestId });
                return;
            }

            RpcRequest rpc;
            try
            {
                rpc = JsonSerializer.Deserialize<RpcRequest>(Encoding.UTF8.GetString(req.Body));
            }
            catch (Exception ex)
            {
                WriteJson(stream, 400, new { ok = false, error = "JSON inválido: " + ex.Message, request_id = requestId });
                return;
            }

            // --- Despacha o comando para Commands.Dispatch ---
            try
            {
                var result = Commands.Dispatch(rpc);
                WriteJson(stream, 200, new { ok = true, result, request_id = requestId });
            }
            catch (Exception ex)
            {
                LogException($"[{requestId}] Dispatch " + (rpc?.cmd ?? "?"), ex);

                var payload = new
                {
                    ok = false,
                    // A mensagem é necessária para a IA corrigir a chamada; o stack
                    // trace só é exposto em development_mode.
                    error = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message,
                    request_id = requestId,
                    type = _developmentMode ? ex.GetType().FullName : null,
                    stack = _developmentMode ? ex.ToString() : null
                };

                try { WriteJson(stream, 500, payload); }
                catch (Exception writeEx) { LogException("WriteJson-500", writeEx); }
            }
        }

        private bool IsLoopbackHost(Request req)
        {
            if (!req.Headers.TryGetValue("Host", out var host) || string.IsNullOrWhiteSpace(host)) return true; // HTTP/1.0
            string name = host;
            if (name.StartsWith("["))
            {
                int end = name.IndexOf(']');
                name = end > 0 ? name.Substring(1, end - 1) : name;
            }
            else
            {
                int colon = name.LastIndexOf(':');
                if (colon >= 0) name = name.Substring(0, colon);
            }
            return name.Equals("127.0.0.1") || name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name.Equals("::1");
        }

        private bool IsAuthorized(Request request)
        {
            if (!request.Headers.TryGetValue("X-Revit-Mcp-Token", out var provided)) return false;
            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(_token)) return false;
            var expectedBytes = Encoding.UTF8.GetBytes(_token);
            var actualBytes = Encoding.UTF8.GetBytes(provided);
            return expectedBytes.Length == actualBytes.Length &&
                CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        /// <summary>
        /// Lê %LOCALAPPDATA%\RevitMcp\bridge.json. Se o arquivo ainda não existe
        /// (Revit aberto sem rodar reload-revit-mcp.ps1), gera um token aleatório
        /// e o grava, para que o servidor Python o encontre no mesmo local.
        /// </summary>
        private static (string token, bool developmentMode, int? port) LoadSecurityConfiguration()
        {
            string path = Path.Combine(StateDirectory(), "bridge.json");
            int? envPort = int.TryParse(Environment.GetEnvironmentVariable("REVIT_MCP_PORT"), out var ep) && ep > 0 && ep < 65536 ? ep : (int?)null;
            try
            {
                if (File.Exists(path))
                {
                    using (var json = JsonDocument.Parse(File.ReadAllText(path)))
                    {
                        var root = json.RootElement;
                        var token = root.TryGetProperty("token", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString() : null;
                        var developmentMode = root.TryGetProperty("development_mode", out var dv) && dv.ValueKind == JsonValueKind.True;
                        int? port = root.TryGetProperty("port", out var pv) && pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out var p) && p > 0 && p < 65536
                            ? p : envPort;
                        if (!string.IsNullOrWhiteSpace(token)) return (token, developmentMode, port);
                    }
                }
            }
            catch (Exception ex)
            {
                LogException("bridge.json inválido; um novo token será gerado", ex);
            }

            var bytes = RandomNumberGenerator.GetBytes(32);
            var generated = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var content = JsonSerializer.Serialize(new { token = generated, development_mode = false, created_at = DateTime.Now.ToString("o") },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, content, new UTF8Encoding(false));
            WriteBridgeLog("bridge.json ausente; token novo gerado em " + path + ".");
            return (generated, false, envPort);
        }

        // =====================================================================
        // Utilitários de resposta e log
        // =====================================================================

        private static string ReasonPhrase(int status) => status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            411 => "Length Required",
            413 => "Payload Too Large",
            431 => "Request Header Fields Too Large",
            _ => "Internal Server Error"
        };

        private static void WriteJson(NetworkStream stream, int status, object payload)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(payload);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        /// <summary>
        /// Grava exceções em arquivo de log para diagnóstico.
        /// Caminho: %LOCALAPPDATA%\RevitMcp\logs\revit_mcp.log
        /// </summary>
        private static readonly object _logLock = new object();
        internal static void LogException(string where, Exception ex)
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
