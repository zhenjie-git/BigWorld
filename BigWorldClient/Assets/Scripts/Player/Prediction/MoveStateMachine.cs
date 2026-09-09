using System;
using System.Collections.Generic;
using BigWorldClient.Network.Protocol;
using UnityEngine;

namespace BigWorldClient
{
    public enum MoveTransitionReason
    {
        None = 0,
        StateCompleted = 1,
        SprintTimeout = 2,
        RollCompleted = 3,
        HardStopCompleted = 4,
        DashCompleted = 5,
        AirborneLanded = 6,
        Ledged = 7,
    }

    public struct MoveTransitionRequest
    {
        public MoveState To { get; }
        public MoveTransitionReason Reason { get; }
        public bool HasValue { get; }

        public MoveTransitionRequest(MoveState to, MoveTransitionReason reason)
        {
            To = to;
            Reason = reason;
            HasValue = true;
        }

        public static readonly MoveTransitionRequest None = default;
    }

    public abstract class MoveStateBase
    {
        public abstract MoveState Type { get; }

        public virtual void Enter(ref MoveSimEntity entity, long startTick) { }
        public virtual void Exit(ref MoveSimEntity entity) { }

        public abstract MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs);
    }

    public sealed class MoveStateMachine
    {
        public MoveState CurrentState => _current?.Type ?? MoveState.MoveIdle;
        public MoveTransitionTable TransitionTable => _table;

        public event Action<MoveState, double> StateApplied = delegate { };
        public event Action<MoveState, MoveState, MoveTransitionReason> TransitionOccurred = delegate { };

        private readonly MoveTransitionTable _table;
        private MoveStateBase _current;

        public MoveStateMachine(MoveTransitionTable table)
        {
            _table = table ?? new MoveTransitionTable();
        }

        public void Tick(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
        {
            if (_current == null)
                SyncCurrentState(entity.State);

            MoveTransitionRequest request = _current.TickUpdate(ref entity, ctx, tick, dtMs);
            if (request.HasValue)
                ChangeState(ref entity, request.To, tick, request.Reason);
        }

        public bool TryChangeState(ref MoveSimEntity entity, MoveState to, long tick)
        {
            if (_current == null)
                SyncCurrentState(entity.State);

            if (to == _current.Type) return true;
            if (!_table.CanTransition(_current.Type, to)) return false;

            ChangeState(ref entity, to, tick, MoveTransitionReason.None);
            return true;
        }

        public void ForceChangeState(ref MoveSimEntity entity, MoveState to, long tick)
        {
            if (_current == null)
                SyncCurrentState(entity.State);

            if (to == _current.Type) return;
            ChangeState(ref entity, to, tick, MoveTransitionReason.None);
        }

        public void ApplyAuthoritative(MoveState state, double normalizedTime)
        {
            if (_current == null || _current.Type != state)
                _current = CreateState(state);

            StateApplied(state, normalizedTime);
        }

        private void ChangeState(ref MoveSimEntity entity, MoveState to, long tick, MoveTransitionReason reason)
        {
            MoveState prev = CurrentState;
            _current?.Exit(ref entity);
            _current = CreateState(to);

            MovementSim.SetMoveState(ref entity, to, MovementSim.TickToMs(tick));
            _current.Enter(ref entity, tick);

            if (prev == MoveState.MoveJumpUp || prev == MoveState.MoveJumpDown
                || to == MoveState.MoveJumpUp || to == MoveState.MoveJumpDown
                || to == MoveState.MoveIdle && (prev == MoveState.MoveJumpUp || prev == MoveState.MoveJumpDown))
            {
            }

            TransitionOccurred(prev, to, reason);
            StateApplied(to, 0.0);
        }

        private void SyncCurrentState(MoveState state)
        {
            _current = CreateState(state);
        }

        private static MoveStateBase CreateState(MoveState state)
        {
            switch (state)
            {
                case MoveState.MoveIdle: return new IdleMoveState();
                case MoveState.MoveWalk: return new WalkMoveState();
                case MoveState.MoveRun: return new RunMoveState();
                case MoveState.MoveSprint: return new SprintMoveState();
                case MoveState.MoveStopLight: return new StopMoveState(MoveState.MoveStopLight);
                case MoveState.MoveStopMed: return new StopMoveState(MoveState.MoveStopMed);
                case MoveState.MoveStopHard: return new StopMoveState(MoveState.MoveStopHard);
                case MoveState.MoveLandLight: return new LandingMoveState(MoveState.MoveLandLight);
                case MoveState.MoveRoll: return new RollMoveState();
                case MoveState.MoveDash: return new DashMoveState();
                case MoveState.MoveJumpUp: return new JumpUpMoveState();
                case MoveState.MoveFall: return new FallMoveState();
                case MoveState.MoveJumpDown: return new JumpDownMoveState();
                default: return new IdleMoveState();
            }
        }
    }

    public sealed class IdleMoveState : MoveStateBase
    {
        public override MoveState Type => MoveState.MoveIdle;
        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs) => MoveTransitionRequest.None;
    }

    public sealed class WalkMoveState : MoveStateBase
    {
        public override MoveState Type => MoveState.MoveWalk;
        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
            => MovementSim.CyclicUpdate(ref entity, ctx, dtMs, MovementSim.TickToMs(tick), MoveState.MoveWalk);
    }

    public sealed class RunMoveState : MoveStateBase
    {
        public override MoveState Type => MoveState.MoveRun;
        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
            => MovementSim.CyclicUpdate(ref entity, ctx, dtMs, MovementSim.TickToMs(tick), MoveState.MoveRun);
    }

    public sealed class SprintMoveState : MoveStateBase
    {
        public override MoveState Type => MoveState.MoveSprint;
        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
            => MovementSim.SprintUpdate(ref entity, ctx, dtMs, MovementSim.TickToMs(tick));
    }

    public sealed class RollMoveState : MoveStateBase
    {
        public override MoveState Type => MoveState.MoveRoll;
        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
            => MovementSim.RollUpdate(ref entity, ctx, dtMs, MovementSim.TickToMs(tick));
    }

    public sealed class DashMoveState : MoveStateBase
    {
        public override MoveState Type => MoveState.MoveDash;
        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
            => MovementSim.DashUpdate(ref entity, ctx, dtMs, MovementSim.TickToMs(tick));
    }

    public sealed class StopMoveState : MoveStateBase
    {
        public override MoveState Type { get; }

        public StopMoveState(MoveState type)
        {
            Type = type;
        }

        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
            => MovementSim.StopUpdate(ref entity, ctx, dtMs, MovementSim.TickToMs(tick));
    }

    public sealed class LandingMoveState : MoveStateBase
    {
        public override MoveState Type { get; }

        public LandingMoveState(MoveState type)
        {
            Type = type;
        }

        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
            => MoveTransitionRequest.None;
    }

    public sealed class JumpUpMoveState : MoveStateBase
    {
        public override MoveState Type => MoveState.MoveJumpUp;
        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
            => MovementSim.JumpUpUpdate(ref entity, ctx, dtMs, MovementSim.TickToMs(tick));
    }

    public sealed class JumpDownMoveState : MoveStateBase
    {
        public override MoveState Type => MoveState.MoveJumpDown;
        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
            => MovementSim.JumpDownUpdate(ref entity, ctx, dtMs, MovementSim.TickToMs(tick));
    }

    public sealed class FallMoveState : MoveStateBase
    {
        public override MoveState Type => MoveState.MoveFall;
        public override MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs)
            => MovementSim.FallUpdate(ref entity, ctx, dtMs, MovementSim.TickToMs(tick));
    }
}
