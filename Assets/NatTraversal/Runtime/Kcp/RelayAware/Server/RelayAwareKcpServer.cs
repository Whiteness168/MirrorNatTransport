using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using kcp2k;
using UnityEngine;

namespace NatTraversal.Runtime.Kcp.RelayAware.Server {
    public class RelayAwareKcpServer : KcpServer {
        private static readonly byte[] PunchPacket = { (byte)'p', (byte)'u', (byte)'n', (byte)'c', (byte)'h' };
        private readonly Dictionary<int, IPEndPoint> _sendRouteOverrides = new();

        public RelayAwareKcpServer(Action<int, IPEndPoint> onConnected,
            Action<int, ArraySegment<byte>, KcpChannel> onData,
            Action<int> onDisconnected,
            Action<int, ErrorCode, string> onError,
            KcpConfig config) : base(onConnected, onData, onDisconnected, onError, config) {
        }

        public virtual void SetSendRouteOverride(int connectionId, IPEndPoint endPoint) {
            if (endPoint == null) {
                _sendRouteOverrides.Remove(connectionId);

                return;
            }

            _sendRouteOverrides[connectionId] = endPoint;
            Log.Info($"[KCP] RelayAwareServer: route override set for connectionId={connectionId} endpoint={endPoint}");
        }

        public virtual void ClearSendRouteOverrides() {
            _sendRouteOverrides.Clear();
        }

        public virtual void SendPunchPackets(IPEndPoint endPoint, int count) {
            if (socket == null || endPoint == null) {
                Log.Warning($"[KCP] RelayAwareServer: punch skipped. active={socket != null} endpoint={endPoint}");

                return;
            }

            var packet = new ArraySegment<byte>(PunchPacket);
            for (var i = 0; i < count; i++) {
                socket.SendToNonBlocking(packet, endPoint);
            }

            Log.Info($"[KCP] RelayAwareServer: sent {count} punch packets to {endPoint}");
        }

        protected override void RawSend(int connectionId, ArraySegment<byte> data) {
            if (_sendRouteOverrides.TryGetValue(connectionId, out var endPoint) && socket != null) {
                try {
                    SendRawTo(data, endPoint);

                    return;
                }
                catch (Exception e) {
                    Log.Error($"[KCP] RelayAwareServer: SendTo override failed for connectionId={connectionId}: {e}");
                }
            }

            base.RawSend(connectionId, data);
        }

        public void SendRawTo(ArraySegment<byte> segment, IPEndPoint endPoint) {
            if (socket == null || endPoint == null) return;

            try {
                EndPoint remoteEndPoint = endPoint;
                if (socket.AddressFamily == AddressFamily.InterNetworkV6 &&
                    endPoint.AddressFamily == AddressFamily.InterNetwork) {
                    remoteEndPoint = new IPEndPoint(endPoint.Address.MapToIPv6(), endPoint.Port);
                }

                socket.SendToNonBlocking(segment, remoteEndPoint);
            }
            catch (SocketException e) {
                Log.Error($"[KCP] Server: SendRawTo failed: {e}");
            }
        }

        public override void Stop() {
            _sendRouteOverrides.Clear();
            base.Stop();
        }
    }
}
