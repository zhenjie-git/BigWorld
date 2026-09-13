using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BigWorldClient.UI.Panels
{

    public class LoginPanel : Framework.BasePanel
    {
        [Header("UI References")]
        [SerializeField] private TMP_InputField _usernameInput;
        [SerializeField] private TMP_InputField _passwordInput;
        [SerializeField] private Button _loginButton;
        [SerializeField] private TMP_Text _statusText;

        [Header("Settings")]
        [SerializeField] private int _minUsernameLength = 3;
        [SerializeField] private int _minPasswordLength = 6;

        private LoginViewModel _vm;
        private TMP_Text _loginButtonText;

        protected override void OnInit()
        {
            _vm = new LoginViewModel();
            _vm.MinUsernameLength = _minUsernameLength;
            _vm.MinPasswordLength = _minPasswordLength;

            _vm.PropertyChanged += OnVMPropertyChanged;

            _usernameInput.onValueChanged.AddListener(v => _vm.Username = v);
            _passwordInput.onValueChanged.AddListener(v => _vm.Password = v);
            _passwordInput.onSubmit.AddListener(_ => _vm.Login());
            _loginButton.onClick.AddListener(() => _vm.Login());

            Transform buttonText = _loginButton.transform.Find("Text");
            _loginButtonText = buttonText != null ? buttonText.GetComponent<TMP_Text>() : null;
            Framework.UiSkin.ApplyLogin(transform, _usernameInput, _passwordInput, _loginButton, _statusText);
        }

        protected override void OnShow(object args)
        {
            _vm.ClearInputs();
            if (args is string message && message.Length > 0)
                _vm.ShowFailure(message);
            RefreshAll();
            _vm.SubscribeToEvents();
        }

        protected override void OnHide()
        {

        }

        protected override void OnCleanup()
        {
            _vm.UnsubscribeFromEvents();
            _vm.PropertyChanged -= OnVMPropertyChanged;

            _loginButton.onClick.RemoveAllListeners();
            _usernameInput.onValueChanged.RemoveAllListeners();
            _passwordInput.onValueChanged.RemoveAllListeners();
            _passwordInput.onSubmit.RemoveAllListeners();
        }

        private void OnVMPropertyChanged(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(_vm.StatusText):
                case nameof(_vm.HasError):
                    _statusText.text = _vm.StatusText;
                    _statusText.color = _vm.HasError ? Framework.UiSkin.ErrorColor : Framework.UiSkin.SuccessColor;
                    break;

                case nameof(_vm.IsSubmitting):
                    ApplyInteractable();
                    if (_loginButtonText != null)
                        _loginButtonText.text = _vm.IsSubmitting ? "SIGNING IN..." : "SIGN IN";
                    break;

                case nameof(_vm.Username):
                case nameof(_vm.Password):
                    ApplyInteractable();
                    break;
            }
        }

        private void RefreshAll()
        {
            _statusText.text = _vm.StatusText;
            _statusText.color = _vm.HasError ? Framework.UiSkin.ErrorColor : Framework.UiSkin.SuccessColor;
            ApplyInteractable();
        }

        private void ApplyInteractable()
        {
            bool interactable = !_vm.IsSubmitting;
            _usernameInput.interactable = interactable;
            _passwordInput.interactable = interactable;
            _loginButton.interactable = interactable && _vm.CanSubmit;
        }
    }
}
