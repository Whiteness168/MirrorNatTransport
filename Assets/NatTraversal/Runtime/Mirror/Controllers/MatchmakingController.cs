using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cysharp.Threading.Tasks;
using NatTraversal.Runtime.Core.Models.Match;
using NatTraversal.Runtime.Core.Routing;
using Unity.Plastic.Newtonsoft.Json;
using UltimateMatchmaker.Common;
using UnityEngine;

namespace NatTraversal.Runtime.Mirror.Controllers {
    public class MatchmakingController {
        private const int RelayServerPort = 52100;
        private const int KeepAliveIntervalMs = 15000;
        public static readonly string RelayServerAddress = "194.24.161.46";

        private readonly IPEndPoint _relayServerEndpoint = new(IPAddress.Parse(RelayServerAddress), RelayServerPort);

        private bool _isDestroyed;
        private uint? _clientId;
        private string _registeredMatchId = string.Empty;
        private int _transportPort;
        private UdpClient _controlClient;

        public RelayRoute TransportRoute { get; private set; } = RelayRoute.None;
        public event Action<RelayRoute> PunchRouteReceived;

        public string GetRouteDiagnostics() {
            return
                $"DirectKcp=true TransportPort={_transportPort} ControlLocal={_controlClient?.Client.LocalEndPoint?.ToString() ?? "none"}";
        }

        public void Initialize(int port) {
            _transportPort = port;
            Debug.Log(
                $"[Matchmaking]: Initialize direct KCP matchmaking. MirrorPort={_transportPort}, relay registry={_relayServerEndpoint}");
        }

        public void Destroy() {
            _isDestroyed = true;
            Debug.Log("[Matchmaking]: Destroy called, disposing matchmaking control socket.");
            DisposeControlClient();
        }

        public void GetMatchList(Action<bool, Match[]> callback) {
            GetMatchListAsync(callback).Forget();
        }

        public void CreateMatch(string ip,
            ushort port,
            int maxConnections,
            MatchData matchData,
            Action<bool, Match> callback) {
            CreateMatchAsync(maxConnections, matchData, callback).Forget();
        }

        public async UniTask<bool> JoinMatchAsync(Match match) {
            try {
                _isDestroyed = false;
                DisposeControlClient();

                // IF YOU WANT TO CHECK LOCAL NETWORK USE IT. 

                /*if (ShouldConnectDirectlyInLocalNetwork(match)) {
                    TransportRoute = BuildLocalRoute(match);
                    Logs.Log($"[Matchmaking]: Local direct KCP join prepared. MatchId={match.MatchId} Endpoint={match.Ip}:{match.Port}");
                    return true;
                }*/

                _controlClient = new UdpClient();
                _clientId = await RegisterClientAsync(_controlClient);

                var punchRequest = new Message {
                    Type = MessageType.PunchRequest,
                    ClientId = _clientId,
                    TargetClientId = match.HostClientId,
                    MatchId = match.MatchId,
                    Payload = SerializePayload(new PunchRequest { MatchId = match.MatchId })
                };

                await SendControlMessageAsync(punchRequest, _controlClient);
                var punchResponse = await ReceiveControlMessageAsync(_controlClient);

                if (punchResponse.Type != MessageType.PunchResponse || !string.IsNullOrEmpty(punchResponse.Error)) {
                    Debug.LogError($"[Matchmaking]: Punch request failed: {punchResponse.Error}");
                    DisposeControlClient();

                    return false;
                }

                TransportRoute = BuildPunchRoute(punchResponse, _controlClient.Client.LocalEndPoint as IPEndPoint);
                Debug.Log(
                    $"[Matchmaking]: NAT punch KCP route prepared. MatchId={match.MatchId} Route={TransportRoute}");

                DisposeControlClientKeepRoute();

                return TransportRoute.GetInitialRemoteEndPoint() != null;
            }
            catch (Exception e) {
                Debug.LogError($"[Matchmaking]: JoinMatchAsync failed: {e}");
                TransportRoute = RelayRoute.None;
                DisposeControlClient();

                return false;
            }
        }

        public void DestroyMatch() {
            var matchId = _registeredMatchId;
            _registeredMatchId = string.Empty;
            _isDestroyed = true;

            if (_controlClient == null || _clientId == null) {
                DisposeControlClient();

                return;
            }

            Debug.Log($"[Matchmaking]: DestroyMatch. ClientId={_clientId} MatchId={matchId}");
            SendControlMessageAsync(
                    new Message { Type = MessageType.Disconnect, ClientId = _clientId, MatchId = matchId },
                    _controlClient)
                .Forget();
            DisposeControlClient();
        }

