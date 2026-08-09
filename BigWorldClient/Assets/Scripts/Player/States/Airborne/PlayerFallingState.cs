using UnityEngine;

namespace BigWorldClient
{
    public class PlayerFallingState : PlayerMovementState
    {
        private PlayerFallData fallData;
        private float currentFallVelocityY;

        public override PlayerMovementStateType Type => PlayerMovementStateType.Falling;

        public PlayerFallingState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
            fallData = PlayerConfig.Instance.AirborneData.FallData;
        }

        public override void Enter()
        {
            base.Enter();
            Debug.LogError($"Enter {GetType().Name}");

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.CrossFade(animData.FallAnimationHash, animData.TransitionDuration);

            stateMachine.Controller.ResetVerticalVelocity();
            currentFallVelocityY = 0f;
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void PhysicsUpdate()
        {
            base.PhysicsUpdate();

            var controller = stateMachine.Controller;
            var rb = controller.RigidBody;

            float prevVelocityY = currentFallVelocityY;
            Vector3 startPos = rb.position;

            // 1. Accumulate gravity-driven velocity
            currentFallVelocityY -= fallData.Gravity * Time.fixedDeltaTime;
            currentFallVelocityY = Mathf.Max(currentFallVelocityY, -fallData.FallSpeedLimit);

            // 2. Compute desired position for this frame
            Vector3 currentPos = rb.position;
            Vector3 desiredPos = currentPos;
            float deltaY = currentFallVelocityY * Time.fixedDeltaTime;
            desiredPos.y += deltaY;

            Debug.Log($"[Fall] dt={Time.fixedDeltaTime:F4} g={fallData.Gravity} velY {prevVelocityY:F4} -> {currentFallVelocityY:F4} (limit={-fallData.FallSpeedLimit:F2}) deltaY={deltaY:F4} pos.y {startPos.y:F4} -> {desiredPos.y:F4}");

            // 3. Voxel landing check: don't fall through a voxel surface
            if (TryLandOnVoxel(controller, desiredPos, out float landY))
            {
                Debug.Log($"[Fall] Land on voxel: desiredY={desiredPos.y:F4} clampedY={landY:F4} (totalDropThisFrame={(landY - startPos.y):F4})");
                desiredPos.y = landY;
                rb.position = desiredPos;
                stateMachine.OnGroundContact(null);
            }
            else
            {
                rb.position = desiredPos;
            }
        }

        /// <summary>
        /// Check whether the desired position would land on a voxel surface.
        /// </summary>
        private bool TryLandOnVoxel(PlayerController controller, Vector3 desiredPos, out float landY)
        {
            landY = 0f;

            // Only check while falling downward
            if (currentFallVelocityY > 0f)
                return false;

            // Find the closest voxel column under the desired feet position
            if (!controller.FindCurrentVoxel(desiredPos, out int x, out int z, out int k) || k < 0)
                return false;

            float voxelTopY = controller.GetVoxelTopWorldY(x, z, k);

            if (desiredPos.y <= voxelTopY)
            {
                landY = voxelTopY;
                return true;
            }

            return false;
        }
    }
}
