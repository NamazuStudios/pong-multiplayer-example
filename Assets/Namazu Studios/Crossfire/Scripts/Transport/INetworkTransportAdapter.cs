using System;
using Unity.Netcode;

namespace Elements.Crossfire
{
    using Model;

    /// <summary>
    /// Core transport abstraction
    /// </summary>
    public interface INetworkTransportAdapter
    {
        event Action<string> OnPeerReady;
        event Action<string> OnPeerDisconnected;
        event Action<string, ConnectionQuality> OnConnectionQualityChanged;
        event Action<string, NetworkStats> OnNetworkStatsUpdated;
        event Action<string, string> OnConnectionError;
        event Action<string, ConnectionState> OnConnectionStateChanged;
        bool Initialized { get; }

        void Initialize(NetworkManager networkManager);

        void BeginConnection(string peerId, bool isOfferer);

        void DisconnectPeer(string peerId);

        void Shutdown();

        bool IsPeerReady(string peerId);
        
        void HandleSignalingMessage(MessageType messageType, string fromPeerId, string payload);

        NetworkStats GetNetworkStats(string peerId);

        ConnectionQuality GetConnectionQuality(string peerId);
    }
}