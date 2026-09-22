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
        /// <summary>
        /// Кто сейчас на связи: пользователь → ВСЕ его соединения.
        ///
        /// Раньше здесь лежало одно соединение на пользователя, и второй вход
        /// того же человека — телефон рядом с компьютером — просто вытеснял
        /// первый: сокет оставался открытым, но не получал уже ничего. То есть
        /// между СВОИМИ устройствами мгновенной доставки не было вовсе, и
        /// всякое общее состояние (прочитано, закрепления) доезжало только
        /// сверкой, через секунды. Внешний ws-сервер (ws-server/server.js)
        /// хранил набор с самого начала — теперь обе стороны ведут себя одинаково.
        ///
        /// ConcurrentDictionary вместо множества: готового потокобезопасного
        /// набора в стандартной библиотеке нет, а значение здесь не нужно.
        /// </summary>
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<WebSocket, byte>> _clients = new();

        private void AddClient(int userId, WebSocket ws) =>
            _clients.GetOrAdd(userId, _ => new ConcurrentDictionary<WebSocket, byte>())[ws] = 0;

        private void RemoveClient(int userId, WebSocket ws)
        {
            if (!_clients.TryGetValue(userId, out var set)) return;
            set.TryRemove(ws, out _);
            if (set.IsEmpty) _clients.TryRemove(userId, out _);
        }
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
                foreach (var sock in pair.Value.Keys)
                {
                    try { sock.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None); } catch { }
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

                            // Проверка живости. Обрабатываем ДО пересылки и
                            // отвечаем сразу, даже до register.
                            //
                            // Иначе ping уходил в общую ветку, а там нет
                            // targetUserId — и «ноль» означает широковещательно.
                            // То есть каждая проверка связи каждого клиента
                            // рассылалась ВСЕМ подключённым, каждые семь секунд
                            // от каждого. Node-сервер (ws-server/server.js) так
                            // не делает — здесь была расходящаяся реализация
                            // одного и того же протокола.
                            if (type == "ping")
                            {
                                string t = root.TryGetProperty("t", out var tProp) ? tProp.GetRawText() : "0";
                                var pong = Encoding.UTF8.GetBytes("{\"type\":\"pong\",\"t\":" + t + "}");
                                try
                                {
                                    await ws.SendAsync(new ArraySegment<byte>(pong),
                                        WebSocketMessageType.Text, true, token);
                                }
                                catch { }
                                continue;
                            }

                            if (type == "register")
                            {
                                registeredUserId = root.GetProperty("userId").GetInt32();
                                AddClient(registeredUserId, ws);
                                System.Diagnostics.Debug.WriteLine($"[WS SERVER] Клиент зарегистрирован: userId={registeredUserId}");
                            }
                            else
                            {
                                // Пересылка сообщения целевому пользователю или широковещательно
                                int targetUserId = root.TryGetProperty("targetUserId", out var tEl)
                                                   && tEl.TryGetInt32(out int tVal) ? tVal : 0;
                                var sendBytes = Encoding.UTF8.GetBytes(msgJson);

                                // Отправителю самому не шлём — но отправитель это
                                // СОЕДИНЕНИЕ, а не пользователь: остальные свои
                                // устройства получить должны.
                                async Task SendTo(ConcurrentDictionary<WebSocket, byte> set)
                                {
                                    if (set == null) return;
                                    foreach (var sock in set.Keys)
                                    {
                                        if (ReferenceEquals(sock, ws) || sock.State != WebSocketState.Open) continue;
                                        try
                                        {
                                            await sock.SendAsync(new ArraySegment<byte>(sendBytes),
                                                WebSocketMessageType.Text, true, token);
                                        }
                                        catch { }
                                    }
                                }

                                if (targetUserId > 0)
                                {
                                    _clients.TryGetValue(targetUserId, out var targetSet);
                                    await SendTo(targetSet);
                                    // И своим же остальным устройствам.
                                    if (targetUserId != registeredUserId && registeredUserId > 0)
                                    {
                                        _clients.TryGetValue(registeredUserId, out var mine);
                                        await SendTo(mine);
                                    }
                                }
                                else
                                {
                                    // Широковещательная рассылка (например, для групп).
                                    foreach (var pair in _clients) await SendTo(pair.Value);
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
                    RemoveClient(registeredUserId, ws);
                    System.Diagnostics.Debug.WriteLine($"[WS SERVER] Клиент отключен: userId={registeredUserId}");
                }
                try { if (ws.State != WebSocketState.Closed) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None); } catch { }
                ws.Dispose();
            }
        }
    }
}