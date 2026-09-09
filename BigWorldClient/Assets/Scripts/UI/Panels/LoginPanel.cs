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

        protected override void OnInit()
        {
            _vm = new LoginViewModel();
            _vm.MinUsernameLength = _minUsernameLength;
            _vm.MinPasswordLength = _minPasswordLength;

            _vm.PropertyChanged += OnVMPropertyChanged;

            _usernameInput.onValueChanged.AddListener(v => _vm.Username = v);
            _passwordInput.onValueChanged.AddListener(v => _vm.Password = v);
            _loginButton.onClick.AddListener(() => _vm.Login());
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
        }

        private void OnVMPropertyChanged(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(_vm.StatusText):
                case nameof(_vm.HasError):
                    _statusText.text = _vm.StatusText;
                    _statusText.color = _vm.HasError
                        ? new Color(1f, 0.4f, 0.4f)
                        : new Color(0.4f, 1f, 0.4f);
                    break;

                case nameof(_vm.IsSubmitting):
                    ApplyInteractable();
                    break;

                case nameof(_vm.Username):
                case nameof(_vm.Password):

                    break;
            }
        }

        private void RefreshAll()
        {
            _statusText.text = _vm.StatusText;
            _statusText.color = _vm.HasError
                ? new Color(1f, 0.4f, 0.4f)
                : new Color(0.4f, 1f, 0.4f);
            ApplyInteractable();
        }

        private void ApplyInteractable()
        {
            bool interactable = !_vm.IsSubmitting;
            _usernameInput.interactable = interactable;
            _passwordInput.interactable = interactable;
            _loginButton.interactable = interactable;
        }
    }
}
