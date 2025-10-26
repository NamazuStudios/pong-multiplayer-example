using UnityEngine;
using Unity.Netcode;
using System;
using System.Linq;
using System.Collections.Generic;

namespace Elements.Crossfire
{
    using Model;

    [DefaultExecutionOrder(-10)]
    public class NetworkSessionManager : MonoBehaviour
    {
        // Singleton pattern for persistence across scenes
        public static NetworkSessionManager Instance { get; private set; }

        [Header("Components")]
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private GameObject webRtcTransportPrefab;
        [SerializeField] private GameObject webSocketTransportPrefab;

        [Header("Configuration")]
        [SerializeField] private NetworkSessionConfig sessionConfig;
        [SerializeField] private bool useWebRTC = true;
        [SerializeField] private bool persistAcrossScenes = true;
        [SerializeField] private bool loggingEnabled = false;

        // Session state tracking
        public NetworkSessionState State { get; private set; } = NetworkSessionState.Disconnected;
        public bool IsSessionActive => State != NetworkSessionState.Disconnected;
        public bool IsConnectedToMatch => State == NetworkSessionState.InMatch || State == NetworkSessionState.MatchReady;
        public bool IsSignalingConnected => SignalingClient?.IsConnected ?? false;

        // Core components
        public ISignalingClient SignalingClient { get; private set; }
        public INetworkTransportAdapter TransportAdapter { get; private set; }

        // Session state
        private string hostProfileId;
        private bool isHost;
        private bool networkStarted;
        private readonly HashSet<string> connectedPeers = new();
        private readonly HashSet<string> pendingPeers = new();
        private readonly Dictionary<string, PlayerInfo> players = new();

        // Events with state information
        public event Action<NetworkSessionState> OnSessionStateChanged;
        public event Action<string> OnMatchJoined;
        public event Action<string, bool> OnHostChanged;
        public event Action<List<PlayerInfo>> OnPlayerListUpdated;
        public event Action<PlayerInfo> OnPlayerJoined;
        public event Action<PlayerInfo, string> OnPlayerLeft;
        public event Action OnAllPlayersConnected;
        public event Action<string> OnConnectionError;

        // Initialization state
        private bool isInitialized = false;

        private Logger logger = LoggerFactory.GetLogger("NetworkSessionManager");

        private void Awake()
        {
            // Singleton pattern with scene persistence
            if (Instance == null)
            {
                Instance = this;
                Logger.LoggingEnabled = loggingEnabled;

                // DontDestroyOnLoad only works on root game objects, not children
                if (persistAcrossScenes && transform.parent == null)
                {
                    DontDestroyOnLoad(gameObject);
                    logger.Log("Marked as persistent across scenes");
                }

                // Initialize if the networkManager is discoverable
                if (networkManager != null || FindAnyObjectByType<NetworkManager>() != null)
                {
                    InitializeComponents();
                }
            }
            else if (Instance != this)
            {
                logger.Log("Another instance already exists. Destroying duplicate.");
                Destroy(gameObject);
            }
        }

#region PUBLIC API

        /// <summary>
        /// Start a new networking session. If a session is already active, this will restart it.
        /// </summary>
        public void StartSession(string profileId, string sessionToken, string matchId = null, bool forceRestart = false)
        {
            if (IsSessionActive && !forceRestart)
            {
                logger.LogWarning($"Session already active (State: {State}). Use forceRestart=true to restart or call EndSession() first.");
                return;
            }

            if (IsSessionActive)
            {
                logger.Log("Restarting existing session");

                EndSession();
            }

            sessionConfig.profileId = profileId;
            sessionConfig.sessionToken = sessionToken;

            if (matchId != null)
                sessionConfig.matchId = matchId;

            SetSessionState(NetworkSessionState.Connecting);

            // Ensure components are initialized
            if (!isInitialized)
            {
                InitializeComponents();
            }
            else if(!TransportAdapter.Initialized)
            {
                // Reinitialize transport
                TransportAdapter.Initialize(networkManager);
            }

            // Connect to signaling server
            SignalingClient.Connect(sessionConfig.serverHost, profileId, sessionToken);
        }

