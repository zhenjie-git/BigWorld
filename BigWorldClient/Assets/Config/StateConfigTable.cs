using System;
using System.Collections.Generic;
using BigWorldClient.Network.Protocol;
using Google.FlatBuffers;
using UnityEngine;

namespace BigWorldClient
{

    public sealed class SimCurve
    {
        private readonly CurveMsg _msg;

        public SimCurve(CurveMsg msg)
        {
            _msg = msg;
        }

        public bool IsEmpty => _msg.TLength == 0;

        public double Eval(double t)
        {
            int len = _msg.TLength;
            if (len == 0) return 0;
            if (t <= _msg.T(0)) return _msg.V(0);

            int last = len - 1;
            if (t >= _msg.T(last)) return _msg.V(last);

            for (int i = 1; i < len; i++)
            {
                if (t <= _msg.T(i))
                {
                    double k0t = _msg.T(i - 1);
                    double k1t = _msg.T(i);
                    double frac = (t - k0t) / (k1t - k0t);
                    return _msg.V(i - 1) + frac * (_msg.V(i) - _msg.V(i - 1));
                }
            }
            return _msg.V(last);
        }

        public double AxisDelta(double a, double b)
        {
            if (IsEmpty) return 0;
            double ca = Eval(a);
            if (b <= 1.0)
                return Eval(b) - ca;
            return Eval(1.0) - ca + Eval(b - 1.0) - Eval(0.0);
        }
    }

    public sealed class SimMoveState
    {
        public MoveState State;
        public double DurationSeconds;
        public long TotalTicks;
        public bool Loop;
        public Dictionary<string, SimCurve> Curves = new();

        public bool TryGetCurve(string axis, out SimCurve curve) => Curves.TryGetValue(axis, out curve);
    }

    public sealed class StateConfigTable
    {
        private static StateConfigTable _cached;

        private readonly Dictionary<MoveState, StateConfigEntry> _entries = new();
        private readonly Dictionary<MoveState, SimMoveState> _states = new();

        public static StateConfigTable Instance => _cached ??= Load();

        public string GetAnimationName(MoveState state)
        {
            return _entries.TryGetValue(state, out StateConfigEntry entry) ? entry.AnimationName : null;
        }

        public int GetAnimationHash(MoveState state)
        {
            string name = GetAnimationName(state);
            return string.IsNullOrEmpty(name) ? 0 : Animator.StringToHash(name);
        }

        public bool TryGetState(MoveState state, out SimMoveState entry)
        {
            return _states.TryGetValue(state, out entry);
        }

        static StateConfigTable Load()
        {
            var table = new StateConfigTable();
            TextAsset asset = Resources.Load<TextAsset>("Config/StateConfig");
            if (asset != null)
            {
                StateConfigMsg msg = StateConfigMsg.GetRootAsStateConfigMsg(new ByteBuffer(asset.bytes));
                for (int i = 0; i < msg.EntriesLength; i++)
                {
                    var entry = msg.Entries(i);
                    if (!entry.HasValue || !TryParseStateName(entry.Value.State, out MoveState state)) continue;
                    table._entries[state] = entry.Value;
                    table._states[state] = BuildMoveState(state, entry.Value);
                }
            }

            return table;
        }

        static SimMoveState BuildMoveState(MoveState state, StateConfigEntry entry)
        {
            var moveState = new SimMoveState
            {
                State = state,
                DurationSeconds = entry.DurationSeconds,
                TotalTicks = Math.Max(1, entry.TotalTicks),
                Loop = entry.Loop != 0,
            };

            AddCurve(moveState, "x", entry.CurveX);
            AddCurve(moveState, "y", entry.CurveY);
            AddCurve(moveState, "z", entry.CurveZ);
            return moveState;
        }

        static void AddCurve(SimMoveState moveState, string axis, string resourcesPath)
        {
            if (string.IsNullOrEmpty(resourcesPath)) return;
            TextAsset asset = Resources.Load<TextAsset>(resourcesPath);
            if (asset == null) return;
            CurveMsg msg = CurveMsg.GetRootAsCurveMsg(new ByteBuffer(asset.bytes));
            if (msg.TLength == 0) return;
            moveState.Curves[axis] = new SimCurve(msg);
        }

        static bool TryParseStateName(string name, out MoveState state)
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
