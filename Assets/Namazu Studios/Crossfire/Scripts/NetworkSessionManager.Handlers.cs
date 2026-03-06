using System;
using UnityEngine;
using System.Collections.Generic;

namespace Elements.Crossfire
{
    using Model;

    public partial class NetworkSessionManager
    {
#region SIGNALING EVENT HANDLERS

        private void HandleSignalingConnected()
        {
            logger.Log("Signaling connected and ready");

            // Only update to Connected if we're not already in a more advanced state
            if (State is NetworkSessionState.Connecting or NetworkSessionState.Reconnecting)
            {
                SetSessionState(NetworkSessionState.Connected);
            }
        }

        private void HandleSignalingDisconnected()
        {
            logger.Log("Signaling disconnected");

            // Only set to reconnecting if we had an active session
            if (IsSessionActive && State != NetworkSessionState.Disconnected)
            {
                SetSessionState(NetworkSessionState.Reconnecting);
            }
        }

        private void HandleSignalingError(string error)
        {
            logger.LogError($"Signaling error: {error}");

            OnSignalingError?.Invoke(error);

            if (State != NetworkSessionState.Disconnected)
            {
                SetSessionState(NetworkSessionState.Error);
            }
        }

        private void HandleReconnectAttempt(int attemptNumber)
        {
            logger.Log($"Reconnect attempt {attemptNumber}");

            SetSessionState(NetworkSessionState.Reconnecting);
        }

        private void HandleReconnectCountdown(float secondsRemaining)
        {
            logger.Log($"Reconnecting in {secondsRemaining:F0}s");
        }

        private void HandleReconnectFailed()
        {
            logger.LogError("Signaling reconnection failed after maximum retries");

            OnSignalingError?.Invoke("Reconnection failed — maximum retries exhausted");

            SetSessionState(NetworkSessionState.Error);
        }

