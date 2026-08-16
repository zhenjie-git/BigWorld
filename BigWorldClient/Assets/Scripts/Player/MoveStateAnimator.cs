using BigWorldClient.Network.Protocol;
using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// 纯表现层：订阅 MoveStateMachine.StateApplied，统一使用 Play 播放/定位动画，
    /// 不做任何状态转换或位移计算。
    /// </summary>
    public sealed class MoveStateAnimator
    {
        private readonly PlayerController _controller;
        private readonly MovePredictor _predictor;
        private PlayerAnimationData _anim;

        public MoveStateAnimator(PlayerController controller, MovePredictor predictor)
        {
            _controller = controller;
            _predictor = predictor;
            _predictor.StateMachine.StateApplied += OnStateApplied;
        }

        public void PlayInitialState()
        {
            PlayerAnimationData anim = Anim;
            if (anim != null && _controller.Animator != null)
                _controller.Animator.Play(anim.IdleAnimationHash, 0, 0f);
        }

        private PlayerAnimationData Anim
        {
            get
            {
                if (_anim == null && _controller.Config != null)
                    _anim = _controller.Config.AnimationData;
                return _anim;
            }
        }

        public void ApplyRenderPose(Vector3 position, float yaw)
        {
            Transform tr = _controller.transform;
            Vector3 pos = position;
            tr.position = pos;
            if (_controller.RigidBody != null)
            {
                _controller.RigidBody.position = pos;
                _controller.RigidBody.velocity = Vector3.zero;
            }

            // 修复旧实现“角色不会转向”：移动朝向直接跟随模拟方向。
            double len = System.Math.Sqrt(
                _predictor.CurrentDirX * _predictor.CurrentDirX +
                _predictor.CurrentDirZ * _predictor.CurrentDirZ);
            if (len > 1e-6)
            {
                Quaternion target = Quaternion.Euler(0f, yaw, 0f);
                tr.rotation = Quaternion.Slerp(tr.rotation, target, 0.35f);
            }
        }

        private void OnStateApplied(MoveState state, double normalizedTime)
        {
            PlayerAnimationData anim = Anim;
            if (anim == null || _controller.Animator == null) return;

            int hash = HashFor(state, anim);
            if (hash == 0) return;

            // 统一使用 Play，不做 CrossFade 融合。
            // 本地新状态 normalizedTime=0；权威恢复携带服务器曲线进度时从对应动画帧继续。
            float norm = normalizedTime > 0.0001 ? (float)normalizedTime : 0f;
            if (state == MoveState.MoveJumpUp || state == MoveState.MoveJumpDown || state == MoveState.MoveIdle)
            _controller.Animator.Play(hash, 0, norm);
        }

        private static int HashFor(MoveState state, PlayerAnimationData anim)
        {
            switch (state)
            {
                case MoveState.MoveIdle: return anim.IdleAnimationHash;
                case MoveState.MoveWalk: return anim.WalkAnimationHash;
                case MoveState.MoveRun: return anim.RunAnimationHash;
                case MoveState.MoveSprint: return anim.SprintAnimationHash;
                case MoveState.MoveDash: return anim.DashAnimationHash;
                case MoveState.MoveRoll: return anim.RollAnimationHash;
                case MoveState.MoveJumpUp: return anim.JumpUpAnimationHash;
                case MoveState.MoveJumpDown: return anim.JumpDownAnimationHash;
                case MoveState.MoveFall: return anim.FallAnimationHash;
                case MoveState.MoveStopLight: return anim.LightStopAnimationHash;
                case MoveState.MoveStopMed: return anim.MediumStopAnimationHash;
                case MoveState.MoveStopHard: return anim.HardStopAnimationHash;
                case MoveState.MoveLandLight: return anim.LightLandAnimationHash;
                default: return 0;
            }
        }
    }
}
