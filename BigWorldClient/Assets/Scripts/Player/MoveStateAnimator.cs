using BigWorldClient.Network.Protocol;
using UnityEngine;

namespace BigWorldClient
{

    public sealed class MoveStateAnimator
    {
        private readonly PlayerController _controller;
        private readonly MovePredictor _predictor;
        private StateConfigTable _anim;

        public MoveStateAnimator(PlayerController controller, MovePredictor predictor)
        {
            _controller = controller;
            _predictor = predictor;
            _predictor.StateMachine.StateApplied += OnStateApplied;
        }

        public void PlayInitialState()
        {
            StateConfigTable anim = Anim;
            if (anim != null && _controller.Animator != null)
                _controller.Animator.Play(anim.GetAnimationHash(MoveState.MoveIdle), 0, 0f);
        }

        private StateConfigTable Anim
        {
            get
            {
                return _anim ??= StateConfigTable.Instance;
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
            StateConfigTable anim = Anim;
            if (anim == null || _controller.Animator == null) return;

            int hash = anim.GetAnimationHash(state);
            if (hash == 0) return;

            float norm = normalizedTime > 0.0001 ? (float)normalizedTime : 0f;
            _controller.Animator.Play(hash, 0, norm);
        }
    }
}
