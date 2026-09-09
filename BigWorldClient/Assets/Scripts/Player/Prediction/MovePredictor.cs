using System;
using System.Collections.Generic;
using BigWorldClient.Network;
using BigWorldClient.Network.Protocol;
using UnityEngine;

namespace BigWorldClient
{
    public enum PredictedEventKind { StartState = 0, DirChange = 1, Stop = 2 }

    public struct PendingMoveEvent
    {
        public ulong Seq;
        public long Tick;
        public PredictedEventKind Kind;
        public MoveState State;
        public double DirX, DirZ;
    }

    public sealed class MovePredictor
    {
        public const long TickMs = ServerClock.TickMs;
        private const int MaxWindowTick = 15;
        private const int MaxCatchUpPerFrame = 8;
        private const long MaxSnapDeficit = 60;

        public bool Ready { get; private set; }
        public MoveState CurrentState => _cur.State;
        public long CurrentTick => _simTick;
        public int PendingCount => _pending.Count;
        public float CurrentDirX => (float)_cur.MoveDirX;
        public float CurrentDirZ => (float)_cur.MoveDirZ;
        public Vector3 CurrentPosition => new((float)_cur.X, (float)_cur.Y, (float)_cur.Z);

        public MoveStateMachine StateMachine { get; private set; }

        private SimContext _ctx;
        private MoveSimEntity _cur;
        private MoveSimEntity _prev;
        private long _simTick;
        private ulong _seq;
        private readonly List<PendingMoveEvent> _pending = new();

        private Vector2 _currentMovement;
        private Vector2 _lastReportedDir;
        private float _lastDirReportTime;
        private bool _dirReported;
        private bool _resumeSprintAfterAirborne;

        private readonly GameNetworkManager _net;
        private readonly ServerClock _clock;

        public MovePredictor(GameNetworkManager net, ServerClock clock)
        {
            _net = net;
            _clock = clock;
        }

        public void Init(SimContext ctx, double spawnX, double spawnZ)
        {
            _ctx = ctx;
            StateMachine = new MoveStateMachine(MoveTransitionTable.Instance);
            StateMachine.TransitionOccurred += OnTransitionOccurred;
            _simTick = _clock != null && _clock.IsSynced ? _clock.TickNow() : 0;
            ctx.InitSpawnState(spawnX, spawnZ, out int voxelK, out double y);

            _cur = new MoveSimEntity
            {
                X = spawnX,
                Z = spawnZ,
                Y = y,
                VoxelK = voxelK,
                State = MoveState.MoveIdle,
                StateStartMs = MovementSim.TickToMs(_simTick),
            };
            _prev = _cur;
            _pending.Clear();
            _seq = 0;
            Ready = _simTick > 0;
            {}
        }

        public void ActivateWhenSynced()
        {
            if (Ready || _ctx == null || _clock == null || !_clock.IsSynced) return;
            _simTick = _clock.TickNow();
            _cur.StateStartMs = MovementSim.TickToMs(_simTick);
            _prev = _cur;
            Ready = true;
            {}
        }

        public void Update(List<PlayerInputCommand> commands, Vector2 movement, Vector3 worldDir, float realtime)
        {
            if (!Ready) return;

            _currentMovement = movement;
            if (commands != null)
            {
                foreach (PlayerInputCommand command in commands)
                    ProcessInputCommand(command, worldDir);
            }
            UpdateDirectionReporting(worldDir, realtime);

            long target = _clock.TickNow();
            if (target <= _simTick) return;

            long deficit = target - _simTick;
            if (deficit > MaxSnapDeficit)
            {

                {}
                _simTick = target - 1;
                _prev = _cur;
                AdvanceTo(target);
                return;
            }

            long limit = Math.Min(target, _simTick + MaxCatchUpPerFrame);
            AdvanceTo(limit);
        }

        private void AdvanceTo(long target)
        {
            while (_simTick < target)
            {
                _prev = _cur;
                _simTick++;
                StateMachine.Tick(ref _cur, _ctx, _simTick, TickMs);
                ApplyEventsAtTick(_simTick);
            }
        }

