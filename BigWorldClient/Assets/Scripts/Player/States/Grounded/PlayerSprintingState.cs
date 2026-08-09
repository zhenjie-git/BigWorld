using System;
using UnityEngine;

namespace BigWorldClient
{
    public class PlayerSprintingState : PlayerGroundedState
    {
        private PlayerSprintData sprintData;

        public override PlayerMovementStateType Type => PlayerMovementStateType.Sprinting;
        public override float MovementSpeedModifier => sprintData.SpeedModifier;

        private float startTime;
        private bool keepSprinting;

        public PlayerSprintingState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
            sprintData = PlayerConfig.Instance.GroundedData.SprintData;
        }

        public override void Enter()
        {
            base.Enter();
            Debug.LogError($"Enter {GetType().Name}");
            stateMachine.Controller.ReportMoveStart();

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.CrossFade(animData.SprintAnimationHash, animData.TransitionDuration);

            startTime = Time.time;
            keepSprinting = false;
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void Update()
        {
            base.Update();
            stateMachine.Controller.CheckAndReportDirectionChange();

            if (keepSprinting)
                return;

            if (Time.time < startTime + sprintData.SprintToRunTime)
                return;

            StopSprinting();
        }

        private void StopSprinting()
        {
            if (stateMachine.Controller.MovementInput == Vector2.zero)
            {
                stateMachine.ChangeState(stateMachine.HardStoppingState);
                return;
            }

            stateMachine.ChangeState(stateMachine.RunningState);
        }

        public void OnSprintInput()
        {
            keepSprinting = true;
        }
    }
}
