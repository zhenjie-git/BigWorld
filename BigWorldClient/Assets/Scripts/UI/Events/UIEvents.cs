namespace BigWorldClient.UI.Events
{
    public struct PanelOpenEvent
    {
        public string PanelId;
    }

    public struct PanelCloseEvent
    {
        public string PanelId;
    }

    public struct PanelFocusChanged
    {
        public string PanelId;
        public bool IsFocused;
    }

    public struct LoginAttemptEvent
    {
        public string Username;
        public string Password;
    }

    public struct LoginResultEvent
    {
        public bool Success;
        public string Message;
    }

    public struct SceneLoadCompleteEvent
    {
        public string SceneName;
    }

    public struct SessionEndedEvent
    {
        /// <summary>Why the session ended: "logout", "disconnected", "server_shutdown".</summary>
        public string Reason;
    }
}
