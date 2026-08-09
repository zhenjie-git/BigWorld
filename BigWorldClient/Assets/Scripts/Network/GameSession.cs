using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Google.Protobuf;
using BigWorldClient.Network.Protocol;

namespace BigWorldClient.Network
{
    public enum SessionEventKind { LoginSucceeded, LoginFailed, ServerShutdown, Disconnected, LoggedOut }

    public readonly struct PlayerInfo
    {
        public readonly ulong PlayerId;
        public readonly string WorldId;
        public readonly string WorldAddr;
        public readonly double X, Z, Width, Height;

        public PlayerInfo(ulong playerId, string worldId, string worldAddr,
            double x, double z, double width, double height)
        {
            PlayerId = playerId;
            WorldId = worldId;
            WorldAddr = worldAddr;
            X = x;
            Z = z;
            Width = width;
            Height = height;
        }
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
        public readonly double X, Z, Y;
        public readonly string Message;

        public MoveRspInfo(bool success, double x, double z, double y, string message)
        {
            Success = success;
            X = x;
            Z = z;
            Y = y;
            Message = message;
        }
    }

    /// <summary>
    /// Pure-C# two-phase login session: connect to the login server, exchange
    /// credentials for a token + gateway address, then connect to the gateway and
    /// wait for the enter-scene notify. Afterwards it maintains the session
    /// (heartbeat, logout, server-shutdown notify). Runs a background poll thread
    /// and emits SessionEvents that a MonoBehaviour drains on the main thread.
    /// </summary>
    public sealed class GameSession : IDisposable
    {
        private enum State { Idle, Phase1, Phase2, InGame, LoggingOut }

        private const long LoginTimeoutMs = 15_000;

        private readonly NetClient _client = new NetClient();
        private readonly ConcurrentQueue<SessionEvent> _events = new ConcurrentQueue<SessionEvent>();
        private readonly ConcurrentQueue<MoveRspInfo> _moveResponses = new ConcurrentQueue<MoveRspInfo>();

        private Thread _thread;
        private volatile bool _running;
        private volatile State _state = State.Idle;
        private volatile bool _expectingDisconnect;

        // Phase-1 captured credentials / phase-2 captured token.
        private string _username;
        private string _password;
        private string _token;

        // Session data filled during phase 2.
        private ulong _playerId;
        private string _worldId;
        private string _worldAddr;
        private double _x, _z, _width, _height;
        private bool _gotGwRsp;
        private bool _gotEnter;

        private long _phaseStartTicks;

        /// <summary>Server-clock estimate, fed by each heartbeat reply while InGame.</summary>
        public ServerClock Clock { get; } = new ServerClock();

        public bool TryDequeue(out SessionEvent evt) => _events.TryDequeue(out evt);

        public bool TryDequeueMove(out MoveRspInfo info) => _moveResponses.TryDequeue(out info);

        public bool SendWalkStart(float dirX, float dirZ)
        {
            if (_state != State.InGame) return false;
            var req = new WalkStartReq { PlayerId = _playerId, Dir = new MoveDir { X = dirX, Z = dirZ }, ServerTimeMs = Clock.NowMs() };
            _client.Send(MessageTypes.Cli2Wd_WalkStartReq, req.ToByteArray());
            return true;
        }

        public bool SendRunStart(float dirX, float dirZ)
        {
            if (_state != State.InGame) return false;
            var req = new RunStartReq { PlayerId = _playerId, Dir = new MoveDir { X = dirX, Z = dirZ }, ServerTimeMs = Clock.NowMs() };
            _client.Send(MessageTypes.Cli2Wd_RunStartReq, req.ToByteArray());
            return true;
        }

        public bool SendSprintStart(float dirX, float dirZ)
        {
            if (_state != State.InGame) return false;
            var req = new SprintStartReq { PlayerId = _playerId, Dir = new MoveDir { X = dirX, Z = dirZ }, ServerTimeMs = Clock.NowMs() };
            _client.Send(MessageTypes.Cli2Wd_SprintStartReq, req.ToByteArray());
            return true;
        }

        public bool SendJumpStart(float dirX, float dirZ)
        {
            if (_state != State.InGame) return false;
            var req = new JumpStartReq { PlayerId = _playerId, Dir = new MoveDir { X = dirX, Z = dirZ }, ServerTimeMs = Clock.NowMs() };
            _client.Send(MessageTypes.Cli2Wd_JumpStartReq, req.ToByteArray());
            return true;
        }

        public bool SendDashStart(float dirX, float dirZ)
        {
            if (_state != State.InGame) return false;
            var req = new DashStartReq { PlayerId = _playerId, Dir = new MoveDir { X = dirX, Z = dirZ }, ServerTimeMs = Clock.NowMs() };
            _client.Send(MessageTypes.Cli2Wd_DashStartReq, req.ToByteArray());
            return true;
        }

        public bool SendRollStart(float dirX, float dirZ)
        {
            if (_state != State.InGame) return false;
            var req = new RollStartReq { PlayerId = _playerId, Dir = new MoveDir { X = dirX, Z = dirZ }, ServerTimeMs = Clock.NowMs() };
            _client.Send(MessageTypes.Cli2Wd_RollStartReq, req.ToByteArray());
            return true;
        }

