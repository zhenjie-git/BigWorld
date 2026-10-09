using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Google.Protobuf;
using BigWorldClient.Network.Protocol;

namespace BigWorldClient.Network
{
    public enum SessionEventKind { LoginSucceeded, LoginFailed, SceneTransfer, ServerShutdown, Disconnected, LoggedOut, Kicked }

    public readonly struct PlayerInfo
    {
        public readonly ulong PlayerId;
        public readonly string WorldId;
        public readonly string WorldAddr;
        public readonly long SceneId;
        public readonly double X, Z;
        public readonly bool Seamless;

        public PlayerInfo(ulong playerId, string worldId, string worldAddr, long sceneId,
            double x, double z, bool seamless = false)
        {
            PlayerId = playerId;
            WorldId = worldId;
            WorldAddr = worldAddr;
            SceneId = sceneId;
            X = x;
            Z = z;
            Seamless = seamless;
        }
    }

    public enum RemoteEntityEventKind { Enter, Leave, MoveEvent, Snapshot }

    public struct RemoteEntityEvent
    {
        public RemoteEntityEventKind Kind;
        public ulong PlayerId;
        public string Account;
        public uint EventKind;
        public double X, Y, Z;
        public int VoxelK;
        public bool Airborne;
        public MoveState State;
        public double DirX, DirZ;
        public double CurveNorm;
        public long StateStartMs;
        public double FallVelY;
        public long SimTick;
        public long ServerTimeMs;
    }

    public readonly struct SessionEvent
    {
        public readonly SessionEventKind Kind;
        public readonly string Message;
        public readonly PlayerInfo Player;

        public SessionEvent(SessionEventKind kind, string message, PlayerInfo player)
        {
            Kind = kind;
            Message = message;
            Player = player;
        }
    }

    public readonly struct MoveRspInfo
    {
        public readonly bool Success;
        public readonly bool HasState;
        public readonly double X, Z, Y;
        public readonly string Message;
        public readonly long AckTimeMs;
        public readonly long SimTick;
        public readonly long AckTick;
        public readonly MoveState State;
        public readonly int VoxelK;
        public readonly bool Airborne;
        public readonly double DirX, DirZ;
        public readonly double CurveNorm;
        public readonly long StateStartMs;
        public readonly double FallVelY;

        public MoveRspInfo(bool success, bool hasState, double x, double z, double y, string message,
            long ackTimeMs, long simTick, long ackTick, MoveState state, int voxelK, bool airborne,
            double dirX, double dirZ, double curveNorm, long stateStartMs, double fallVelY)
        {
            Success = success;
            HasState = hasState;
            X = x;
            Z = z;
            Y = y;
            Message = message;
            AckTimeMs = ackTimeMs;
            SimTick = simTick;
            AckTick = ackTick;
            State = state;
            VoxelK = voxelK;
            Airborne = airborne;
            DirX = dirX;
            DirZ = dirZ;
            CurveNorm = curveNorm;
            StateStartMs = stateStartMs;
            FallVelY = fallVelY;
        }
    }

    public sealed class GameSession : IDisposable
    {
        private enum State { Idle, Phase1, Phase2, InGame, LoggingOut }

        private const long LoginTimeoutMs = 15_000;
        private const int ConnectTimeoutMs = 5000;
        private const int EventWaitMs = 500;
        private const long LogoutTimeoutMs = 10_000;

        private volatile NetClient _client;
        private readonly ConcurrentQueue<SessionEvent> _events = new ConcurrentQueue<SessionEvent>();
        private readonly ConcurrentQueue<MoveRspInfo> _moveResponses = new ConcurrentQueue<MoveRspInfo>();
        private readonly ConcurrentQueue<RemoteEntityEvent> _remoteEvents = new ConcurrentQueue<RemoteEntityEvent>();

        private Thread _thread;
        private volatile bool _running;
        private volatile int _sessionToken;
        private volatile State _state = State.Idle;
        private int _activeConnectionId;

        private string _host;
        private int _port;
        private string _username;
        private string _password;
        private string _token;
        private long _lastHeartbeatRspMs;

        private ulong _playerId;
        private string _worldId;
        private string _worldAddr;
        private long _sceneId;
        private double _x, _z;

        private long _phaseStartTicks;

