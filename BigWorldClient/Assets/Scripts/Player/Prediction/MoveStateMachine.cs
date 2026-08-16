using System;
using System.Collections.Generic;
using System.Linq;
using BigWorldClient.Network.Protocol;
using UnityEngine;

namespace BigWorldClient
{
    public enum MoveTransitionReason
    {
        None = 0,
        StateCompleted = 1,   // 曲线/动画自然结束
        SprintTimeout = 2,    // Sprint 超时自动转 Run
        RollCompleted = 3,    // Roll 曲线结束自动转 Idle
        HardStopCompleted = 4, // HardStop 曲线结束自动转 Idle
        DashCompleted = 5,    // Dash 曲线结束自动转 Idle
        AirborneLanded = 6,   // JumpDown/Fall 落地自动转 Idle
        Ledged = 7,           // 地面走出悬崖自动转 Fall
    }

    /// <summary>状态类在 tick 更新结束后提交的迁移请求。状态类不直接切换状态。</summary>
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

    /// <summary>
    /// 单个移动状态。所有运行进度都保存在 SimEntity 里；
    /// 状态类实例自身不保存任何影响模拟的数据，因此回滚无需恢复状态类。
    /// </summary>
    public abstract class MoveStateBase
    {
        public abstract MoveState Type { get; }

        public virtual void Enter(ref MoveSimEntity entity, long startTick) { }
        public virtual void Exit(ref MoveSimEntity entity) { }

        /// <summary>
        /// 推进一个 tick。需要切换时返回 TransitionRequest，由状态机在 tick 末统一切换。
        /// </summary>
        public abstract MoveTransitionRequest TickUpdate(ref MoveSimEntity entity, SimContext ctx, long tick, long dtMs);
    }

    // ── 共享状态迁移表。服务器是权威；客户端加载同一份 JSON 只做本地预判。 ──

    [Serializable]
    public class StateTransitionEntryJson
    {
        public string source;
        public string[] allowed_targets;
    }

    [Serializable]
    public class StateTransitionTableJson
    {
        public StateTransitionEntryJson[] entries;
    }

    public sealed class MoveTransitionTable
    {
        private readonly Dictionary<MoveState, HashSet<MoveState>> _allowed = new();

        /// <summary>状态迁移表里允许出现的状态名（顺序即导出的规范顺序）。</summary>
        public static readonly (string Name, MoveState State)[] StateMappings =
        {
            ("Idling", MoveState.MoveIdle),
            ("Walking", MoveState.MoveWalk),
            ("Running", MoveState.MoveRun),
            ("Sprinting", MoveState.MoveSprint),
            ("LightStopping", MoveState.MoveStopLight),
            ("MediumStopping", MoveState.MoveStopMed),
            ("HardStopping", MoveState.MoveStopHard),
            ("LightLanding", MoveState.MoveLandLight),
            ("Rolling", MoveState.MoveRoll),
            ("Dashing", MoveState.MoveDash),
            ("JumpUp", MoveState.MoveJumpUp),
            ("Falling", MoveState.MoveFall),
            ("JumpDown", MoveState.MoveJumpDown),
        };

        public static readonly string[] StateNames = StateMappings.Select(m => m.Name).ToArray();

        public static MoveTransitionTable Load()
        {
            var table = new MoveTransitionTable();
            TextAsset asset = Resources.Load<TextAsset>("Config/StateTransitionTable");
            if (asset == null) return table;

            StateTransitionTableJson json = JsonUtility.FromJson<StateTransitionTableJson>(asset.text);
            if (json?.entries == null) return table;

            foreach (StateTransitionEntryJson entry in json.entries)
            {
                if (!TryParseStateName(entry.source, out MoveState from)) continue;

                HashSet<MoveState> targets = new();
                if (entry.allowed_targets != null)
                {
                    foreach (string name in entry.allowed_targets)
                    {
                        if (TryParseStateName(name, out MoveState to))
                            targets.Add(to);
                    }
                }
                table._allowed[from] = targets;
            }
            return table;
        }

        public bool CanTransition(MoveState from, MoveState to)
        {
            if (from == to) return true;
            if (_allowed.Count == 0) return true; // 配置缺失时降级为允许，服务器仍会权威校验
            return _allowed.TryGetValue(from, out HashSet<MoveState> targets) && targets.Contains(to);
        }

        private static bool TryParseStateName(string name, out MoveState state)
        {
            for (int i = 0; i < StateMappings.Length; i++)
            {
                if (StateMappings[i].Name == name)
                {
                    state = StateMappings[i].State;
                    return true;
                }
            }

            state = MoveState.MoveIdle;
            return false;
        }
    }

    /// <summary>
    /// tick 驱动的移动状态机。
    /// 职责：
    ///   1. 每个 tick 调用当前状态类的 TickUpdate；
    ///   2. 收集状态类提交的 TransitionRequest，在 tick 末统一切换；
    ///   3. 外部输入切换（Start/Stop）通过 TryChangeState 检查迁移表后进入；
    ///   4. 向表现层广播 StateApplied（状态 + 应从哪段动画进度开始）。
    /// </summary>
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

        /// <summary>MoveStop 与服务器一致：不查迁移表，直接进入目标状态。</summary>
        public void ForceChangeState(ref MoveSimEntity entity, MoveState to, long tick)
        {
            if (_current == null)
                SyncCurrentState(entity.State);

            if (to == _current.Type) return;
            ChangeState(ref entity, to, tick, MoveTransitionReason.None);
        }

        /// <summary>服务器权威快照恢复：只同步当前状态对象，不修改 SimEntity。</summary>
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

            // 本地切换从 0 开始；回滚恢复时由 ApplyAuthoritative 传入实际进度。
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
