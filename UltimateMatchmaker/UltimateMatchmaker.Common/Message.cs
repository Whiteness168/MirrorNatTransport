using System.Net;
using System.Runtime.Serialization;
using System;

namespace UltimateMatchmaker.Common {

    public enum MessageType {
        RegisterRequest, // Client registering with relay server
        RegisterResponse, // Response with client id
        MatchRegisterRequest,
        MatchRegisterResponse,
        MatchListRequest,
        MatchListResponse,
        PunchRequest, // Request to initiate hole punching
        PunchResponse, // Response with endpoint information
        KeepAlive, // Keep-alive message
        Disconnect, // Disconnect notification
    }

    [Serializable]
    public class Message {
        public MessageType Type { get; set; }
        public uint? ClientId { get; set; }
        public uint? TargetClientId { get; set; }
        public string MatchId { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; }
        public string RelayIpAddress { get; set; } = string.Empty;
        public int RelayPort { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public string Error { get; set; } = string.Empty;

        public IPEndPoint? GetEndPoint() {
            if (IPAddress.TryParse(IpAddress, out var ipAddress)) {
                return new IPEndPoint(ipAddress, Port);
            }

            return null;
        }

        public IPEndPoint? GetRelayEndPoint() {
            if (IPAddress.TryParse(RelayIpAddress, out var relayIpAddress)) {
                return new IPEndPoint(relayIpAddress, RelayPort);
            }

            return null;
        }

        public void SetEndPoint(IPEndPoint endpoint) {
            IpAddress = endpoint.Address.ToString();
            Port = endpoint.Port;
        }

        public void SetRelayEndPoint(IPEndPoint endpoint) {
            RelayIpAddress = endpoint.Address.ToString();
            RelayPort = endpoint.Port;
        }

        public string PrettyAddress() {
            return IpAddress + ":" + Port;
        }
    }

    [Serializable]
    public class ClientInfo {
        public uint ClientId { get; set; }
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; }

        [IgnoreDataMember] public IPEndPoint? PublicEndPoint { get; set; }

        [IgnoreDataMember] public DateTime LastSeen { get; set; }
    }
}
