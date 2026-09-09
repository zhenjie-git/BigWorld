using UnityEngine;

namespace BigWorldClient.UI.Panels
{

    public class LoginViewModel : Framework.BindableObject
    {

        private string _username = "";
        private string _password = "";
        private string _statusText = "";
        private bool _hasError;
        private bool _isSubmitting;

        public int MinUsernameLength { get; set; } = 3;
        public int MinPasswordLength { get; set; } = 6;

        public string Username
        {
            get { return _username; }
            set
            {
                SetProperty(ref _username, value);

                if (_hasError) ClearStatus();
            }
        }

        public string Password
        {
            get { return _password; }
            set
            {
                SetProperty(ref _password, value);
                if (_hasError) ClearStatus();
            }
        }

        public string StatusText
        {
            get { return _statusText; }
            set { SetProperty(ref _statusText, value); }
        }

        public bool HasError
        {
            get { return _hasError; }
            set { SetProperty(ref _hasError, value); }
        }

        public bool IsSubmitting
        {
            get { return _isSubmitting; }
            set { SetProperty(ref _isSubmitting, value); }
        }

        public bool CanSubmit
        {
            get
            {
                return !_isSubmitting
                    && !string.IsNullOrWhiteSpace(_username);
            }
        }

        private bool _subscribedToEvents;

        public void Login()
        {
            string usernameTrimmed = _username.Trim();

            if (string.IsNullOrWhiteSpace(_username))
            {
                ShowError("Please enter a _username");
                return;
            }
            if (usernameTrimmed.Length < MinUsernameLength)
            {
                ShowError(string.Format("Username must be at least {0} characters", MinUsernameLength));
                return;
            }
            ClearStatus();
            IsSubmitting = true;

            Events.UIEventBus.Publish(new Events.LoginAttemptEvent
            {
                Username = usernameTrimmed,
                Password = _password
            });

            {}
        }

        public void SubscribeToEvents()
        {
            if (_subscribedToEvents) return;
            Events.UIEventBus.Subscribe<Events.LoginResultEvent>(OnLoginResult);
            _subscribedToEvents = true;
        }

        public void UnsubscribeFromEvents()
        {
            if (!_subscribedToEvents) return;
            Events.UIEventBus.Unsubscribe<Events.LoginResultEvent>(OnLoginResult);
            _subscribedToEvents = false;
        }

        public void ShowFailure(string message)
        {
            StatusText = message;
            HasError = true;
        }

        public void ClearInputs()
        {
            Username = "";
            Password = "";
            ClearStatus();
            IsSubmitting = false;
        }

        private void OnLoginResult(Events.LoginResultEvent evt)
        {
            IsSubmitting = false;
            if (evt.Success)
            {
                StatusText = evt.Message;
                HasError = false;
            }
            else
            {
                StatusText = evt.Message;
                HasError = true;
            }
        }

        private void ShowError(string message)
        {
            StatusText = message;
            HasError = true;
        }

        private void ClearStatus()
        {
            StatusText = "";
            HasError = false;
        }
    }
}
