using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using System.Threading.Tasks;
using Elements.Crossfire;

public class MainMenuViewController : MonoBehaviour
{
    [Header("Prefabs")]
    [SerializeField]
    private NetworkManager networkManagerPrefab;


    [Header("UI Management")]
    [SerializeField]
    private LoginViewController loginViewController;

    [SerializeField]
    private LobbyViewController lobbyViewController;

    [SerializeField]
    private MatchLoadingViewController matchLoadingViewController;

    [SerializeField]
    private Toggle loginViewControllerToggle;

    [SerializeField]
    private Toggle lobbyViewControllerToggle;

    [SerializeField]
    private Toggle matchLoadingViewControllerToggle;

    private void Awake()
    {
        if (NetworkManager.Singleton == null)
        {
            Instantiate(networkManagerPrefab);
        }
    }

    private void Start()
    {
        loginViewController.OnNext += GoToLobby;
        lobbyViewController.OnNext += GoToMatch;
        lobbyViewController.OnBack += GoToLogin;
        matchLoadingViewController.OnBack += GoToLobby;
        matchLoadingViewController.OnNext += LoadGame;
    }

    private void GoToLobby()
    {
        Debug.Log("Navigating to lobby screen");
        lobbyViewControllerToggle.isOn = true;
    }

    private void GoToLogin()
    {
        Debug.Log("Navigating to login screen");
        loginViewControllerToggle.isOn = true;
    }

    private void GoToMatch()
    {
        Debug.Log("Navigating to match loading screen");
        matchLoadingViewControllerToggle.isOn = true;
        matchLoadingViewController.LoadMatch(lobbyViewController.SelectedMatch);
    }

    private async void LoadGame()
    {
        int timeout = 1000;
        int tick = 100;
        // This is important as it may take a moment to fully set up the
        // NetworkManager, and the SceneManager is created later in the process
        while (NetworkManager.Singleton.SceneManager == null || timeout <= 0)
        {
            if (NetworkManager.Singleton == null) return;

            Debug.Log("Waiting for network manager to start...");
            await Task.Delay(tick);
            timeout -= tick;
        }

        //Clients cannot load the scene
        if (NetworkManager.Singleton.IsServer)
            NetworkManager.Singleton.SceneManager.LoadScene("PongMulti", UnityEngine.SceneManagement.LoadSceneMode.Single);
    }
}
