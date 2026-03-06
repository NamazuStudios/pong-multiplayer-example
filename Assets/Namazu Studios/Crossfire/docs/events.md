# Events Reference

All events follow a subscribe-in-`Start`-or-`Awake`, unsubscribe-in-`OnDestroy` pattern. The plugin never queues missed events, so subscribe before calling `StartSession`.

---

## NetworkSessionManager

These are the primary events for game and UI code to consume.

| Event | Signature | When it fires |
|-------|-----------|---------------|
| `OnSessionStateChanged` | `Action<NetworkSessionState>` | Any state transition (see [Architecture](architecture.md) for the full state machine) |
| `OnMatchJoined` | `Action<string>` | Server confirms a match; parameter is the match ID |
| `OnMatchLeft` | `Action<string>` | `LeaveMatch()` completes; parameter is the match ID that was left |
| `OnHostChanged` | `Action<string, bool>` | Host is assigned or changes; first param is the host profile ID, second is `true` when ownership transferred from a previous host |
| `OnPlayerListUpdated` | `Action<List<PlayerInfo>>` | Any change to the player roster (join, leave, state or quality change) |
| `OnPlayerJoined` | `Action<PlayerInfo>` | A peer's data channel is open and NGO is ready to send to them |
| `OnPlayerLeft` | `Action<PlayerInfo, string>` | A peer disconnected; second param is a human-readable reason string |
| `OnAllPlayersConnected` | `Action` | Every active (non-failed, non-disconnected) player has reached `Connected` state |
| `OnConnectionError` | `Action<string>` | A transport-level or NGO startup error (peer connection failures, failed host/client start) |
| `OnSignalingError` | `Action<string>` | A signaling-layer error (WebSocket errors, authentication failures, reconnect exhausted) |

### Distinguishing OnConnectionError from OnSignalingError

Use `OnSignalingError` to show "server unreachable" or "reconnecting" UI. Use `OnConnectionError` for per-peer issues like ICE failure or failed NGO startup. They are intentionally separate so you can respond differently without parsing the message string.

### Notes on specific events

**OnPlayerLeft:** The `PlayerInfo` passed to this handler contains the player's data at the time they left. The player is removed from the internal roster immediately after the event fires, so calling `GetPlayerInfo(player.profileId)` inside the handler returns null.

**OnAllPlayersConnected:** Players who failed or disconnected before everyone connected are excluded from the check. The event fires once all remaining active players have connected. If your game requires an exact player count, compare `GetConnectedPlayers().Count` against your expected count before acting on this event.

**OnHostChanged:** The second parameter (`wasTransferred`) is `false` the first time the host is assigned and `true` on any subsequent change. Use this to distinguish initial host assignment from host migration.

---

## WebRtcTransportAdapter

These events expose per-peer connection details. Most game code should use `NetworkSessionManager` events instead, which translate NGO client IDs into profile ID strings and maintain the player roster. Subscribe directly to `WebRtcTransportAdapter` only when you need stats or quality data for display.

| Event | Signature | When it fires |
|-------|-----------|---------------|
| `OnPeerReady` | `Action<string>` | Data channel is open; parameter is the peer profile ID |
| `OnPeerDisconnected` | `Action<string>` | ICE connection has permanently failed (not on transient interruptions) |
| `OnConnectionQualityChanged` | `Action<string, ConnectionQuality>` | Quality tier changes for a peer |
| `OnNetworkStatsUpdated` | `Action<string, NetworkStats>` | Stats snapshot updated; fires at `statsUpdateInterval` |
| `OnConnectionError` | `Action<string, string>` | A sustained quality problem detected; first param is peer profile ID, second is a description |
| `OnConnectionStateChanged` | `Action<string, ConnectionState>` | ICE state changed for a peer |

### ConnectionQuality levels

| Value | Latency | Packet Loss | Typical gameplay impact |
|-------|---------|-------------|------------------------|
| `Excellent` | below 50 ms | below 1% | No noticeable impact |
| `Good` | below 100 ms | below 2% | Acceptable for most genres |
| `Fair` | below 200 ms | below 5% | Noticeable in fast-paced games |
| `Poor` | 200 ms or above, or 5% loss or above | See note | Lag, visual desync likely |

`OnConnectionError` fires only after `consecutivePoorSamplesBeforeError` consecutive poor samples (default: 3), not on the first poor reading. This prevents transient spikes from triggering error handling.

### ConnectionState values