        public ServerClock Clock { get; } = new ServerClock();

        public bool TryDequeue(out SessionEvent evt) => _events.TryDequeue(out evt);

        public bool TryDequeueMove(out MoveRspInfo info) => _moveResponses.TryDequeue(out info);

        public bool TryDequeueRemote(out RemoteEntityEvent evt) => _remoteEvents.TryDequeue(out evt);

        public void ClearMoveResponses()
        {
            while (_moveResponses.TryDequeue(out _)) { }
        }

        public long HeartbeatRspAgeMs
        {
            get
            {
                long last = Interlocked.Read(ref _lastHeartbeatRspMs);
                return last == 0 ? long.MaxValue : NowMs() - last;
            }
        }

        public void ResetHeartbeatWatch() => Interlocked.Exchange(ref _lastHeartbeatRspMs, NowMs());

        public bool SendDirStart(int msgType, float dirX, float dirZ, long tick)
        {
            if (_state != State.InGame) return false;
            var dir = new MoveDir { X = dirX, Z = dirZ };
            long serverTimeMs = ServerClock.TickToMs(tick);
            IMessage req;
            switch (msgType)
            {
                case MessageTypes.Cli2Wd_WalkStartReq:
                    req = new WalkStartReq { PlayerId = _playerId, Dir = dir, ServerTimeMs = serverTimeMs };
                    break;
                case MessageTypes.Cli2Wd_RunStartReq:
                    req = new RunStartReq { PlayerId = _playerId, Dir = dir, ServerTimeMs = serverTimeMs };
                    break;
                case MessageTypes.Cli2Wd_SprintStartReq:
                    req = new SprintStartReq { PlayerId = _playerId, Dir = dir, ServerTimeMs = serverTimeMs };
                    break;
                case MessageTypes.Cli2Wd_JumpStartReq:
                    req = new JumpStartReq { PlayerId = _playerId, Dir = dir, ServerTimeMs = serverTimeMs };
                    break;
                case MessageTypes.Cli2Wd_DashStartReq:
                    req = new DashStartReq { PlayerId = _playerId, Dir = dir, ServerTimeMs = serverTimeMs };
                    break;
                case MessageTypes.Cli2Wd_RollStartReq:
                    req = new RollStartReq { PlayerId = _playerId, Dir = dir, ServerTimeMs = serverTimeMs };
                    break;
                case MessageTypes.Cli2Wd_MoveDirChangeReq:
                    req = new MoveDirChangeReq { PlayerId = _playerId, Dir = dir, ServerTimeMs = serverTimeMs };
                    break;
                default:
                    return false;
            }
            return _client.Send(msgType, req.ToByteArray());
        }

        public bool SendStopStart(MoveState stopKind, long tick)
        {
            if (_state != State.InGame) return false;
            var req = new StopStartReq { PlayerId = _playerId, StopKind = stopKind, ServerTimeMs = ServerClock.TickToMs(tick) };
            return _client.Send(MessageTypes.Cli2Wd_StopStartReq, req.ToByteArray());
        }

        public bool SendHalt(long tick)
        {
            if (_state != State.InGame) return false;
            var req = new MoveHaltReq { PlayerId = _playerId, ServerTimeMs = ServerClock.TickToMs(tick) };
            return _client.Send(MessageTypes.Cli2Wd_MoveHaltReq, req.ToByteArray());
        }

        public void BeginLogin(string host, int port, string username, string password)
        {
            StopThread();
            DrainStaleEvents();

            Clock.Reset();
            Interlocked.Exchange(ref _lastHeartbeatRspMs, 0);
            _state = State.Phase1;
            _host = host;
            _port = port;
            _username = username ?? "";
            _password = password ?? "";
            _token = null;
            _playerId = 0;
            _worldId = null;
            _worldAddr = null;
            _activeConnectionId = 0;
            _phaseStartTicks = NowMs();

            NetClient client = new NetClient();
            _client = client;
            int token = ++_sessionToken;
            _running = true;
            _thread = new Thread(() => Loop(client, token)) { IsBackground = true };
            _thread.Start();
        }

