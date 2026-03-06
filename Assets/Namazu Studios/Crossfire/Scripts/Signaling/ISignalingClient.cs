using System;

namespace Elements.Crossfire
{
    using Model;

    /// <summary>
    /// Signaling abstraction
    /// </summary>
    public interface ISignalingClient
    {
        event Action OnConnected;
        event Action<SignalingMessage> OnMessageReceived;
        event Action OnDisconnected;

        event Action<int> OnReconnectAttempt;    // attempt number
        event Action<float> OnReconnectCountdown; // seconds remaining
        event Action OnReconnectFailed;           // all retries exhausted
        event Action<string> OnSignalingError;    // error details

        void Connect(string url, string profileId, string sessionToken);
        void Dispatch(string message);
        void Disconnect();
        bool IsConnected { get; }
    }
}