using UnityEngine;
using System;
using Elements.Crossfire;

namespace Elements.Crossfire.Model
{

    [Serializable]
    public class NetworkSessionConfig
    {
        [Header("Network Config")]
        public string serverHost = "ws://localhost:8080/app/ws/crossfire";
        public float reconnectDelay = CrossfireConstants.ReconnectDelay;
        public bool autoReconnect = true;

        [Header("For Debugging")]
        public string matchId;
        public string profileId;
        public string sessionToken;
    }
}