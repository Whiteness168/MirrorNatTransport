using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using UltimateMatchmaker.Common;

namespace UltimateMatchmaker.Server.NatPunchthrough;

public sealed class RelayServer : IDisposable
{
    private const string RelayPunchMarker = "relay-punch";
    private readonly Config _config;
    private readonly UdpClient _controlServer;
    private readonly ConcurrentDictionary<int, UdpClient> _relayServers = new();
    private readonly ConcurrentDictionary<uint, ClientInfo> _connectedClients = new();
    private readonly ConcurrentDictionary<string, RegisteredMatch> _matches = new();
    private readonly ConcurrentDictionary<int, RelaySession> _relaySessions = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly TimeSpan _clientTimeout;
    private bool _disposed;

    public RelayServer(Config config)
    {
        _config = config;
        _clientTimeout = TimeSpan.FromSeconds(config.ClientTimeoutSeconds);
        _controlServer = new UdpClient(new IPEndPoint(IPAddress.Parse(config.ListenIp), config.ControlPort));

        for (var port = config.RelayPortStart; port <= config.RelayPortEnd; port++)
        {
            var relayServer = new UdpClient(new IPEndPoint(IPAddress.Parse(config.ListenIp), port));
            _relayServers[port] = relayServer;
        }
    }

    public void Start()
    {
        _ = RunControlLoopAsync();

        foreach (var relayServer in _relayServers)
        {
            _ = RunRelayLoopAsync(relayServer.Key, relayServer.Value);
        }

        _ = RunCleanupLoopAsync();
    }

    public void Stop()
    {
        _cancellationTokenSource.Cancel();
    }

