namespace NatTraversal.Runtime.Core.Messages.Client {
    public struct ClientConnectedMessage {
        public readonly bool Success;
        public readonly bool IsClientOnly;
        public readonly string Error;

        public ClientConnectedMessage(bool success, bool isClientOnly, string error = null) {
            Success = success;
            IsClientOnly = isClientOnly;
            Error = error;
        }
    }
}
