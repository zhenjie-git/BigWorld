using UnityEngine;

namespace BigWorldClient
{
    public class PlayerRollingState : PlayerGroundedState
    {
        private PlayerRollData rollData;

        public override PlayerMovementStateType Type => PlayerMovementStateType.Rolling;
        public override float MovementSpeedModifier => rollData.SpeedModifier;

        public PlayerRollingState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
            rollData = PlayerConfig.Instance.GroundedData.RolllData;
        }

        public override void Enter()
        {
            base.Enter();
            Debug.LogError($"Enter {GetType().Name}");
            stateMachine.Controller.ReportMoveStart();

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.CrossFade(animData.RollAnimationHash, animData.TransitionDuration);
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void PhysicsUpdate()
        {
            base.PhysicsUpdate();

            if (stateMachine.Controller.MovementInput != Vector2.zero)
                return;

            stateMachine.Controller.RotateTowardTargetRotation();
        }

        public override void OnAnimationEnterEvent()
        {
            if (stateMachine.Controller.MovementInput == Vector2.zero)
            {
                stateMachine.ChangeState(stateMachine.MediumStoppingState);
                return;
            }

            if (stateMachine.ResumeAfterLandingState != null)
            {
                var resumeState = stateMachine.ResumeAfterLandingState;
                stateMachine.ResumeAfterLandingState = null;
                stateMachine.ChangeState(resumeState);
                return;
            }

            stateMachine.StartMove();
        }
    }
}