    private async Task RunControlLoopAsync()
    {
        Console.WriteLine($"[RelayServer]: Control socket listening on {_config.ListenIp}:{_config.ControlPort}");

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var udpReceiveResult = await _controlServer.ReceiveAsync(_cancellationTokenSource.Token);
                var jsonBuffer = Encoding.UTF8.GetString(udpReceiveResult.Buffer);
                var message = JsonConvert.DeserializeObject<Message>(jsonBuffer);

                if (message == null)
                {
                    Console.WriteLine("[RelayServer]: Received null control message.");
                    continue;
                }

                Console.WriteLine($"[RelayServer]: {message.Type} from {udpReceiveResult.RemoteEndPoint}");

                switch (message.Type)
                {
                    case MessageType.RegisterRequest:
                        await HandleRegistrationAsync(message, udpReceiveResult.RemoteEndPoint);
                        break;

                    case MessageType.MatchRegisterRequest:
                        await HandleMatchRegistrationAsync(message, udpReceiveResult.RemoteEndPoint);
                        break;

                    case MessageType.MatchListRequest:
                        await HandleMatchListAsync(message, udpReceiveResult.RemoteEndPoint);
                        break;

                    case MessageType.PunchRequest:
                        await HandlePunchRequestAsync(message, udpReceiveResult.RemoteEndPoint);
                        break;

                    case MessageType.KeepAlive:
                        HandleKeepAlive(message);
                        break;

                    case MessageType.Disconnect:
                        HandleDisconnect(message);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    private async Task RunRelayLoopAsync(int relayPort, UdpClient relayServer)
    {
        Console.WriteLine($"[RelayServer]: Relay socket listening on {_config.ListenIp}:{relayPort}");

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var receiveResult = await relayServer.ReceiveAsync(_cancellationTokenSource.Token);

                if (!_relaySessions.TryGetValue(relayPort, out var session))
                {
                    continue;
                }

                session.Touch();
                TryRefreshRelayEndpoint(session, receiveResult.RemoteEndPoint);

                if (receiveResult.RemoteEndPoint.Equals(session.HostEndPoint))
                {
                    if (IsRelayPunchPacket(receiveResult.Buffer))
                    {
                        Console.WriteLine($"[RelayServer]: Host relay-punch received on port {relayPort} from {receiveResult.RemoteEndPoint}");
                    }

                    await relayServer.SendAsync(receiveResult.Buffer, receiveResult.Buffer.Length, session.ClientEndPoint);
                    continue;
                }

                if (receiveResult.RemoteEndPoint.Equals(session.ClientEndPoint))
                {
                    if (IsRelayPunchPacket(receiveResult.Buffer))
                    {
                        Console.WriteLine($"[RelayServer]: Client relay-punch received on port {relayPort} from {receiveResult.RemoteEndPoint}");
                    }

                    await relayServer.SendAsync(receiveResult.Buffer, receiveResult.Buffer.Length, session.HostEndPoint);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[RelayServer]: Relay loop error on port {relayPort}: {e}");
            }
        }
    }

    private async Task RunCleanupLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(_cancellationTokenSource.Token))
        {
            CleanupExpiredClientsAndMatches();
        }
    }

    private async Task HandleRegistrationAsync(Message message, IPEndPoint remoteEndPoint)
    {
        Console.WriteLine($"[RelayServer]: Handling client registration from {remoteEndPoint}. Existing clientId={message.ClientId?.ToString() ?? "none"}");
        var clientId = message.ClientId ?? GetNextClientId();
        var clientInfo = new ClientInfo
        {
            ClientId = clientId,
            IpAddress = remoteEndPoint.Address.ToString(),
            Port = remoteEndPoint.Port,
            PublicEndPoint = remoteEndPoint,
            LastSeen = DateTime.UtcNow
        };

        _connectedClients.AddOrUpdate(clientId, clientInfo, (_, _) => clientInfo);

        var response = new Message
        {
            Type = MessageType.RegisterResponse,
            ClientId = clientId
        };
        response.SetEndPoint(remoteEndPoint);

        await SendControlMessageAsync(response, remoteEndPoint);
        Console.WriteLine($"[RelayServer]: Registered client {clientId} at {remoteEndPoint}. Total clients={_connectedClients.Count}");
    }

    private async Task HandleMatchRegistrationAsync(Message message, IPEndPoint remoteEndPoint)
    {
        Console.WriteLine($"[RelayServer]: Handling match registration from {remoteEndPoint}. ClientId={message.ClientId}, ExistingMatchId={message.MatchId}");
        if (message.ClientId == null)
        {
            await SendErrorAsync(MessageType.MatchRegisterResponse, "ClientId is required.", remoteEndPoint);
            return;
        }

        if (!_connectedClients.TryGetValue(message.ClientId.Value, out var hostClient) ||
            hostClient.PublicEndPoint == null)
        {
            await SendErrorAsync(MessageType.MatchRegisterResponse, "Host client is not registered.", remoteEndPoint);
            return;
        }

        if (message.Payload.Length == 0)
        {
            await SendErrorAsync(MessageType.MatchRegisterResponse, "Match payload is empty.", remoteEndPoint);
            return;
        }

        var payloadJson = Encoding.UTF8.GetString(message.Payload);
        var registration = JsonConvert.DeserializeObject<MatchRegistrationRequest>(payloadJson);

        if (registration == null)
        {
            await SendErrorAsync(MessageType.MatchRegisterResponse, "Failed to deserialize match payload.", remoteEndPoint);
            return;
        }

        var matchId = string.IsNullOrWhiteSpace(message.MatchId) ? Guid.NewGuid().ToString("N") : message.MatchId;
        var match = new RegisteredMatch
        {
            MatchId = matchId,
            HostClientId = message.ClientId.Value,
            HostEndPoint = hostClient.PublicEndPoint,
            Name = registration.Name,
            LocalIpAddress = registration.LocalIpAddress,
            LocalPort = registration.LocalPort,
            MapId = registration.MapId,
            MapWidth = registration.MapWidth,
            MapHeight = registration.MapHeight,
            MapSeed = registration.MapSeed,
            CityNpcAmount = registration.CityNpcAmount,
            ItemWeights = registration.ItemWeights,
            MaxConnections = registration.MaxConnections,
            LastSeen = DateTime.UtcNow
        };

        _matches[matchId] = match;

        var response = new Message
        {
            Type = MessageType.MatchRegisterResponse,
            ClientId = message.ClientId,
            MatchId = matchId
        };
        response.SetEndPoint(hostClient.PublicEndPoint);

        await SendControlMessageAsync(response, remoteEndPoint);
        Console.WriteLine(
            $"[RelayServer]: Registered match {matchId} for host {message.ClientId}. " +
            $"Name=\"{registration.Name}\" Map=\"{registration.MapId}\" LocalEndpoint={registration.LocalIpAddress}:{registration.LocalPort} " +
            $"MaxConnections={registration.MaxConnections} ActiveMatches={_matches.Count}");
    }

    private async Task HandleMatchListAsync(Message message, IPEndPoint remoteEndPoint)
    {
        Console.WriteLine($"[RelayServer]: Handling match list request from {remoteEndPoint}. RequestingClientId={message.ClientId}");
        var matches = _matches.Values
            .Where(match => _connectedClients.ContainsKey(match.HostClientId))
            .Select(match => match.ToMatchInfo())
            .ToArray();

        var response = new Message
        {
            Type = MessageType.MatchListResponse,
            ClientId = message.ClientId,
            Payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(matches))
        };
        response.SetEndPoint(remoteEndPoint);

        await SendControlMessageAsync(response, remoteEndPoint);
        Console.WriteLine($"[RelayServer]: Sent {matches.Length} matches to {remoteEndPoint}");
    }

    private async Task HandlePunchRequestAsync(Message message, IPEndPoint remoteEndPoint)
    {
        Console.WriteLine(
            $"[RelayServer]: Handling punch request from {remoteEndPoint}. " +
            $"ClientId={message.ClientId} TargetClientId={message.TargetClientId} MatchId={message.MatchId}");
        if (message.ClientId == null)
        {
            await SendErrorAsync(MessageType.PunchResponse, "ClientId is required.", remoteEndPoint);
            return;
        }

        if (!_connectedClients.TryGetValue(message.ClientId.Value, out var requester) || requester.PublicEndPoint == null)
        {
            await SendErrorAsync(MessageType.PunchResponse, "Requesting client is not registered.", remoteEndPoint);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.MatchId) || !_matches.TryGetValue(message.MatchId, out var match))
        {
            await SendErrorAsync(MessageType.PunchResponse, "Requested match was not found.", remoteEndPoint);
            return;
        }

        if (!_connectedClients.TryGetValue(match.HostClientId, out var hostClient) || hostClient.PublicEndPoint == null)
        {
            await SendErrorAsync(MessageType.PunchResponse, "Host client is not available.", remoteEndPoint);
            return;
        }

        var relayPort = AllocateRelayPort();
        if (relayPort == null)
        {
            await SendErrorAsync(MessageType.PunchResponse, "No relay ports are available.", remoteEndPoint);
            return;
        }

        var hostKcpEndPoint = new IPEndPoint(hostClient.PublicEndPoint.Address, match.LocalPort);

        _relaySessions[relayPort.Value] = new RelaySession
        {
            MatchId = match.MatchId,
            RelayPort = relayPort.Value,
            HostClientId = match.HostClientId,
            ClientClientId = requester.ClientId,
            HostEndPoint = hostKcpEndPoint,
            ClientEndPoint = requester.PublicEndPoint,
            LastSeen = DateTime.UtcNow
        };

        var responsePayload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new PunchConnectionDetails
        {
            MatchId = match.MatchId,
            HostClientId = match.HostClientId,
            ForceRelayOnly = false
        }));

        var initiatorResponse = new Message
        {
            Type = MessageType.PunchResponse,
            ClientId = match.HostClientId,
            MatchId = match.MatchId,
            Payload = responsePayload
        };
        initiatorResponse.SetEndPoint(hostKcpEndPoint);
        initiatorResponse.SetRelayEndPoint(new IPEndPoint(IPAddress.Parse(_config.PublicAddress), relayPort.Value));

        var hostResponse = new Message
        {
            Type = MessageType.PunchResponse,
            ClientId = requester.ClientId,
            MatchId = match.MatchId,
            Payload = responsePayload
        };
        hostResponse.SetEndPoint(requester.PublicEndPoint);
        hostResponse.SetRelayEndPoint(new IPEndPoint(IPAddress.Parse(_config.PublicAddress), relayPort.Value));

        await SendControlMessageAsync(initiatorResponse, requester.PublicEndPoint);
        await SendControlMessageAsync(hostResponse, hostClient.PublicEndPoint);

        Console.WriteLine(
            $"[RelayServer]: Prepared relay session for match {match.MatchId}. " +
            $"Direct host={hostKcpEndPoint} client={requester.PublicEndPoint} relay={_config.PublicAddress}:{relayPort}");
        Console.WriteLine(
            $"[RelayServer]: Punch notifications sent. HostClientId={match.HostClientId} JoinClientId={requester.ClientId} ActiveRelaySessions={_relaySessions.Count}");
    }

    private void HandleKeepAlive(Message message)
    {
        if (message.ClientId == null)
        {
            return;
        }

        if (_connectedClients.TryGetValue(message.ClientId.Value, out var clientInfo))
        {
            clientInfo.LastSeen = DateTime.UtcNow;
            Console.WriteLine($"[RelayServer]: KeepAlive from client {message.ClientId}. MatchId={message.MatchId}");
        }

        if (!string.IsNullOrWhiteSpace(message.MatchId) && _matches.TryGetValue(message.MatchId, out var match))
        {
            match.LastSeen = DateTime.UtcNow;
        }
    }

    private void HandleDisconnect(Message message)
    {
        if (message.ClientId == null)
        {
            return;
        }

        _connectedClients.TryRemove(message.ClientId.Value, out _);

        var matchesToRemove = _matches.Values
            .Where(match => match.HostClientId == message.ClientId.Value)
            .Select(match => match.MatchId)
            .ToArray();

        foreach (var matchId in matchesToRemove)
        {
            _matches.TryRemove(matchId, out _);
            Console.WriteLine($"[RelayServer]: Removed match {matchId} because host client {message.ClientId} disconnected.");
        }

        var relayPortsToRemove = _relaySessions.Values
            .Where(session => session.HostClientId == message.ClientId.Value || session.ClientClientId == message.ClientId.Value)
            .Select(session => session.RelayPort)
            .ToArray();

        foreach (var relayPort in relayPortsToRemove)
        {
            _relaySessions.TryRemove(relayPort, out _);
            Console.WriteLine($"[RelayServer]: Removed relay session on port {relayPort} due to disconnect of client {message.ClientId}.");
        }

        Console.WriteLine($"[RelayServer]: Disconnected client {message.ClientId}. Total clients={_connectedClients.Count} ActiveMatches={_matches.Count}");
    }

    private void CleanupExpiredClientsAndMatches()
    {
        var now = DateTime.UtcNow;

        foreach (var client in _connectedClients.Values)
        {
            if (now - client.LastSeen <= _clientTimeout)
            {
                continue;
            }

            _connectedClients.TryRemove(client.ClientId, out _);
            Console.WriteLine($"[RelayServer]: Removed stale client {client.ClientId} after timeout {_clientTimeout.TotalSeconds}s.");
        }

        foreach (var match in _matches.Values)
        {
            if (!_connectedClients.ContainsKey(match.HostClientId) || now - match.LastSeen > _clientTimeout)
            {
                _matches.TryRemove(match.MatchId, out _);
                Console.WriteLine($"[RelayServer]: Removed stale match {match.MatchId}.");
            }
        }

        foreach (var session in _relaySessions.Values)
        {
            if (!_connectedClients.ContainsKey(session.HostClientId) ||
                !_connectedClients.ContainsKey(session.ClientClientId))
            {
                if (now - session.LastSeen > _clientTimeout)
                {
                    _relaySessions.TryRemove(session.RelayPort, out _);
                    Console.WriteLine($"[RelayServer]: Removed stale relay session on port {session.RelayPort}.");
                }
            }
        }
    }

    private int? AllocateRelayPort()
    {
        for (var port = _config.RelayPortStart; port <= _config.RelayPortEnd; port++)
        {
            if (!_relaySessions.ContainsKey(port))
            {
                return port;
            }
        }

        return null;
    }

    private void TryRefreshRelayEndpoint(RelaySession session, IPEndPoint remoteEndPoint)
    {
        if (session.HostEndPoint.Address.Equals(session.ClientEndPoint.Address))
        {
            return;
        }

        if (session.HostEndPoint.Address.Equals(remoteEndPoint.Address) &&
            !session.HostEndPoint.Equals(remoteEndPoint))
        {
            Console.WriteLine(
                $"[RelayServer]: Updating host relay endpoint for match {session.MatchId} " +
                $"from {session.HostEndPoint} to {remoteEndPoint}");
            session.HostEndPoint = remoteEndPoint;
            return;
        }

        if (session.ClientEndPoint.Address.Equals(remoteEndPoint.Address) &&
            !session.ClientEndPoint.Equals(remoteEndPoint))
        {
            Console.WriteLine(
                $"[RelayServer]: Updating client relay endpoint for match {session.MatchId} " +
                $"from {session.ClientEndPoint} to {remoteEndPoint}");
            session.ClientEndPoint = remoteEndPoint;
        }
    }

    private bool IsRelayPunchPacket(byte[] buffer)
    {
        if (buffer.Length != RelayPunchMarker.Length)
        {
            return false;
        }

        return Encoding.UTF8.GetString(buffer) == RelayPunchMarker;
    }

    private async Task SendControlMessageAsync(Message message, IPEndPoint endPoint)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
        await _controlServer.SendAsync(bytes, bytes.Length, endPoint);
        Console.WriteLine(
            $"[RelayServer]: Sent control message {message.Type} to {endPoint}. " +
            $"ClientId={message.ClientId} MatchId={message.MatchId} Error={(string.IsNullOrEmpty(message.Error) ? "none" : message.Error)}");
    }

    private async Task SendErrorAsync(MessageType type, string error, IPEndPoint endPoint)
    {
        var response = new Message
        {
            Type = type,
            Error = error
        };
        response.SetEndPoint(endPoint);
        Console.WriteLine($"[RelayServer]: Sending error response {type} to {endPoint}: {error}");
        await SendControlMessageAsync(response, endPoint);
    }

    private uint GetNextClientId()
    {
        return _connectedClients.IsEmpty ? 1 : _connectedClients.Keys.Max() + 1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        _controlServer.Dispose();

        foreach (var relayServer in _relayServers.Values)
        {
            relayServer.Dispose();
        }

        _cancellationTokenSource.Dispose();
        _disposed = true;
    }

    private sealed class RegisteredMatch
    {
        public string MatchId { get; init; } = string.Empty;
        public uint HostClientId { get; init; }
        public required IPEndPoint HostEndPoint { get; init; }
        public string Name { get; init; } = string.Empty;
        public string LocalIpAddress { get; init; } = string.Empty;
        public int LocalPort { get; init; }
        public string MapId { get; init; } = string.Empty;
        public int MapWidth { get; init; }
        public int MapHeight { get; init; }
        public int MapSeed { get; init; }
        public int CityNpcAmount { get; init; }
        public Dictionary<string, float> ItemWeights { get; init; } = new();
        public int MaxConnections { get; init; }
        public DateTime LastSeen { get; set; }

        public MatchInfo ToMatchInfo()
        {
            return new MatchInfo
            {
                MatchId = MatchId,
                HostClientId = HostClientId,
                Name = Name,
                LocalIpAddress = LocalIpAddress,
                LocalPort = LocalPort,
                MapId = MapId,
                MapWidth = MapWidth,
                MapHeight = MapHeight,
                MapSeed = MapSeed,
                CityNpcAmount = CityNpcAmount,
                ItemWeights = ItemWeights,
                MaxConnections = MaxConnections
            };
        }
    }

    private sealed class RelaySession
    {
        public string MatchId { get; init; } = string.Empty;
        public int RelayPort { get; init; }
        public uint HostClientId { get; init; }
        public uint ClientClientId { get; init; }
        public required IPEndPoint HostEndPoint { get; set; }
        public required IPEndPoint ClientEndPoint { get; set; }
        public DateTime LastSeen { get; set; }

        public void Touch()
        {
            LastSeen = DateTime.UtcNow;
        }
    }
}
