namespace NatTraversal.Runtime.Core.Messages.Server {
    public struct ServerCreatedMessage {
        public readonly bool Success;
        public readonly string Error;

        public ServerCreatedMessage(bool success, string error = null) {
            Success = success;
            Error = error;
        }
    }
}
