# Extending the Plugin

---

## Custom Transport Adapter

Implement `INetworkTransportAdapter` if you want to replace the WebRTC transport with something else (for example, a relay server or a different P2P library) while keeping `NetworkSessionManager` and the signaling layer unchanged.

```csharp
public class CustomTransportAdapter : MonoBehaviour, INetworkTransportAdapter
{
    public event Action<string> OnPeerReady;
    public event Action<string> OnPeerDisconnected;
    public event Action<string, ConnectionQuality> OnConnectionQualityChanged;
    public event Action<string, NetworkStats> OnNetworkStatsUpdated;
    public event Action<string, string> OnConnectionError;
    public event Action<string, ConnectionState> OnConnectionStateChanged;

    public bool Initialized { get; private set; }

    public void Initialize(NetworkManager networkManager)
    {
        // Configure networkManager.NetworkConfig.NetworkTransport here
        Initialized = true;
    }

    public void BeginConnection(string peerId, bool isOfferer)
    {
        // isOfferer is true when this peer should initiate the connection.
        // Typically the host offers and clients answer, but for P2P connections
        // between two clients a lexicographic tiebreak is used (see
        // NetworkSessionManager.DetermineIfShouldOffer).
    }

    public void DisconnectPeer(string peerId) { }
    public void Shutdown() { Initialized = false; }
    public bool IsPeerReady(string peerId) => false;
    public void HandleSignalingMessage(MessageType type, string fromPeerId, string payload) { }
    public NetworkStats GetNetworkStats(string peerId) => default;
    public ConnectionQuality GetConnectionQuality(string peerId) => ConnectionQuality.Poor;
}
```

To use it, assign the custom adapter to a GameObject in the scene and set `useWebRTC = false` on `NetworkSessionManager`, or assign the prefab to `webSocketTransportPrefab`.

---

## Custom Signaling Client

Implement `ISignalingClient` to connect to a different signaling server or use a different transport protocol.

```csharp
public class CustomSignalingClient : MonoBehaviour, ISignalingClient
{
    public event Action OnConnected;
    public event Action<SignalingMessage> OnMessageReceived;
    public event Action OnDisconnected;
    public event Action<int> OnReconnectAttempt;
    public event Action<float> OnReconnectCountdown;
    public event Action OnReconnectFailed;
    public event Action<string> OnSignalingError;

    public bool IsConnected { get; private set; }

    public void Connect(string url, string profileId, string sessionToken)
    {
        // Connect to your signaling server.
        // Fire OnConnected when ready.
    }

    public void Dispatch(string message)
    {
        // Send a JSON string to the signaling server.
        // If not yet connected, queue it for delivery once connected.
    }

    public void Disconnect()
    {
        // Close the connection intentionally.
        // Do not attempt reconnection after an intentional close.
    }
}
```

The `Dispatch` method receives pre-serialized JSON. Messages include SDP offers and answers, ICE candidates, matchmaking requests, and control messages. The signaling server is responsible for routing them to the correct recipient.

---

## Best Practices

### Event subscriptions

Always unsubscribe in `OnDestroy` to prevent callbacks firing on destroyed objects:

```csharp
private void OnDestroy()
{
    sessionManager.OnPlayerJoined -= HandlePlayerJoined;
    sessionManager.OnPlayerLeft   -= HandlePlayerLeft;
    sessionManager.OnConnectionError -= HandleError;
}
```

### Stats collection

`enableStatsCollection` polls `RTCStatsReport` every `statsUpdateInterval` seconds. This is useful for debugging and for showing quality indicators in-game, but it adds CPU overhead. For production builds where you do not display connection stats, consider disabling it or increasing the interval.

### Error handling

`OnConnectionError` on `NetworkSessionManager` covers transport and NGO startup failures. `OnSignalingError` covers WebSocket-level problems. Subscribe to both for complete coverage:

```csharp
sessionManager.OnConnectionError += error =>
    Debug.LogError($"[Transport] {error}");

sessionManager.OnSignalingError += error =>
    Debug.LogError($"[Signaling] {error}");
```

### Scene management

`NetworkSessionManager` calls `DontDestroyOnLoad` when `persistAcrossScenes` is true (default). Because it is a singleton, loading a scene that also contains a `NetworkSessionManager` in the hierarchy will destroy the duplicate. Either place `NetworkSessionManager` only in a one-time initialization scene, or check `NetworkSessionManager.Instance != null` before creating a new one.

---

## Troubleshooting

**WebRTC connections fail to establish**
- Confirm that the signaling server (Crossfire) is reachable and that SDP offers and answers are being exchanged. Check the console for `[WebRtcTransport]` log messages.
- For connections between machines on different networks, ensure `mode` is set to `REMOTE` and that STUN traffic (UDP on port 3478) is not blocked by a firewall.
- On mobile platforms, some carriers perform deep-packet inspection that interferes with WebRTC. Consider deploying a TURN server and adding it to `CrossfireConstants.GoogleStunServers`.

**Stats are not updating**
- Confirm `enableStatsCollection` is `true` on the `WebRtcTransport` component.
- `RTCStatsReport` only produces data after the ICE connection has reached the `Connected` or `Completed` state. Stats will not appear until `OnPeerReady` has fired.

**OnAllPlayersConnected never fires**
- Check that all players have reached `ConnectionState.Connected` via `GetConnectedPlayers()`.
- A player stuck in `Connecting` will block the event. If a peer failed to negotiate (check the console for `CreateOfferAndSend failed` or `RespondToOffer failed`), their state will remain `Connecting` until ICE times out and transitions to `Failed`, at which point they are excluded from the check.

**Duplicate NetworkSessionManager instances**
- If you see log output "Another instance already exists. Destroying duplicate.", a second `NetworkSessionManager` was instantiated in a loaded scene. Place it only in a scene that is loaded once, or instantiate it programmatically with an instance check.
