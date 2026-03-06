using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.WebRTC;
using UnityEngine;

namespace Elements.Crossfire
{
    using Model;

    /// <summary>
    /// Enhanced NGO transport over Unity.WebRTC with stats tracking.
    /// Requires an external signaling layer to exchange SDP/ICE.
    /// </summary>
    public partial class WebRtcTransport : NetworkTransport, IWebRtcTransportStats
    {
        public enum Mode
        {
            REMOTE,
            LOCAL // Local testing only, ignores STUN
        }

        [SerializeField] private Mode mode = Mode.REMOTE;

        [Header("Stats Configuration")]
        [SerializeField] private bool enableStatsCollection = true;
        [SerializeField] private float statsUpdateInterval = CrossfireConstants.TransportStatsInterval;

        [Header("Connection Recovery")]
        [SerializeField] private int webRtcOperationMaxRetries = CrossfireConstants.WebRtcOperationMaxRetries;

        // Internal plumbing events — consumed only by WebRtcTransportAdapter
        internal event Action<ulong, RTCSessionDescription> OnSendOffer;
        internal event Action<ulong, RTCSessionDescription> OnSendAnswer;
        internal event Action<ulong, RTCIceCandidate> OnSendIce;
        internal event Action<ulong, RTCIceConnectionState> OnPeerConnectionStateChanged;
        internal event Action<ulong> OnDataChannelReady;
        internal event Action<ulong, ParsedWebRTCStats> OnStatsUpdated;

        private readonly Queue<(NetworkEvent ev, ulong clientId, ArraySegment<byte> payload, float receiveTime)> events = new();

        private readonly Dictionary<ulong, RTCPeerConnection> peerConnections = new();
        private readonly Dictionary<ulong, RTCDataChannel> dataChannels = new();
        private readonly HashSet<ulong> readyPeers = new();

        private ulong serverPeerId;

        private Logger logger = LoggerFactory.GetLogger("WebRtcTransport");

        private bool initialized = false;

        private void Update()
        {
            if (initialized)
                WebRTC.Update();
        }

#region Network Transport (NGO)

        public override void Initialize(NetworkManager networkManager = null)
        {
            logger.Log("Initializing WebRTC");
            initialized = true;

            //  Start stats collection
            if (enableStatsCollection)
            {
                StartStatsCollection();
            }
        }

        public override bool StartClient()
        {
            logger.Log("Starting Client");
            return true;
        }

        public override bool StartServer()
        {
            logger.Log("Starting Server");
            return true;
        }

        public override void Shutdown()
        {
            logger.Log("Shutting Down WebRTC");

            //  Stop stats collection
            StopStatsCollection();

            foreach (var dataChannel in dataChannels.Values)
                dataChannel.Close();

            foreach (var peerConnection in peerConnections.Values)
                peerConnection.Close();

            peerConnections.Clear();
            dataChannels.Clear();
            readyPeers.Clear();
            events.Clear();

            //  Clear stats
            ClearStatsData();
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            logger.Log($"DisconnectRemoteClient({clientId})");

            if (dataChannels.Remove(clientId, out var dataChannel))
                dataChannel.Close();

            if (peerConnections.Remove(clientId, out var peerConnection))
                peerConnection.Close();

            readyPeers.Remove(clientId);
            CleanupPeerStats(clientId);
            Enqueue(NetworkEvent.Disconnect, clientId, default);
        }

        public override void DisconnectLocalClient()
        {
            logger.Log("Disconnecting Local Client");

            if (serverPeerId != 0)
                DisconnectRemoteClient(serverPeerId);
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            //  Return actual measured latency if available
            if (measuredLatencies.TryGetValue(clientId, out var latency))
            {
                return (ulong)latency;
            }

            return 0;
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            if (events.Count > 0)
            {
                var (ev, id, data, t) = events.Dequeue();
                clientId = id;
                payload = data;
                receiveTime = t;

                logger.Log($"Polling event " + ev);

                return ev;
            }

            clientId = 0;
            payload = default;
            receiveTime = 0;

            return NetworkEvent.Nothing;
        }

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery delivery)
        {
            if (!readyPeers.Contains(clientId))
            {
                logger.Log($"[Transport.Send] Peer {clientId} not ready yet, skipping send.");
                return;
            }

            //logger.Log($"[Transport.Send] to={clientId} len={payload.Count}");

            if (clientId == 0)
            {
                logger.LogWarning("[Transport.Send] Ignoring send to self (host id=0)");
                return;
            }

            if (dataChannels.TryGetValue(clientId, out var dc) && dc.ReadyState == RTCDataChannelState.Open)
            {
                dc.Send(payload.ToArray());
            }
            else
            {
                logger.LogWarning($"[Transport.Send] data channel for {clientId} not found or not open. ReadyState={(dataChannels.TryGetValue(clientId, out var ch) ? ch.ReadyState.ToString() : "missing")}");
            }
        }