        private bool ShouldConnectDirectlyInLocalNetwork(Match match) {
            Debug.Log($"[Matchmaking]: match ip {match.Ip} port {match.Port}...");
            if (!TryBuildEndpoint(match, out var hostEndPoint)) {
                Debug.Log(
                    $"[Matchmaking]: Direct KCP path is unavailable for match {match.MatchId} because endpoint is invalid: {match.Ip}:{match.Port}");

                return false;
            }

            if (IPAddress.IsLoopback(hostEndPoint.Address)) {
                Debug.Log($"[Matchmaking]: Direct KCP path enabled via loopback {hostEndPoint}.");

                return true;
            }

            if (!IsPrivateIpv4(hostEndPoint.Address)) {
                Debug.Log(
                    $"[Matchmaking]: Local-network direct path skipped because host endpoint {hostEndPoint} is public.");

                return false;
            }

            foreach (var localAddress in GetLocalIpv4Addresses()) {
                if (AreInSameLocalNetwork(localAddress, hostEndPoint.Address)) {
                    Debug.Log(
                        $"[Matchmaking]: Direct KCP path enabled on LAN. LocalAddress={localAddress} HostEndpoint={hostEndPoint}");

                    return true;
                }
            }

            Debug.Log(
                $"[Matchmaking]: Local-network direct path skipped. No local IPv4 subnet matched host {hostEndPoint}.");

            return false;
        }

        public void ReleaseRelayPromotion(string reason) {
            Debug.Log($"[Matchmaking]: ReleaseRelayPromotion ignored in direct KCP mode. Reason={reason}");
        }

        public void PinRelayPromotionForGameplay(string reason) {
            Debug.Log($"[Matchmaking]: PinRelayPromotionForGameplay ignored in direct KCP mode. Reason={reason}");
        }

        private async UniTaskVoid GetMatchListAsync(Action<bool, Match[]> callback) {
            try {
                using var udpClient = new UdpClient();
                Debug.Log("[Matchmaking]: Requesting match list from relay registry.");
                await SendControlMessageAsync(new Message { Type = MessageType.MatchListRequest }, udpClient);
                var response = await ReceiveControlMessageAsync(udpClient);

                if (response.Type != MessageType.MatchListResponse || !string.IsNullOrEmpty(response.Error)) {
                    callback(false, Array.Empty<Match>());

                    return;
                }

                var payloadJson = Encoding.UTF8.GetString(response.Payload);
                var matches = JsonConvert.DeserializeObject<MatchInfo[]>(payloadJson) ?? Array.Empty<MatchInfo>();
                Debug.Log($"[Matchmaking]: Received match list. Count={matches.Length}");
                callback(true, matches.Select(ToUnityMatch).ToArray());
            }
            catch (Exception e) {
                Debug.LogError($"[Matchmaking]: GetMatchList failed: {e}");
                callback(false, Array.Empty<Match>());
            }
        }

        private async UniTaskVoid CreateMatchAsync(int maxConnections,
            MatchData matchData,
            Action<bool, Match> callback) {
            try {
                _isDestroyed = false;
                DisposeControlClient();

                _controlClient = new UdpClient();
                Debug.Log(
                    $"[Matchmaking]: Registering direct KCP match. LocalControl={_controlClient.Client.LocalEndPoint} MirrorPort={_transportPort}");

                _clientId = await RegisterClientAsync(_controlClient);

                var registration = new MatchRegistrationRequest {
                    Name = matchData.Name,
                    LocalIpAddress = GetPreferredLocalIpAddress(),
                    LocalPort = _transportPort,
                    MapId = matchData.MapId,
                    MapWidth = matchData.MapSize.x,
                    MapHeight = matchData.MapSize.y,
                    MapSeed = matchData.MapSeed,
                    CityNpcAmount = matchData.CityNpcAmount,
                    ItemWeights = matchData.ItemWeights,
                    MaxConnections = maxConnections
                };

                var request = new Message {
                    Type = MessageType.MatchRegisterRequest,
                    ClientId = _clientId,
                    Payload = SerializePayload(registration)
                };

                await SendControlMessageAsync(request, _controlClient);
                var response = await ReceiveControlMessageAsync(_controlClient);

                if (response.Type != MessageType.MatchRegisterResponse || !string.IsNullOrEmpty(response.Error)) {
                    callback(false, default);
                    DisposeControlClient();

                    return;
                }

                _registeredMatchId = response.MatchId;
                ControlReceiveLoopAsync(_controlClient).Forget();
                KeepAliveAsync(_controlClient, "host").Forget();

                Debug.Log(
                    $"[Matchmaking]: Registered direct KCP match {_registeredMatchId} at {registration.LocalIpAddress}:{registration.LocalPort}");
                callback(true,
                    new Match(_registeredMatchId, _clientId ?? 0, registration.LocalIpAddress,
                        (ushort)registration.LocalPort, matchData));
            }
            catch (Exception e) {
                Debug.LogError($"[Matchmaking]: CreateMatch failed: {e}");
                callback(false, default);
                DisposeControlClient();
            }
        }

