using UnityEngine;
using TMPro;
using Elements.Model;

public class JoinMatchButton : MonoBehaviour
{
    [SerializeField]
    private TMP_Text text;

    private LobbyViewController viewController;

    private MultiMatch match;
    
    public void SetMatch(LobbyViewController viewController, MultiMatch match)
    {
        this.viewController = viewController;
        this.match = match;
        this.text.text = $"Match: {match.Id} ({match.Status})";

        if(match.Status != MultiMatch.StatusEnum.OPEN)
        {
            GetComponent<UnityEngine.UI.Button>().interactable = false;
        }
    }

    public void OnClick()
    {
        viewController.JoinMatchLobby(match);
    }
}
