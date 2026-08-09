using UnityEngine;

namespace BigWorldClient
{
    public class PlayerWalkingState : PlayerGroundedState
    {
        private PlayerWalkData walkData;

        public override PlayerMovementStateType Type => PlayerMovementStateType.Walking;
        public override float MovementSpeedModifier => walkData.SpeedModifier;

        public PlayerWalkingState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
            walkData = PlayerConfig.Instance.GroundedData.WalkData;
        }

        public override void Enter()
        {
            base.Enter();
            Debug.LogError($"Enter {GetType().Name}");
            stateMachine.Controller.ReportMoveStart();

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.CrossFade(animData.WalkAnimationHash, 0f);

            stateMachine.Controller.BeginDisplacementMovement();
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void PhysicsUpdate()
        {
            // Displacement curves drive movement via ApplyCyclicDisplacementMovement in Update().
            // Voxel Y is set by ApplyVoxelMovement within the displacement pipeline.
        }

        public override void Update()
        {
            base.Update();
            stateMachine.Controller.CheckAndReportDirectionChange();
            stateMachine.Controller.ApplyCyclicDisplacementMovement(walkData.DisplacementCurveX, walkData.DisplacementCurveZ);
        }
    }
}
