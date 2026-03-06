# Configuration

---

## NetworkSessionConfig

This object is a `[SerializeField]` on `NetworkSessionManager` and can be edited in the Inspector. It is also updated at runtime by `StartSession()`.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `serverHost` | `string` | `ws://localhost:8080/app/ws/crossfire` | Base WebSocket URL for the signaling server. The client automatically appends `/match` to this value, so do not include that path here. Use `wss://` in production. |
| `reconnectDelay` | `float` | `5.0` | Seconds to wait before each reconnection attempt after an unexpected disconnect. |
| `autoReconnect` | `bool` | `true` | Whether to automatically attempt reconnection when the WebSocket closes unexpectedly. Set to `false` if you want full manual control over reconnection. |
| `matchId` | `string` | empty | Set automatically when a match is found. Can be pre-filled in the Inspector for debugging a specific match without going through matchmaking. |
| `profileId` | `string` | empty | Set by `StartSession()`. Can be pre-filled for debugging. |
| `sessionToken` | `string` | empty | Set by `StartSession()`. Can be pre-filled for debugging. |

---

## WebRtcTransport Inspector Fields

These appear on the `WebRtcTransport` component (usually a child of the `NetworkSessionManager` GameObject).

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `mode` | `Mode` | `REMOTE` | `REMOTE` uses the configured STUN servers. `LOCAL` disables STUN entirely and is intended for same-machine testing only (see [Architecture](architecture.md)). |
| `enableStatsCollection` | `bool` | `true` | Enables periodic `RTCStatsReport` polling. Disable in production builds where you do not need latency or packet-loss data, to reduce CPU overhead. |
| `statsUpdateInterval` | `float` | `1.0` | Seconds between WebRTC stats polls. Lower values give more responsive data but increase CPU usage on the WebRTC update loop. |
| `webRtcOperationMaxRetries` | `int` | `2` | How many times offer/answer negotiation is retried after a failure before giving up. Each retry waits one second before attempting again. |

---

## WebRtcTransportAdapter Inspector Fields

These appear on the `WebRtcTransportAdapter` component.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `statsUpdateInterval` | `float` | `2.0` | Seconds between adapter-level stats polls (separate from the lower-level WebRTC stats collection above). Controls how often `OnNetworkStatsUpdated` fires. |
| `qualityCheckInterval` | `float` | `1.0` | Seconds between connection health checks. Controls how often `CheckForConnectionIssues` runs and therefore how quickly `OnConnectionError` can fire for sustained latency or packet-loss problems. |
| `consecutivePoorSamplesBeforeError` | `int` | `3` | How many consecutive poor-quality samples must be observed before `OnConnectionError` fires. At the default check interval of 1 second, this means a sustained problem must last at least 3 seconds before an error event is raised, which avoids false positives from transient spikes. |

---

## CrossfireConstants

`CrossfireConstants` is a static class used as the source of truth for all thresholds and default values. Change values here to tune behaviour globally rather than hunting for inline numbers.

### Connection quality thresholds

These values determine which `ConnectionQuality` tier a peer falls into.

| Constant | Value | Meaning |
|----------|-------|---------|
| `LatencyGoodMs` | 50 ms | Below this: `Excellent` quality |
| `LatencyFairMs` | 100 ms | Below this: `Good` quality |
| `LatencyPoorMs` | 200 ms | Below this: `Fair` quality; at or above: `Poor` |
| `LatencySevereMs` | 500 ms | Sustained above this threshold triggers `OnConnectionError` |
| `PacketLossGood` | 0.01 (1%) | Below this: `Excellent` quality |
| `PacketLossFair` | 0.02 (2%) | Below this: `Good` quality |
| `PacketLossPoor` | 0.05 (5%) | Below this: `Fair` quality; at or above: `Poor` |
| `PacketLossSevere` | 0.10 (10%) | Sustained above this threshold triggers `OnConnectionError` |

### Intervals and timing

| Constant | Value | Used by |
|----------|-------|---------|
| `TransportStatsInterval` | 1.0 s | `WebRtcTransport.statsUpdateInterval` default |
| `AdapterStatsInterval` | 2.0 s | `WebRtcTransportAdapter.statsUpdateInterval` default |
| `QualityCheckInterval` | 1.0 s | `WebRtcTransportAdapter.qualityCheckInterval` default |
| `ReconnectDelay` | 5.0 s | `NetworkSessionConfig.reconnectDelay` default |

### Hysteresis and retry

| Constant | Value | Meaning |
|----------|-------|---------|
| `PoorQualitySamplesBeforeError` | 3 | Consecutive poor samples before `OnConnectionError` fires |
| `MaxReconnectAttempts` | 5 | Signaling reconnect attempts before `OnReconnectFailed` fires |
| `WebRtcOperationMaxRetries` | 2 | Offer/answer retries before giving up on a peer |
| `WebRtcRetryDelay` | 1.0 s | Delay between each offer/answer retry |

At the default `QualityCheckInterval` of 1 second and `PoorQualitySamplesBeforeError` of 3, a connection must be in a degraded state for at least 3 consecutive seconds before `OnConnectionError` fires. Reduce `PoorQualitySamplesBeforeError` for faster detection, or increase it to tolerate longer transient spikes.
