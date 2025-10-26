using UnityEngine;
using Unity.Netcode;
using System;

namespace Elements.Crossfire
{
    using Model;

    /// <summary>
    /// FUTURE IMPLEMENTATION
    /// This would implement a pure WebSocket transport without WebRTC
    /// Data would flow through the signaling server instead of P2P
    /// </summary>
    public class WebSocketTransportAdapter : MonoBehaviour, INetworkTransportAdapter
    {
        public event Action<string> OnPeerReady;
        public event Action<string> OnPeerDisconnected;
        public event Action<string, ConnectionQuality> OnConnectionQualityChanged;
        public event Action<string, NetworkStats> OnNetworkStatsUpdated;
        public event Action<string, string> OnConnectionError;
        public event Action<string, ConnectionState> OnConnectionStateChanged;
        public bool Initialized { get; private set; }

        [SerializeField] private NetworkTransport webSocketTransport; // Your custom WebSocket NetworkTransport        

        public void Initialize(NetworkManager networkManager)
        {
            // Initialize WebSocket-based transport
            Debug.Log("[WebSocketTransport] Initializing");
            networkManager.NetworkConfig.NetworkTransport = webSocketTransport;

            //Temp - just removing warnings by adding implementations
            OnConnectionQualityChanged?.Invoke("", ConnectionQuality.Excellent);
            OnNetworkStatsUpdated?.Invoke("", new());
            OnConnectionError?.Invoke("", "");
            OnConnectionStateChanged("", ConnectionState.Connected);

            Initialized = true;
        }

        public void BeginConnection(string peerId, bool isOfferer)
        {
            // No SDP negotiation needed, just establish logical connection
            Debug.Log($"[WebSocketTransport] Connecting to {peerId}");
            OnPeerReady?.Invoke(peerId);
        }

        public void DisconnectPeer(string peerId)
        {
            Debug.Log($"[WebSocketTransport] Disconnecting {peerId}");
            OnPeerDisconnected?.Invoke(peerId);
        }

        public bool IsPeerReady(string peerId)
        {
            // Check if WebSocket connection to peer is established
            return true; // Simplified
        }

        public void HandleSignalingMessage(MessageType messageType, string fromPeerId, string payload)
        {
            // Handle data messages directly
            switch (messageType)
            {
                case MessageType.BINARY_BROADCAST:
                case MessageType.BINARY_RELAY:

                    // Forward to NetworkManager through custom transport
                    
                    break;
            }
        }

        public void Shutdown()
        {
            Debug.Log("[WebSocketTransport] Shutting down");
            Initialized = false;
        }

        public NetworkStats GetNetworkStats(string peerId)
        {
            throw new NotImplementedException();
        }

        public ConnectionQuality GetConnectionQuality(string peerId)
        {
            throw new NotImplementedException();
        }
    }
}