        private void Loop(NetClient client, int token)
{
    try
    {
        int connectionId = client.Connect(_host, _port, ConnectTimeoutMs);
        if (connectionId == 0)
        {
            if (token == _sessionToken)
                Push(SessionEventKind.LoginFailed, "connect failed");
            return;
        }
        if (!_running || token != _sessionToken)
            return;
        _activeConnectionId = connectionId;

        while (_running && token == _sessionToken)
        {
            if ((_state == State.Phase1 || _state == State.Phase2)
                && NowMs() - _phaseStartTicks > LoginTimeoutMs)
            {
                Push(SessionEventKind.LoginFailed, "login timeout");
                return;
            }

            if (_state == State.LoggingOut && NowMs() - _phaseStartTicks > LogoutTimeoutMs)
            {
                Push(SessionEventKind.LoggedOut, "logout timeout");
                return;
            }

            if (client.TryDequeue(out NetClient.NetEvent evt))
            {
                try
                {
                    HandleNetEvent(client, evt);
                }
                catch (Exception ex)
                {
                    if (token == _sessionToken)
                    {
                        if (_state == State.InGame)
                            Push(SessionEventKind.Disconnected, ex.Message);
                        else if (_state == State.LoggingOut)
                            Push(SessionEventKind.LoggedOut, ex.Message);
                        else
                            Push(SessionEventKind.LoginFailed, ex.Message);
                    }
                    _running = false;
                    return;
                }
            }
            else
            {
                client.WaitForEvent(EventWaitMs);
            }
        }
    }
    finally
    {
        client.Dispose();
    }
}
private void HandleNetEvent(NetClient client, NetClient.NetEvent evt)
        {
            if (evt.ConnectionId != _activeConnectionId)
                return;

            switch (evt.Kind)
            {
                case NetClient.NetEventKind.Connected:
                    if (_state == State.Phase1)
                    {
                        var req = new LoginReq { Account = _username, Password = _password };
                        client.Send(MessageTypes.Cli2Lg_LoginReq, req.ToByteArray());
                    }
                    else if (_state == State.Phase2)
                    {
                        var req = new LoginReq { Account = _token ?? "", Password = "" };
                        client.Send(MessageTypes.Cli2Gw_LoginReq, req.ToByteArray());
                    }
                    break;

                case NetClient.NetEventKind.Message:
                    HandleMessage(client, evt.MsgType, evt.Payload);
                    break;

                case NetClient.NetEventKind.Disconnected:
                    if (_state == State.Phase1 || _state == State.Phase2)
                    {
                        Push(SessionEventKind.LoginFailed, "connection closed");
                        _running = false;
                    }
                    else if (_state == State.InGame)
                    {
                        Push(SessionEventKind.Disconnected, "connection closed");
                        _running = false;
                    }
                    else if (_state == State.LoggingOut)
                    {
                        Push(SessionEventKind.LoggedOut, "connection closed during logout");
                        _running = false;
                    }
                    break;
            }
        }

        private delegate void MessageHandler(NetClient client, byte[] payload);

        private readonly Dictionary<State, Dictionary<int, MessageHandler>> _phaseHandlers = new();

        public GameSession()
        {
            _phaseHandlers[State.Phase1] = new Dictionary<int, MessageHandler>
            {
                [MessageTypes.Lg2Cli_LoginRsp] = HandleLoginServerRsp,
            };
            _phaseHandlers[State.Phase2] = new Dictionary<int, MessageHandler>
            {
                [MessageTypes.Gw2Cli_LoginRsp] = HandleGatewayLoginRsp,
            };
            _phaseHandlers[State.InGame] = new Dictionary<int, MessageHandler>
            {
                [MessageTypes.Gw2Cli_ServerShutdownNotify] = HandleServerShutdownNotify,
                [MessageTypes.Gw2Cli_HeartbeatRsp] = HandleHeartbeatRsp,
                [MessageTypes.Wd2Cli_MoveRsp] = HandleMoveRsp,
                [MessageTypes.Gw2Cli_LogoutRsp] = HandleForceKickRsp,
                [MessageTypes.Wd2Cli_EnterSceneNotify] = HandleInGameEnterSceneNotify,
                [MessageTypes.Wd2Cli_EntityEnterNotify] = HandleEntityEnterNotify,
                [MessageTypes.Wd2Cli_EntityLeaveNotify] = HandleEntityLeaveNotify,
                [MessageTypes.Wd2Cli_EntityMoveEventNotify] = HandleEntityMoveEventNotify,
                [MessageTypes.Wd2Cli_EntitySnapshotNotify] = HandleEntitySnapshotNotify,
            };

            _phaseHandlers[State.LoggingOut] = new Dictionary<int, MessageHandler>
            {
                [MessageTypes.Gw2Cli_LogoutRsp] = HandleLogoutRsp,
            };
        }