        /// <summary>
        /// End the current session and clean up connections
        /// </summary>
        public void EndSession()
        {
            if (!IsSessionActive)
            {
                logger.LogWarning("No active session to end");
                return;
            }

            logger.Log("Ending session");

            if (networkStarted && networkManager != null)
            {
                networkManager.Shutdown();
                networkStarted = false;
            }

            SignalingClient?.Disconnect();
            TransportAdapter?.Shutdown();
            ResetSessionState();
            SetSessionState(NetworkSessionState.Disconnected);
        }

        /// <summary>
        /// Check if we can start a new session (not already connected)
        /// </summary>
        public bool CanStartSession()
        {
            return !IsSessionActive || State == NetworkSessionState.Error;
        }

        /// <summary>
        /// Get current session information for UI display
        /// </summary>
        public SessionInfo GetSessionInfo()
        {
            return new SessionInfo
            {
                state = State,
                profileId = sessionConfig?.profileId,
                matchId = sessionConfig?.matchId,
                isHost = isHost,
                connectedPlayerCount = connectedPeers.Count,
                totalPlayerCount = players.Count,
                isNetworkStarted = networkStarted,
                isSignalingConnected = IsSignalingConnected
            };
        }

        /// <summary>
        /// Find or create a match. Messages will be queued if signaling isn't ready yet.
        /// </summary>
        public void FindOrCreateMatch(string configurationName)
        {
            if (!IsSessionActive)
            {
                logger.LogError("Cannot find match - no active session. Call StartSession() first.");
                OnConnectionError?.Invoke("No active session - call StartSession() first");
                return;
            }

            if (State == NetworkSessionState.InMatch || State == NetworkSessionState.MatchReady)
            {
                logger.LogWarning("Already in a match");
                return;
            }

            SetSessionState(NetworkSessionState.FindingMatch);

            var request = new FindHandshakeRequest();
            request.setProfileId(sessionConfig.profileId);
            request.setSessionKey(sessionConfig.sessionToken);
            request.setConfiguration(configurationName);

            logger.Log($"Finding/creating match: '{configurationName}'");

            // SendMessage will automatically queue if signaling isn't ready yet
            SignalingClient.SendWSMessage(request.ToJsonString<FindHandshakeRequest>());
        }

        /// <summary>
        /// Join a specific match by ID. Messages will be queued if signaling isn't ready yet.
        /// </summary>
        public void JoinMatch(string matchId)
        {
            if (!IsSessionActive)
            {
                logger.LogError("Cannot join match - no active session. Call StartSession() first.");
                OnConnectionError?.Invoke("No active session - call StartSession() first");
                return;
            }

            if (State == NetworkSessionState.InMatch || State == NetworkSessionState.MatchReady)
            {
                logger.LogWarning("Already in a match");
                return;
            }

            SetSessionState(NetworkSessionState.JoiningMatch);

            var request = new JoinHandshakeRequest();
            request.setMatchId(matchId);
            request.setProfileId(sessionConfig.profileId);
            request.setSessionKey(sessionConfig.sessionToken);

            logger.Log($"Joining match: '{matchId}'");

            // SendMessage will automatically queue if signaling isn't ready yet
            SignalingClient.SendWSMessage(request.ToJsonString<JoinHandshakeRequest>());
        }

