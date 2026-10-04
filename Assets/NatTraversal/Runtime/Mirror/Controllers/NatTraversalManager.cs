using System;
using Cysharp.Threading.Tasks;
using Mirror;
using NatTraversal.Runtime.Core.Messages.Client;
using NatTraversal.Runtime.Core.Messages.Server;
using NatTraversal.Runtime.Core.Models.Match;
using NatTraversal.Runtime.Core.Routing;
using NatTraversal.Runtime.Mirror.Controllers;
using NatTraversal.Runtime.Mirror.Transport;
using UnityEngine;

public class NatTraversalManager : NetworkManager {
    public bool IsServer { get; private set; }
    public MatchmakingController MatchmakingController => _matchmakingController;
    
    private readonly MatchmakingController _matchmakingController = new();
    
    private NatKcpTransport _natKcpTransport;
    private ushort _defaultKcpPort;
    
    private MatchData _matchData;
    
    private bool _hasMatchData;
    private bool _connectionTimerCompleted;
    private bool _clientDisconnected;
    private bool _isReconnect;
    
    private const int KcpPunchAttempts = 5;
    private const float DirectConnectionTimeout = 10.0f;

    private Func<UniTask> _unloadGameplayScenes;
    
    //Events for server
    public event Action<ServerCreatedMessage> ServerCreated;
    public event Action<ServerPreparedMessage> ServerPrepared;

    //Events for client
    public event Action<ClientConnectedMessage> ClientConnected;
    public event Action OnClientDisconnected;
    
     public override void Awake() {
            base.Awake();

            autoCreatePlayer = true;
            _natKcpTransport = Transport.active as NatKcpTransport;
            _defaultKcpPort = _natKcpTransport.Port;
            _matchmakingController.Initialize(_natKcpTransport.Port);
            _matchmakingController.PunchRouteReceived += OnPunchRouteReceived;
            _natKcpTransport.OnServerDataSent += OnServerDataSent;
            _natKcpTransport.OnClientConnected += TransportClientConnected;
            _natKcpTransport.OnClientDisconnected += TransportClientDisconnected;
            _natKcpTransport.OnClientError += TransportClientError;
            _natKcpTransport.OnServerConnectedWithAddress += TransportServerConnected;
            _natKcpTransport.OnServerDisconnected += TransportServerDisconnected;
            _natKcpTransport.OnServerError += TransportServerError;
            Debug.Log($"[NetworkController]: Awake completed. Transport={_natKcpTransport?.GetType().Name} " +
                     $"DefaultPort={_defaultKcpPort} AutoCreatePlayer={autoCreatePlayer}");
        }

        public override void OnDestroy() {
            if (_natKcpTransport != null) {
                _natKcpTransport.OnServerDataSent -= OnServerDataSent;
                _natKcpTransport.OnClientConnected -= TransportClientConnected;
                _natKcpTransport.OnClientDisconnected -= TransportClientDisconnected;
                _natKcpTransport.OnClientError -= TransportClientError;
                _natKcpTransport.OnServerConnectedWithAddress -= TransportServerConnected;
                _natKcpTransport.OnServerDisconnected -= TransportServerDisconnected;
                _natKcpTransport.OnServerError -= TransportServerError;
            }

            _matchmakingController.PunchRouteReceived -= OnPunchRouteReceived;
            Debug.Log(
                $"[NetworkController]: OnDestroy. Mode={mode} IsServerFlag={IsServer} Route={_matchmakingController.GetRouteDiagnostics()}");

            base.OnDestroy();
            _matchmakingController.Destroy();
        }
        
        public async void StartServer(bool isHost, int maxPlayers, MatchData matchData, Func<UniTask> loadGameplayScenes = null) {
            try {
                IsServer = true;
                maxConnections = maxPlayers;
                _matchData = matchData;
                _hasMatchData = true;
                Debug.Log($"[NetworkController]: StartServer requested. IsHost={isHost} MaxPlayers={maxPlayers} " +
                          $"MatchName=\"{matchData.Name}\" Map=\"{matchData.MapId}\" KcpPort={_defaultKcpPort}");
                if (loadGameplayScenes != null) {
                    await loadGameplayScenes();
                }
                
                if (isHost) {
                    Debug.Log("[NetworkController]: Starting Mirror host.");
                    StartHost();
                }
                else {
                    Debug.Log("[NetworkController]: Starting dedicated Mirror server.");
                    StartServer();
                }

                _matchmakingController.CreateMatch("127.0.0.1", 42424, maxConnections + 1, _matchData,
                    MatchCreationCallback);
            }
            catch (Exception exception) {
                ServerCreated.Invoke(new ServerCreatedMessage(false, exception.Message));
                Debug.LogError($"[NetworkController]: StartServer() exception: {exception}");
            }
        }

        public async void JoinByIp(string ip, Func<UniTask> loadGameplayScenes) {
            try {
                IsServer = false;
                Debug.Log($"[NetworkController]: JoinByIp (ip {ip})");
                if (loadGameplayScenes != null) {
                    await loadGameplayScenes();
                }
                
                networkAddress = ip;
                _natKcpTransport.Port = _defaultKcpPort;
                StartClient();
            }
            catch (Exception e) {
                Debug.LogError("[NetworkController]: JoinByIp() exception: " + e);
            }
        }

