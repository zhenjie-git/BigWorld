using System.Collections.Generic;
using BigWorldClient.Network.Protocol;
using UnityEngine;

namespace BigWorldClient
{

    public sealed class MoveTransitionTable
    {
        private readonly Dictionary<MoveState, HashSet<MoveState>> _allowed = new();

        static MoveTransitionTable _cached;

        public static MoveTransitionTable Instance => _cached ??= Load();

        static MoveTransitionTable Load()
        {
            var table = new MoveTransitionTable();
            TextAsset asset = Resources.Load<TextAsset>("Config/StateTransitionTable");
            if (asset == null) return table;

            TransitionTableMsg msg = TransitionTableMsg.GetRootAsTransitionTableMsg(
                new Google.FlatBuffers.ByteBuffer(asset.bytes));
            for (int i = 0; i < msg.EntriesLength; i++)
            {
                var entry = msg.Entries(i);
                if (!entry.HasValue || !TryParseStateName(entry.Value.Source, out MoveState from)) continue;

                HashSet<MoveState> targets = new();
                for (int j = 0; j < entry.Value.AllowedTargetsLength; j++)
                {
                    if (TryParseStateName(entry.Value.AllowedTargets(j), out MoveState to))
                        targets.Add(to);
                }
                table._allowed[from] = targets;
            }
            return table;
        }

        public bool CanTransition(MoveState from, MoveState to)
        {
            if (from == to) return true;
            if (_allowed.Count == 0) return true;
            return _allowed.TryGetValue(from, out HashSet<MoveState> targets) && targets.Contains(to);
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
