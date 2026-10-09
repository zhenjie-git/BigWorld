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

    public struct SessionEndedEvent
    {

        public string Reason;
    }
}
