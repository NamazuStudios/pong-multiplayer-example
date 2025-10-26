using UnityEngine;
using UnityEngine.UI;
using System;
using System.Linq;
using System.Threading.Tasks;
using Elements.Client;
using Elements.Model;
using Elements.Crossfire;

public class LoginViewController : MonoBehaviour, IViewController
{
    [SerializeField]
    private Toggle loginToExistingAccountView;

    [SerializeField]
    private Toggle activeSessionView;

    [SerializeField]
    private Toggle newSessionView;

    [SerializeField]
    private Toggle createAccountView;

    [SerializeField]
    private Toggle createProfileView;

    [SerializeField]
    private TMPro.TMP_Text welcomeBackText;

    [SerializeField]
    private TMPro.TMP_InputField loginUsernameInputField;

    [SerializeField]
    private TMPro.TMP_InputField loginPasswordInputField;

    [SerializeField]
    private TMPro.TMP_InputField signUpUsernameInputField;

    [SerializeField]
    private TMPro.TMP_InputField signUpPasswordInputField;

    [SerializeField]
    private TMPro.TMP_InputField signUpDisplayNameInputField;

    [SerializeField]
    private TMPro.TMP_InputField createProfileDisplayNameInputField;

    [SerializeField]
    private TMPro.TMP_Text createAccountErrorText;

    // IViewController Events
    public event Action OnBack;
    public event Action OnNext;

#region Setup

    private void Start()
    {
        ElementsClient.InitializeDefault(ElementsProperties.ELEMENTS_ROOT_URL);

        SetDefaultViewState();        
    }

    private async void SetDefaultViewState()
    {
        if(ElementsClient.Default.IsSessionActive())
        {
            if(ElementsClient.Default.GetSession().Profile == null)
            {
                await FetchProfile(ElementsClient.Default.GetSession().User.Id);
            }

            activeSessionView.isOn = true;
            welcomeBackText.text = $"Welcome back\n{ElementsClient.Default.GetSession()?.Profile?.DisplayName}!";
        }
        else
        {            
            newSessionView.isOn = true;
        }

        createAccountErrorText.text = null;
    }

#endregion
#region Button Click Events

    public void OnNewAccountButtonPress()
    {        
        createAccountView.isOn = true;
    }

    public void OnExistingAccountButtonPress()
    {
        loginToExistingAccountView.isOn = true;
    }

    public void OnLoginButtonPress()
    {
        DoLogin(loginUsernameInputField.text, loginPasswordInputField.text);
    }

    public void OnContinuePress()
    {
        OnNext?.Invoke();
    }

    public void OnLogoutPress()
    {
        ElementsClient.Default.ClearSession();
        OnBack?.Invoke();
        newSessionView.isOn = true;
    }

    public void OnCreateAccount()
    {
        DoUsernamePasswordSignUp(signUpUsernameInputField.text, signUpPasswordInputField.text, signUpDisplayNameInputField.text);
    }

    public void OnGoogleLoginButtonPressed()
    {
        DoLoginWithGoogle();
    }

    public void OnCreateProfilePressed()
    {
        CreateProfile(createProfileDisplayNameInputField.text);
    }

    #endregion


    private async void DoLogin(string username, string password, string profileId = null)
    {
        var session = await ElementsClient.Default.DoLoginAsync(username, password, profileId);

        if (session != null)
        {
            OnNext?.Invoke();
        }
    }

    private async void DoUsernamePasswordSignUp(string username, string password, string displayname)
    {
        var userCreateResponse = await ElementsClient.Default.DoSignUpAsync(username, password, displayname);

        if (userCreateResponse != null)
        {
            DoLogin(username, password, userCreateResponse.Profiles.FirstOrDefault()?.Id);
        }
    }

    private async Task FetchProfile(string userId)
    {
        await ElementsClient.Default.FetchProfileAsync(userId);
    }

    private async void DoLoginWithGoogle()
    {
        var googleLogin = new GoogleOAuthService();
        string idToken = await googleLogin.LoginAsync();
        Debug.Log("Google ID Token: " + idToken);

        // Send to your server
        var sessionCreation = await ElementsClient.Default.Api.CreateOidcSessionAsync(
            new OidcSessionRequest(jwt: idToken)
        );

        if (sessionCreation != null)
        {
            ElementsClient.Default.SetSessionCreation(sessionCreation);
            await FetchProfile(sessionCreation.Session.User.Id);

            if(ElementsClient.Default.GetSession().Profile == null)
            {
                createProfileView.isOn = true;
            }
            else
            {
                OnNext?.Invoke();
            }
        }
    }

    private async void CreateProfile(string displayName)
    {
        var profile = await ElementsClient.Default.Api.CreateProfileAsync(new CreateProfileRequest
        (
            applicationId: ElementsProperties.ELEMENTS_APPLICATION_NAME,
            displayName: displayName,
            userId: ElementsClient.Default.GetSession().User.Id
        ));

        if (profile != null)
        {
            ElementsClient.Default.GetSession().Profile = profile;
            OnNext?.Invoke();
        }
    }

}
