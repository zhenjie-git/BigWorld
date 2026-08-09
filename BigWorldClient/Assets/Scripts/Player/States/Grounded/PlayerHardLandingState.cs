using UnityEngine;

namespace BigWorldClient
{
    public class PlayerHardLandingState : PlayerGroundedState
    {
        public override PlayerMovementStateType Type => PlayerMovementStateType.HardLanding;

        public PlayerHardLandingState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
        }

        public override void Enter()
        {
            base.Enter();
            Debug.LogError($"Enter {GetType().Name}");
            stateMachine.Controller.ReportMoveStart();

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.CrossFade(animData.HardLandAnimationHash, animData.TransitionDuration);

            stateMachine.Controller.ResetVelocity();
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void PhysicsUpdate()
        {
            base.PhysicsUpdate();

            if (!stateMachine.Controller.IsMovingHorizontally())
                return;

            stateMachine.Controller.ResetVelocity();
        }

        public override void OnAnimationTransitionEvent()
        {
            stateMachine.ChangeState(stateMachine.IdlingState);
        }
    }
}
