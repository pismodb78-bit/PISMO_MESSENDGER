using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;

namespace PISMO
{
    /// <summary>
    /// WebRTC-транспорт с поддержкой TURN.
    /// Заменяет старый прямой UDP-транспорт.
    /// Сигналинг (SDP/ICE) по-прежнему идёт через БД — как раньше шли ip/port.
    ///
    /// Протокол обмена через БД (поля caller_sdp, callee_sdp, ice_candidates):
    ///   Звонящий:  записывает offer SDP → ждёт answer SDP от принимающего
    ///   Принимающий: читает offer → записывает answer SDP
    ///   Оба: дописывают ICE-кандидатов по мере их сбора
    /// </summary>
    public class CallTransport : IDisposable
    {
        // Типы пакетов — совместимо со старым кодом CallForm
        public const byte TypeAudio = 1;
        public const byte TypeVideo = 2;
        public const byte TypeScreen = 3;
        public const byte TypeScreenAudio = 4; // звук демонстрации экрана (системный звук), отдельно от микрофона
        public const byte TypeScreenStop = 5;  // явный сигнал об остановке демонстрации экрана
        public const byte TypeHangup = 99;
        public const byte TypeKeepAlive = 98;

        // Максимальный размер одного сообщения DataChannel (байт)
        private const int MaxChunkSize = 60_000;

        private RTCPeerConnection _pc;
        private RTCDataChannel _dc;
        private volatile bool _running = false;
        private volatile bool _disposed = false;

        /// <summary>Вызывается при получении данных от собеседника.</summary>
        public event Action<byte, byte[]> FrameReceived;

        /// <summary>Вызывается когда DataChannel закрылся / peer отключился.</summary>
        public event Action Disconnected;

        /// <summary>Вызывается когда соединение установлено (DataChannel открыт).</summary>
        public event Action Connected;

        /// <summary>
        /// Вызывается когда собран новый ICE-кандидат — его нужно передать собеседнику через БД.
        /// string = JSON кандидата (RTCIceCandidateInit.ToString())
        /// </summary>
        public event Action<string> IceCandidateReady;

        /// <summary>
        /// Вызывается когда ICE-сбор завершён — можно сразу отправить финальный SDP.
        /// </summary>
        public event Action GatheringComplete;

        // ─────────────────────────────────────────────────────────────
        //  СОЗДАНИЕ PeerConnection
        // ─────────────────────────────────────────────────────────────
        private RTCConfiguration BuildRtcConfig()
        {
            System.Diagnostics.Debug.WriteLine($"[TURN CFG] Enabled={TurnSettings.TurnEnabled}");
            System.Diagnostics.Debug.WriteLine($"[TURN CFG] Address={TurnSettings.TurnServerAddress}");
            System.Diagnostics.Debug.WriteLine($"[TURN CFG] Transport={TurnSettings.TurnTransport}");
            var (u, p) = TurnSettings.GetCredentials();
            System.Diagnostics.Debug.WriteLine($"[TURN CFG] username={u}");
            System.Diagnostics.Debug.WriteLine($"[TURN CFG] password={p}");

         

            var iceServers = new List<RTCIceServer>();
            var (username, password) = TurnSettings.GetCredentials();
            string transport = TurnSettings.TurnTransport ?? "udp";

            iceServers.Add(new RTCIceServer
            {
                urls = "stun:stun.l.google.com:19302"
            });

            iceServers.Add(new RTCIceServer
            {
                urls = $"turn:{TurnSettings.TurnServerAddress}:{TurnSettings.TurnServerPort}?transport={transport}",
                username = username,
                credential = password,
                credentialType = RTCIceCredentialType.password
            });

            return new RTCConfiguration
            {
                iceServers = iceServers,
                
                X_BindAddress = System.Net.IPAddress.Any
            };
        }

