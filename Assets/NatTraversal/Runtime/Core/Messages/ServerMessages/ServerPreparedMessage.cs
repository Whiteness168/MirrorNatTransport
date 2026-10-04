using NatTraversal.Runtime.Core.Models.Match;

public readonly struct ServerPreparedMessage {
    public readonly Match Match;

    public ServerPreparedMessage(Match match) {
        Match = match;
    }
}