        public override bool IsSupported => true;

        public override ulong ServerClientId => serverPeerId;

        public void SetServerClientId(ulong id)
        {
            serverPeerId = id;
        }

#endregion
#region WebRTC

        public void BeginConnection(ulong remoteId, bool isOfferer)
        {
            logger.Log($"Beginning WebRTC Connection with remoteId: {remoteId} and isOfferer: {isOfferer}");

            var peerConnection = CreateAndConfigurePeerConnection(remoteId);

            if (isOfferer)
            {
                logger.Log("Creating data channel");
                var dc = peerConnection.CreateDataChannel("game");
                WireDataChannel(remoteId, dc);
                StartCoroutine(CreateOfferAndSend(peerConnection, remoteId));
            }
        }

        public void InjectEvent(NetworkEvent ev, ulong id, ArraySegment<byte> data)
        {
            Enqueue(ev, id, data);
        }

        public void OnRemoteOffer(ulong remoteId, RTCSessionDescription offer)
        {
            logger.Log($"OnRemoteOffer from {remoteId}");

            if (!peerConnections.TryGetValue(remoteId, out var pc))
            {
                logger.Log($"No PC for {remoteId} - creating one now");
                pc = CreateAndConfigurePeerConnection(remoteId);
                peerConnections[remoteId] = pc;
            }

            StartCoroutine(RespondToOffer(pc, remoteId, offer));
        }

        public void OnRemoteAnswer(ulong remoteId, RTCSessionDescription answer)
        {
            if (peerConnections.TryGetValue(remoteId, out var pc))
            {
                logger.Log("Handling remote answer from id " + remoteId);
                StartCoroutine(SetRemoteDesc(pc, answer));
            }
        }

        public void OnRemoteIce(ulong remoteId, RTCIceCandidate candidate)
        {
            if (candidate == null) return;

            if (peerConnections.TryGetValue(remoteId, out var pc))
            {
                pc.AddIceCandidate(candidate);
            }
        }

#endregion
#region Data Channel

        private void WireDataChannel(ulong remoteId, RTCDataChannel channel)
        {
            logger.Log($"Wiring data channel for {remoteId}, label={channel.Label}");
            dataChannels[remoteId] = channel;

            channel.OnMessage = bytes =>
                Enqueue(NetworkEvent.Data, remoteId, new ArraySegment<byte>(bytes));

            channel.OnClose = () =>
            {
                logger.Log($"DataChannel closed for {remoteId}");

                dataChannels.Remove(remoteId);
                readyPeers.Remove(remoteId);

                CleanupPeerStats(remoteId);

                Enqueue(NetworkEvent.Disconnect, remoteId, default);
            };

            channel.OnOpen = () =>
            {
                logger.Log($"DataChannel open for {remoteId}");

                // Mark peer ready
                TryMarkPeerReady(remoteId);

                // Tell CrossfireClient that a channel is ready.
                OnDataChannelReady?.Invoke(remoteId);
                OnPeerConnectionStateChanged?.Invoke(remoteId, RTCIceConnectionState.Completed);
            };

            if (channel.ReadyState == RTCDataChannelState.Open)
            {
                logger.Log($"DataChannel for {remoteId} already open, manually firing OnOpen");

                channel.OnOpen?.Invoke();
            }
        }

#endregion
#region Peer Ready Detection

        public bool IsPeerReady(ulong clientId)
        {
            return readyPeers.Contains(clientId);
        }

        private bool TryMarkPeerReady(ulong remoteId)
        {
            if (!peerConnections.ContainsKey(remoteId))
                return false;

            if (!dataChannels.TryGetValue(remoteId, out var dc))
                return false;

            if (dc.ReadyState != RTCDataChannelState.Open)
                return false;

            if (readyPeers.Add(remoteId))
            {
                logger.Log($"Peer {remoteId} is fully ready, enqueuing Connect");

                Enqueue(NetworkEvent.Connect, remoteId, default);

                return true;
            }

            return false;
        }

#endregion
#region Coroutines

