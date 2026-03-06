using UnityEngine;
using Unity.Netcode;
using System;
using System.Linq;
using System.Collections.Generic;

namespace Elements.Crossfire
{
    using Model;

    [DefaultExecutionOrder(-10)]
    public partial class NetworkSessionManager : MonoBehaviour
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
        [SerializeField] private LoggerLevel loggerLevel = LoggerLevel.Debug;

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
        public event Action<string> OnMatchLeft;
        public event Action<string, bool> OnHostChanged;
        public event Action<List<PlayerInfo>> OnPlayerListUpdated;
        public event Action<PlayerInfo> OnPlayerJoined;
        public event Action<PlayerInfo, string> OnPlayerLeft;
        public event Action OnAllPlayersConnected;
        public event Action<string> OnConnectionError;
        public event Action<string> OnSignalingError;

        // Initialization state
        private bool isInitialized;

        private readonly Logger logger = LoggerFactory.GetLogger("NetworkSessionManager");

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
            request.SetProfileId(sessionConfig.profileId);
            request.SetSessionKey(sessionConfig.sessionToken);
            request.SetConfiguration(configurationName);

            logger.Log($"Finding/creating match: '{configurationName}'");

            // SendMessage will automatically queue if signaling isn't ready yet
            SignalingClient.Dispatch(request.ToJsonString<FindHandshakeRequest>());
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
            request.SetMatchId(matchId);
            request.SetProfileId(sessionConfig.profileId);
            request.SetSessionKey(sessionConfig.sessionToken);

            logger.Log($"Joining match: '{matchId}'");

            // SendMessage will automatically queue if signaling isn't ready yet
            SignalingClient.Dispatch(request.ToJsonString<JoinHandshakeRequest>());
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

            // Capture matchId before ResetMatchState clears it
            var leavingMatchId = sessionConfig.matchId;

            // Reset match state but keep session active
            ResetMatchState();
            SetSessionState(NetworkSessionState.Connected);
            OnMatchLeft?.Invoke(leavingMatchId);

            // Let the server know that we are leaving so that it can signal the other players
            var request = new LeaveControlMessage();
            request.SetProfileId(sessionConfig.profileId);

            SignalingClient.Dispatch(request.ToJsonString<LeaveControlMessage>());
        }

        /// <summary>
        /// (Host only) Attempts to close the match, preventing any new players from joining.
        /// </summary>
        public void CloseMatch()
        {
            if (!isHost) return;

            var request = new CloseControlMessage();
            request.SetProfileId(sessionConfig.profileId);

            SignalingClient.Dispatch(request.ToJsonString<CloseControlMessage>());
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

            SignalingClient.Dispatch(request.ToJsonString<OpenControlMessage>());
        }

        /// <summary>
        /// (Host only) Attempts to end the match, indicating that the server can start the cleanup process.
        /// </summary>
        public void EndMatch()
        {
            if (!isHost) return;

            var request = new EndControlMessage();
            request.SetProfileId(sessionConfig.profileId);

            SignalingClient.Dispatch(request.ToJsonString<EndControlMessage>());
        }

        #endregion
#region INITIALIZATION

        private void Awake()
        {
            // Singleton pattern with scene persistence
            if (Instance == null)
            {
                Instance = this;
                Logger.LogLevel = loggerLevel;

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
            SignalingClient = gameObject.GetComponent<WebSocketSignalingClient>() ?? 
                              gameObject.AddComponent<WebSocketSignalingClient>();

            // Subscribe to signaling events
            SignalingClient.OnConnected += HandleSignalingConnected;
            SignalingClient.OnMessageReceived += HandleSignalingMessage;
            SignalingClient.OnDisconnected += HandleSignalingDisconnected;
            SignalingClient.OnSignalingError += HandleSignalingError;
            SignalingClient.OnReconnectAttempt += HandleReconnectAttempt;
            SignalingClient.OnReconnectCountdown += HandleReconnectCountdown;
            SignalingClient.OnReconnectFailed += HandleReconnectFailed;

            // Create transport adapter
            InitializeTransportAdapter();

            isInitialized = true;

            logger.Log("Initialization complete");
        }

        private void InitializeTransportAdapter()
        {
            // Unsubscribe from any previous transport adapter to prevent double-firing
            if (TransportAdapter != null)
            {
                UnsubscribeFromTransportAdapter();
            }

            GameObject transportGameObject;

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
                    transportGameObject = Instantiate(webRtcTransportPrefab, transform);
                }
                else
                {
                    transportGameObject = new GameObject("WebRtcTransportAdapter");
                    transportGameObject.transform.SetParent(transform);
                    transportGameObject.AddComponent<WebRtcTransportAdapter>();
                }

                TransportAdapter = transportGameObject.GetComponent<WebRtcTransportAdapter>();

                // Configure WebRTC adapter
                var webRtcAdapter = (WebRtcTransportAdapter)TransportAdapter;

                webRtcAdapter?.SetSignalingClient(SignalingClient, sessionConfig);
            }
            else
            {
                // WebSocket transport
                if (webSocketTransportPrefab != null)
                {
                    transportGameObject = Instantiate(webSocketTransportPrefab, transform);
                }
                else
                {
                    transportGameObject = new GameObject("WebSocketTransportAdapter");
                    transportGameObject.transform.SetParent(transform);
                    transportGameObject.AddComponent<WebSocketTransportAdapter>();
                }

                TransportAdapter = transportGameObject.GetComponent<INetworkTransportAdapter>();
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
            if (TransportAdapter is not WebRtcTransportAdapter adapter) return;

            adapter.OnConnectionQualityChanged += HandleConnectionQualityChanged;
            adapter.OnConnectionStateChanged += HandleConnectionStateChanged;
            adapter.OnConnectionError += HandleTransportConnectionError;
            adapter.OnNetworkStatsUpdated += HandleNetworkStatsUpdated;
        }

        private void UnsubscribeFromTransportAdapter()
        {
            TransportAdapter.OnPeerReady -= HandlePeerReady;
            TransportAdapter.OnPeerDisconnected -= HandlePeerDisconnected;

            if (TransportAdapter is WebRtcTransportAdapter adapter)
            {
                adapter.OnConnectionQualityChanged -= HandleConnectionQualityChanged;
                adapter.OnConnectionStateChanged -= HandleConnectionStateChanged;
                adapter.OnConnectionError -= HandleTransportConnectionError;
                adapter.OnNetworkStatsUpdated -= HandleNetworkStatsUpdated;
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
#region CONNECTION MANAGEMENT

        private void BeginConnectionWithPeer(string remoteProfileId)
        {
            if (remoteProfileId == sessionConfig.profileId)
                return;

            if (!connectedPeers.Add(remoteProfileId))
                return;

            var shouldOffer = DetermineIfShouldOffer(remoteProfileId);

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
            if (connectedPeers.Count == 0) return false;

            // Exclude players who have dropped — they shouldn't block the "all ready" signal.
            // Ready when every remaining active player is Connected.
            var activePlayers = players.Values
                .Where(p => p.connectionState != ConnectionState.Disconnected &&
                            p.connectionState != ConnectionState.Failed)
                .ToList();

            return activePlayers.Count > 0 &&
                   activePlayers.All(p => p.connectionState == ConnectionState.Connected);
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
            if (Instance != this) return;
            
            EndSession();
            Instance = null;
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
            return players.GetValueOrDefault(profileId);
        }

        public bool IsPlayerConnected(string profileId)
        {
            return players.TryGetValue(profileId, out var player) && player.connectionState == ConnectionState.Connected;
        }

#endregion
    }

}
