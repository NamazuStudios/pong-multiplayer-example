using UnityEngine;
using System;
using System.Collections;
using Elements.Client;
using Elements.Crossfire;
using Elements.Model;
using Elements.Crossfire.Model;
using TMPro;

public class MatchLoadingViewController : MonoBehaviour, IViewController
{
    [SerializeField]
    private int timeout = 10;

    [SerializeField]
    private TMP_Text messageText;
    
    // IViewController Events
    public event Action OnBack;
    public event Action OnNext;

    private void OnDestroy()
    {
        if(gameObject.activeSelf)
            ClearCallbacks();
    }

    public void LoadMatch(MultiMatch match = null)
    {
        messageText.text = "Waiting for opponent...";

        var sessionManager = NetworkSessionManager.Instance;

        if (!sessionManager.IsSessionActive || !sessionManager.IsSignalingConnected)
        {
            sessionManager.StartSession(ElementsClient.Default.GetSession().Profile.Id,
                                        ElementsClient.Default.GetSessionToken(),
                                        null,
                                        true);

            RegisterCallbacks();
        }

        if (match != null)
        {
            sessionManager.JoinMatch(match.Id);
        }
        else
        {
            sessionManager.FindOrCreateMatch(ElementsProperties.MATCHMAKING_CONFIGURATION);
        }

        StartCoroutine(TimeoutCoroutine());
    }

    public void CancelMatch()
    {
        NetworkSessionManager.Instance.LeaveMatch();
        ClearCallbacks();
        StopAllCoroutines();
        OnBack?.Invoke();
    }

    private void OnMatchJoined(string matchId)
    {
        messageText.text = "Joined match: " + matchId;
    }

    private void OnPlayerJoined(PlayerInfo playerInfo)
    {
        messageText.text = "All players connected! Starting match...";
        StopAllCoroutines();
        OnNext?.Invoke();
    }

    private IEnumerator TimeoutCoroutine()
    {
        int delay = 3;
        int countdown = timeout - delay;

        //Give a chance to establish connection
        yield return new WaitForSeconds(3);

        // We won't time out if we're the host
        if (NetworkSessionManager.Instance.GetSessionInfo().isHost)
            yield break;

        while (countdown > 0)
        {
            yield return new WaitForSeconds(1);

            messageText.text = $"No connection detected, leaving in {countdown}";

            countdown--;
        }

        CancelMatch();
    }

    private void RegisterCallbacks()
    {
        NetworkSessionManager.Instance.OnMatchJoined += OnMatchJoined;
        NetworkSessionManager.Instance.OnPlayerJoined += OnPlayerJoined;
    }

    private void ClearCallbacks()
    {
        NetworkSessionManager.Instance.OnMatchJoined -= OnMatchJoined;
        NetworkSessionManager.Instance.OnPlayerJoined -= OnPlayerJoined;
    }
}
