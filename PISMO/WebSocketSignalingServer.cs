using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PISMO
{
    /// <summary>
    /// Встроенный WebSocket сервер для мгновенного сигнального обмена (WebRTC, звонки, чат).
    /// Заменяет необходимость постоянного опроса (polling) базы данных.
    /// Работает в фоновом потоке.
    /// </summary>
    public class WebSocketSignalingServer
    {
        private static WebSocketSignalingServer _instance;
        public static WebSocketSignalingServer Instance => _instance ??= new WebSocketSignalingServer();

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private readonly ConcurrentDictionary<int, WebSocket> _clients = new();
        public bool IsRunning { get; private set; }

        public void Start(int port = 8080)
        {
            if (IsRunning) return;

            try
            {
                _listener = new HttpListener();
                // Используем localhost и 127.0.0.1, так как они не требуют прав администратора в Windows (в отличие от +)
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _cts = new CancellationTokenSource();
                IsRunning = true;

                Task.Run(() => ListenLoop(_cts.Token));
                System.Diagnostics.Debug.WriteLine($"[WS SERVER] Сервер запущен на порту {port}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WS SERVER ERROR] Не удалось запустить сервер: {ex.Message}");
                IsRunning = false;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                foreach (var pair in _clients)
                {
                    try { pair.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None); } catch { }
                }
                _clients.Clear();
                IsRunning = false;
                System.Diagnostics.Debug.WriteLine("[WS SERVER] Сервер остановлен");
            }
            catch { }
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        var wsContext = await context.AcceptWebSocketAsync(null);
                        _ = Task.Run(() => HandleClientAsync(wsContext.WebSocket, token));
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WS SERVER LISTEN ERROR] {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(WebSocket ws, CancellationToken token)
        {
            int registeredUserId = 0;
            var buffer = new byte[1024 * 64];

            try
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string msgJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        try
                        {
                            using var doc = JsonDocument.Parse(msgJson);
                            var root = doc.RootElement;
                            string type = root.GetProperty("type").GetString();

                            if (type == "register")
                            {
                                registeredUserId = root.GetProperty("userId").GetInt32();
                                _clients[registeredUserId] = ws;
                                System.Diagnostics.Debug.WriteLine($"[WS SERVER] Клиент зарегистрирован: userId={registeredUserId}");
                            }
                            else
                            {
                                // Пересылка сообщения целевому пользователю или широковещательно
                                int targetUserId = root.GetProperty("targetUserId").GetInt32();
                                if (targetUserId > 0)
                                {
                                    if (_clients.TryGetValue(targetUserId, out var targetWs) && targetWs.State == WebSocketState.Open)
                                    {
                                        var sendBytes = Encoding.UTF8.GetBytes(msgJson);
                                        await targetWs.SendAsync(new ArraySegment<byte>(sendBytes), WebSocketMessageType.Text, true, token);
                                    }
                                }
                                else
                                {
                                    // Широковещательная рассылка (например, для групп)
                                    var sendBytes = Encoding.UTF8.GetBytes(msgJson);
                                    foreach (var pair in _clients)
                                    {
                                        if (pair.Key != registeredUserId && pair.Value.State == WebSocketState.Open)
                                        {
                                            await pair.Value.SendAsync(new ArraySegment<byte>(sendBytes), WebSocketMessageType.Text, true, token);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WS SERVER MSG PARSE ERROR] {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WS SERVER CLIENT ERROR] {ex.Message}");
            }
            finally
            {
                if (registeredUserId > 0)
                {
                    _clients.TryRemove(registeredUserId, out _);
                    System.Diagnostics.Debug.WriteLine($"[WS SERVER] Клиент отключен: userId={registeredUserId}");
                }
                try { if (ws.State != WebSocketState.Closed) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None); } catch { }
                ws.Dispose();
            }
        }
    }
}