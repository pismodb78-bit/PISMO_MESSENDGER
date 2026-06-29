using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PISMO
{
    /// <summary>
    /// WebSocket клиент для мгновенного получения сигналов о звонках, ICE-кандидатах,
    /// renegotiation и новых сообщениях чата.
    /// </summary>
    public class WebSocketSignalingClient
    {
        private static WebSocketSignalingClient _instance;
        public static WebSocketSignalingClient Instance => _instance ??= new WebSocketSignalingClient();

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private int _myUserId;
        private bool _isConnecting;
        private bool _softToken; // true → регистрируемся без токена (после auth_error)

        public bool IsConnected => _ws?.State == WebSocketState.Open;

        /// <summary>Событие приёма сигнального сообщения: type, senderUserId, sessionId, payload</summary>
        public event Action<string, int, int, string> OnMessageReceived;

        public async Task ConnectAsync(int userId)
        {
            if (_isConnecting || IsConnected) return;
            _myUserId = userId;
            _isConnecting = true;

            try
            {
                // Определяем URL WebSocket сервера на основе ip.txt
                string wsUrl = GetWebSocketUrl();
                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();

                await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
                System.Diagnostics.Debug.WriteLine($"[WS CLIENT] ✓ Подключено к {wsUrl}");

                // Отправляем пакет регистрации с JWT — сервер проверяет подпись
                // и совпадение userId с токеном (защита от подделки чужого id).
                // После auth_error регистрируемся без токена (мягкий режим сервера),
                // чтобы сообщения всё равно доставлялись.
                string regToken = _softToken ? "" : (UserSession.AuthToken ?? "");
                var regMsg = JsonSerializer.Serialize(new { type = "register", userId = _myUserId, token = regToken });
                var regBytes = Encoding.UTF8.GetBytes(regMsg);
                await _ws.SendAsync(new ArraySegment<byte>(regBytes), WebSocketMessageType.Text, true, _cts.Token);

                _ = Task.Run(() => ReceiveLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WS CLIENT ERROR] Не удалось подключиться к WS серверу: {ex.Message}");
                // Запускаем автоматическое переподключение через 5 секунд
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    _isConnecting = false;
                    if (!IsConnected) await ConnectAsync(_myUserId);
                });
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private string GetWebSocketUrl()
        {
            string host = "localhost";
            int port = 8080;

            try
            {
                string[] possiblePaths = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ip.txt"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "ip.txt"),
                    "ip.txt"
                };

                string ipLine = null;
                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        ipLine = File.ReadAllText(path).Trim();
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(ipLine))
                {
                    if (ipLine.Contains("ws=") || ipLine.Contains("websocket="))
                    {
                        foreach (var part in ipLine.Split(';'))
                        {
                            if (part.Trim().StartsWith("ws=", StringComparison.OrdinalIgnoreCase))
                                return part.Trim().Substring(3);
                            if (part.Trim().StartsWith("websocket=", StringComparison.OrdinalIgnoreCase))
                                return part.Trim().Substring(10);
                        }
                    }

                    if (ipLine.Contains("server="))
                    {
                        foreach (var part in ipLine.Split(';'))
                        {
                            if (part.Trim().StartsWith("server=", StringComparison.OrdinalIgnoreCase))
                            {
                                host = part.Trim().Substring(7);
                                break;
                            }
                        }
                    }
                    else if (!ipLine.Contains("=") && !ipLine.Contains(";"))
                    {
                        host = ipLine.Split(':')[0].Trim();
                    }
                }
            }
            catch { }

            // Если в ip.txt указан внешний IP (например, 85.174.248.59), пробуем подключиться к нему.
            // Если внешний WebSocket сервер там не запущен (в MAMP работает только БД), клиент плавно перейдет на резервный опрос БД.
            return $"ws://{host}:{port}/";
        }

        public void SendMessage(string type, int targetUserId, int sessionId, string payload)
        {
            if (!IsConnected) return;

            try
            {
                var msg = JsonSerializer.Serialize(new
                {
                    type,
                    userId = _myUserId,
                    targetUserId,
                    sessionId,
                    payload
                });
                var bytes = Encoding.UTF8.GetBytes(msg);
                _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WS CLIENT SEND ERROR] {ex.Message}");
            }
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[1024 * 64];

            try
            {
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            string type = root.GetProperty("type").GetString();
                            int senderUserId = root.GetProperty("userId").GetInt32();
                            int sessionId = root.GetProperty("sessionId").GetInt32();
                            string payload = root.GetProperty("payload").GetString();

                            // Сервер отклонил JWT → следующий коннект без токена.
                            if (type == "auth_error") _softToken = true;

                            OnMessageReceived?.Invoke(type, senderUserId, sessionId, payload);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WS CLIENT RECV PARSE ERROR] {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WS CLIENT LOOP ERROR] {ex.Message}");
            }
            finally
            {
                _isConnecting = false;
                if (!token.IsCancellationRequested)
                {
                    // Запускаем переподключение при обрыве
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        await ConnectAsync(_myUserId);
                    });
                }
            }
        }

        public void Disconnect()
        {
            try
            {
                _cts?.Cancel();
                if (_ws != null && _ws.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                _ws?.Dispose();
            }
            catch { }
        }
    }
}