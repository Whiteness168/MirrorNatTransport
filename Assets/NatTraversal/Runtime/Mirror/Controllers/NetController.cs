/*
using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Mirror;
using NatTraversal.Runtime.Core.Models.Match;
using NatTraversal.Runtime.Core.Messages.Client;
using NatTraversal.Runtime.Core.Messages.Server;
using NatTraversal.Runtime.Core.Routing;
using NatTraversal.Runtime.Mirror.Controllers;
using NatTraversal.Runtime.Mirror.Transport;
using UnityEngine;

namespace NuclearBox.LaC.Runtime.Network.Controllers {
    public class NetController : NetworkManager {
        public bool IsServer { get; private set; }
        public Dictionary<uint, NetEntity> EntitiesByUniqIdentity { get; } = new();
        public Dictionary<NetworkConnectionToClient, uint> ConnectionToNetId { get; } = new();
        public MatchmakingController MatchmakingController => _matchmakingController;
        public MatchData MatchData => _matchData;
        public bool HasMatchData => _hasMatchData;

        private readonly MatchmakingController _matchmakingController = new();
        private MatchData _matchData;
        private bool _hasMatchData;
        private NatKcpTransport _natKcpTransport;
        private ushort _defaultKcpPort;
        private const int KcpPunchAttempts = 5;
        private const float DirectConnectionTimeout = 10.0f;
        private bool _connectionTimerCompleted;
        private bool _clientDisconnected;
        private bool _isReconnect;

        //Events for server
        public event Action<ServerCreatedMessage> ServerCreated;

        //Events for client
        public event Action<ClientConnectedMessage> ClientConnected;

        /*[Inject] private SignalBus _signalBus;
        [Inject] private ScenesController _scenesController;

        [Inject]
        private void Construct() {
        }#1#

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

        public async void StartServer(bool isHost, int maxPlayers, MatchData matchData) {
            try {
                IsServer = true;
                maxConnections = maxPlayers;
                _matchData = matchData;
                _hasMatchData = true;
                Debug.Log($"[NetworkController]: StartServer requested. IsHost={isHost} MaxPlayers={maxPlayers} " +
                          $"MatchName=\"{matchData.Name}\" Map=\"{matchData.MapId}\" KcpPort={_defaultKcpPort}");

                await LoadGameplayScenes();
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
                //_signalBus.Fire(new SignalServerCreated(false, e.Message));
                ServerCreated.Invoke(new ServerCreatedMessage(false, exception.Message));
                Debug.LogError($"[NetworkController]: StartServer() exception: {exception}");
            }
        }

        public async void JoinByIp(string ip) {
            try {
                IsServer = false;
                Debug.Log($"[NetworkController]: JoinByIp (ip {ip})");
                await LoadGameplayScenes();
                networkAddress = ip;
                _natKcpTransport.Port = _defaultKcpPort;
                StartClient();
            }
            catch (Exception e) {
                Debug.LogError("[NetworkController]: JoinByIp() exception: " + e);
            }
        }

        public async void JoinMatch(Match match) {
            try {
                IsServer = false;
                _matchData = match.Data;
                _hasMatchData = true;
                Debug.Log(
                    $"[NetworkController]: JoinMatch requested. MatchId={match.MatchId} HostClientId={match.HostClientId} " +
                    $"Name=\"{match.Data.Name}\"");

                await LoadGameplayScenes();

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

        private async UniTask StartTimerConnection() {
            await UniTask.WaitForSeconds(DirectConnectionTimeout);
            _connectionTimerCompleted = true;
            if (NetworkClient.isConnected) {
                return;
            }

            _isReconnect = true;
            StopClient();
        }

        public bool GetSpawnedComponent<T>(uint netId, out T component) where T : Component {
            var dict = NetworkServer.spawned;
            if (mode == NetworkManagerMode.ClientOnly) {
                dict = NetworkClient.spawned;
            }

            if (dict.ContainsKey(netId)) {
                component = dict[netId].GetComponent<T>();

                return component != null;
            }

            component = null;

            return false;
        }

        public void AddNetEntity(NetEntity netEntity) {
            if (netEntity.connectionToClient != null) {
                ConnectionToNetId.Add(netEntity.connectionToClient, netEntity.netId);
            }

            EntitiesByUniqIdentity.Add(netEntity.Model.State.UniqIdentity, netEntity);
            _signalBus.Fire(new SignalNetEntitySpawned(netEntity.netId, netEntity.Model.State.UniqIdentity));
        }

        public void RemoveNetEntity(NetEntity netEntity) {
            EntitiesByUniqIdentity.Remove(netEntity.Model.State.UniqIdentity);
        }

        public bool GetSpawnedComponentByUniqIdentity<T>(uint uniqIdentity, out T component) where T : Component {
            if (EntitiesByUniqIdentity.TryGetValue(uniqIdentity, out var netEntity)) {
                component = netEntity.GetComponent<T>();

                return component != null;
            }

            component = null;

            return false;
        }

        public uint? NetIdToUniqIdentity(uint? netId) {
            if (netId == null) {
                return null;
            }

            if (!GetSpawnedComponent<NetEntity>(netId.Value, out var netEntity)) {
                return null;
            }

            return netEntity.Model.State.UniqIdentity;
        }

        public async UniTask Disconnect(int menuSection) {
            if (IsServer) {
                Logs.Log("[NetworkController]: Disconnect requested for host/server.");
                ConnectionToNetId.Clear();
                OnDisconnect?.Invoke();
                await UniTask.WaitUntil(() => EntitiesByUniqIdentity.Count == 0);

                _matchmakingController.DestroyMatch();
                StopHost();

                return;
            }

            Logs.Log("[NetworkController]: Disconnect requested for client.");
            foreach (var conn in ConnectionToNetId) {
                if (conn.Value == NetEntity.OwnNetEntity.netId) {
                    ConnectionToNetId.Remove(conn.Key);

                    break;
                }
            }

            OnDisconnect?.Invoke();
            await UniTask.WaitUntil(() => NetEntity.OwnNetEntity == null);

            StopClient();
            _natKcpTransport.Port = _defaultKcpPort;
        }

        private void OnServerDataSent(int connectionId, ArraySegment<byte> segment, int channelId) {
            //Logs.Log($"[NetworkController]: OnServerDataSent connectionId {connectionId} channelId {channelId}");
            _signalBus.Fire(new SignalServerDataSent(connectionId));
        }

        private void MatchCreationCallback(bool success, Match match) {
            Logs.Log($"[NetworkController]: MatchCreationCallback (success {success}) MatchId={match.MatchId} " +
                     $"HostClientId={match.HostClientId} Name=\"{match.Data.Name}\"");

            /*_signalBus.Fire(new SignalServerCreated(success));
            _signalBus.Fire(new SignalServerPrepared(match));#1#
        }

        private void OnPunchRouteReceived(RelayRoute route) {
            if (route.DirectEndPoint != null) {
                _natKcpTransport.SendServerPunchPackets(route.DirectEndPoint, KcpPunchAttempts);
            }

            if (route.RelayEndPoint != null) {
                _natKcpTransport.SendServerPunchPackets(route.RelayEndPoint, KcpPunchAttempts);
            }
        }

        public event Action OnClientDisconnected;
        public event Action OnDisconnect;

        #region Server events

        public override void OnStartServer() {
            base.OnStartServer();
            LogMessageRegistration<AddPlayerCustomMessage>("AddPlayerCustomMessage");
            NetworkServer.RegisterHandler<AddPlayerCustomMessage>(AddPlayerMessageReceived);
            LogMessageRegistration<ClientIsReadyMessage>("ClientIsReadyMessage");
            NetworkServer.RegisterHandler<ClientIsReadyMessage>(ClientIsReadyMessageReceived);
            LogMessageRegistration<MapBootstrapCompletedMessage>("MapBootstrapCompletedMessage");
            NetworkServer.RegisterHandler<MapBootstrapCompletedMessage>(MapBootstrapCompletedMessageReceived);
            LogMessageRegistration<PlayerBootstrapCompletedMessage>("PlayerBootstrapCompletedMessage");
            NetworkServer.RegisterHandler<PlayerBootstrapCompletedMessage>(PlayerBootstrapCompletedMessageReceived);
            Logs.Log(
                $"[NetworkController]: OnStartServer; messages registered. TransportPort={_natKcpTransport.Port} Route={_matchmakingController.GetRouteDiagnostics()}");
        }

        public override void OnStartHost() {
            base.OnStartHost();
            Logs.Log(
                $"[NetworkController]: OnStartHost. Mode={mode} Route={_matchmakingController.GetRouteDiagnostics()}");
        }

        public override void OnStartClient() {
            base.OnStartClient();
            Logs.Log(
                $"[NetworkController]: OnStartClient. Mode={mode} Address={networkAddress} Port={_natKcpTransport.Port} Route={_matchmakingController.GetRouteDiagnostics()}");
        }

        public override void OnServerReady(NetworkConnectionToClient conn) {
            if (conn is LocalConnectionToClient) {
                Logs.Log(
                    $"[NetworkController]: OnServerReady for host local connection. ConnectionId={conn.connectionId}");

                base.OnServerReady(conn);

                return;
            }

            Logs.Log($"[NetworkController]: OnServerReady for remote connection. ConnectionId={conn.connectionId}");
            base.OnServerReady(conn);
        }

        public override async void OnServerDisconnect(NetworkConnectionToClient conn) {
            try {
                _signalBus.Fire(new SignalSaveState(SaveType.None));
                if (!ConnectionToNetId.TryGetValue(conn, out var netId)) {
                    return;
                }

                var characterController = FindAnyObjectByType<NetCharacterController>();
                if (!GetSpawnedComponent<NetEntity>(netId, out _)) {
                    return;
                }

                await characterController.SrvDespawnCharacter(netId);
                ConnectionToNetId.Remove(conn);
                Logs.Log(
                    $"[NetworkController]: Server disconnected player. ConnectionId={conn.connectionId} NetId={netId}");

                base.OnServerDisconnect(conn);
            }
            catch (Exception e) {
                Logs.LogError("[NetworkController]: OnServerDisconnect() exception: " + e);
            }
        }

        #endregion

        #region Client events

        public override void OnClientConnect() {
            Logs.Log(
                $"[NetworkController]: OnClientConnect (mode {mode}) address={networkAddress} port={_natKcpTransport.Port} Route={_matchmakingController.GetRouteDiagnostics()}");

            _signalBus.Fire(new SignalClientConnected(true, mode == NetworkManagerMode.ClientOnly));
            CompleteClientReadyState("OnClientConnect");

            if (mode == NetworkManagerMode.ClientOnly) {
                var clientIsReadyMsg = new ClientIsReadyMessage( /*_hasMatchData#1#false);
                NetworkClient.connection.Send(clientIsReadyMsg);
                Logs.Log(
                    $"[NetworkController]: ClientIsReadyMessage sent. UsesLocalMapBootstrap={clientIsReadyMsg.UsesLocalMapBootstrap}");
            }
        }

        public override void OnClientSceneChanged() {
            CompleteClientReadyState("OnClientSceneChanged");
        }

        public override async void OnClientDisconnect() {
            try {
                base.OnClientDisconnect();
                Logs.Log(
                    $"[NetworkController]: OnClientDisconnect received from Mirror. Mode={mode} Route={_matchmakingController.GetRouteDiagnostics()}");

                _natKcpTransport.Port = _defaultKcpPort;
                if (!_isReconnect) {
                    OnClientDisconnected?.Invoke();
                    await UnloadGameplayScenes();
                }
            }
            catch (Exception e) {
                Logs.LogError("[NetworkController]: OnClientDisconnect() exception: " + e);
            }
        }

        public override void OnClientError(TransportError error, string reason) {
            base.OnClientError(error, reason);
            Logs.LogError(
                $"[NetworkController]: OnClientError() error: {error} reason {reason} Route={_matchmakingController.GetRouteDiagnostics()}");
        }

        public override void OnClientNotReady() {
            base.OnClientNotReady();
            Logs.LogError("[NetworkController]: OnClientNotReady() !");
        }

        public override void OnStopClient() {
            Logs.Log(
                $"[NetworkController]: OnStopClient. Mode={mode} Route={_matchmakingController.GetRouteDiagnostics()}");

            base.OnStopClient();
        }

        public override void OnStopServer() {
            Logs.Log(
                $"[NetworkController]: OnStopServer. Mode={mode} Connections={NetworkServer.connections.Count} Route={_matchmakingController.GetRouteDiagnostics()}");

            base.OnStopServer();
        }

        public override void OnStopHost() {
            Logs.Log(
                $"[NetworkController]: OnStopHost. Mode={mode} Route={_matchmakingController.GetRouteDiagnostics()}");

            base.OnStopHost();
        }

        #endregion

        #region Scenes

        private async UniTask LoadGameplayScenes() {
            await _scenesController.LoadGameplayAsync();
            Logs.Log("[NetworkController]: Gameplay scenes loaded");
        }

        private async UniTask UnloadGameplayScenes() {
            await _scenesController.UnLoadGameplayAsync();
            Logs.Log("[NetworkController]: Gameplay scenes unloaded");
        }

        #endregion

        #region Messages

        private void AddPlayerMessageReceived(NetworkConnectionToClient conn, AddPlayerCustomMessage message) {
            Logs.Log(
                $"[NetworkController]: AddPlayer message received. ConnectionId={conn.connectionId} UniqIdentity={message.UniqIdentity}");

            _signalBus.Fire(new SignalAddPlayer(conn, message.UniqIdentity));
        }

        private void TransportClientConnected() {
            _clientDisconnected = false;
            Logs.Log(
                $"[NetworkController]: Transport callback OnClientConnected. Address={networkAddress} Port={_natKcpTransport?.Port} Route={_matchmakingController.GetRouteDiagnostics()}");
        }

        private void TransportClientDisconnected() {
            _clientDisconnected = true;
            Logs.Log(
                $"[NetworkController]: Transport callback OnClientDisconnected. Route={_matchmakingController.GetRouteDiagnostics()}");
        }

        private void TransportClientError(TransportError error, string reason) {
            Logs.LogError(
                $"[NetworkController]: Transport callback OnClientError error={error} reason={reason} Route={_matchmakingController.GetRouteDiagnostics()}");
        }

        private void TransportServerConnected(int connectionId, string address) {
            Logs.Log(
                $"[NetworkController]: Transport callback OnServerConnected connId={connectionId} address={address} Route={_matchmakingController.GetRouteDiagnostics()}");
        }

        private void TransportServerDisconnected(int connectionId) {
            Logs.Log(
                $"[NetworkController]: Transport callback OnServerDisconnected connId={connectionId} Route={_matchmakingController.GetRouteDiagnostics()}");
        }

        private void TransportServerError(int connectionId, TransportError error, string reason) {
            Logs.LogError(
                $"[NetworkController]: Transport callback OnServerError connId={connectionId} error={error} reason={reason} Route={_matchmakingController.GetRouteDiagnostics()}");
        }

        private void LogMessageRegistration<T>(string label) where T : struct, NetworkMessage {
            var messageId = NetworkMessageId<T>.Id;
            var existingType = NetworkMessages.Lookup.TryGetValue(messageId, out var lookupType)
                ? lookupType.FullName
                : "none";

            Logs.Log(
                $"[NetworkController]: Registering {label} server handler. MessageId={messageId} ExistingLookupType={existingType}");
        }

        private void CompleteClientReadyState(string source) {
            if (NetworkClient.connection == null || !NetworkClient.connection.isAuthenticated) {
                Logs.Log(
                    $"[NetworkController]: {source} skipped client ready completion because connection/auth state is not ready. " +
                    $"ConnectionPresent={NetworkClient.connection != null} Authenticated={NetworkClient.connection?.isAuthenticated ?? false}");

                return;
            }

            if (ShouldMarkClientReadyBeforeLogin() && !NetworkClient.ready) {
                NetworkClient.Ready();
                Logs.Log($"[NetworkController]: {source} marked client as Ready.");
            }
            else if (!ShouldMarkClientReadyBeforeLogin()) {
                Logs.Log($"[NetworkController]: {source} deferred NetworkClient.Ready for client-only login flow. " +
                         $"Mode={mode} clientLoadedScene={clientLoadedScene} LocalPlayerPresent={NetworkClient.localPlayer != null}");
            }

            if (ShouldAutoCreateMirrorPlayer()) {
                if (NetworkClient.localPlayer == null) {
                    NetworkClient.AddPlayer();
                    Logs.Log($"[NetworkController]: {source} auto-created local Mirror player.");
                }

                return;
            }

            Logs.Log($"[NetworkController]: {source} skipped auto CreatePlayer for client-only flow. " +
                     $"Mode={mode} clientLoadedScene={clientLoadedScene} LocalPlayerPresent={NetworkClient.localPlayer != null}");
        }

        private bool ShouldAutoCreateMirrorPlayer() {
            return mode != NetworkManagerMode.ClientOnly;
        }

        private bool ShouldMarkClientReadyBeforeLogin() {
            return mode != NetworkManagerMode.ClientOnly;
        }

        private struct ClientIsReadyMessage : NetworkMessage {
            public readonly bool UsesLocalMapBootstrap;

            public ClientIsReadyMessage(bool usesLocalMapBootstrap) {
                UsesLocalMapBootstrap = usesLocalMapBootstrap;
            }
        }

        public struct MapBootstrapCompletedMessage : NetworkMessage {
        }

        public struct PlayerBootstrapCompletedMessage : NetworkMessage {
        }

        private void ClientIsReadyMessageReceived(NetworkConnectionToClient conn, ClientIsReadyMessage message) {
            Logs.Log($"[NetworkController]: ClientIsReadyMessage received. ConnectionId={conn.connectionId} " +
                     $"UsesLocalMapBootstrap={message.UsesLocalMapBootstrap}");

            _signalBus.Fire(new SignalClientIsReady(conn, message.UsesLocalMapBootstrap));
        }

        private void MapBootstrapCompletedMessageReceived(NetworkConnectionToClient conn,
            MapBootstrapCompletedMessage message) {
            Logs.Log($"[NetworkController]: MapBootstrapCompletedMessage received. ConnectionId={conn.connectionId}");
            _signalBus.Fire(new SignalClientMapBootstrapCompleted(conn));
            _matchmakingController.ReleaseRelayPromotion("client confirmed map bootstrap");
        }

        private void PlayerBootstrapCompletedMessageReceived(NetworkConnectionToClient conn,
            PlayerBootstrapCompletedMessage message) {
            Logs.Log(
                $"[NetworkController]: PlayerBootstrapCompletedMessage received. ConnectionId={conn.connectionId}");

            _matchmakingController.ReleaseRelayPromotion("client confirmed player bootstrap");
        }

        #endregion
    }
}
*/