        private void StepTo(long target)
        {
            while (_simTick < target)
            {
                _prev = _cur;
                _simTick++;
                StateMachine.Tick(ref _cur, _ctx, _simTick, TickMs);
            }
        }

        private void ApplyEventsAtTick(long tick)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                PendingMoveEvent ev = _pending[i];
                if (ev.Tick < tick) continue;
                if (ev.Tick > tick) break;
                ApplyEvent(ev);
            }
        }

        public Vector3 GetRenderPosition(long nowMs)
        {
            long baseTick = _simTick > 1 ? _simTick - 1 : _simTick;
            double span = (double)(nowMs - MovementSim.TickToMs(baseTick)) / (double)TickMs;
            float t = (float)Math.Max(0.0, Math.Min(1.0, span));
            return new Vector3(
                (float)DoubleLerp(_prev.X, _cur.X, t),
                (float)DoubleLerp(_prev.Y, _cur.Y, t),
                (float)DoubleLerp(_prev.Z, _cur.Z, t));
        }

        public float GetRenderYaw()
        {
            return Mathf.Atan2((float)_cur.MoveDirX, (float)_cur.MoveDirZ) * Mathf.Rad2Deg;
        }

        private static double DoubleLerp(double a, double b, double t) => a + (b - a) * t;

        private void ProcessInputCommand(PlayerInputCommand command, Vector3 worldDir)
        {
            long tick = command.Tick > 0 ? ClampEventTick(command.Tick) : EventTick();
            MoveState state = _cur.State;

            switch (command.Kind)
            {
                case PlayerInputCommandKind.MovementStarted:
                    if (state == MoveState.MoveIdle || state == MoveState.MoveStopLight
                        || state == MoveState.MoveStopMed || state == MoveState.MoveRoll
                        || state == MoveState.MoveLandLight)
                    {
                        EnqueueStartAt(MoveState.MoveWalk, worldDir.x, worldDir.z, tick);
                    }
                    break;

                case PlayerInputCommandKind.MovementCanceled:
                    switch (state)
                    {
                        case MoveState.MoveSprint:
                            EnqueueStartAt(MoveState.MoveStopHard, 0, 0, tick);
                            break;
                        case MoveState.MoveRun:
                            EnqueueStartAt(MoveState.MoveStopMed, 0, 0, tick);
                            break;
                        case MoveState.MoveWalk:
                            EnqueueStartAt(MoveState.MoveStopLight, 0, 0, tick);
                            break;
                    }
                    break;

                case PlayerInputCommandKind.WalkToggle:
                    if (state == MoveState.MoveWalk)
                        EnqueueStartAt(MoveState.MoveRun, worldDir.x, worldDir.z, tick);
                    break;

                case PlayerInputCommandKind.Sprint:
                    if (state == MoveState.MoveWalk || state == MoveState.MoveRun)
                        EnqueueStartAt(MoveState.MoveSprint, worldDir.x, worldDir.z, tick);
                    break;

                case PlayerInputCommandKind.Jump:
                    if (state != MoveState.MoveDash && state != MoveState.MoveRoll
                        && state != MoveState.MoveJumpUp && state != MoveState.MoveJumpDown
                        && state != MoveState.MoveFall)
                    {
                        _resumeSprintAfterAirborne = state == MoveState.MoveSprint;
                        EnqueueStartAt(MoveState.MoveJumpUp, worldDir.x, worldDir.z, tick);
                    }
                    break;

                case PlayerInputCommandKind.Dash:
                    if (state != MoveState.MoveDash)
                        EnqueueStartAt(MoveState.MoveDash, worldDir.x, worldDir.z, tick);
                    break;

                case PlayerInputCommandKind.MovementPerformed:
                    break;
            }
        }

        private long ClampEventTick(long tick)
        {
            long now = _clock.TickNow();
            if (now > _simTick + MaxWindowTick) now = _simTick + MaxWindowTick;
            if (tick > now) tick = now;
            if (tick < _simTick) tick = _simTick;
            return tick;
        }

        private void UpdateDirectionReporting(Vector3 worldDir, float realtime)
        {
            MoveState state = _cur.State;
            bool moving = state == MoveState.MoveWalk || state == MoveState.MoveRun
                          || state == MoveState.MoveSprint || state == MoveState.MoveRoll;
            if (!moving || _currentMovement == Vector2.zero) return;
            if (realtime - _lastDirReportTime < 0.1f) return;
            if (_dirReported && Vector3.Angle(_lastReportedDir, worldDir) <= 5f) return;

            _lastReportedDir = worldDir;
            _lastDirReportTime = realtime;
            _dirReported = true;
            EnqueueDirChangeAt(worldDir.x, worldDir.z, EventTick());
        }

        private void OnTransitionOccurred(MoveState prev, MoveState curr, MoveTransitionReason reason)
        {
            long tick = _simTick;

            if (reason == MoveTransitionReason.DashCompleted)
            {
                if (_currentMovement == Vector2.zero)
                    EnqueueStartAt(MoveState.MoveStopHard, 0, 0, tick);
                else
                    EnqueueStartAt(MoveState.MoveSprint, _lastReportedDir.x, _lastReportedDir.y, tick);
            }
            else if (reason == MoveTransitionReason.AirborneLanded)
            {
                if (_currentMovement != Vector2.zero)
                    EnqueueStartAt(_resumeSprintAfterAirborne ? MoveState.MoveSprint : MoveState.MoveWalk,
                        _lastReportedDir.x, _lastReportedDir.y, tick);
            }
            else if (reason == MoveTransitionReason.RollCompleted && _currentMovement != Vector2.zero)
            {
                EnqueueStartAt(MoveState.MoveWalk, _lastReportedDir.x, _lastReportedDir.y, tick);
            }
            else if (reason == MoveTransitionReason.SprintTimeout && _currentMovement == Vector2.zero)
            {
                EnqueueStartAt(MoveState.MoveStopHard, 0, 0, tick);
            }
        }
        public long EventTick()
        {
            if (!Ready) return 0;
            long now = _clock.TickNow();
            if (now > _simTick + MaxWindowTick) now = _simTick + MaxWindowTick;
            if (now < _simTick) now = _simTick;
            return now;
        }

        public void EnqueueStart(MoveState to, float dirX, float dirZ)
        {
            if (!Ready || _net == null) return;
            long tick = EventTick();
            EnqueueStartAt(to, dirX, dirZ, tick);
        }

        public void EnqueueStartAt(MoveState to, float dirX, float dirZ, long tick)
        {
            if (!Ready || _net == null) return;

            if (_cur.State != to)
            {
                if (dirX != 0f || dirZ != 0f)
                {
                    _lastReportedDir = new Vector2(dirX, dirZ);
                    _dirReported = true;
                }
                AddPending(tick, PredictedEventKind.StartState, to, dirX, dirZ);
                if (tick == _simTick) ApplyEventsAtTick(tick);
                SendStart(to, dirX, dirZ, tick);
            }
            else if (dirX != 0f || dirZ != 0f)
            {
                EnqueueDirChangeAt(dirX, dirZ, tick);
            }
        }

        public void EnqueueDirChange(float dirX, float dirZ)
        {
            if (!Ready || _net == null) return;
            EnqueueDirChangeAt(dirX, dirZ, EventTick());
        }

        public void EnqueueDirChangeAt(float dirX, float dirZ, long tick)
        {
            if (!Ready || _net == null) return;
            AddPending(tick, PredictedEventKind.DirChange, default, dirX, dirZ);
            if (tick == _simTick) ApplyEventsAtTick(tick);
            _net.SendDirStart(MessageTypes.Cli2Wd_MoveDirChangeReq, dirX, dirZ, tick);
        }

        public void EnqueueMoveStop()
        {
            if (!Ready || _net == null) return;
            EnqueueMoveStopAt(EventTick());
        }

        public void EnqueueMoveStopAt(long tick)
        {
            if (!Ready || _net == null) return;
            AddPending(tick, PredictedEventKind.Stop, default, 0, 0);
            if (tick == _simTick) ApplyEventsAtTick(tick);
            _net.SendMoveStop(tick);
        }

        private void AddPending(long tick, PredictedEventKind kind, MoveState state, double dirX, double dirZ)
        {
            _pending.Add(new PendingMoveEvent
            {
                Seq = _seq++,
                Tick = tick,
                Kind = kind,
                State = state,
                DirX = (float)dirX,
                DirZ = (float)dirZ,
            });
            SortPending();
        }

        private void SortPending()
        {
            _pending.Sort((a, b) =>
            {
                int byTick = a.Tick.CompareTo(b.Tick);
                return byTick != 0 ? byTick : a.Seq.CompareTo(b.Seq);
            });
        }

        private void SendStart(MoveState to, float dirX, float dirZ, long tick)
        {
            switch (to)
            {
                case MoveState.MoveWalk: _net.SendDirStart(MessageTypes.Cli2Wd_WalkStartReq, dirX, dirZ, tick); break;
                case MoveState.MoveRun: _net.SendDirStart(MessageTypes.Cli2Wd_RunStartReq, dirX, dirZ, tick); break;
                case MoveState.MoveSprint: _net.SendDirStart(MessageTypes.Cli2Wd_SprintStartReq, dirX, dirZ, tick); break;
                case MoveState.MoveJumpUp: _net.SendDirStart(MessageTypes.Cli2Wd_JumpStartReq, dirX, dirZ, tick); break;
                case MoveState.MoveDash: _net.SendDirStart(MessageTypes.Cli2Wd_DashStartReq, dirX, dirZ, tick); break;
                case MoveState.MoveRoll: _net.SendDirStart(MessageTypes.Cli2Wd_RollStartReq, dirX, dirZ, tick); break;
                case MoveState.MoveStopLight:
                case MoveState.MoveStopMed:
                case MoveState.MoveStopHard: _net.SendStopStart(to, tick); break;
            }
        }

        private void ApplyEvent(in PendingMoveEvent ev)
        {
            switch (ev.Kind)
            {
                case PredictedEventKind.StartState:
                    if (ev.DirX != 0 || ev.DirZ != 0)
                        MovementSim.SetMoveDir(ref _cur, ev.DirX, ev.DirZ);
                    StateMachine.TryChangeState(ref _cur, ev.State, ev.Tick);
                    break;
                case PredictedEventKind.DirChange:
                    MovementSim.SetMoveDir(ref _cur, ev.DirX, ev.DirZ);
                    break;
                case PredictedEventKind.Stop:
                    StateMachine.ForceChangeState(ref _cur, MoveState.MoveIdle, ev.Tick);
                    break;
            }
        }

        public void OnAuthoritative(in MoveRspInfo info)
        {
            if (!Ready || !info.HasState) return;

            _clock?.LearnServerTick(info.SimTick, ServerClock.LocalNowMs());

            if (!info.Success)
            {
                _pending.Clear();
                RestoreAuthoritative(in info);
                _prev = _cur;
                return;
            }

            long ackTick = info.AckTick > 0 ? info.AckTick : info.SimTick;
            _pending.RemoveAll(ev => ev.Tick <= ackTick);

            RestoreAuthoritative(in info);
            long localNow = _clock.TickNow();
            ReplayPending(localNow);

            _prev = _cur;
        }

        private void RestoreAuthoritative(in MoveRspInfo info)
        {
            _simTick = info.SimTick;
            _cur = new MoveSimEntity
            {
                X = info.X,
                Y = info.Y,
                Z = info.Z,
                VoxelK = info.VoxelK,
                Airborne = info.Airborne,
                State = info.State,
                CurveNorm = info.CurveNorm,
                StateStartMs = info.StateStartMs,
                FallVelY = info.FallVelY,
                MoveDirX = info.DirX,
                MoveDirZ = info.DirZ,
            };

            StateMachine.ApplyAuthoritative(_cur.State, _cur.CurveNorm);
        }

        private void ReplayPending(long localNow)
        {
            SortPending();
            long limit = Math.Min(localNow, _simTick + MaxCatchUpPerFrame);
            long cur = _simTick;
            for (int i = 0; i < _pending.Count; i++)
            {
                PendingMoveEvent ev = _pending[i];
                if (ev.Tick <= cur) continue;
                if (ev.Tick > limit) break;
                StepTo(ev.Tick);
                ApplyEvent(ev);
                cur = ev.Tick;
            }
            if (limit > cur)
                StepTo(limit);
        }
    }
}