        private IEnumerator CreateOfferAndSend(RTCPeerConnection pc, ulong remoteId)
        {
            int totalAttempts = webRtcOperationMaxRetries + 1;
            for (int attempt = 0; attempt < totalAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    logger.LogWarning($"[WebRtcTransport] CreateOfferAndSend retry {attempt}/{webRtcOperationMaxRetries} for remoteId={remoteId}");
                    yield return new WaitForSeconds(CrossfireConstants.WebRtcRetryDelay);
                }

                var op = pc.CreateOffer();
                yield return op;
                if (op.IsError)
                {
                    logger.LogError($"[WebRtcTransport] CreateOffer failed for remoteId={remoteId}: {op.Error.message}");
                    continue;
                }

                var desc = op.Desc;
                var setOp = pc.SetLocalDescription(ref desc);
                yield return setOp;
                if (setOp.IsError)
                {
                    logger.LogError($"[WebRtcTransport] SetLocalDescription (offer) failed for remoteId={remoteId}: {setOp.Error.message}");
                    continue;
                }

                OnSendOffer?.Invoke(remoteId, desc);
                yield break;
            }

            logger.LogError($"[WebRtcTransport] CreateOfferAndSend failed after all attempts for remoteId={remoteId}");
        }

        private IEnumerator RespondToOffer(RTCPeerConnection pc, ulong remoteId, RTCSessionDescription offer)
        {
            int totalAttempts = webRtcOperationMaxRetries + 1;
            for (int attempt = 0; attempt < totalAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    logger.LogWarning($"[WebRtcTransport] RespondToOffer retry {attempt}/{webRtcOperationMaxRetries} for remoteId={remoteId}");
                    yield return new WaitForSeconds(CrossfireConstants.WebRtcRetryDelay);
                }

                var setOp = pc.SetRemoteDescription(ref offer);
                yield return setOp;
                if (setOp.IsError)
                {
                    logger.LogError($"[WebRtcTransport] SetRemoteDescription (offer) failed for remoteId={remoteId}: {setOp.Error.message}");
                    continue;
                }

                var answerOp = pc.CreateAnswer();
                yield return answerOp;
                if (answerOp.IsError)
                {
                    logger.LogError($"[WebRtcTransport] CreateAnswer failed for remoteId={remoteId}: {answerOp.Error.message}");
                    continue;
                }

                var desc = answerOp.Desc;
                var setLocalOp = pc.SetLocalDescription(ref desc);
                yield return setLocalOp;
                if (setLocalOp.IsError)
                {
                    logger.LogError($"[WebRtcTransport] SetLocalDescription (answer) failed for remoteId={remoteId}: {setLocalOp.Error.message}");
                    continue;
                }

                OnSendAnswer?.Invoke(remoteId, desc);
                yield break;
            }

            logger.LogError($"[WebRtcTransport] RespondToOffer failed after all attempts for remoteId={remoteId}");
        }

        private IEnumerator SetRemoteDesc(RTCPeerConnection pc, RTCSessionDescription desc)
        {
            var op = pc.SetRemoteDescription(ref desc);
            yield return op;
            if (op.IsError)
            {
                logger.LogError($"[WebRtcTransport] SetRemoteDescription (answer) failed: {op.Error.message}");
            }
        }

        private RTCPeerConnection CreateAndConfigurePeerConnection(ulong remoteId)
        {
            var cfg = GetRTCConfiguration();
            var pc = new RTCPeerConnection(ref cfg);
            peerConnections[remoteId] = pc;

            pc.OnIceCandidate = cand => { if (cand != null) OnSendIce?.Invoke(remoteId, cand); };
            pc.OnDataChannel = ch => WireDataChannel(remoteId, ch);
            pc.OnIceConnectionChange = state =>
            {
                logger.Log($"[PC:{remoteId}] ICE state changed: {state}");

                if (state == RTCIceConnectionState.Connected || state == RTCIceConnectionState.Completed)
                    TryMarkPeerReady(remoteId);

                OnPeerConnectionStateChanged?.Invoke(remoteId, state);
            };

            return pc;
        }

        private RTCConfiguration GetRTCConfiguration()
        {
            return mode switch
            {
                Mode.LOCAL => new RTCConfiguration { iceServers = new RTCIceServer[0] },
                _ => new RTCConfiguration
                {
                    iceServers = new[]
                    {
                        new RTCIceServer
                        {
                            urls = CrossfireConstants.GoogleStunServers
                        }
                    }
                }
            };
        }

        private void Enqueue(NetworkEvent @event, ulong id, ArraySegment<byte> data)
        {
            events.Enqueue((@event, id, data, Time.realtimeSinceStartup));
        }
    }

#endregion
}
