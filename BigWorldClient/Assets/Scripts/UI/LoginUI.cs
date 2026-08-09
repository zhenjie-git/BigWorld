using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BigWorldClient.UI
{
    public class LoginUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private TMP_InputField usernameInput;
        [SerializeField] private TMP_InputField passwordInput;
        [SerializeField] private Button loginButton;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text titleText;

        [Header("Settings")]
        [SerializeField] private int minUsernameLength = 3;
        [SerializeField] private int minPasswordLength = 6;

        [Header("Events")]
        public UnityEvent<LoginData> onLoginAttempt;

        private void Awake()
        {
            if (loginButton != null)
                loginButton.onClick.AddListener(OnLoginClicked);

            if (usernameInput != null)
                usernameInput.onValueChanged.AddListener(_ => ClearStatus());
            if (passwordInput != null)
                passwordInput.onValueChanged.AddListener(_ => ClearStatus());
        }

        private void OnDestroy()
        {
            if (loginButton != null)
                loginButton.onClick.RemoveListener(OnLoginClicked);
            if (usernameInput != null)
                usernameInput.onValueChanged.RemoveAllListeners();
            if (passwordInput != null)
                passwordInput.onValueChanged.RemoveAllListeners();
        }

        public void OnLoginClicked()
        {
            string username = usernameInput != null ? usernameInput.text.Trim() : "";
            string password = passwordInput != null ? passwordInput.text : "";

            if (string.IsNullOrEmpty(username))
            {
                ShowError("Please enter a username");
                return;
            }
            if (username.Length < minUsernameLength)
            {
                ShowError($"Username must be at least {minUsernameLength} characters");
                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                ShowError("Please enter a password");
                return;
            }
            if (password.Length < minPasswordLength)
            {
                ShowError($"Password must be at least {minPasswordLength} characters");
                return;
            }

            ClearStatus();
            var loginData = new LoginData
            {
                username = username,
                password = password
            };
            onLoginAttempt?.Invoke(loginData);
            Debug.Log($"[LoginUI] Login attempt for user: {username}");
        }

        public void ShowError(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
                statusText.color = new Color(1f, 0.4f, 0.4f);
            }
        }

        public void ShowSuccess(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
                statusText.color = new Color(0.4f, 1f, 0.4f);
            }
        }

        public void ClearStatus()
        {
            if (statusText != null)
                statusText.text = "";
        }

        public void ClearInputs()
        {
            if (usernameInput != null) usernameInput.text = "";
            if (passwordInput != null) passwordInput.text = "";
            ClearStatus();
        }

        public void SetInteractable(bool interactable)
        {
            if (usernameInput != null) usernameInput.interactable = interactable;
            if (passwordInput != null) passwordInput.interactable = interactable;
            if (loginButton != null) loginButton.interactable = interactable;
        }
    }

    [System.Serializable]
    public struct LoginData
    {
        public string username;
        public string password;
    }
}
