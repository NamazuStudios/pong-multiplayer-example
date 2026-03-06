# Architecture

---

## Component Overview

```
NetworkSessionManager          (main orchestrator, session and player lifecycle)
  ISignalingClient             (WebSocket communication with Crossfire server)
  INetworkTransportAdapter     (peer connection abstraction)
    WebRtcTransportAdapter     (profile ID mapping, quality monitoring, outbound signaling)
      WebRtcTransport          (Unity WebRTC / NGO NetworkTransport implementation)
  NetworkManager               (Unity Netcode for GameObjects)
```

### Responsibilities

**NetworkSessionManager** owns the session lifecycle: connecting to the signaling server, finding or creating matches, tracking which players are in the match, and deciding when to start the NGO `NetworkManager` as host or client. It translates low-level transport events into the player-oriented events that game code consumes.

**WebRtcTransportAdapter** sits between `NetworkSessionManager` and `WebRtcTransport`. Its job is to translate between the signaling system's profile ID strings and the numeric NGO client IDs that `WebRtcTransport` works with. It also runs the quality-monitoring coroutines and rate-limits error events using hysteresis counters.

**WebRtcTransport** implements Unity's `NetworkTransport` abstract class so that NGO can use WebRTC data channels as its byte transport. It handles the WebRTC state machine (offer, answer, ICE), manages `RTCPeerConnection` and `RTCDataChannel` objects, and periodically polls `RTCStatsReport` for latency and packet-loss data. Its events are `internal` and are consumed only by `WebRtcTransportAdapter`.

**WebSocketSignalingClient** maintains the persistent WebSocket connection to the Crossfire signaling server. It handles automatic reconnection with a countdown and fires events when reconnection succeeds, is in progress, or has been exhausted.

---

## NetworkSessionState State Machine

```
Disconnected
     |
     | StartSession()
     v
  Connecting ---(signaling disconnect)---> Reconnecting
     |                                          |
     | OnConnected                              | OnConnected (after retry)
     v                                          |
  Connected <-----------------------------------+
     |
     | FindOrCreateMatch() / JoinMatch()
     v
FindingMatch / JoiningMatch
     |
     | SERVER: MATCHED message
     v
   InMatch
     |
     | All active peers connected via WebRTC
     v
  MatchReady

Any state ----> Error         (unrecoverable failure)
Any state ----> Disconnected  (EndSession())
```

### What each state means for gameplay

| State | Meaning | Recommended response |
|-------|---------|----------------------|
| `Disconnected` | No active session | Show main menu or login screen |
| `Connecting` | Establishing WebSocket | Show "Connecting..." indicator |
| `Reconnecting` | WebSocket lost, retrying | Show "Reconnecting..." with countdown from `OnReconnectCountdown` |
| `Connected` | Signaling ready, no match | Show matchmaking UI |
| `FindingMatch` | Waiting for matchmaking | Show "Finding match..." spinner |
| `JoiningMatch` | Joining a specific match by ID | Show "Joining..." spinner |
| `InMatch` | Matched but WebRTC not yet fully established | Show "Waiting for players..." |
| `MatchReady` | All active players connected | Begin gameplay |
| `Error` | Unrecoverable failure | Show error message with retry option |

---

## Connection Flow

The sequence below describes what happens from `StartSession()` to gameplay start.

1. `StartSession(profileId, sessionToken)` opens a WebSocket to `{serverHost}/match`.
2. On connect, `FindOrCreateMatch(configurationName)` sends a matchmaking request to the server.
3. The server responds with a `MATCHED` message. `NetworkSessionManager` records the match ID and transitions to `InMatch`.
4. The server sends a `HOST` message identifying which profile ID is the NGO host for this match.
5. The non-host client receives a `CONNECT` message for the host and calls `BeginConnection` on the adapter, which creates an `RTCPeerConnection` and sends an SDP offer via the signaling server.
6. The host receives the offer, creates an answer, and sends it back through the signaling server. ICE candidates are exchanged the same way.
7. Once both sides have a connected ICE candidate pair, the offerer's data channel opens. `OnDataChannelReady` fires on `WebRtcTransport`, which triggers `OnPeerReady` on the adapter and `OnPlayerJoined` on `NetworkSessionManager`.
8. `NetworkSessionManager` calls `NetworkManager.StartHost()` on the host and `NetworkManager.StartClient()` on the client. NGO traffic then flows directly over the WebRTC data channel, bypassing the signaling server entirely.
9. When all active players are connected, `OnAllPlayersConnected` fires and `State` transitions to `MatchReady`.

---

## Local Testing

For same-machine testing (two Unity Editor instances), set `WebRtcTransport.mode` to `LOCAL` in the Inspector. LOCAL mode removes all STUN servers from the ICE configuration, which means ICE will only gather host candidates. This works on a single machine or local network but will not traverse NAT.

For remote peers (different networks), keep mode set to `REMOTE`.

### Setting up two-editor testing

1. Use [ParrelSync](https://github.com/VeriorPies/ParrelSync) or [Multiplayer Play Mode](https://docs.unity3d.com/Packages/com.unity.multiplayer.playmode@2.0/manual/index.html) to run two Editor instances from the same project.
2. Open the `TestConnection` scene in both instances.
3. In each instance, log in with a different test player (`TestPlayer1` and `TestPlayer2`). This requires the Elements Codegen plugin and a running Elements + Crossfire instance.
4. Both players click "Find or Create Match". The first player becomes the host; the second connects as a client.
5. The host spawns a `TestNetworkObject`. Confirm it appears in the client Editor. Modify the `Counter` property on the host and confirm it syncs to the client.

The `MATCHMAKING_CONFIGURATION` constant in `NetworkTestViewController.cs` is set to `"default"`, which must match a matchmaking configuration you have created in your Elements application.