| Value | Meaning |
|-------|---------|
| `Connecting` | ICE negotiation in progress |
| `Connected` | ICE connected and data channel open |
| `Reconnecting` | ICE interrupted; may recover automatically |
| `Failed` | ICE permanently failed; `OnPeerDisconnected` fires |
| `Disconnected` | Data channel closed cleanly |

`Reconnecting` is a transient state. Do not treat it as a final disconnect. `OnPeerDisconnected` only fires on `Failed`.

### NetworkStats fields

| Field | Type | Unit |
|-------|------|------|
| `latency` | `float` | Milliseconds (current round-trip time) |
| `packetLoss` | `float` | Ratio from 0.0 to 1.0 (multiply by 100 for a percentage) |
| `bytesReceived` | `long` | Cumulative bytes received since the connection was established |
| `bytesSent` | `long` | Cumulative bytes sent since the connection was established |
| `lastUpdated` | `DateTime` | The local timestamp when this snapshot was taken |

---

## ISignalingClient

Subscribe to these for reconnection UI or to show server connectivity status. In most cases `NetworkSessionManager.OnSignalingError` and `OnSessionStateChanged` are sufficient.

| Event | Signature | When it fires |
|-------|-----------|---------------|
| `OnConnected` | `Action` | WebSocket connection established and ready |
| `OnDisconnected` | `Action` | WebSocket closed (intentional or unexpected) |
| `OnMessageReceived` | `Action<SignalingMessage>` | Raw message received from the signaling server |
| `OnReconnectAttempt` | `Action<int>` | A reconnect attempt starts; parameter is the attempt number (1-based) |
| `OnReconnectCountdown` | `Action<float>` | Fires each second of the reconnect delay; parameter is seconds remaining |
| `OnReconnectFailed` | `Action` | All retry attempts exhausted; the client will not reconnect automatically |
| `OnSignalingError` | `Action<string>` | WebSocket error; parameter is the error description |

### PlayerInfo fields

| Field | Type | Notes |
|-------|------|-------|
| `profileId` | `string` | The remote player's profile ID from the auth system |
| `networkId` | `ulong` | The Unity Netcode client ID for this player (deterministic, derived from profile ID and match ID) |
| `isHost` | `bool` | Whether this player is currently the NGO host |
| `connectionState` | `ConnectionState` | Current ICE connection state |
| `connectionQuality` | `ConnectionQuality` | Most recent quality assessment |

The `players` collection in `NetworkSessionManager` contains only remote peers, not the local player.

---

## Usage Examples

### Player roster UI

```csharp
private void Start()
{
    sessionManager.OnPlayerJoined += AddPlayerRow;
    sessionManager.OnPlayerLeft += RemovePlayerRow;
    sessionManager.OnPlayerListUpdated += RefreshQualityIndicators;
}

private void AddPlayerRow(PlayerInfo player)
{
    var row = Instantiate(playerRowPrefab, playerList);
    row.GetComponent<PlayerRowUI>().Bind(player);
    playerRows[player.profileId] = row;
}

private void RemovePlayerFromList(PlayerInfo player, string reason)
{
    if (playerRows.TryGetValue(player.profileId, out var row))
    {
        Destroy(row);
        playerRows.Remove(player.profileId);
    }
}
```

### Connection quality display

```csharp
private void Start()
{
    var adapter = FindObjectOfType<WebRtcTransportAdapter>();
    adapter.OnConnectionQualityChanged += UpdateQualityBar;
    adapter.OnNetworkStatsUpdated += UpdateLatencyLabel;
}

private void UpdateQualityBar(string peerId, ConnectionQuality quality)
{
    float fill = quality switch
    {
        ConnectionQuality.Excellent => 1.0f,
        ConnectionQuality.Good      => 0.75f,
        ConnectionQuality.Fair      => 0.5f,
        ConnectionQuality.Poor      => 0.25f,
        _                           => 0f
    };
    qualityBar.value = fill;
    lagWarning.SetActive(quality == ConnectionQuality.Poor);
}

private void UpdateLatencyLabel(string peerId, NetworkStats stats)
{
    latencyLabel.text = $"{stats.latency:F0} ms";
}
```

### Reconnection UI

```csharp
private void Start()
{
    var signaling = FindObjectOfType<WebSocketSignalingClient>();
    signaling.OnReconnectAttempt   += attempt  => ShowPanel($"Reconnecting (attempt {attempt})");
    signaling.OnReconnectCountdown += seconds  => UpdateCountdown(seconds);
    signaling.OnConnected          +=           () => HidePanel();
    signaling.OnReconnectFailed    +=           () => ShowPanel("Could not reconnect. Please restart.");
}
```