        private async UniTask<uint> RegisterClientAsync(UdpClient udpClient) {
            var request = new Message { Type = MessageType.RegisterRequest, ClientId = _clientId };
            await SendControlMessageAsync(request, udpClient);
            var response = await ReceiveControlMessageAsync(udpClient);

            if (response.Type != MessageType.RegisterResponse || response.ClientId == null) {
                throw new Exception("Failed to register with relay registry.");
            }

            Debug.Log($"[Matchmaking]: RegisterResponse received. AssignedClientId={response.ClientId}");

            return response.ClientId.Value;
        }

        private async UniTask<Message> ReceiveControlMessageAsync(UdpClient udpClient) {
            while (true) {
                var result = await udpClient.ReceiveAsync();
                if (!result.RemoteEndPoint.Equals(_relayServerEndpoint)) {
                    Debug.Log($"[Matchmaking]: Ignoring non-control UDP packet from {result.RemoteEndPoint}.");

                    continue;
                }

                try {
                    var jsonData = Encoding.UTF8.GetString(result.Buffer);
                    var message = JsonConvert.DeserializeObject<Message>(jsonData);
                    if (message != null) {
                        Debug.Log(
                            $"[Matchmaking]: Control message received. Type={message.Type} ClientId={message.ClientId} MatchId={message.MatchId} Error={(string.IsNullOrEmpty(message.Error) ? "none" : message.Error)}");

                        return message;
                    }
                }
                catch (Exception e) {
                    Debug.LogError($"[Matchmaking]: Failed to parse control message: {e}");
                }
            }
        }

        private async UniTask SendControlMessageAsync(Message message, UdpClient udpClient) {
            var jsonData = JsonConvert.SerializeObject(message);
            var bytesData = Encoding.UTF8.GetBytes(jsonData);
            await udpClient.SendAsync(bytesData, bytesData.Length, _relayServerEndpoint);
            Debug.Log(
                $"[Matchmaking]: Control message sent. Type={message.Type} ClientId={message.ClientId} TargetClientId={message.TargetClientId} MatchId={message.MatchId}");
        }

        private async UniTaskVoid ControlReceiveLoopAsync(UdpClient udpClient) {
            try {
                Debug.Log("[Matchmaking]: Host control receive loop started.");

                while (!_isDestroyed && udpClient == _controlClient) {
                    var message = await ReceiveControlMessageAsync(udpClient);
                    if (message.Type != MessageType.PunchResponse || !string.IsNullOrEmpty(message.Error)) {
                        continue;
                    }

                    var route = BuildPunchRoute(message, null);
                    TransportRoute = route;
                    Debug.Log($"[Matchmaking]: Host received NAT punch route. Route={route}");
                    PunchRouteReceived?.Invoke(route);
                }
            }
            catch (ObjectDisposedException) {
            }
            catch (Exception e) {
                Debug.LogError($"[Matchmaking]: Host control receive loop failed: {e}");
            }
        }

        private async UniTaskVoid KeepAliveAsync(UdpClient udpClient, string role) {
            try {
                Debug.Log(
                    $"[Matchmaking]: {role} keep-alive loop started. ClientId={_clientId} MatchId={_registeredMatchId}");

                while (!_isDestroyed && udpClient == _controlClient) {
                    await UniTask.Delay(KeepAliveIntervalMs);

                    if (_isDestroyed || udpClient != _controlClient || _clientId == null) {
                        continue;
                    }

                    await SendControlMessageAsync(
                        new Message {
                            Type = MessageType.KeepAlive, ClientId = _clientId, MatchId = _registeredMatchId
                        }, udpClient);
                }
            }
            catch (ObjectDisposedException) {
            }
            catch (Exception e) {
                Debug.LogError($"[Matchmaking]: {role} keep-alive failed: {e}");
            }
        }

