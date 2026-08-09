using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{
    [Serializable]
    public class StateTransitionEntry
    {
        public PlayerMovementStateType SourceState;
        public List<PlayerMovementStateType> AllowedTargets = new();
    }

    [CreateAssetMenu(menuName = "Player/State Transition Table")]
    public class PlayerStateTransitionTable : ScriptableObject
    {
        public List<StateTransitionEntry> Entries = new();

        private Dictionary<PlayerMovementStateType, HashSet<PlayerMovementStateType>> _lookup;

        public void BuildLookup()
        {
            _lookup = new Dictionary<PlayerMovementStateType, HashSet<PlayerMovementStateType>>();
            foreach (var entry in Entries)
            {
                _lookup[entry.SourceState] = new HashSet<PlayerMovementStateType>(entry.AllowedTargets);
            }
        }

        public bool CanTransition(PlayerMovementStateType from, PlayerMovementStateType to)
        {
            if (_lookup == null)
                BuildLookup();

            return _lookup.TryGetValue(from, out var targets) && targets.Contains(to);
        }
    }
}
