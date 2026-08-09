using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BigWorldClient.UI.Panels
{
    /// <summary>
    /// Thin View layer for the login panel. All data and logic live in LoginViewModel.
    /// This class only handles UI widget wiring and ViewModel→UI refresh.
    /// </summary>
    public class LoginPanel : Framework.BasePanel
    {
        [Header("UI References")]
        [SerializeField] private TMP_InputField usernameInput;
        [SerializeField] private TMP_InputField passwordInput;
        [SerializeField] private Button loginButton;
        [SerializeField] private TMP_Text statusText;

        [Header("Settings")]
        [SerializeField] private int minUsernameLength = 3;
        [SerializeField] private int minPasswordLength = 6;

        // The ViewModel is created per-panel-instance. If you want a globally
        // shared ViewModel, inject it via OnShow(args) instead.
        private LoginViewModel vm;

        protected override void OnInit()
        {
            vm = new LoginViewModel();
            vm.MinUsernameLength = minUsernameLength;
            vm.MinPasswordLength = minPasswordLength;

            // VM → View binding
            vm.PropertyChanged += OnVMPropertyChanged;

            // View → VM binding
            usernameInput.onValueChanged.AddListener(v => vm.Username = v);
            passwordInput.onValueChanged.AddListener(v => vm.Password = v);
            loginButton.onClick.AddListener(() => vm.Login());
        }

        protected override void OnShow(object args)
        {
            vm.ClearInputs();
            RefreshAll();
            vm.SubscribeToEvents();
        }

        protected override void OnHide()
        {
            // No-op. ViewModel state is preserved for cache/re-open scenarios.
        }

        protected override void OnCleanup()
        {
            vm.UnsubscribeFromEvents();
            vm.PropertyChanged -= OnVMPropertyChanged;

            loginButton.onClick.RemoveAllListeners();
            usernameInput.onValueChanged.RemoveAllListeners();
            passwordInput.onValueChanged.RemoveAllListeners();
        }

        // ===== VM → View refresh =====

        /// <summary>
        /// Called whenever any ViewModel property changes.
        /// Maps property names to individual UI updates.
        /// </summary>
        private void OnVMPropertyChanged(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(vm.StatusText):
                case nameof(vm.HasError):
                    statusText.text = vm.StatusText;
                    statusText.color = vm.HasError
                        ? new Color(1f, 0.4f, 0.4f)
                        : new Color(0.4f, 1f, 0.4f);
                    break;

                case nameof(vm.IsSubmitting):
                    ApplyInteractable();
                    break;

                case nameof(vm.Username):
                case nameof(vm.Password):
                    // Could update CanSubmit visual here if desired
                    break;
            }
        }

        /// <summary>
        /// Full UI refresh (called once on OnShow to sync everything).
        /// </summary>
        private void RefreshAll()
        {
            statusText.text = vm.StatusText;
            statusText.color = vm.HasError
                ? new Color(1f, 0.4f, 0.4f)
                : new Color(0.4f, 1f, 0.4f);
            ApplyInteractable();
        }

        private void ApplyInteractable()
        {
            bool interactable = !vm.IsSubmitting;
            usernameInput.interactable = interactable;
            passwordInput.interactable = interactable;
            loginButton.interactable = interactable;
        }
    }
}