        private void InitPeerConnection()
        {
            _pc = new RTCPeerConnection(BuildRtcConfig());

            // DataChannel для нашего бинарного протокола (аудио/видео/экран/хангап)
            _pc.ondatachannel += channel =>
            {
                System.Diagnostics.Debug.WriteLine($"[WebRTC] DataChannel получен от peer: {channel.label}");
                BindDataChannel(channel);
            };

            // ICE-кандидаты — передаём через БД
            _pc.onicecandidate += candidate =>
            {
                if (candidate == null) return; // null = сбор завершён
                try
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        candidate = candidate.candidate,
                        sdpMid = candidate.sdpMid,
                        sdpMLineIndex = candidate.sdpMLineIndex
                    });
                    System.Diagnostics.Debug.WriteLine($"[ICE] Кандидат готов: {candidate.candidate}");
                    IceCandidateReady?.Invoke(json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ICE] Ошибка сериализации кандидата: {ex.Message}");
                }
            };


            _pc.onconnectionstatechange += state =>
            {
                System.Diagnostics.Debug.WriteLine($"[WebRTC] Connection state: {state}");
                if (state == RTCPeerConnectionState.failed ||
                    state == RTCPeerConnectionState.disconnected ||
                    state == RTCPeerConnectionState.closed)
                {
                    if (_running)
                    {
                        _running = false;
                        Disconnected?.Invoke();
                    }
                }
                else if (state == RTCPeerConnectionState.connected)
                {
                    _running = true;
                    System.Diagnostics.Debug.WriteLine("[WebRTC] ✅ Соединение установлено!");
                }
            };
            _pc.onicecandidateerror += (candidate, error) =>
            {
                System.Diagnostics.Debug.WriteLine($"[ICE ERROR] {error} — {candidate}");
            };
        }

        private void BindDataChannel(RTCDataChannel dc)
        {
            _dc = dc;

            _dc.onopen += () =>
            {
                _running = true;
                System.Diagnostics.Debug.WriteLine("[DC] DataChannel открыт");
                Connected?.Invoke();
            };

            _dc.onclose += () =>
            {
                System.Diagnostics.Debug.WriteLine("[DC] DataChannel закрыт");
                if (_running)
                {
                    _running = false;
                    Disconnected?.Invoke();
                }
            };

            _dc.onmessage += (dc, proto, data) =>
            {
                if (data == null || data.Length < 1) return;

                byte type = data[0];

                if (type == TypeHangup)
                {
                    _running = false;
                    Disconnected?.Invoke();
                    return;
                }
                if (type == TypeKeepAlive) return;

                var payload = new byte[data.Length - 1];
                if (payload.Length > 0)
                    Buffer.BlockCopy(data, 1, payload, 0, payload.Length);

                FrameReceived?.Invoke(type, payload);
            };
        }

        // ─────────────────────────────────────────────────────────────
        //  ЗВОНЯЩИЙ: создать offer
        // ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Звонящий вызывает этот метод.
        /// Возвращает SDP offer (строку) для записи в БД.
        /// </summary>
        public async Task<string> CreateOfferAsync()
        {
            InitPeerConnection();

            var dc = await _pc.createDataChannel("media", new RTCDataChannelInit { ordered = false });
            BindDataChannel(dc);

            var offer = _pc.createOffer();

            // Подписываемся ДО setLocalDescription
            var tcs = new TaskCompletionSource<bool>();
            _pc.onicegatheringstatechange += state =>
            {
                System.Diagnostics.Debug.WriteLine($"[ICE Offer] Gathering state: {state}");
                if (state == RTCIceGatheringState.complete)
                    tcs.TrySetResult(true);
            };

            await _pc.setLocalDescription(offer);

            await Task.WhenAny(tcs.Task, Task.Delay(8000));

            System.Diagnostics.Debug.WriteLine($"[SDP] Offer с кандидатами:\n{_pc.localDescription.sdp}");
            return _pc.localDescription.sdp.ToString();
        }

        // ─────────────────────────────────────────────────────────────
        //  ПРИНИМАЮЩИЙ: применить offer, создать answer
        // ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Принимающий вызывает этот метод.
        /// offerSdp — строка из БД (от звонящего).
        /// Возвращает SDP answer для записи в БД.
        /// </summary>
        public async Task<string> CreateAnswerAsync(string offerSdp)
        {
            InitPeerConnection();

            var offer = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offerSdp
            };
            var setResult = _pc.setRemoteDescription(offer);
            if (setResult != SetDescriptionResultEnum.OK)
                throw new Exception($"setRemoteDescription failed: {setResult}");

            var answer = _pc.createAnswer();

            // Принудительно passive — callee всегда passive
            answer.sdp = answer.sdp.Replace("a=setup:active", "a=setup:passive");

            // Подписываемся ДО setLocalDescription
            var tcs = new TaskCompletionSource<bool>();
            _pc.onicegatheringstatechange += state =>
            {
                System.Diagnostics.Debug.WriteLine($"[ICE Answer] Gathering state: {state}");
                if (state == RTCIceGatheringState.complete)
                    tcs.TrySetResult(true);
            };

            await _pc.setLocalDescription(answer);
            await Task.WhenAny(tcs.Task, Task.Delay(8000));

            // Возвращаем модифицированный SDP, а не из localDescription
            string resultSdp = _pc.localDescription.sdp.ToString()
                .Replace("a=setup:active", "a=setup:passive");
            System.Diagnostics.Debug.WriteLine($"[SDP] Answer с кандидатами:\n{resultSdp}");
            return resultSdp;
        }

        // ─────────────────────────────────────────────────────────────
        //  ЗВОНЯЩИЙ: применить answer от принимающего
        // ─────────────────────────────────────────────────────────────
        public void ApplyAnswer(string answerSdp)
        {
            var answer = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answerSdp
            };
            var result = _pc.setRemoteDescription(answer);
            System.Diagnostics.Debug.WriteLine($"[SDP] Answer применён: {result}");
        }

        // ─────────────────────────────────────────────────────────────
        //  ДОБАВИТЬ ICE-кандидата от собеседника
        // ─────────────────────────────────────────────────────────────
        public void AddRemoteIceCandidate(string candidateJson)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(candidateJson);
                var root = doc.RootElement;

                string candidate = root.GetProperty("candidate").GetString();
                string sdpMid = root.TryGetProperty("sdpMid", out var m) ? m.GetString() : "0";
                ushort mLineIndex = root.TryGetProperty("sdpMLineIndex", out var ml)
                    ? (ushort)ml.GetUInt16() : (ushort)0;

                var init = new RTCIceCandidateInit
                {
                    candidate = candidate,
                    sdpMid = sdpMid,
                    sdpMLineIndex = mLineIndex
                };
                _pc.addIceCandidate(init);
                System.Diagnostics.Debug.WriteLine($"[ICE] Кандидат добавлен: {candidate}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ICE] Ошибка добавления кандидата: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  ОТПРАВКА ДАННЫХ (совместимо со старым CallForm)
        // ─────────────────────────────────────────────────────────────
        public void Send(byte type, byte[] payload)
        {
            if (_dc == null || !_running) return;

            try
            {
                var packet = new byte[1 + payload.Length];
                packet[0] = type;
                if (payload.Length > 0)
                    Buffer.BlockCopy(payload, 0, packet, 1, payload.Length);

                // DataChannel надёжно справляется с UDP-ограничениями сам,
                // но ограничиваем до разумного предела
                if (packet.Length <= MaxChunkSize)
                {
                    _dc.send(packet);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DC SEND] Пакет слишком большой ({packet.Length} байт), пропускаем");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DC SEND ERROR] {ex.Message}");
            }
        }

        public void SendHangup()
        {
            try
            {
                if (_dc != null && _running)
                    _dc.send(new byte[] { TypeHangup });
            }
            catch { }
        }

        // backward compat alias
        public void SendHangup_Old() => SendHangup();

        // ─────────────────────────────────────────────────────────────
        //  СТАРЫЙ API — оставляем для совместимости с CallForm
        //  (StartListening / Connect больше не нужны, но оставим заглушки
        //   чтобы не ломать существующий код до рефакторинга CallForm)
        // ─────────────────────────────────────────────────────────────
        [Obsolete("Используйте WebRTC API: CreateOfferAsync / CreateAnswerAsync")]
        public (string ip, int port) StartListening()
        {
            // В WebRTC-режиме этот метод не используется.
            // IP/port больше не нужны — соединение идёт через ICE.
            return (TurnSettings.TurnServerAddress, TurnSettings.TurnServerPort);
        }

        [Obsolete("Используйте WebRTC API: ApplyAnswer + AddRemoteIceCandidate")]
        public void Connect(string remoteIp, int remotePort)
        {
            // Не используется в WebRTC-режиме
        }

        // ─────────────────────────────────────────────────────────────
        //  DISPOSE
        // ─────────────────────────────────────────────────────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;

            try { _dc?.close(); } catch { }
            try { _pc?.close(); } catch { }
            try { _pc?.Dispose(); } catch { }
        }
    }
}