        private void HandleSignalingMessage(SignalingMessage message)
        {
            logger.Log($"Received: {message.type}");

            switch (message.type)
            {
                case MessageType.HOST:
                    HandleHostMessage(message);
                    break;

                case MessageType.MATCHED:
                    HandleMatchedMessage(message);
                    break;

                case MessageType.CONNECT:
                    HandleConnectMessage(message);
                    break;

                case MessageType.SDP_OFFER:
                case MessageType.SDP_ANSWER:
                case MessageType.CANDIDATE:
                    TransportAdapter?.HandleSignalingMessage(message.type, message.profileId, message.payload);
                    break;

                case MessageType.DISCONNECT:
                    HandleDisconnectMessage(message);
                    break;
                
                case MessageType.FIND:
                case MessageType.JOIN:
                case MessageType.CREATE:
                case MessageType.CREATED:
                case MessageType.JOIN_CODE:
                case MessageType.BINARY_BROADCAST:
                case MessageType.BINARY_RELAY:
                case MessageType.ERROR:
                case MessageType.LEAVE:
                case MessageType.OPEN:
                case MessageType.CLOSE:
                case MessageType.END:
                case MessageType.SIGNAL_JOIN:
                case MessageType.SIGNAL_LEAVE:
                    break;
                
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void HandleHostMessage(SignalingMessage message)
        {
            var oldHostId = hostProfileId;
            hostProfileId = message.profileId;
            var wasTransferred = !string.IsNullOrEmpty(oldHostId) && oldHostId != hostProfileId;

            isHost = hostProfileId == sessionConfig.profileId;

            // Update host in player list
            foreach (var player in players.Values)
            {
                player.isHost = player.profileId == hostProfileId;
            }

            OnHostChanged?.Invoke(hostProfileId, wasTransferred);

            UpdatePlayerList();

            logger.Log($"Host {(wasTransferred ? "transferred to" : "is")}: {hostProfileId}, I am host: {isHost}");

            // Connect to host if we're a client
            if (!isHost)
            {
                BeginConnectionWithPeer(hostProfileId);
            }

            // Process any pending peers
            foreach (var peerId in pendingPeers)
            {
                BeginConnectionWithPeer(peerId);
            }

            pendingPeers.Clear();
        }

        private void HandleMatchedMessage(SignalingMessage message)
        {
            sessionConfig.matchId = message.matchId;

            SetSessionState(NetworkSessionState.InMatch);
            OnMatchJoined?.Invoke(sessionConfig.matchId);

            logger.Log($"Matched to: {sessionConfig.matchId}");
        }

        private void HandleConnectMessage(SignalingMessage message)
        {
            var remoteProfileId = message.profileId;

            // Skip self
            if (remoteProfileId == sessionConfig.profileId)
                return;

            // Add or update player info
            if (!players.TryGetValue(remoteProfileId, out var playerInfo))
            {
                playerInfo = new PlayerInfo
                {
                    profileId = remoteProfileId,
                    networkId = NetworkIdMapper.DeterministicClientId(remoteProfileId, sessionConfig.matchId),
                    isHost = remoteProfileId == hostProfileId,
                    connectionState = ConnectionState.Connecting,
                    connectionQuality = ConnectionQuality.Poor
                };

                players[remoteProfileId] = playerInfo;
            }

            UpdatePlayerList();

            // If host unknown and this is not the host, queue for later
            if (hostProfileId == null && remoteProfileId != hostProfileId)
            {
                pendingPeers.Add(remoteProfileId);
                return;
            }

            BeginConnectionWithPeer(remoteProfileId);
        }

        private void HandleDisconnectMessage(SignalingMessage message)
        {
            var remoteProfileId = message.profileId;

            if (!connectedPeers.Remove(remoteProfileId)) return;
            
            TransportAdapter?.DisconnectPeer(remoteProfileId);

            logger.Log($"Peer disconnected: {remoteProfileId}");

            if (!players.TryGetValue(remoteProfileId, out var player)) return;
            
            OnPlayerLeft?.Invoke(player, "Disconnected");
            players.Remove(remoteProfileId);
            UpdatePlayerList();
        }

#endregion
#region TRANSPORT EVENT HANDLERS

        private void HandlePeerReady(string peerId)
        {
            logger.Log($"Peer ready: {peerId}");

            if (players.TryGetValue(peerId, out var player))
            {
                player.connectionState = ConnectionState.Connected;
                OnPlayerJoined?.Invoke(player);
                UpdatePlayerList();
            }

            if (!networkStarted)
            {
                StartNetworkManager();
            }

            // Check if all expected players are connected
            if (!AllExpectedPlayersConnected()) return;
            
            SetSessionState(NetworkSessionState.MatchReady);
            OnAllPlayersConnected?.Invoke();
        }

        private void HandlePeerDisconnected(string peerId)
        {
            connectedPeers.Remove(peerId);

            logger.Log($"Peer disconnected: {peerId}");

            if (!players.TryGetValue(peerId, out var player)) return;
            
            player.connectionState = ConnectionState.Disconnected;
            OnPlayerLeft?.Invoke(player, "Connection lost");
            UpdatePlayerList();
        }

        private void HandleConnectionQualityChanged(string peerId, ConnectionQuality quality)
        {
            if (!players.TryGetValue(peerId, out var player)) return;
            
            player.connectionQuality = quality;

            UpdatePlayerList();

            if (quality == ConnectionQuality.Poor)
            {
                logger.LogWarning($"Poor connection quality with {peerId}");
            }
        }

        private void HandleConnectionStateChanged(string peerId, ConnectionState state)
        {
            if (!players.TryGetValue(peerId, out var player)) return;
            
            player.connectionState = state;

            UpdatePlayerList();
        }

        private void HandleTransportConnectionError(string peerId, string error)
        {
            logger.LogError($"Transport error with {peerId}: {error}");

            OnConnectionError?.Invoke($"Connection error with {peerId}: {error}");
        }

        private void HandleNetworkStatsUpdated(string peerId, NetworkStats stats)
        {
            // Could expose this via events if needed for UI
            logger.Log($"Stats for {peerId}: {stats.latency}ms latency, {stats.packetLoss * 100:F1}% loss");
        }

#endregion
    }
}