        public bool SendStopStart(MoveState stopKind)
        {
            if (_state != State.InGame) return false;
            var req = new StopStartReq { PlayerId = _playerId, StopKind = stopKind, ServerTimeMs = Clock.NowMs() };
            _client.Send(MessageTypes.Cli2Wd_StopStartReq, req.ToByteArray());
            return true;
        }

        public bool SendMoveStop()
        {
            if (_state != State.InGame) return false;
            var req = new MoveStopReq { PlayerId = _playerId, ServerTimeMs = Clock.NowMs() };
            _client.Send(MessageTypes.Cli2Wd_MoveStopReq, req.ToByteArray());
            return true;
        }

        public bool SendMoveDirChange(float dirX, float dirZ)
        {
            if (_state != State.InGame) return false;
            var req = new MoveDirChangeReq { PlayerId = _playerId, Dir = new MoveDir { X = dirX, Z = dirZ }, ServerTimeMs = Clock.NowMs() };
            _client.Send(MessageTypes.Cli2Wd_MoveDirChangeReq, req.ToByteArray());
            return true;
        }

        /// <summary>Begin the two-phase login. Result arrives as a SessionEvent.</summary>
        public void  BeginLogin(string host, int port, string username, string password)
        {
            StopThread();
            // Discard anything a previous session left in the queues (e.g. a stale
            // Disconnected), so it can't be misread as a failure of this fresh login.
            DrainStaleEvents();

            Clock.Reset();
            _state = State.Phase1;
            _username = username ?? "";
            _password = password ?? "";
            _token = null;
            _playerId = 0;
            _worldId = null;
            _worldAddr = null;
            _gotGwRsp = false;
            _gotEnter = false;
            _expectingDisconnect = false;
            _phaseStartTicks = NowMs();

            if (!_client.Connect(host, port, 5000))
            {
                Push(SessionEventKind.LoginFailed, "无法连接登录服务器");
                return;
            }

            _running = true;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                if ((_state == State.Phase1 || _state == State.Phase2)
                    && NowMs() - _phaseStartTicks > LoginTimeoutMs)
                {
                    Push(SessionEventKind.LoginFailed, "登录超时");
                    Cleanup();
                    return;
                }

                if (_client.TryDequeue(out NetClient.NetEvent evt))
                    HandleNetEvent(evt);
                else
                    Thread.Sleep(10);
            }
        }

        private void HandleNetEvent(NetClient.NetEvent evt)
        {
            switch (evt.Kind)
            {
                case NetClient.NetEventKind.Connected:
                    if (_state == State.Phase1)
                    {
                        var req = new LoginReq { Account = _username, Password = _password };
                        _client.Send(MessageTypes.Cli2Lg_LoginReq, req.ToByteArray());
                    }
                    else if (_state == State.Phase2)
                    {
                        var req = new LoginReq { Account = _token ?? "", Password = "" };
                        _client.Send(MessageTypes.Cli2Gw_LoginReq, req.ToByteArray());
                    }
                    break;

                case NetClient.NetEventKind.Message:
                    HandleMessage(evt.MsgType, evt.Payload);
                    break;

                case NetClient.NetEventKind.Disconnected:
                    if (_expectingDisconnect)
                    {
                        // We initiated this close (phase1 -> phase2 handoff). Clear and move on.
                        _expectingDisconnect = false;
                        break;
                    }
                    if (_state == State.Phase1 || _state == State.Phase2)
                    {
                        Push(SessionEventKind.LoginFailed, "连接断开");
                        Cleanup();
                    }
                    else if (_state == State.InGame)
                    {
                        Push(SessionEventKind.Disconnected, "与服务器连接断开");
                        Cleanup();
                    }
                    // LoggingOut: the server closes after LogoutRsp - expected, ignore.
                    break;
            }
        }

        private delegate void MessageHandler(byte[] payload);

