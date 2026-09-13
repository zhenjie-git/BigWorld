namespace BigWorldClient.UI.Events
{
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

    public struct SceneTransferEvent
    {
        public string SceneId;
        public float X;
        public float Z;
    }

    public struct SessionEndedEvent
    {

        public string Reason;
    }
}
