using UnityEngine;
using System;
using System.Collections;
using System.Collections.Concurrent;
using WebSocketSharp;
using Newtonsoft.Json.Linq;

namespace Elements.Crossfire
{
    using Model;

    public class WebSocketSignalingClient : MonoBehaviour, ISignalingClient
    {
        public event Action OnConnected;
        public event Action<SignalingMessage> OnMessageReceived;
        public event Action OnDisconnected;
        public event Action<int> OnReconnectAttempt;
        public event Action<float> OnReconnectCountdown;
        public event Action<string> OnSignalingError;

        public bool IsConnected => ws?.IsAlive ?? false;

        private WebSocket ws;
        private NetworkSessionConfig config;
        private bool intentionalClose;
        private readonly ConcurrentQueue<Action> mainThreadQueue = new();
        private readonly ConcurrentQueue<string> outboundQueue = new();

        private int reconnectAttempts = 0;
        private Coroutine reconnectCoroutine;

        private Logger logger = LoggerFactory.GetLogger("WebSocketSignalingClient");

        private void Update()
        {
            while (mainThreadQueue.TryDequeue(out var action))
                action();
        }

        public void Connect(string url, string profileId, string sessionToken)
        {
            config = new NetworkSessionConfig
            {
                serverHost = url,
                profileId = profileId,
                sessionToken = sessionToken
            };

            DoConnect();
        }

        private void DoConnect()
        {
            string url = $"{config.serverHost}/match";
            logger.Log($"[SignalingClient] Connecting to {url} (attempt {reconnectAttempts + 1})");

            ws = new WebSocket(url);

            ws.OnOpen += (s, e) =>
            {
                logger.Log($"[SignalingClient] Connected");
                reconnectAttempts = 0; // Reset on successful connection
                FlushOutboundQueue();
                mainThreadQueue.Enqueue(() => OnConnected?.Invoke());
            };

            ws.OnMessage += (s, e) =>
            {
                mainThreadQueue.Enqueue(() => ProcessMessage(e.Data));
            };

            ws.OnError += (s, e) =>
            {
                string errorMsg = $"WebSocket error: {e.Message}";
                logger.LogError($"[SignalingClient] {errorMsg}");
                mainThreadQueue.Enqueue(() => OnSignalingError?.Invoke(errorMsg));
            };

            ws.OnClose += (s, e) =>
            {
                logger.Log($"[SignalingClient] Closed: {e.Reason}");
                mainThreadQueue.Enqueue(() => OnDisconnected?.Invoke());

                if (!intentionalClose && config.autoReconnect)
                {
                    reconnectCoroutine = StartCoroutine(Reconnect());
                }
            };

            ws.ConnectAsync();
        }
        
        private void ProcessMessage(string raw)
        {
            logger.Log("Processing message: " + raw);

            try
            {
                var json = JObject.Parse(raw);
                var type = (string)json["type"];

                if (!Enum.TryParse(type, out MessageType messageType))
                {
                    logger.LogError($"Received unmapped message type {type}");
                    return;
                }

                var message = new SignalingMessage
                {
                    type = messageType,
                    profileId = (string)json["profileId"],
                    recipientProfileId = (string)json["recipientProfileId"],
                    payload = json.ToString(),
                    matchId = (string)json["matchId"]
                };

                OnMessageReceived?.Invoke(message);
            }
            catch (Exception e)
            {
                logger.LogError($"[SignalingClient] Failed to parse message: {e.Message}");
            }
        }

        public void SendWSMessage(string message)
        {
            if (IsConnected)
            {
                logger.Log("Sending message: " + message);
                ws.Send(message);
            }
            else
            {
                logger.Log("Enqueuing message");
                outboundQueue.Enqueue(message);
            }
        }

        private void FlushOutboundQueue()
        {
            logger.Log("Flushing queue");
            while (outboundQueue.TryDequeue(out string message))
            {
                if (IsConnected)
                {
                    logger.Log("Sending message: " + message);
                    ws.Send(message);
                }
                else
                {
                    logger.Log("Not connected re-enqueue");
                    outboundQueue.Enqueue(message);
                    break;
                }
            }
        }

        private IEnumerator Reconnect()
        {
            reconnectAttempts++;
            OnReconnectAttempt?.Invoke(reconnectAttempts);

            float countdown = config.reconnectDelay;
            while (countdown > 0)
            {
                OnReconnectCountdown?.Invoke(countdown);
                yield return new WaitForSeconds(1f);
                countdown -= 1f;
            }

            try
            {
                DoConnect();
            }
            catch (Exception e)
            {
                OnSignalingError?.Invoke($"Reconnect failed: {e.Message}");

                if (reconnectAttempts < 5) // Max retries
                {
                    reconnectCoroutine = StartCoroutine(Reconnect());
                }
            }
        }

        public void Disconnect()
        {
            intentionalClose = true;
            ws?.Close();
            outboundQueue.Clear();

            if (reconnectCoroutine != null)
                StopCoroutine(reconnectCoroutine);
        }       

        private void OnDestroy()
        {
            Disconnect();
        }
    }
}