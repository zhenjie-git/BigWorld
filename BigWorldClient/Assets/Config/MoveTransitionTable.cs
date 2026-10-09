using System.Collections.Generic;
using BigWorldClient.Network.Protocol;
using UnityEngine;

namespace BigWorldClient
{

    public enum MoveEvent { Walk = 0, Run, Sprint, Jump, Dash, Roll, StopLight, StopMed, StopHard, Halt, DirChange }

    public sealed class MoveTransitionTable
    {
        private readonly Dictionary<MoveState, Dictionary<string, EventPolicy>> _policies = new();
        private static readonly HashSet<string> Warned = new();

        static readonly Dictionary<MoveState, string> EventOfTarget = new()
        {
            { MoveState.MoveWalk, "Walk" },
            { MoveState.MoveRun, "Run" },
            { MoveState.MoveSprint, "Sprint" },
            { MoveState.MoveJumpUp, "Jump" },
            { MoveState.MoveDash, "Dash" },
            { MoveState.MoveRoll, "Roll" },
            { MoveState.MoveStopLight, "StopLight" },
            { MoveState.MoveStopMed, "StopMed" },
            { MoveState.MoveStopHard, "StopHard" },
            { MoveState.MoveIdle, "Halt" },
        };

        static MoveTransitionTable _cached;

        public static MoveTransitionTable Instance => _cached ??= Load();

        static MoveTransitionTable Load()
        {
            var table = new MoveTransitionTable();
            TextAsset asset = Resources.Load<TextAsset>("Config/StateTransitionTable");
            if (asset == null)
            {
                Debug.LogError("[MoveTransitionTable] 缺少 Resources/Config/StateTransitionTable.bytes，所有移动转移将被拒绝（重新执行配置导出中心导出状态迁移表）");
                return table;
            }

            TransitionTableMsg msg = TransitionTableMsg.GetRootAsTransitionTableMsg(
                new Google.FlatBuffers.ByteBuffer(asset.bytes));
            for (int i = 0; i < msg.EntriesLength; i++)
            {
                var entry = msg.Entries(i);
                if (!entry.HasValue || !TryParseStateName(entry.Value.Source, out MoveState from)) continue;

                var row = new Dictionary<string, EventPolicy>
                {
                    { "Walk", entry.Value.Walk },
                    { "Run", entry.Value.Run },
                    { "Sprint", entry.Value.Sprint },
                    { "Jump", entry.Value.Jump },
                    { "Dash", entry.Value.Dash },
                    { "Roll", entry.Value.Roll },
                    { "StopLight", entry.Value.StopLight },
                    { "StopMed", entry.Value.StopMed },
                    { "StopHard", entry.Value.StopHard },
                    { "Halt", entry.Value.Halt },
                    { "DirChange", entry.Value.DirChange },
                };
                table._policies[from] = row;
            }
            return table;
        }

        public EventPolicy PolicyOf(MoveState from, MoveEvent ev)
        {
            if (!_policies.TryGetValue(from, out Dictionary<string, EventPolicy> row))
            {
                WarnOnce($"源状态 {from} 不在状态迁移表中");
                return EventPolicy.Block;
            }
            if (!row.TryGetValue(ev.ToString(), out EventPolicy policy))
            {
                WarnOnce($"事件 {ev} 缺少 source={from} 的策略格");
                return EventPolicy.Block;
            }
            return policy;
        }

        public bool CanTransition(MoveState from, MoveState to)
        {
            if (from == to) return true;
            if (_policies.Count == 0) return false;
            if (!_policies.TryGetValue(from, out Dictionary<string, EventPolicy> row)) return false;
            if (!EventOfTarget.TryGetValue(to, out string ev)) return false;
            if (!row.TryGetValue(ev, out EventPolicy policy)) return false;
            return policy != EventPolicy.Block;
        }

        private static void WarnOnce(string message)
        {
            if (Warned.Add(message))
                Debug.LogError($"[MoveTransitionTable] {message}，按 BLOCK 关死（重新执行配置导出中心导出状态迁移表）");
        }

        private static bool TryParseStateName(string name, out MoveState state)
        {
            var mappings = MoveStateMappings.Mappings;
            for (int i = 0; i < mappings.Length; i++)
            {
                if (mappings[i].Name == name)
                {
                    state = mappings[i].State;
                    return true;
                }
            }

            state = MoveState.MoveIdle;
            return false;
        }
    }
}
