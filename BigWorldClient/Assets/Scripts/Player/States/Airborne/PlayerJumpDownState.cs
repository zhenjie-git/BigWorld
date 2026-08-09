using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// Jump descent phase, driven by its own JumpDown displacement curves.
    /// Lands on a voxel surface, or falls if the animation ends while airborne.
    /// </summary>
    public class PlayerJumpDownState : PlayerMovementState
    {
        private bool shouldKeepRotating;

        private PlayerJumpData jumpData;
        private DisplacementCurveAsset curveX;
        private DisplacementCurveAsset curveY;
        private DisplacementCurveAsset curveZ;

        public override PlayerMovementStateType Type => PlayerMovementStateType.JumpDown;
        public override float TimeToReachTargetRotationY => jumpData.RotationData.TargetRotationReachTime.y;

        public PlayerJumpDownState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
            jumpData = PlayerConfig.Instance.AirborneData.JumpData;
            curveX = jumpData.JumpDownCurveX;
            curveY = jumpData.JumpDownCurveY;
            curveZ = jumpData.JumpDownCurveZ;
        }

        #region IState Methods

        public override void Enter()
        {
            base.Enter();

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.Play(animData.JumpDownAnimationHash);

            stateMachine.Controller.BeginDisplacementMovement();

            shouldKeepRotating = stateMachine.Controller.MovementInput != Vector2.zero;
            stateMachine.Controller.ResetVelocity();
        }

        public override void Exit()
        {
            base.Exit();
        }

        // Horizontal displacement via the voxel system; vertical is curve-driven with a
        // landing check. Transitions out on land, wrap-around, or animation end.
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

            if (normTime < lastNormTime)
            {
                Debug.Log("[JumpDown] Animation wrapped -> FallingState");
                stateMachine.RecordEnterFallPosition();
                stateMachine.ChangeState(stateMachine.FallingState);
                return;
            }

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

                if (deltaY > 0f)
                    deltaY = 0f;

                float newY = savedY + deltaY;

                if (TryLandOnVoxel(controller, newY, out float landY))
                {
                    pos = rb.position;
                    pos.y = landY;
                    rb.position = pos;
                    Debug.Log($"[JumpDown] Landed at {landY:F4} -> OnGroundContact");
                    stateMachine.OnGroundContact(null);
                    return;
                }

                pos = rb.position;
                pos.y = newY;
                rb.position = pos;
            }

            if (normTime >= 1f)
            {
                Debug.Log("[JumpDown] Animation ended, still airborne -> FallingState");
                stateMachine.RecordEnterFallPosition();
                stateMachine.ChangeState(stateMachine.FallingState);
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

        /// <summary>
        /// True if the character's pivot Y has reached a voxel top surface.
        /// pivotY is RigidBody.position.y (the ground-contact/feet level), not the
        /// capsule center. On success, landY is the surface Y to snap the pivot to,
        /// matching SnapToVoxelSurface / FallingState conventions.
        /// </summary>
        private bool TryLandOnVoxel(PlayerController controller, float pivotY, out float landY)
        {
            landY = 0f;

            Vector3 feetPos = controller.RigidBody.position;
            feetPos.y = pivotY;

            if (!controller.FindCurrentVoxel(feetPos, out int x, out int z, out int k) || k < 0)
                return false;

            float voxelTopY = controller.GetVoxelTopWorldY(x, z, k);

            if (pivotY <= voxelTopY)
            {
                landY = voxelTopY;
                return true;
            }

            return false;
        }
    }
}
