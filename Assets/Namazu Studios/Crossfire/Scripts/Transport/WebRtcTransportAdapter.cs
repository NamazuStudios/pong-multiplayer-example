using UnityEngine;
using Unity.Netcode;
using Unity.WebRTC;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Elements.Crossfire
{
    using Model;

    public class WebRtcTransportAdapter : MonoBehaviour, INetworkTransportAdapter
    {
        public event Action<string> OnPeerReady;
        public event Action<string> OnPeerDisconnected;
        public event Action<string, ConnectionQuality> OnConnectionQualityChanged;
        public event Action<string, NetworkStats> OnNetworkStatsUpdated;
        public event Action<string, string> OnConnectionError;
        public event Action<string, ConnectionState> OnConnectionStateChanged;
        public bool Initialized { get; private set; }

        [SerializeField] private WebRtcTransport transport;
        [SerializeField] private float statsUpdateInterval = CrossfireConstants.AdapterStatsInterval;
        [SerializeField] private float qualityCheckInterval = CrossfireConstants.QualityCheckInterval;
        [SerializeField] private int consecutivePoorSamplesBeforeError = CrossfireConstants.PoorQualitySamplesBeforeError;

        private ConcurrentDictionary<string, NetworkStats> peerStats = new();
        private Dictionary<string, ConnectionQuality> peerQualities = new();
        private Dictionary<string, ConnectionState> peerStates = new();
        private Dictionary<string, DateTime> lastStatsUpdate = new();
        private Coroutine statsUpdateCoroutine;
        private Coroutine qualityMonitorCoroutine;

        // Hysteresis counters for error events
        private readonly Dictionary<string, int> _poorQualityCount = new();
        private readonly Dictionary<string, int> _highLatencyCount = new();
        private readonly Dictionary<string, int> _highLossCount = new();

        private Dictionary<string, ulong> profileToNgoId = new();
        private Dictionary<ulong, string> ngoIdToProfile = new();
        private ISignalingClient signalingClient;
        private NetworkSessionConfig sessionConfig;

        private Logger logger = LoggerFactory.GetLogger("WebRtcTransportAdapter");

        public void Initialize(NetworkManager networkManager)
        {
            // Guard against double-subscription if re-initialized
            if (Initialized)
            {
                Shutdown();
            }

            if (transport == null)
                transport = GetComponent<WebRtcTransport>();

            networkManager.NetworkConfig.NetworkTransport = transport;

            transport.Initialize(networkManager);

            // Subscribe to transport events
            transport.OnDataChannelReady += HandleDataChannelReady;
            transport.OnPeerConnectionStateChanged += HandlePeerStateChanged;

            // Subscribe to WebRTC signaling events
            transport.OnSendOffer += HandleSendOffer;
            transport.OnSendAnswer += HandleSendAnswer;
            transport.OnSendIce += HandleSendIce;

            transport.OnStatsUpdated += HandleStatsUpdated;

            transport.StartStatsCollection();

            statsUpdateCoroutine = StartCoroutine(UpdateNetworkStats());
            qualityMonitorCoroutine = StartCoroutine(MonitorConnectionQuality());

            Initialized = true;
        }

        public void SetSignalingClient(ISignalingClient signalingClient, NetworkSessionConfig sessionConfig)
        {
            this.signalingClient = signalingClient;
            this.sessionConfig = sessionConfig;
        }

        public void BeginConnection(string peerId, bool isOfferer)
        {
            var ngoId = GetOrCreateNgoId(peerId);
            transport.BeginConnection(ngoId, isOfferer);
        }

        public void DisconnectPeer(string peerId)
        {
            if (profileToNgoId.TryGetValue(peerId, out var ngoId))
            {
                transport.DisconnectRemoteClient(ngoId);
                profileToNgoId.Remove(peerId);
                ngoIdToProfile.Remove(ngoId);
            }
        }

        public bool IsPeerReady(string peerId)
        {
            return profileToNgoId.TryGetValue(peerId, out var ngoId) &&
                   transport.IsPeerReady(ngoId);
        }

        public void HandleSignalingMessage(MessageType messageType, string fromPeerId, string payload)
        {
            var ngoId = GetOrCreateNgoId(fromPeerId);
            var json = JObject.Parse(payload);

            //logger.Log($"Received signaling message {json}");

            switch (messageType)
            {
                case MessageType.SDP_OFFER:

                    var offer = new RTCSessionDescription
                    {
                        type = RTCSdpType.Offer,
                        sdp = (string)json["peerSdp"]
                    };

                    transport.OnRemoteOffer(ngoId, offer);
                    break;

                case MessageType.SDP_ANSWER:

                    var answer = new RTCSessionDescription
                    {
                        type = RTCSdpType.Answer,
                        sdp = (string)json["peerSdp"]
                    };

                    transport.OnRemoteAnswer(ngoId, answer);
                    break;

                case MessageType.CANDIDATE:

                    var candidate = new RTCIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = (string)json["candidate"],
                        sdpMid = (string)json["mid"]
                    });

                    transport.OnRemoteIce(ngoId, candidate);
                    break;
            }
        }

        public void Shutdown()
        {
            if (statsUpdateCoroutine != null)
            {
                StopCoroutine(statsUpdateCoroutine);
                statsUpdateCoroutine = null;
            }

            if (qualityMonitorCoroutine != null)
            {
                StopCoroutine(qualityMonitorCoroutine);
                qualityMonitorCoroutine = null;
            }

            if (transport != null)
            {
                transport.OnDataChannelReady -= HandleDataChannelReady;
                transport.OnPeerConnectionStateChanged -= HandlePeerStateChanged;
                transport.OnSendOffer -= HandleSendOffer;
                transport.OnSendAnswer -= HandleSendAnswer;
                transport.OnSendIce -= HandleSendIce;
                transport.OnStatsUpdated -= HandleStatsUpdated;
            }

            transport.Shutdown();
            profileToNgoId.Clear();
            ngoIdToProfile.Clear();
            peerStats.Clear();
            peerQualities.Clear();
            peerStates.Clear();
            lastStatsUpdate.Clear();
            _poorQualityCount.Clear();
            _highLatencyCount.Clear();
            _highLossCount.Clear();

            Initialized = false;
        }

        public NetworkStats GetNetworkStats(string peerId)
        {
            return peerStats.GetValueOrDefault(peerId);
        }

        public ConnectionQuality GetConnectionQuality(string peerId)
        {
            return peerQualities.GetValueOrDefault(peerId, ConnectionQuality.Poor);
        }

        private IEnumerator UpdateNetworkStats()
        {
            while (true)
            {
                yield return new WaitForSeconds(statsUpdateInterval);

                foreach (var kvp in profileToNgoId.ToList())
                {
                    var profileId = kvp.Key;
                    var ngoId = kvp.Value;

                    if (transport.IsPeerReady(ngoId))
                    {
                        UpdatePeerStats(profileId, ngoId);
                    }
                }
            }
        }

        private IEnumerator MonitorConnectionQuality()
        {
            while (true)
            {
                yield return new WaitForSeconds(qualityCheckInterval);

                foreach (var kvp in profileToNgoId.ToList())
                {
                    var profileId = kvp.Key;
                    var ngoId = kvp.Value;

                    if (transport.IsPeerReady(ngoId))
                    {
                        CheckForConnectionIssues(profileId, ngoId);
                    }
                }
            }
        }

        private void UpdatePeerStats(string profileId, ulong ngoId)
        {
            // Get stats from WebRTC transport
            var newStats = new NetworkStats
            {
                latency = transport.GetPeerLatency(ngoId),
                packetLoss = transport.GetPeerPacketLoss(ngoId),
                bytesReceived = transport.GetBytesReceived(ngoId),
                bytesSent = transport.GetBytesSent(ngoId),
                lastUpdated = DateTime.Now
            };

            peerStats[profileId] = newStats;
            OnNetworkStatsUpdated?.Invoke(profileId, newStats);

            // Update connection quality based on stats
            var newQuality = CalculateConnectionQuality(newStats);

            if (!peerQualities.TryGetValue(profileId, out var oldQuality) || oldQuality != newQuality)
            {
                peerQualities[profileId] = newQuality;
                OnConnectionQualityChanged?.Invoke(profileId, newQuality);
            }
        }

        private ConnectionQuality CalculateConnectionQuality(NetworkStats stats)
        {
            if (stats.latency > CrossfireConstants.LatencyPoorMs || stats.packetLoss > CrossfireConstants.PacketLossPoor)
                return ConnectionQuality.Poor;

            if (stats.latency > CrossfireConstants.LatencyFairMs || stats.packetLoss > CrossfireConstants.PacketLossFair)
                return ConnectionQuality.Fair;

            if (stats.latency > CrossfireConstants.LatencyGoodMs || stats.packetLoss > CrossfireConstants.PacketLossGood)
                return ConnectionQuality.Good;

            return ConnectionQuality.Excellent;
        }

        private ulong GetOrCreateNgoId(string profileId)
        {
            if (profileToNgoId.TryGetValue(profileId, out var ngoId)) 
                return ngoId;
            
            ngoId = NetworkIdMapper.DeterministicClientId(profileId, sessionConfig.matchId);
            profileToNgoId[profileId] = ngoId;
            ngoIdToProfile[ngoId] = profileId;

            return ngoId;
        }

        // Outbound WebRTC signaling
        private void HandleSendOffer(ulong to, RTCSessionDescription offer)
        {
            if (!ngoIdToProfile.TryGetValue(to, out var recipientProfileId))
            {
                logger.LogError($"Cannot send offer - unknown NGO ID: {to}");
                return;
            }

            // Don't send to self
            if (recipientProfileId == sessionConfig.profileId) return;

            var signal = new SdpOfferDirectSignal();
            signal.setPeerSdp(offer.sdp);
            signal.setProfileId(sessionConfig.profileId);
            signal.setRecipientProfileId(recipientProfileId);

            var msg = signal.ToJsonString<SdpOfferDirectSignal>();

            logger.Log($"Sending offer: {msg}");

            signalingClient?.Dispatch(msg);
        }

        private void HandleSendAnswer(ulong to, RTCSessionDescription answer)
        {
            if (!ngoIdToProfile.TryGetValue(to, out var recipientProfileId))
            {
                logger.LogError($"Cannot send answer - unknown NGO ID: {to}");
                return;
            }

            // Don't send to self
            if (recipientProfileId == sessionConfig.profileId) return;

            var signal = new SdpAnswerDirectSignal();
            signal.setPeerSdp(answer.sdp);
            signal.setProfileId(sessionConfig.profileId);
            signal.setRecipientProfileId(recipientProfileId);

            var msg = signal.ToJsonString<SdpAnswerDirectSignal>();

            logger.Log($"Sending answer: {msg}");

            signalingClient?.Dispatch(msg);
        }

        private void HandleSendIce(ulong to, RTCIceCandidate candidate)
        {
            if (!ngoIdToProfile.TryGetValue(to, out var recipientProfileId))
            {
                logger.LogError($"Cannot send ICE - unknown NGO ID: {to}");
                return;
            }

            // Don't send to self
            if (recipientProfileId == sessionConfig.profileId) return;

            var signal = new CandidateDirectSignal();
            signal.setProfileId(sessionConfig.profileId);
            signal.setRecipientProfileId(recipientProfileId);
            signal.setMid(candidate.SdpMid);
            signal.setCandidate(candidate.Candidate);

            var msg = signal.ToJsonString<CandidateDirectSignal>();

            logger.Log($"Sending ICE candidate: {msg}");

            signalingClient?.Dispatch(msg);
        }

        private void HandleDataChannelReady(ulong ngoId)
        {
            if (ngoIdToProfile.TryGetValue(ngoId, out var profileId))
            {
                OnPeerReady?.Invoke(profileId);
            }
        }

        private void HandlePeerStateChanged(ulong ngoId, RTCIceConnectionState state)
        {
            if (ngoIdToProfile.TryGetValue(ngoId, out var profileId))
            {
                ConnectionState newState = state switch
                {
                    RTCIceConnectionState.New => ConnectionState.Connecting,
                    RTCIceConnectionState.Checking => ConnectionState.Connecting,
                    RTCIceConnectionState.Completed => ConnectionState.Connected,
                    RTCIceConnectionState.Connected => ConnectionState.Connected,
                    RTCIceConnectionState.Disconnected => ConnectionState.Reconnecting,
                    RTCIceConnectionState.Failed => ConnectionState.Failed,
                    _ => ConnectionState.Disconnected
                };

                if (!peerStates.TryGetValue(profileId, out var oldState) || oldState != newState)
                {
                    peerStates[profileId] = newState;
                    OnConnectionStateChanged?.Invoke(profileId, newState);
                }

                // Only fire OnPeerDisconnected for permanent failure, not transient Disconnected
                // (Disconnected can recover; Failed is terminal)
                if (state == RTCIceConnectionState.Failed)
                {
                    OnPeerDisconnected?.Invoke(profileId);
                }
            }
        }

        private void HandleStatsUpdated(ulong clientId, ParsedWebRTCStats webRtcStats)
        {
            if (ngoIdToProfile.TryGetValue(clientId, out var profileId))
            {
                // Convert WebRTC stats to our NetworkStats format
                var networkStats = new NetworkStats
                {
                    latency = webRtcStats.GetLatencyMs(),
                    packetLoss = webRtcStats.GetPacketLossRatio(),
                    bytesReceived = webRtcStats.bytesReceived,
                    bytesSent = webRtcStats.bytesSent,
                    lastUpdated = webRtcStats.timestamp
                };

                peerStats[profileId] = networkStats;
                OnNetworkStatsUpdated?.Invoke(profileId, networkStats);

                // Update connection quality
                var newQuality = CalculateConnectionQuality(networkStats);
                if (!peerQualities.TryGetValue(profileId, out var oldQuality) || oldQuality != newQuality)
                {
                    peerQualities[profileId] = newQuality;
                    OnConnectionQualityChanged?.Invoke(profileId, newQuality);
                }

                // Fire connection error only after N consecutive poor quality samples (hysteresis)
                if (newQuality == ConnectionQuality.Poor)
                {
                    _poorQualityCount.TryGetValue(profileId, out var poorCount);
                    poorCount++;
                    _poorQualityCount[profileId] = poorCount;
                    if (poorCount == consecutivePoorSamplesBeforeError)
                    {
                        OnConnectionError?.Invoke(profileId, $"Connection quality degraded to Poor (latency: {networkStats.latency:F0}ms, packet loss: {networkStats.packetLoss * 100:F1}%)");
                    }
                }
                else
                {
                    _poorQualityCount[profileId] = 0;
                }
            }
        }

        private void CheckForConnectionIssues(string profileId, ulong ngoId)
        {
            if (transport is IWebRtcTransportStats statsProvider)
            {
                var latency = statsProvider.GetPeerLatency(ngoId);
                var packetLoss = statsProvider.GetPeerPacketLoss(ngoId);

                // Check for severe latency with hysteresis
                if (latency > CrossfireConstants.LatencySevereMs)
                {
                    _highLatencyCount.TryGetValue(profileId, out var count);
                    count++;
                    _highLatencyCount[profileId] = count;
                    if (count == consecutivePoorSamplesBeforeError)
                    {
                        OnConnectionError?.Invoke(profileId, $"High latency detected: {latency:F0}ms");
                    }
                }
                else
                {
                    _highLatencyCount[profileId] = 0;
                }

                // Check for severe packet loss with hysteresis
                if (packetLoss > CrossfireConstants.PacketLossSevere)
                {
                    _highLossCount.TryGetValue(profileId, out var count);
                    count++;
                    _highLossCount[profileId] = count;
                    if (count == consecutivePoorSamplesBeforeError)
                    {
                        OnConnectionError?.Invoke(profileId, $"High packet loss detected: {packetLoss * 100:F1}%");
                    }
                }
                else
                {
                    _highLossCount[profileId] = 0;
                }

                // Check for connection timeout (no stats update in a while)
                if (lastStatsUpdate.TryGetValue(profileId, out var lastUpdate))
                {
                    var timeSinceUpdate = DateTime.Now - lastUpdate;
                    if (timeSinceUpdate.TotalSeconds > statsUpdateInterval * 3) // 3x the normal interval
                    {
                        OnConnectionError?.Invoke(profileId, $"Stats update timeout: {timeSinceUpdate.TotalSeconds:F1}s since last update");
                    }
                }
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }
    }
}