        /// <summary>
        /// Per-phase inbound dispatch: the current state picks a sub-table, then
        /// msgType picks the handler. A message not registered for the current
        /// phase is silently ignored. To add a message: register it under the
        /// phase(s) it is valid in and write one handler method.
        /// </summary>
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
                [MessageTypes.Wd2Cli_EnterSceneNotify] = HandleEnterSceneNotify,
            };
            _phaseHandlers[State.InGame] = new Dictionary<int, MessageHandler>
            {
                [MessageTypes.Gw2Cli_ServerShutdownNotify] = HandleServerShutdownNotify,
                [MessageTypes.Gw2Cli_HeartbeatRsp] = HandleHeartbeatRsp,
                [MessageTypes.Wd2Cli_MoveRsp] = HandleMoveRsp,
            };
            // LogoutRsp is accepted in both InGame and LoggingOut.
            _phaseHandlers[State.LoggingOut] = new Dictionary<int, MessageHandler>
            {
                [MessageTypes.Gw2Cli_LogoutRsp] = HandleLogoutRsp,
            };
        }

        private void HandleMessage(int msgType, byte[] payload)
        {
            if (_phaseHandlers.TryGetValue(_state, out var phase)
                && phase.TryGetValue(msgType, out var handler))
                handler(payload);
            // No handler for this msgType in the current phase: ignored.
        }

        /// <summary>Login server's LoginRsp (Phase1): capture token, hand off to the gateway.</summary>
        private void HandleLoginServerRsp(byte[] payload)
        {
            var rsp = LoginRsp.Parser.ParseFrom(payload);
            if (!rsp.Success)
            {
                Push(SessionEventKind.LoginFailed, rsp.Message);
                Cleanup();
                return;
            }
            _token = rsp.Token;

            var parts = (rsp.GatewayAddr ?? "").Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int gwPort))
            {
                Push(SessionEventKind.LoginFailed, "服务器返回的网关地址无效");
                Cleanup();
                return;
            }

            // Hand off to the gateway: close the login connection, then dial the gateway.
            _expectingDisconnect = true;
            _state = State.Phase2;
            _phaseStartTicks = NowMs();
            _client.Disconnect();
            if (!_client.Connect(parts[0], gwPort, 5000))
            {
                Push(SessionEventKind.LoginFailed, "无法连接网关服务器");
                Cleanup();
            }
        }

        /// <summary>Gateway's LoginRsp (Phase2): record player info, wait for enter-scene.</summary>
        private void HandleGatewayLoginRsp(byte[] payload)
        {
            var rsp = LoginRsp.Parser.ParseFrom(payload);
            if (!rsp.Success)
            {
                Push(SessionEventKind.LoginFailed, rsp.Message);
                Cleanup();
                return;
            }
            _playerId = rsp.PlayerId;
            _worldAddr = rsp.WorldAddr;
            if (string.IsNullOrEmpty(_worldId)) _worldId = rsp.WorldId;
            _gotGwRsp = true;
            MaybeEnterScene();
        }

        /// <summary>Enter-scene notify (Phase2): fills scene + spawn data, completes login.</summary>
        private void HandleEnterSceneNotify(byte[] payload)
        {
            var n = EnterSceneNotify.Parser.ParseFrom(payload);
            _worldId = n.WorldId;
            _playerId = n.PlayerId;
            _x = n.X;
            _z = n.Z;
            _width = n.Width;
            _height = n.Height;
            _gotEnter = true;
            MaybeEnterScene();
        }

        /// <summary>Server is shutting down (InGame): notify the UI and tear down.</summary>
        private void HandleServerShutdownNotify(byte[] payload)
        {
            var n = ServerShutdownNotify.Parser.ParseFrom(payload);
            Push(SessionEventKind.ServerShutdown, n.Message);
            Cleanup();
        }

        /// <summary>Heartbeat reply (InGame): feeds the RTT-offset clock estimate.</summary>
        private void HandleHeartbeatRsp(byte[] payload)
        {
            var rsp = ClientHeartbeatRsp.Parser.ParseFrom(payload);
            // rsp.ClientTimeMs is the gateway's echo of our send time, so the
            // reply alone provides both timestamps for the RTT-offset estimate.
            Clock.Sync(rsp.ServerTimeMs, rsp.ClientTimeMs, NowMs());
        }

        /// <summary>Move reply (InGame): a failed start/correction feeds the authoritative position back to the mover.</summary>
        private void HandleMoveRsp(byte[] payload)
        {
            var rsp = MoveRsp.Parser.ParseFrom(payload);
            _moveResponses.Enqueue(new MoveRspInfo(rsp.Success, rsp.X, rsp.Z, rsp.Y, rsp.Message));
        }

        /// <summary>Logout reply (InGame or LoggingOut): the server closes after this.</summary>
        private void HandleLogoutRsp(byte[] payload)
        {
            var rsp = LogoutRsp.Parser.ParseFrom(payload);
            Push(SessionEventKind.LoggedOut, rsp.Message);
            Cleanup();
        }

        private void MaybeEnterScene()
        {
            if (!_gotGwRsp || !_gotEnter) return;
            _state = State.InGame;
            Push(SessionEventKind.LoginSucceeded, "",
                new PlayerInfo(_playerId, _worldId, _worldAddr, _x, _z, _width, _height));
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
                var req = new LogoutReq { PlayerId = _playerId };
                _client.Send(MessageTypes.Cli2Gw_LogoutReq, req.ToByteArray());
            }
        }

        // netstandard2.1 has no Environment.TickCount64; DateTime ticks work everywhere.
        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        private void Push(SessionEventKind kind, string message = "", PlayerInfo player = default)
            => _events.Enqueue(new SessionEvent(kind, message, player));

        private void Cleanup()
        {
            _running = false;
            _expectingDisconnect = false;
            _client.Disconnect();
        }

        private void DrainStaleEvents()
        {
            while (_client.TryDequeue(out NetClient.NetEvent ne)) { }
            while (_events.TryDequeue(out SessionEvent se)) { }
        }

        private void StopThread()
        {
            _running = false;
            _client.Disconnect();
            if (_thread != null && _thread.IsAlive) _thread.Join(200);
            _thread = null;
        }

        public void Dispose() => StopThread();
    }
}