        private Match ToUnityMatch(MatchInfo matchInfo) {
            var data = new MatchData(matchInfo.Name, matchInfo.MapId,
                new Vector2Int(matchInfo.MapWidth, matchInfo.MapHeight), matchInfo.MapSeed, matchInfo.CityNpcAmount,
                matchInfo.ItemWeights);

            return new Match(matchInfo.MatchId, matchInfo.HostClientId, matchInfo.LocalIpAddress,
                (ushort)matchInfo.LocalPort, data);
        }

        private RelayRoute BuildLocalRoute(Match match) {
            return TryBuildEndpoint(match, out var endPoint)
                ? new RelayRoute(RelayRouteMode.Local, endPoint, endPoint, null)
                : RelayRoute.None;
        }

        private RelayRoute BuildPunchRoute(Message message, IPEndPoint localEndPoint) {
            var directEndPoint = message.GetEndPoint();
            var relayEndPoint = message.GetRelayEndPoint();

            return new RelayRoute(RelayRouteMode.Direct, localEndPoint, directEndPoint, relayEndPoint);
        }

        private bool TryBuildEndpoint(Match match, out IPEndPoint endPoint) {
            endPoint = null;

            if (string.IsNullOrWhiteSpace(match.Ip) || match.Port <= 0) {
                return false;
            }

            if (!IPAddress.TryParse(match.Ip, out var ipAddress)) {
                return false;
            }

            endPoint = new IPEndPoint(ipAddress, match.Port);

            return true;
        }

        private byte[] SerializePayload<T>(T payload) {
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
        }

        private string GetPreferredLocalIpAddress() {
            foreach (var address in GetLocalIpv4Addresses()) {
                if (IsPrivateIpv4(address)) {
                    Debug.Log($"[Matchmaking]: Selected private local IP for match registration: {address}");

                    return address.ToString();
                }
            }

            Debug.Log("[Matchmaking]: No private IPv4 address found for match registration. Falling back to 127.0.0.1");

            return IPAddress.Loopback.ToString();
        }

        private IEnumerable<IPAddress> GetLocalIpv4Addresses() {
            try {
                return Dns.GetHostAddresses(Dns.GetHostName()).Where(address =>
                        address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    .Distinct()
                    .ToArray();
            }
            catch (Exception e) {
                Debug.LogError($"[Matchmaking]: Failed to enumerate local IPv4 addresses: {e}");

                return Array.Empty<IPAddress>();
            }
        }

        private bool AreInSameLocalNetwork(IPAddress localAddress, IPAddress hostAddress) {
            var localBytes = localAddress.GetAddressBytes();
            var hostBytes = hostAddress.GetAddressBytes();

            if (localBytes.Length != 4 || hostBytes.Length != 4) {
                return false;
            }

            if (localBytes[0] == 10 && hostBytes[0] == 10) {
                return true;
            }

            if (localBytes[0] == 192 && localBytes[1] == 168 && hostBytes[0] == 192 && hostBytes[1] == 168) {
                return localBytes[2] == hostBytes[2];
            }

            var localIs172 = localBytes[0] == 172 && localBytes[1] >= 16 && localBytes[1] <= 31;
            var hostIs172 = hostBytes[0] == 172 && hostBytes[1] >= 16 && hostBytes[1] <= 31;

            return localIs172 && hostIs172 && localBytes[1] == hostBytes[1] && localBytes[2] == hostBytes[2];
        }

        private bool IsPrivateIpv4(IPAddress address) {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4) {
                return false;
            }

            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        private void DisposeControlClient() {
            if (_controlClient != null) {
                Debug.Log(
                    $"[Matchmaking]: Disposing control client. Local endpoint={_controlClient.Client.LocalEndPoint}");
            }

            _controlClient?.Dispose();
            _controlClient = null;
            TransportRoute = RelayRoute.None;
        }

        private void DisposeControlClientKeepRoute() {
            if (_controlClient != null) {
                Debug.Log(
                    $"[Matchmaking]: Disposing transient control client. Local endpoint={_controlClient.Client.LocalEndPoint}");
            }

            _controlClient?.Dispose();
            _controlClient = null;
        }
    }
}
