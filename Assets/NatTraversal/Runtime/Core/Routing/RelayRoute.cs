using System;
using System.Net;

namespace NatTraversal.Runtime.Core.Routing {
    public sealed class RelayRoute {
        public static RelayRoute None { get; } = new(RelayRouteMode.None, null, null, null);

        public RelayRoute(
            RelayRouteMode preferredMode,
            IPEndPoint localEndPoint,
            IPEndPoint directEndPoint,
            IPEndPoint relayEndPoint) {
            PreferredMode = preferredMode;
            LocalEndPoint = localEndPoint;
            DirectEndPoint = directEndPoint;
            RelayEndPoint = relayEndPoint;
        }

        public RelayRouteMode PreferredMode { get; }
        public IPEndPoint LocalEndPoint { get; }
        public IPEndPoint DirectEndPoint { get; }
        public IPEndPoint RelayEndPoint { get; }

        public bool HasLocal => LocalEndPoint != null;
        public bool HasDirect => DirectEndPoint != null;
        public bool HasRelay => RelayEndPoint != null;

        public IPEndPoint GetInitialRemoteEndPoint() {
            return PreferredMode switch {
                RelayRouteMode.Local when HasLocal => LocalEndPoint,
                RelayRouteMode.Direct when HasDirect => DirectEndPoint,
                RelayRouteMode.RelayFallback when HasRelay => RelayEndPoint,
                _ when HasDirect => DirectEndPoint,
                _ => RelayEndPoint
            };
        }

        public RelayRoute WithPreferredMode(RelayRouteMode preferredMode) {
            return new RelayRoute(preferredMode, LocalEndPoint, DirectEndPoint, RelayEndPoint);
        }

        public RelayRoute WithDirectEndPoint(IPEndPoint directEndPoint) {
            return new RelayRoute(PreferredMode, LocalEndPoint, directEndPoint, RelayEndPoint);
        }

        public RelayRoute WithRelayEndPoint(IPEndPoint relayEndPoint) {
            return new RelayRoute(PreferredMode, LocalEndPoint, DirectEndPoint, relayEndPoint);
        }

        public override string ToString() {
            return $"Mode={PreferredMode} Local={Format(LocalEndPoint)} Direct={Format(DirectEndPoint)} Relay={Format(RelayEndPoint)}";
        }

        private static string Format(IPEndPoint endPoint) {
            return endPoint == null ? "none" : endPoint.ToString();
        }
    }
}