        /// <summary>
        /// Leave current match but keep session active
        /// </summary>
        public void LeaveMatch()
        {
            if (!IsConnectedToMatch)
            {
                logger.LogWarning("Not currently in a match");
                return;
            }

            logger.Log("Leaving match");

            // Disconnect from all peers
            foreach (var peerId in connectedPeers.ToArray())
            {
                TransportAdapter.DisconnectPeer(peerId);
            }

            // Shut down Netcode but keep signaling connection
            if (networkStarted && networkManager != null)
            {
                networkManager.Shutdown();
                networkStarted = false;
            }

            // Reset match state but keep session active
            ResetMatchState();
            SetSessionState(NetworkSessionState.Connected);

            // Let the server know that we are leaving so that it can signal the other players
            var request = new LeaveControlMessage();
            request.SetProfileId(sessionConfig.profileId);

            SignalingClient.SendWSMessage(request.ToJsonString<LeaveControlMessage>());
        }

        /// <summary>
        /// (Host only) Attempts to close the match, preventing any new players from joining.
        /// </summary>
        public void CloseMatch()
        {
            if (!isHost) return;

            var request = new CloseControlMessage();
            request.SetProfileId(sessionConfig.profileId);

            SignalingClient.SendWSMessage(request.ToJsonString<CloseControlMessage>());
        }

        /// <summary>
        /// (Host only) Attempts to open the match, allowing new players to join.
        /// Newly created matches are OPEN by default.
        /// </summary>
        public void OpenMatch()
        {
            if (!isHost) return;

            var request = new OpenControlMessage();
            request.SetProfileId(sessionConfig.profileId);

            SignalingClient.SendWSMessage(request.ToJsonString<OpenControlMessage>());
        }

        /// <summary>
        /// (Host only) Attempts to end the match, indicating that the server can start the cleanup process.        
        /// </summary>
        public void EndMatch()
        {
            if (!isHost) return;

            var request = new EndControlMessage();
            request.SetProfileId(sessionConfig.profileId);

            SignalingClient.SendWSMessage(request.ToJsonString<EndControlMessage>());
        }

        #endregion
        #region INITIALIZATION

        private void InitializeComponents()
        {
            if (isInitialized)
            {
                logger.LogWarning("Already initialized");
                return;
            }

            logger.Log("Initializing components");

            // Find NetworkManager if not assigned
            if (networkManager == null)
            {
                networkManager = FindAnyObjectByType<NetworkManager>();
                if (networkManager == null)
                {
                    logger.LogError("No NetworkManager found in scene!");
                    SetSessionState(NetworkSessionState.Error);
                    return;
                }
            }

            // Create signaling client
            SignalingClient = gameObject.GetComponent<WebSocketSignalingClient>();

            if (SignalingClient == null)
            {
                SignalingClient = gameObject.AddComponent<WebSocketSignalingClient>();
            }

            // Subscribe to signaling events
            SignalingClient.OnConnected += HandleSignalingConnected;
            SignalingClient.OnMessageReceived += HandleSignalingMessage;
            SignalingClient.OnDisconnected += HandleSignalingDisconnected;
            SignalingClient.OnSignalingError += HandleSignalingError;
            SignalingClient.OnReconnectAttempt += HandleReconnectAttempt;
            SignalingClient.OnReconnectCountdown += HandleReconnectCountdown;

            // Create transport adapter
            InitializeTransportAdapter();

            isInitialized = true;

            logger.Log("Initialization complete");
        }

