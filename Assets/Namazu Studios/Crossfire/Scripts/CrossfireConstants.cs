namespace Elements.Crossfire
{
    public static class CrossfireConstants
    {
        // Connection quality thresholds (ms)
        public const float LatencyPoorMs      = 200f;
        public const float LatencyFairMs      = 100f;
        public const float LatencyGoodMs      = 50f;
        public const float LatencySevereMs    = 500f;

        // Connection quality thresholds (ratio)
        public const float PacketLossPoor     = 0.05f;
        public const float PacketLossFair     = 0.02f;
        public const float PacketLossGood     = 0.01f;
        public const float PacketLossSevere   = 0.10f;

        // Stats & monitoring intervals (seconds)
        public const float TransportStatsInterval  = 1f;
        public const float AdapterStatsInterval    = 2f;
        public const float QualityCheckInterval    = 1f;

        // Reconnection
        public const float ReconnectDelay          = 5f;
        public const int   MaxReconnectAttempts    = 5;

        // WebRTC operation retry
        public const int   WebRtcOperationMaxRetries = 2;
        public const float WebRtcRetryDelay          = 1f;

        // Hysteresis
        public const int   PoorQualitySamplesBeforeError = 3;

        // STUN servers
        public static readonly string[] GoogleStunServers = {
            "stun:stun.l.google.com:19302",
            "stun:stun1.l.google.com:19302",
            "stun:stun2.l.google.com:19302",
            "stun:stun3.l.google.com:19302",
            "stun:stun4.l.google.com:19302"
        };
    }
}
