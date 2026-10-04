using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using kcp2k;
using NatTraversal.Runtime.Core.Routing;

namespace NatTraversal.Runtime.Kcp.RelayAware.Client {
    public class RelayAwareKcpClient : KcpClient {
        private FieldInfo _activeField =
            typeof(KcpClient).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic);

        public RelayRoute Route { get; private set; } = RelayRoute.None;

        public RelayAwareKcpClient(Action onConnected,
            Action<ArraySegment<byte>, KcpChannel> onData,
            Action onDisconnected,
            Action<ErrorCode, string> onError,
            KcpConfig config) : base(onConnected, onData, onDisconnected, onError, config) {
        }

        public void ConfigureRoute(RelayRoute route) {
            Route = route ?? RelayRoute.None;
            Log.Info($"[KCP] RelayAwareClient: configured route {Route}");
        }

        public void Connect(string address, ushort port, RelayRoute route) {
            Connect(address, port);
        }

        public new void Connect(string address, ushort port) {
            var initialEndPoint = Route.GetInitialRemoteEndPoint();
            if (initialEndPoint != null) {
                if (Route.LocalEndPoint != null) {
                    ConnectBound(initialEndPoint, Route.LocalEndPoint);

                    return;
                }

                Log.Info($"[KCP] RelayAwareClient: connecting via route endpoint {initialEndPoint}");
                base.Connect(initialEndPoint.Address.ToString(), (ushort)initialEndPoint.Port);

                return;
            }

            base.Connect(address, port);
        }

        private void ConnectBound(IPEndPoint remoteEndPoint, IPEndPoint localEndPoint) {
            if (connected) {
                Log.Warning("[KCP] RelayAwareClient: already connected!");

                return;
            }

            Reset(config);

            this.remoteEndPoint = remoteEndPoint;
            socket = new Socket(remoteEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            SetActive(true);
            socket.Blocking = false;
            Common.ConfigureSocketBuffers(socket, config.RecvBufferSize, config.SendBufferSize);
            socket.Bind(new IPEndPoint(
                remoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                localEndPoint.Port));
            socket.Connect(remoteEndPoint);

            Log.Info($"[KCP] RelayAwareClient: bound local={socket.LocalEndPoint} remote={remoteEndPoint}");
            SendHello();
        }

        public virtual void PromoteDirectRoute(IPEndPoint directEndPoint) {
            if (directEndPoint == null) {
                return;
            }

            Route = Route.WithDirectEndPoint(directEndPoint).WithPreferredMode(RelayRouteMode.Direct);
            Log.Info($"[KCP] RelayAwareClient: direct route promoted to {directEndPoint}");
        }

        private void SetActive(bool value) {
            _activeField.SetValue(this, value);
        }
    }
}
