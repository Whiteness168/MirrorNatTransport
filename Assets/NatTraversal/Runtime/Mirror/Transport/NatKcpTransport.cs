using Mirror;
using NatTraversal.Runtime.Core.Routing;
using NatTraversal.Runtime.Kcp.RelayAware.Client;
using NatTraversal.Runtime.Kcp.RelayAware.Server;
using kcp2k;
using UnityEngine;

namespace NatTraversal.Runtime.Mirror.Transport {
    public class NatKcpTransport : KcpTransport {
        public RelayRoute ClientRoute { get; private set; } = RelayRoute.None;
        
        protected RelayAwareKcpClient RelayClient => client as RelayAwareKcpClient;
        protected RelayAwareKcpServer RelayServer => server as RelayAwareKcpServer;
        
        protected override void Awake() {
            config = new KcpConfig(DualMode, RecvBufferSize, SendBufferSize, kcp2k.Kcp.MTU_DEF, NoDelay, Interval, FastResend, false, SendWindowSize, ReceiveWindowSize, Timeout, MaxRetransmit);
            client = CreateClient();
            server = CreateServer();
            
            if (statisticsLog)
                InvokeRepeating(nameof(OnLogStatistics), 1, 1);

            Log.Info("KcpTransport initialized!");
        }
        
        private KcpClient CreateClient() {
            return new RelayAwareKcpClient(() => {
                    Debug.Log(
                        $"[KCP Transport]: Client connected. local={client?.LocalEndPoint} remote={client?.remoteEndPoint} port={Port}");
                    OnClientConnected.Invoke();
                }, (message, channel) => OnClientDataReceived.Invoke(message, FromKcpChannel(channel)), () => {
                    Debug.Log(
                        $"[KCP Transport]: Client disconnected. local={client?.LocalEndPoint} remote={client?.remoteEndPoint} port={Port}");
                    OnClientDisconnected?.Invoke();
                }, // may be null in StopHost(): https://github.com/MirrorNetworking/Mirror/issues/3708
                (error, reason) => {
                    Debug.LogWarning(
                        $"[KCP Transport]: Client error {error}. local={client?.LocalEndPoint} remote={client?.remoteEndPoint} reason={reason}");
                    OnClientError?.Invoke(ToTransportError(error), reason);
                }, // may be null during shutdown: https://github.com/MirrorNetworking/Mirror/issues/3876
                config);
        }

        private KcpServer CreateServer() {
            return new RelayAwareKcpServer((connectionId, endPoint) => {
                    Debug.Log(
                        $"[KCP Transport]: Server accepted connId={connectionId} remote={endPoint} local={server?.LocalEndPoint}");
                    OnServerConnectedWithAddress.Invoke(connectionId, endPoint.PrettyAddress());
                },
                (connectionId, message, channel) =>
                    OnServerDataReceived.Invoke(connectionId, message, FromKcpChannel(channel)), (connectionId) => {
                    Debug.Log(
                        $"[KCP Transport]: Server disconnected connId={connectionId} remote={server?.GetClientEndPoint(connectionId)} local={server?.LocalEndPoint}");
                    OnServerDisconnected.Invoke(connectionId);
                }, (connectionId, error, reason) => {
                    Debug.LogWarning(
                        $"[KCP Transport]: Server error connId={connectionId} remote={server?.GetClientEndPoint(connectionId)} error={error} reason={reason}");
                    OnServerError.Invoke(connectionId, ToTransportError(error), reason);
                }, config);
        }
        
        public override void ClientDisconnect() {
            Debug.Log(
                $"[KCP Transport]: ClientDisconnect requested. local={client?.LocalEndPoint} remote={client?.remoteEndPoint} connected={client?.connected}");

            base.ClientDisconnect();
        }
        
        public void ConfigureClientRoute(RelayRoute route) {
            ClientRoute = route ?? RelayRoute.None;
            Log.Info($"[KCP] RelayAwareTransport: ConfigureClientRoute {ClientRoute}");
            RelayClient?.ConfigureRoute(ClientRoute);
        }

        public void SendServerPunchPackets(System.Net.IPEndPoint endPoint, int count) {
            RelayServer?.SendPunchPackets(endPoint, count);
        }
    }
}