        private void InitializeTransportAdapter()
        {
            GameObject transportGO = null;

            // Check if transport adapter already exists
            var existingAdapter = GetComponent<INetworkTransportAdapter>();

            if (existingAdapter != null)
            {
                TransportAdapter = existingAdapter;
                logger.Log("Using existing transport adapter");
            }
            else if (useWebRTC)
            {
                if (webRtcTransportPrefab != null)
                {
                    transportGO = Instantiate(webRtcTransportPrefab, transform);
                }
                else
                {
                    transportGO = new GameObject("WebRtcTransportAdapter");
                    transportGO.transform.SetParent(transform);
                    transportGO.AddComponent<WebRtcTransportAdapter>();
                }

                TransportAdapter = transportGO.GetComponent<WebRtcTransportAdapter>();

                // Configure WebRTC adapter
                var webRtcAdapter = TransportAdapter as WebRtcTransportAdapter;

                webRtcAdapter?.SetSignalingClient(SignalingClient, sessionConfig);
            }
            else
            {
                // WebSocket transport
                if (webSocketTransportPrefab != null)
                {
                    transportGO = Instantiate(webSocketTransportPrefab, transform);
                }
                else
                {
                    transportGO = new GameObject("WebSocketTransportAdapter");
                    transportGO.transform.SetParent(transform);
                    transportGO.AddComponent<WebSocketTransportAdapter>();
                }

                TransportAdapter = transportGO.GetComponent<INetworkTransportAdapter>();
            }

            if (TransportAdapter == null)
            {
                logger.LogError("Failed to create transport adapter");
                SetSessionState(NetworkSessionState.Error);
                return;
            }

            // Initialize transport
            TransportAdapter.Initialize(networkManager);

            // Subscribe to transport events
            TransportAdapter.OnPeerReady += HandlePeerReady;
            TransportAdapter.OnPeerDisconnected += HandlePeerDisconnected;

            // Subscribe to enhanced transport events if available
            if (TransportAdapter is WebRtcTransportAdapter adapter)
            {
                adapter.OnConnectionQualityChanged += HandleConnectionQualityChanged;
                adapter.OnConnectionStateChanged += HandleConnectionStateChanged;
                adapter.OnConnectionError += HandleTransportConnectionError;
                adapter.OnNetworkStatsUpdated += HandleNetworkStatsUpdated;
            }
        }

#endregion
#region STATE MANAGEMENT

        private void SetSessionState(NetworkSessionState newState)
        {
            if (State != newState)
            {
                var oldState = State;
                State = newState;

                logger.Log($"State: {oldState} → {newState}");
                OnSessionStateChanged?.Invoke(newState);
            }
        }

        private void ResetSessionState()
        {
            hostProfileId = null;
            isHost = false;
            connectedPeers.Clear();
            pendingPeers.Clear();
            players.Clear();
        }

        private void ResetMatchState()
        {
            sessionConfig.matchId = null;
            ResetSessionState();
        }

#endregion
#region SIGNALING EVENT HANDLERS

        private void HandleSignalingConnected()
        {
            logger.Log("Signaling connected and ready");

            // Only update to Connected if we're not already in a more advanced state
            if (State == NetworkSessionState.Connecting || State == NetworkSessionState.Reconnecting)
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

            OnConnectionError?.Invoke($"Signaling error: {error}");

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
            }
        }

        private void HandleHostMessage(SignalingMessage message)
        {
            string oldHostId = hostProfileId;
            hostProfileId = message.profileId;
            bool wasTransferred = !string.IsNullOrEmpty(oldHostId) && oldHostId != hostProfileId;

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

            if (connectedPeers.Remove(remoteProfileId))
            {
                TransportAdapter?.DisconnectPeer(remoteProfileId);

                logger.Log($"Peer disconnected: {remoteProfileId}");

                if (players.TryGetValue(remoteProfileId, out var player))
                {
                    OnPlayerLeft?.Invoke(player, "Disconnected");
                    players.Remove(remoteProfileId);
                    UpdatePlayerList();
                }
            }
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
            if (AllExpectedPlayersConnected())
            {
                SetSessionState(NetworkSessionState.MatchReady);
                OnAllPlayersConnected?.Invoke();
            }
        }

        private void HandlePeerDisconnected(string peerId)
        {
            connectedPeers.Remove(peerId);

            logger.Log($"Peer disconnected: {peerId}");

            if (players.TryGetValue(peerId, out var player))
            {
                player.connectionState = ConnectionState.Disconnected;
                OnPlayerLeft?.Invoke(player, "Connection lost");
                UpdatePlayerList();
            }
        }

