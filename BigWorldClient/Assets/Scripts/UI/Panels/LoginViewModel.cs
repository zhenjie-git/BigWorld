using UnityEngine;

namespace BigWorldClient.UI.Panels
{
    /// <summary>
    /// Pure C# ViewModel for the login panel. Contains all data, validation, and
    /// event publishing — no Unity UI dependencies. Can be unit tested independently.
    /// </summary>
    public class LoginViewModel : Framework.BindableObject
    {
        // ===== Backing fields =====
        private string username = "";
        private string password = "";
        private string statusText = "";
        private bool hasError;
        private bool isSubmitting;

        // ===== Config =====
        public int MinUsernameLength { get; set; } = 3;
        public int MinPasswordLength { get; set; } = 6;

        // ===== Observable properties =====

        public string Username
        {
            get { return username; }
            set
            {
                SetProperty(ref username, value);
                // When either input changes, clear any stale error
                if (hasError) ClearStatus();
            }
        }

        public string Password
        {
            get { return password; }
            set
            {
                SetProperty(ref password, value);
                if (hasError) ClearStatus();
            }
        }

        public string StatusText
        {
            get { return statusText; }
            set { SetProperty(ref statusText, value); }
        }

        public bool HasError
        {
            get { return hasError; }
            set { SetProperty(ref hasError, value); }
        }

        public bool IsSubmitting
        {
            get { return isSubmitting; }
            set { SetProperty(ref isSubmitting, value); }
        }

        // ===== Computed properties =====

        /// <summary>
        /// Whether the login button should be clickable right now.
        /// True when all inputs are non-empty and not currently submitting.
        /// </summary>
        public bool CanSubmit
        {
            get
            {
                return !isSubmitting
                    && !string.IsNullOrWhiteSpace(username);
            }
        }

        // ===== Event subscription state =====
        private bool subscribedToEvents;

        // ===== Commands =====

        /// <summary>
        /// Validates input and publishes a LoginAttemptEvent if valid.
        /// Call this from the View's login button click handler.
        /// </summary>
        public void Login()
        {
            string usernameTrimmed = username.Trim();

            if (string.IsNullOrWhiteSpace(username))
            {
                ShowError("Please enter a username");
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
                Password = password
            });

            Debug.Log("[LoginVM] Login attempt: " + usernameTrimmed);
        }

        // ===== Event subscription =====

        public void SubscribeToEvents()
        {
            if (subscribedToEvents) return;
            Events.UIEventBus.Subscribe<Events.LoginResultEvent>(this, OnLoginResult);
            subscribedToEvents = true;
        }

        public void UnsubscribeFromEvents()
        {
            if (!subscribedToEvents) return;
            Events.UIEventBus.Unsubscribe<Events.LoginResultEvent>(this);
            subscribedToEvents = false;
        }

        // ===== Reset =====

        /// <summary>
        /// Reset all fields for a fresh show of the panel.
        /// </summary>
        public void ClearInputs()
        {
            Username = "";
            Password = "";
            ClearStatus();
            IsSubmitting = false;
        }

        // ===== Private helpers =====

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
