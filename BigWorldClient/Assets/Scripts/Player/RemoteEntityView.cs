using System.Collections.Generic;
using BigWorldClient.Network;
using BigWorldClient.Network.Protocol;
using UnityEngine;

namespace BigWorldClient
{
    public class RemoteEntityView : MonoBehaviour
    {
        public bool Ready { get; private set; }

        SimContext _ctx;
        ServerClock _clock;
        MoveStateMachine _machine;
        MoveSimEntity _cur;
        MoveSimEntity _prev;
        long _simTick;
        readonly List<PendingMoveEvent> _pending = new();
        Animator _animator;
        TextMesh _label;

        public void Init(in RemoteEntityEvent enter, SimContext ctx, ServerClock clock)
        {
            _ctx = ctx;
            _clock = clock;
            _animator = GetComponentInChildren<Animator>();
            _machine = new MoveStateMachine(MoveTransitionTable.Instance);
            _machine.StateApplied += OnStateApplied;
            RestoreFrom(in enter);
            _simTick = enter.SimTick > 0 ? enter.SimTick : clock.TickNow();
            _prev = _cur;
            transform.position = new Vector3((float)enter.X, (float)enter.Y, (float)enter.Z);
            OnStateApplied(_cur.State, _cur.CurveNorm);
            Ready = true;
        }

        public void SetLabel(string text)
        {
            if (_label != null)
            {
                _label.text = text;
                return;
            }
            var labelGo = new GameObject("NameLabel");
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 2.3f, 0f);
            _label = labelGo.AddComponent<TextMesh>();
            _label.text = text;
            _label.fontSize = 48;
            _label.characterSize = 0.09f;
            _label.anchor = TextAnchor.MiddleCenter;
            _label.alignment = TextAlignment.Center;
            _label.color = new Color(1f, 0.92f, 0.6f, 0.95f);
        }

        public void ApplyMoveEvent(in RemoteEntityEvent evt)
        {
            if (!Ready) return;
            const uint ServerKindStart = 0, ServerKindHalt = 1, ServerKindDirChange = 2;
            PredictedEventKind kind;
            switch (evt.EventKind)
            {
                case ServerKindStart: kind = PredictedEventKind.StartState; break;
                case ServerKindHalt: kind = PredictedEventKind.Halt; break;
                case ServerKindDirChange: kind = PredictedEventKind.DirChange; break;
                default: return;
            }
            long tick = evt.ServerTimeMs > 0 ? evt.ServerTimeMs / ServerClock.TickMs : _clock.TickNow();
            var pending = new PendingMoveEvent
            {
                Seq = (ulong)_pending.Count,
                Tick = tick,
                Kind = kind,
                State = evt.State,
                DirX = evt.DirX,
                DirZ = evt.DirZ,
            };
            _pending.Add(pending);
            if (tick <= _simTick)
                ApplyEvent(pending);
        }

        public void ApplySnapshot(in RemoteEntityEvent evt)
        {
            if (!Ready) return;
            RestoreFrom(in evt);
            long snapTick = evt.SimTick > 0 ? evt.SimTick : _simTick;
            _simTick = snapTick;
            _pending.RemoveAll(ev => ev.Tick <= snapTick);
            ReplayPending(_clock.TickNow());
            _prev = _cur;
        }

        void RestoreFrom(in RemoteEntityEvent s)
        {
            _cur = new MoveSimEntity
            {
                X = s.X,
                Y = s.Y,
                Z = s.Z,
                VoxelK = s.VoxelK,
                Airborne = s.Airborne,
                State = s.State,
                CurveNorm = s.CurveNorm,
                StateStartMs = s.StateStartMs,
                FallVelY = s.FallVelY,
                MoveDirX = s.DirX,
                MoveDirZ = s.DirZ,
            };
            _machine?.ApplyAuthoritative(_cur.State, _cur.CurveNorm);
        }

        void ApplyEvent(in PendingMoveEvent ev)
        {
            switch (ev.Kind)
            {
                case PredictedEventKind.StartState:
                    if (ev.DirX != 0 || ev.DirZ != 0)
                        MovementSim.SetMoveDir(ref _cur, ev.DirX, ev.DirZ);
                    _machine.TryChangeState(ref _cur, ev.State, ev.Tick);
                    break;
                case PredictedEventKind.DirChange:
                    MovementSim.SetMoveDir(ref _cur, ev.DirX, ev.DirZ);
                    break;
                case PredictedEventKind.Halt:
                    _machine.ForceChangeState(ref _cur, MoveState.MoveIdle, ev.Tick);
                    break;
            }
        }

        void ReplayPending(long limit)
        {
            _pending.Sort((a, b) =>
            {
                int byTick = a.Tick.CompareTo(b.Tick);
                return byTick != 0 ? byTick : a.Seq.CompareTo(b.Seq);
            });
            for (int i = 0; i < _pending.Count; i++)
            {
                PendingMoveEvent ev = _pending[i];
                if (ev.Tick <= _simTick)
                {
                    ApplyEvent(ev);
                    continue;
                }
                if (ev.Tick > limit) break;
                StepTo(ev.Tick);
                ApplyEvent(ev);
            }
            if (limit > _simTick)
                StepTo(limit);
        }

        void StepTo(long target)
        {
            while (_simTick < target)
            {
                _prev = _cur;
                _simTick++;
                _machine.Tick(ref _cur, _ctx, _simTick, ServerClock.TickMs);
            }
        }

        void Update()
        {
            if (!Ready || _clock == null || !_clock.IsSynced) return;
            long target = _clock.TickNow();
            if (target > _simTick)
                StepTo(System.Math.Min(target, _simTick + 8));

            long baseTick = _simTick > 1 ? _simTick - 1 : _simTick;
            double span = (double)(_clock.NowMs() - MovementSim.TickToMs(baseTick)) / ServerClock.TickMs;
            float t = (float)Mathf.Clamp01((float)span);
            Vector3 pos = new Vector3(
                (float)Mathf.Lerp((float)_prev.X, (float)_cur.X, t),
                (float)Mathf.Lerp((float)_prev.Y, (float)_cur.Y, t),
                (float)Mathf.Lerp((float)_prev.Z, (float)_cur.Z, t));
            transform.position = pos;

            double len = System.Math.Sqrt(_cur.MoveDirX * _cur.MoveDirX + _cur.MoveDirZ * _cur.MoveDirZ);
            if (len > 1e-6)
            {
                float yaw = Mathf.Atan2((float)_cur.MoveDirX, (float)_cur.MoveDirZ) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, yaw, 0f), 0.35f);
            }
        }

        void LateUpdate()
        {
            if (_label == null)
                return;
            Camera cam = Camera.main;
            if (cam != null)
                _label.transform.rotation = cam.transform.rotation;
        }

        void OnStateApplied(MoveState state, double normalizedTime)
        {
            StateConfigTable anim = StateConfigTable.Instance;
            if (anim == null || _animator == null) return;
            int hash = anim.GetAnimationHash(state);
            if (hash == 0) return;
            _animator.Play(hash, 0, normalizedTime > 0.0001 ? (float)normalizedTime : 0f);
        }
    }
}
