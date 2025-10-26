using UnityEngine;
using ParrelSync;
using System.Linq;
using System.Threading.Tasks;
using Elements.Client;
using Elements.Crossfire;

public class CPUPlayerLoader : MonoBehaviour
{     

    public static string USER_NAME = "cpu_player";
    public static string DISPLAY_NAME = "CPU";
    
    private async void Start()
    {
        await CheckCpuPlayer();
    }

    private async Task CheckCpuPlayer()
    {
        if (ClonesManager.IsClone())
        {
            Debug.Log("Logging in as a CPU player");

            const string password = "test";

            if(ElementsClient.Default == null)
            {
                ElementsClient.InitializeDefault(ElementsProperties.ELEMENTS_ROOT_URL, false);
            }

            if(!ElementsClient.Default.IsSessionActive())
            {
                await DoUsernamePasswordSignUp(USER_NAME, password, DISPLAY_NAME);
            }

            //Forcibly proceed through the UI
            var loginVC = FindAnyObjectByType<LoginViewController>();
            loginVC.OnContinuePress();
        }
    }

    private async Task DoUsernamePasswordSignUp(string username, string password, string displayname)
    {
        try
        {
            var userCreateResponse = await ElementsClient.Default.DoSignUpAsync(username, password, displayname);

            if (userCreateResponse != null)
            {                
                await DoLogin(username, password, userCreateResponse.Profiles.FirstOrDefault()?.Id);
            }
        }
        catch
        {
            await DoLogin(username, password);
        }
    }

    private async Task DoLogin(string username, string password, string profileId = null)
    {
        // Clear headers of any old session data in case it expired
        ElementsClient.Default.LogOut();

        var session = await ElementsClient.Default.DoLoginAsync(username, password, profileId);

        if (session != null)
        {
            if (!NetworkSessionManager.Instance.IsSessionActive)
            {
                Debug.Log($"Starting session with id {session.Session.Profile.Id}");
                NetworkSessionManager.Instance.StartSession(session.Session.Profile.Id, session.SessionSecret);
            }            
        }
    }

}