        private void HandleMessage(NetClient client, int msgType, byte[] payload)
        {
            if (_phaseHandlers.TryGetValue(_state, out var phase)
                && phase.TryGetValue(msgType, out var handler))
                handler(client, payload);
        }

        private void HandleLoginServerRsp(NetClient client, byte[] payload)
        {
            var rsp = LoginRsp.Parser.ParseFrom(payload);
            if (!rsp.Success)
            {
                Push(SessionEventKind.LoginFailed, rsp.Message);
                _running = false;
                return;
            }
            _token = rsp.Token;

            var parts = (rsp.GatewayAddr ?? "").Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int gwPort))
            {
                Push(SessionEventKind.LoginFailed, "服务器返回的网关地址无效");
                _running = false;
                return;
            }

            _state = State.Phase2;
            _phaseStartTicks = NowMs();
            client.Disconnect();
            int gatewayId = client.Connect(parts[0], gwPort, ConnectTimeoutMs);
            if (gatewayId == 0)
            {
                Push(SessionEventKind.LoginFailed, "无法连接网关服务器");
                _running = false;
                return;
            }
            _activeConnectionId = gatewayId;
        }

        private void HandleGatewayLoginRsp(NetClient client, byte[] payload)
        {
            var rsp = LoginRsp.Parser.ParseFrom(payload);
            if (!rsp.Success)
            {
                Push(SessionEventKind.LoginFailed, rsp.Message);
                _running = false;
                return;
            }
            _playerId = rsp.PlayerId;
            _worldId = rsp.WorldId;
            _worldAddr = rsp.WorldAddr;
            _sceneId = rsp.SceneId;
            _x = rsp.X;
            _z = rsp.Z;
            _state = State.InGame;
            Push(SessionEventKind.LoginSucceeded, "",
                new PlayerInfo(_playerId, _worldId, _worldAddr, _sceneId, _x, _z));
        }

        private void HandleInGameEnterSceneNotify(NetClient client, byte[] payload)
        {
            var n = EnterSceneNotify.Parser.ParseFrom(payload);
            if (!n.Seamless)
                ClearMoveResponses();
            _playerId = n.PlayerId;
            _worldId = n.WorldId;
            _sceneId = n.SceneId;
            _x = n.X;
            _z = n.Z;
            Push(SessionEventKind.SceneTransfer, "",
                new PlayerInfo(_playerId, _worldId, _worldAddr, _sceneId, _x, _z, n.Seamless));
        }

        private static RemoteEntityEvent FromEntityState(EntityStatePb s)
        {
            return new RemoteEntityEvent
            {
                PlayerId = s.PlayerId,
                Account = s.Account,
                X = s.X,
                Y = s.Y,
                Z = s.Z,
                VoxelK = s.VoxelK,
                Airborne = s.Airborne,
                State = s.State,
                DirX = s.DirX,
                DirZ = s.DirZ,
                CurveNorm = s.CurveNorm,
                StateStartMs = s.StateStartMs,
                FallVelY = s.FallVelY,
                SimTick = s.SimTick,
            };
        }

        private void HandleEntityEnterNotify(NetClient client, byte[] payload)
        {
            var n = EntityEnterNotify.Parser.ParseFrom(payload);
            RemoteEntityEvent evt = FromEntityState(n.Entity);
            evt.Kind = RemoteEntityEventKind.Enter;
            _remoteEvents.Enqueue(evt);
        }

        private void HandleEntityLeaveNotify(NetClient client, byte[] payload)
        {
            var n = EntityLeaveNotify.Parser.ParseFrom(payload);
            _remoteEvents.Enqueue(new RemoteEntityEvent { Kind = RemoteEntityEventKind.Leave, PlayerId = n.PlayerId });
        }

        private void HandleEntityMoveEventNotify(NetClient client, byte[] payload)
        {
            var n = EntityMoveEventNotify.Parser.ParseFrom(payload);
            _remoteEvents.Enqueue(new RemoteEntityEvent
            {
                Kind = RemoteEntityEventKind.MoveEvent,
                PlayerId = n.PlayerId,
                EventKind = n.Kind,
                State = n.State,
                DirX = n.DirX,
                DirZ = n.DirZ,
                ServerTimeMs = n.ServerTimeMs,
            });
        }

        private void HandleEntitySnapshotNotify(NetClient client, byte[] payload)
        {
            var n = EntitySnapshotNotify.Parser.ParseFrom(payload);
            RemoteEntityEvent evt = FromEntityState(n.Entity);
            evt.Kind = RemoteEntityEventKind.Snapshot;
            _remoteEvents.Enqueue(evt);
        }

        private void HandleServerShutdownNotify(NetClient client, byte[] payload)
        {
            var n = ServerShutdownNotify.Parser.ParseFrom(payload);
            Push(SessionEventKind.ServerShutdown, n.Message);
            _running = false;
        }

        private void HandleHeartbeatRsp(NetClient client, byte[] payload)
        {
            var rsp = ClientHeartbeatRsp.Parser.ParseFrom(payload);
            Interlocked.Exchange(ref _lastHeartbeatRspMs, NowMs());
            Clock.Sync(rsp.ServerTimeMs, rsp.ClientTimeMs, NowMs());
        }

        private void HandleMoveRsp(NetClient client, byte[] payload)
        {
            var rsp = MoveRsp.Parser.ParseFrom(payload);
            _moveResponses.Enqueue(new MoveRspInfo(
                rsp.Success,
                rsp.SimTick > 0,
                rsp.X, rsp.Z, rsp.Y, rsp.Message,
                rsp.AckTimeMs, rsp.SimTick, rsp.AckTick,
                rsp.State, rsp.VoxelK, rsp.Airborne,
                rsp.DirX, rsp.DirZ, rsp.CurveNorm,
                rsp.StateStartMs, rsp.FallVelY));
        }

        private void HandleLogoutRsp(NetClient client, byte[] payload)
        {
            var rsp = LogoutRsp.Parser.ParseFrom(payload);
            Push(SessionEventKind.LoggedOut, rsp.Message);
            _running = false;
        }

        private void HandleForceKickRsp(NetClient client, byte[] payload)
        {
            var rsp = LogoutRsp.Parser.ParseFrom(payload);
            Push(SessionEventKind.Kicked, string.IsNullOrEmpty(rsp.Message) ? "account logged in elsewhere" : rsp.Message);
            _running = false;
        }

        public void SendHeartbeat()
        {
            if (_state == State.InGame)
            {
                var req = new ClientHeartbeatReq { ClientTimeMs = NowMs() };
                _client.Send(MessageTypes.Cli2Gw_HeartbeatReq, req.ToByteArray());
            }
        }

        public void Logout()
        {
            if (_state == State.InGame)
            {
                _state = State.LoggingOut;
                _phaseStartTicks = NowMs();
                var req = new LogoutReq { PlayerId = _playerId };
                _client.Send(MessageTypes.Cli2Gw_LogoutReq, req.ToByteArray());
            }
            else if (_state != State.Idle && _state != State.LoggingOut)
            {
                _state = State.Idle;
                _running = false;
                Push(SessionEventKind.LoggedOut, "logout requested");
            }
        }

        public void Abort(string reason)
        {
            if (_state == State.InGame)
            {
                _state = State.Idle;
                Push(SessionEventKind.Disconnected, reason);
            }
            _running = false;
        }

        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        private void Push(SessionEventKind kind, string message = "", PlayerInfo player = default)
            => _events.Enqueue(new SessionEvent(kind, message, player));

        private void DrainStaleEvents()
        {
            _client?.Drain();
            while (_events.TryDequeue(out _)) { }
            while (_moveResponses.TryDequeue(out _)) { }
            while (_remoteEvents.TryDequeue(out _)) { }
        }

        private void StopThread()
        {
            _running = false;
            _sessionToken++;
            var old = _thread;
            _thread = null;
            if (old != null && old.IsAlive && old != Thread.CurrentThread)
                old.Join(500);
        }

        public void Dispose() => StopThread();
    }
}
