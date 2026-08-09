using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// Jump ascent phase, driven entirely by the Jump displacement curves.
    /// Transitions to JumpDown on ceiling hit or animation end.
    /// </summary>
    public class PlayerJumpUpState : PlayerMovementState
    {
        private bool shouldKeepRotating;

        private PlayerJumpData jumpData;
        private DisplacementCurveAsset curveX;
        private DisplacementCurveAsset curveY;
        private DisplacementCurveAsset curveZ;

        public override PlayerMovementStateType Type => PlayerMovementStateType.JumpUp;
        public override float TimeToReachTargetRotationY => jumpData.RotationData.TargetRotationReachTime.y;

        public PlayerJumpUpState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
            jumpData = PlayerConfig.Instance.AirborneData.JumpData;
            curveX = jumpData.JumpUpCurveX;
            curveY = jumpData.JumpUpCurveY;
            curveZ = jumpData.JumpUpCurveZ;
        }

        #region IState Methods

        public override void Enter()
        {
            base.Enter();
            stateMachine.Controller.ReportMoveStart();

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.Play(animData.JumpUpAnimationHash);

            stateMachine.Controller.BeginDisplacementMovement();
            stateMachine.Controller.JumpStartPosition = stateMachine.Controller.RigidBody.position;

            shouldKeepRotating = stateMachine.Controller.MovementInput != Vector2.zero;
            stateMachine.Controller.ResetVelocity();
        }

        public override void Exit()
        {
            base.Exit();
        }

        // Horizontal displacement via the voxel system; vertical is curve-driven with a
        // ceiling check. Both stay in sync with the animation's normalizedTime.
        public override void Update()
        {
            base.Update();

            var controller = stateMachine.Controller;
            var rb = controller.RigidBody;

            if (controller.ShouldSkipDisplacementFrame())
                return;

            var animStateInfo = controller.Animator.GetCurrentAnimatorStateInfo(0);
            float normTime = Mathf.Clamp01(animStateInfo.normalizedTime);

            float lastNormTime = controller.DisplacementLastNormTime;

            float savedY = rb.position.y;
            controller.ApplyTimedDisplacementMovement(curveX, curveZ, normTime);

            Vector3 pos = rb.position;
            pos.y = savedY;
            rb.position = pos;

            if (curveY != null)
            {
                float prevCurveY = curveY.curve.Evaluate(lastNormTime);
                float curCurveY = curveY.curve.Evaluate(normTime);
                float deltaY = curCurveY - prevCurveY;

                float newY = savedY + deltaY;
                float halfHeight = controller.ColliderVerticalExtents.y;
                float centerY = controller.ColliderCenterInLocalSpace.y;
                float headTopY = newY + centerY + halfHeight;
                float feetY = newY + centerY - halfHeight;

                if (controller.CheckCeilingVoxel(feetY, headTopY, out float ceilingY))
                {
                    float clampedY = ceilingY - centerY - halfHeight;
                    pos = rb.position;
                    pos.y = clampedY;
                    rb.position = pos;
                    Debug.Log($"[JumpUp] Ceiling hit at {ceilingY:F4} -> JumpDown");
                    stateMachine.ChangeState(stateMachine.JumpDownState);
                    return;
                }

                pos = rb.position;
                pos.y = newY;
                rb.position = pos;
            }

            if (normTime >= 1f)
            {
                Debug.Log("[JumpUp] Animation ended -> JumpDown");
                stateMachine.ChangeState(stateMachine.JumpDownState);
                return;
            }

            if (shouldKeepRotating)
                controller.RotateTowardTargetRotation();
        }

        // Movement is driven in Update() to stay in sync with animation frames.
        public override void PhysicsUpdate()
        {
        }

        #endregion
    }
}
