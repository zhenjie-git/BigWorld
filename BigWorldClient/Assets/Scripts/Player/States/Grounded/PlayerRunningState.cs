using System;
using UnityEngine;

namespace BigWorldClient
{
    public class PlayerRunningState : PlayerGroundedState
    {
        private PlayerSprintData sprintData;
        private PlayerRunData runData;

        public override PlayerMovementStateType Type => PlayerMovementStateType.Running;
        public override float MovementSpeedModifier => runData.SpeedModifier;

        public PlayerRunningState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
            sprintData = PlayerConfig.Instance.GroundedData.SprintData;
            runData = PlayerConfig.Instance.GroundedData.RunData;
        }

        public override void Enter()
        {
            base.Enter();
            Debug.LogError($"Enter {GetType().Name}");
            stateMachine.Controller.ReportMoveStart();

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.CrossFade(animData.RunAnimationHash, 0f);

            stateMachine.Controller.BeginDisplacementMovement();
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void PhysicsUpdate()
        {
            // Displacement curves drive movement via ApplyCyclicDisplacementMovement in Update().
        }

        public override void Update()
        {
            base.Update();
            stateMachine.Controller.CheckAndReportDirectionChange();
            stateMachine.Controller.ApplyCyclicDisplacementMovement(runData.DisplacementCurveX, runData.DisplacementCurveZ);
        }

        private void StopRunning()
        {
            if (stateMachine.Controller.MovementInput == Vector2.zero)
            {
                stateMachine.ChangeState(stateMachine.IdlingState);
                return;
            }

            stateMachine.ChangeState(stateMachine.WalkingState);
        }
    }
}
