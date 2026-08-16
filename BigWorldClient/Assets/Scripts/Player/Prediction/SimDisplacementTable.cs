using System;
using System.Collections.Generic;
using BigWorldClient.Network;
using BigWorldClient.Network.Protocol;
using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// 线性插值位移曲线。键点时间/数值来自 DisplacementCurveAsset 的 AnimationCurve，
    /// 求值算法与服务器 EvalCurve/AxisDelta 完全一致（曲线只取键点，不取切线）。
    /// </summary>
    public sealed class SimCurve
    {
        private readonly double[] _times;
        private readonly double[] _values;

        public SimCurve(AnimationCurve curve)
        {
            if (curve == null || curve.keys == null || curve.keys.Length == 0)
            {
                _times = Array.Empty<double>();
                _values = Array.Empty<double>();
                return;
            }

            var keys = curve.keys;
            _times = new double[keys.Length];
            _values = new double[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                _times[i] = keys[i].time;
                _values[i] = keys[i].value;
            }
        }

        public bool IsEmpty => _times.Length == 0;

        public double Eval(double t)
        {
            if (_times.Length == 0) return 0;
            if (t <= _times[0]) return _values[0];

            int last = _times.Length - 1;
            if (t >= _times[last]) return _values[last];

            for (int i = 1; i < _times.Length; i++)
            {
                if (t <= _times[i])
                {
                    double k0t = _times[i - 1];
                    double k1t = _times[i];
                    double frac = (t - k0t) / (k1t - k0t);
                    return _values[i - 1] + frac * (_values[i] - _values[i - 1]);
                }
            }
            return _values[last];
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

    /// <summary>
    /// 客户端移动状态表。曲线引用沿用 PlayerConfig 的 DisplacementCurveAsset，
    /// 时长从 Resources 里的 AnimationClip.length 推导（与服务器导出器读取
    /// m_StopTime 同一来源），总 tick 数统一为 ceil(duration / TickMs)。
    /// </summary>
    public sealed class SimDisplacementTable
    {
        private readonly Dictionary<MoveState, SimMoveState> _states = new();

        public bool TryGetState(MoveState state, out SimMoveState entry) => _states.TryGetValue(state, out entry);

        public static SimDisplacementTable Build(PlayerConfig cfg)
        {
            var table = new SimDisplacementTable();
            if (cfg == null || cfg.AnimationData == null) return table;

            var anim = cfg.AnimationData;
            var grounded = cfg.GroundedData;

            if (grounded != null)
            {
                AddState(table, MoveState.MoveWalk, true,
                    grounded.WalkData?.DisplacementCurveX, grounded.WalkData?.DisplacementCurveZ, null,
                    anim.WalkAnimationName);
                AddState(table, MoveState.MoveRun, true,
                    grounded.RunData?.DisplacementCurveX, grounded.RunData?.DisplacementCurveZ, null,
                    anim.RunAnimationName);
                AddState(table, MoveState.MoveDash, false,
                    grounded.DashData?.DisplacementCurveX, grounded.DashData?.DisplacementCurveZ, null,
                    anim.DashAnimationName);
                AddState(table, MoveState.MoveStopLight, false,
                    grounded.StopData?.LightDisplacementCurveX, grounded.StopData?.LightDisplacementCurveZ, null,
                    anim.LightStopAnimationName);
                AddState(table, MoveState.MoveStopMed, false,
                    grounded.StopData?.MediumDisplacementCurveX, grounded.StopData?.MediumDisplacementCurveZ, null,
                    anim.MediumStopAnimationName);
                AddState(table, MoveState.MoveStopHard, false, null, null, null, anim.HardStopAnimationName);
                AddState(table, MoveState.MoveRoll, false, null, null, null, anim.RollAnimationName);
            }

            if (cfg.AirborneData?.JumpData != null)
            {
                var jump = cfg.AirborneData.JumpData;
                AddState(table, MoveState.MoveJumpUp, false,
                    jump.JumpUpCurveX, jump.JumpUpCurveZ, jump.JumpUpCurveY, anim.JumpUpAnimationName);
                AddState(table, MoveState.MoveJumpDown, false,
                    jump.JumpDownCurveX, jump.JumpDownCurveZ, jump.JumpDownCurveY, anim.JumpDownAnimationName);
            }

            return table;
        }

        private static void AddState(SimDisplacementTable table, MoveState state, bool loop,
            DisplacementCurveAsset curveX, DisplacementCurveAsset curveZ, DisplacementCurveAsset curveY,
            string animationName)
        {
            double duration = LoadAnimationDuration(animationName);
            var entry = new SimMoveState
            {
                State = state,
                DurationSeconds = duration,
                TotalTicks = Math.Max(1, (long)Math.Ceiling(duration * 1000.0 / ServerClock.TickMs)),
                Loop = loop,
            };

            AddCurve(entry, "x", curveX);
            AddCurve(entry, "z", curveZ);
            AddCurve(entry, "y", curveY);
            table._states[state] = entry;
        }

        private static void AddCurve(SimMoveState entry, string axis, DisplacementCurveAsset asset)
        {
            if (asset == null || asset.curve == null) return;
            entry.Curves[axis] = new SimCurve(asset.curve);
        }

        private static double LoadAnimationDuration(string animationName)
        {
            if (!string.IsNullOrEmpty(animationName))
            {
                var clip = Resources.Load<AnimationClip>($"Animations/Player/{animationName}");
                if (clip != null) return clip.length;
            }

            // 兜底：与 Resources/Animations/Player 里 .anim 的 m_StopTime 一致，
            // 防止动画名配置差异导致 TotalTicks 变成 1（Roll/HardStop 立刻结束）。
            switch (animationName)
            {
                case "Idle": return 8.333334;
                case "Walk": return 1.0333334;
                case "Run": return 0.6333334;
                case "Sprint": return 0.70000005;
                case "Dash": return 0.8000001;
                case "LightStop": return 3.0000002;
                case "MediumStop": return 3.0000002;
                case "HardStop": return 0.90000004;
                case "LightLand": return 0.8333334;
                case "HardLand": return 2.5000002;
                case "Roll":
                case "Rolling": return 2.1333334;
                case "JumpUp": return 0.8333334;
                case "JumpDown": return 1.0666667;
                case "Fall": return 0.70000005;
                default: return 0;
            }
        }
    }
}