        public async void JoinMatch(Match match, Func<UniTask> loadGameplayScenes) {
            try {
                IsServer = false;
                _matchData = match.Data;
                _hasMatchData = true;
                Debug.Log(
                    $"[NetworkController]: JoinMatch requested. MatchId={match.MatchId} HostClientId={match.HostClientId} " +
                    $"Name=\"{match.Data.Name}\"");

                if (loadGameplayScenes != null) {
                    await loadGameplayScenes();
                }

                if (!await _matchmakingController.JoinMatchAsync(match)) {
                    throw new Exception("Direct KCP route setup failed.");
                }

                var remoteEndPoint = _matchmakingController.TransportRoute.GetInitialRemoteEndPoint();
                if (remoteEndPoint == null) {
                    throw new Exception($"Match endpoint is invalid: {match.Ip}:{match.Port}");
                }

                _natKcpTransport.ConfigureClientRoute(_matchmakingController.TransportRoute);
                networkAddress = remoteEndPoint.Address.ToString();
                _natKcpTransport.Port = (ushort)remoteEndPoint.Port;
                Debug.Log(
                    $"[NetworkController]: Starting Mirror client via direct KCP route {networkAddress}:{_natKcpTransport.Port}");

                StartTimerConnection().Forget();
                StartClient();

                await UniTask.WaitUntil(() => _connectionTimerCompleted || NetworkClient.isConnected);

                _connectionTimerCompleted = false;

                if (NetworkClient.isConnected) {
                    return;
                }

                await UniTask.WaitUntil(() => !NetworkClient.active && !NetworkClient.isConnecting);

                var relayRoute = _matchmakingController.TransportRoute.WithPreferredMode(RelayRouteMode.RelayFallback);
                _natKcpTransport.ConfigureClientRoute(relayRoute);

                remoteEndPoint = relayRoute.RelayEndPoint ??
                                 throw new Exception("Relay KCP route setup failed.");

                networkAddress = remoteEndPoint.Address.ToString();
                _natKcpTransport.Port = (ushort)remoteEndPoint.Port;
                _isReconnect = false;
                StartClient();
            }
            catch (Exception e) {
                Debug.LogError("[NetworkController]: JoinMatch() exception: " + e);
                ClientConnected.Invoke(new ClientConnectedMessage(false, true)); // FIX SECOND ARG
            }
        }
        
        public override async void OnClientDisconnect() {
            try {
                base.OnClientDisconnect();
                Debug.Log(
                    $"[NetworkController]: OnClientDisconnect received from Mirror. Mode={mode} Route={_matchmakingController.GetRouteDiagnostics()}");

                _natKcpTransport.Port = _defaultKcpPort;
                if (!_isReconnect) {
                    OnClientDisconnected?.Invoke();
                    if (_unloadGameplayScenes != null) {
                        await _unloadGameplayScenes();
                    }
                }
            }
            catch (Exception e) {
                Debug.LogError("[NetworkController]: OnClientDisconnect() exception: " + e);
            }
        }

        private async UniTask StartTimerConnection() {
            await UniTask.WaitForSeconds(DirectConnectionTimeout);
            _connectionTimerCompleted = true;
            if (NetworkClient.isConnected) {
                return;
            }

            _isReconnect = true;
            StopClient();
        }
        
        private void OnPunchRouteReceived(RelayRoute route) {
            if (route.DirectEndPoint != null) {
                _natKcpTransport.SendServerPunchPackets(route.DirectEndPoint, KcpPunchAttempts);
            }

            if (route.RelayEndPoint != null) {
                _natKcpTransport.SendServerPunchPackets(route.RelayEndPoint, KcpPunchAttempts);
            }
        }
        
        private void OnServerDataSent(int connectionId, ArraySegment<byte> segment, int channelId) {
            Debug.Log($"[NetworkController]: OnServerDataSent connectionId {connectionId} channelId {channelId}");
            //_signalBus.Fire(new SignalServerDataSent(connectionId));  Think about it. Maybe it is bottle neck
        }
        
        private void MatchCreationCallback(bool success, Match match) {
            Debug.Log($"[NetworkController]: MatchCreationCallback (success {success}) MatchId={match.MatchId} " +
                      $"HostClientId={match.HostClientId} Name=\"{match.Data.Name}\"");
            var serverCreatedMessage = new ServerCreatedMessage(success);
            var serverPreparedMessage = new ServerPreparedMessage(match);
            
            ServerCreated?.Invoke(serverCreatedMessage);
            ServerPrepared?.Invoke(serverPreparedMessage);
        }
        
        private void TransportClientConnected() {
            _clientDisconnected = false;
            Debug.Log(
                $"[NetworkController]: Transport callback OnClientConnected. Address={networkAddress} Port={_natKcpTransport?.Port} Route={_matchmakingController.GetRouteDiagnostics()}");
        }
        
        private void TransportClientDisconnected() {
            _clientDisconnected = true;
            Debug.Log(
                $"[NetworkController]: Transport callback OnClientDisconnected. Route={_matchmakingController.GetRouteDiagnostics()}");
        }
        
        private void TransportClientError(TransportError error, string reason) {
            Debug.LogError(
                $"[NetworkController]: Transport callback OnClientError error={error} reason={reason} Route={_matchmakingController.GetRouteDiagnostics()}");
        }
        
        private void TransportServerConnected(int connectionId, string address) {
            Debug.Log(
                $"[NetworkController]: Transport callback OnServerConnected connId={connectionId} address={address} Route={_matchmakingController.GetRouteDiagnostics()}");
        }
        
        private void TransportServerDisconnected(int connectionId) {
            Debug.Log(
                $"[NetworkController]: Transport callback OnServerDisconnected connId={connectionId} Route={_matchmakingController.GetRouteDiagnostics()}");
        }
        
        private void TransportServerError(int connectionId, TransportError error, string reason) {
            Debug.LogError(
                $"[NetworkController]: Transport callback OnServerError connId={connectionId} error={error} reason={reason} Route={_matchmakingController.GetRouteDiagnostics()}");
        }
}
