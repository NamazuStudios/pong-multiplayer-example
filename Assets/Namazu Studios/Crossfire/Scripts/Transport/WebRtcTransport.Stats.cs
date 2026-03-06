using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.WebRTC;
using UnityEngine;

namespace Elements.Crossfire
{
    using Model;

    public partial class WebRtcTransport
    {
        // Stats tracking
        private readonly Dictionary<ulong, ParsedWebRTCStats> parsedStats = new();
        private readonly Dictionary<ulong, DateTime> lastStatsUpdate = new();
        private readonly Dictionary<ulong, float> measuredLatencies = new();
        private readonly Dictionary<ulong, long> lastBytesReceived = new();
        private readonly Dictionary<ulong, long> lastBytesSent = new();
        private Coroutine statsUpdateCoroutine;

#region Stats Collection

        public void StartStatsCollection()
        {
            if (statsUpdateCoroutine == null && enableStatsCollection)
            {
                statsUpdateCoroutine = StartCoroutine(UpdateWebRTCStats());
                logger.Log("[WebRtcTransport] Started stats collection");
            }
        }

        public void StopStatsCollection()
        {
            if (statsUpdateCoroutine != null)
            {
                StopCoroutine(statsUpdateCoroutine);
                statsUpdateCoroutine = null;
                logger.Log("[WebRtcTransport] Stopped stats collection");
            }
        }

        private IEnumerator UpdateWebRTCStats()
        {
            while (true)
            {
                // Snapshot to avoid InvalidOperationException if readyPeers is modified mid-iteration
                var connectedClients = readyPeers.ToList();
                foreach (var clientId in connectedClients)
                {
                    if (peerConnections.ContainsKey(clientId))
                    {
                        yield return StartCoroutine(UpdatePeerStatsCoroutine(clientId));
                    }
                }

                yield return new WaitForSeconds(statsUpdateInterval);
            }
        }

        private IEnumerator UpdatePeerStatsCoroutine(ulong clientId)
        {
            if (peerConnections.TryGetValue(clientId, out var peerConnection))
            {
                var statsOp = peerConnection.GetStats();
                yield return statsOp;

                if (!statsOp.IsError && statsOp.Value != null)
                {
                    var parsed = ParseRTCStats(statsOp.Value);
                    parsedStats[clientId] = parsed;
                    lastStatsUpdate[clientId] = DateTime.Now;

                    // Update cached values for quick access
                    measuredLatencies[clientId] = parsed.currentRoundTripTime * 1000f; // Convert to ms
                    lastBytesReceived[clientId] = parsed.bytesReceived;
                    lastBytesSent[clientId] = parsed.bytesSent;

                    // Fire event for external listeners
                    OnStatsUpdated?.Invoke(clientId, parsed);
                }
                else if (statsOp.IsError)
                {
                    logger.LogWarning($"[WebRtcTransport] Failed to get stats for client {clientId}: {statsOp.Error.message}");
                }
            }
        }

        private ParsedWebRTCStats ParseRTCStats(RTCStatsReport statsReport)
        {
            var parsed = new ParsedWebRTCStats
            {
                timestamp = DateTime.Now
            };

            // Parse the stats report
            foreach (var statsPair in statsReport.Stats)
            {
                var stats = statsPair.Value;

                // Extract RTT from candidate pair stats
                if (stats.Type == RTCStatsType.CandidatePair)
                {
                    if (stats.Dict.TryGetValue("state", out var state) && state.ToString() == "succeeded")
                    {
                        if (stats.Dict.TryGetValue("currentRoundTripTime", out var rtt))
                        {
                            parsed.currentRoundTripTime = Convert.ToSingle(rtt);
                            parsed.candidatePairState = state.ToString();
                        }
                    }
                }
                // Extract packet loss and bytes from inbound RTP
                else if (stats.Type == RTCStatsType.InboundRtp)
                {
                    if (stats.Dict.TryGetValue("packetsLost", out var packetsLost))
                        parsed.packetsLost += Convert.ToInt64(packetsLost);

                    if (stats.Dict.TryGetValue("packetsReceived", out var packetsReceived))
                        parsed.packetsReceived += Convert.ToInt64(packetsReceived);

                    if (stats.Dict.TryGetValue("bytesReceived", out var bytesReceived))
                        parsed.bytesReceived += Convert.ToInt64(bytesReceived);
                }
                // Extract bytes sent from outbound RTP
                else if (stats.Type == RTCStatsType.OutboundRtp)
                {
                    if (stats.Dict.TryGetValue("bytesSent", out var bytesSent))
                        parsed.bytesSent += Convert.ToInt64(bytesSent);
                }
            }

            return parsed;
        }

        private void CleanupPeerStats(ulong clientId)
        {
            parsedStats.Remove(clientId);
            lastStatsUpdate.Remove(clientId);
            measuredLatencies.Remove(clientId);
            lastBytesReceived.Remove(clientId);
            lastBytesSent.Remove(clientId);
        }

        private void ClearStatsData()
        {
            parsedStats.Clear();
            lastStatsUpdate.Clear();
            measuredLatencies.Clear();
            lastBytesReceived.Clear();
            lastBytesSent.Clear();
        }

#endregion
#region IWebRtcTransportStats

        public float GetPeerLatency(ulong clientId)
        {
            if (measuredLatencies.TryGetValue(clientId, out var latency))
            {
                return latency;
            }

            // Fallback based on connection state
            return readyPeers.Contains(clientId) ? 50f : 999f;
        }

        public float GetPeerPacketLoss(ulong clientId)
        {
            if (parsedStats.TryGetValue(clientId, out var stats))
            {
                var totalPackets = stats.packetsLost + stats.packetsReceived;
                if (totalPackets > 0)
                {
                    return (float)stats.packetsLost / totalPackets;
                }
            }

            return 0f;
        }

        public long GetBytesReceived(ulong clientId)
        {
            return lastBytesReceived.TryGetValue(clientId, out var bytes) ? bytes : 0;
        }

        public long GetBytesSent(ulong clientId)
        {
            return lastBytesSent.TryGetValue(clientId, out var bytes) ? bytes : 0;
        }

        public RTCStatsReportAsyncOperation GetRTCStatsAsync(ulong clientId)
        {
            if (peerConnections.TryGetValue(clientId, out var peerConnection))
            {
                return peerConnection.GetStats();
            }
            return null;
        }

        public ParsedWebRTCStats GetParsedStats(ulong clientId)
        {
            return parsedStats.TryGetValue(clientId, out var stats) ? stats : default;
        }

#endregion
    }
}