        private void HandleConnectionQualityChanged(string peerId, ConnectionQuality quality)
        {
            if (players.TryGetValue(peerId, out var player))
            {
                player.connectionQuality = quality;

                UpdatePlayerList();

                if (quality == ConnectionQuality.Poor)
                {
                    logger.LogWarning($"Poor connection quality with {peerId}");
                }
            }
        }

        private void HandleConnectionStateChanged(string peerId, ConnectionState state)
        {
            if (players.TryGetValue(peerId, out var player))
            {
                player.connectionState = state;

                UpdatePlayerList();
            }
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
#region CONNECTION MANAGEMENT

        private void BeginConnectionWithPeer(string remoteProfileId)
        {
            if (remoteProfileId == sessionConfig.profileId)
                return;

            if (connectedPeers.Contains(remoteProfileId))
                return;

            connectedPeers.Add(remoteProfileId);

            bool shouldOffer = DetermineIfShouldOffer(remoteProfileId);

            TransportAdapter?.BeginConnection(remoteProfileId, shouldOffer);

            logger.Log($"Beginning connection with {remoteProfileId}, offering: {shouldOffer}");
        }

        private bool DetermineIfShouldOffer(string remoteProfileId)
        {
            // Host always offers
            if (isHost)
                return true;

            // Client answers to host
            if (remoteProfileId == hostProfileId)
                return false;

            // For P2P connections, use lexicographic comparison
            return string.Compare(sessionConfig.profileId, remoteProfileId, StringComparison.Ordinal) < 0;
        }

        private bool AllExpectedPlayersConnected()
        {
            // This would depend on your match configuration
            // For now, assume ready when we have at least one peer connection
            return connectedPeers.Count > 0 && players.Values.All(p => p.connectionState == ConnectionState.Connected);
        }

        private void StartNetworkManager()
        {
            if (networkStarted) return;

            try
            {
                if (isHost)
                {
                    logger.Log("Starting as Host");

                    networkManager.StartHost();
                }
                else
                {
                    logger.Log("Starting as Client");

                    networkManager.StartClient();

                    // Set server peer for transport
                    if (TransportAdapter is WebRtcTransportAdapter webRtcAdapter)
                    {
                        var serverNgoId = NetworkIdMapper.DeterministicClientId(hostProfileId, sessionConfig.matchId);
                        webRtcAdapter.GetComponent<WebRtcTransport>().SetServerClientId(serverNgoId);
                    }
                }

                networkStarted = true;

                logger.Log("Network started successfully");
            }
            catch (Exception e)
            {
                logger.LogError($"Failed to start NetworkManager: {e.Message}");

                OnConnectionError?.Invoke($"Failed to start networking: {e.Message}");

                SetSessionState(NetworkSessionState.Error);
            }
        }

        private void UpdatePlayerList()
        {
            OnPlayerListUpdated?.Invoke(new List<PlayerInfo>(players.Values));
        }

#endregion
#region APPLICATION LIFECYCLE

        private void OnDestroy()
        {
            if (Instance == this)
            {
                EndSession();
                Instance = null;
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus && IsSessionActive)
            {
                logger.Log("Application paused - maintaining connection");
                // Could implement connection pause/resume logic here
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus && IsSessionActive)
            {
                logger.Log("Application lost focus - maintaining connection");
                // Could reduce update frequency or pause non-essential networking
            }
        }

#endregion
#region UTILITY METHODS

        public List<PlayerInfo> GetConnectedPlayers()
        {
            return players.Values.Where(p => p.connectionState == ConnectionState.Connected).ToList();
        }

        public PlayerInfo GetPlayerInfo(string profileId)
        {
            return players.TryGetValue(profileId, out var player) ? player : null;
        }

        public bool IsPlayerConnected(string profileId)
        {
            return players.TryGetValue(profileId, out var player) && player.connectionState == ConnectionState.Connected;
        }

#endregion
    }

}