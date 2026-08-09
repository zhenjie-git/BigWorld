using UnityEngine;

namespace BigWorldClient
{
    public class PlayerLightLandingState : PlayerGroundedState
    {
        public override PlayerMovementStateType Type => PlayerMovementStateType.LightLanding;

        public PlayerLightLandingState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
        }

        #region IState Method
        public override void Enter()
        {
            base.Enter();
            Debug.LogError($"Enter {GetType().Name}");
            stateMachine.Controller.ReportMoveStart();

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.CrossFade(animData.LightLandAnimationHash, animData.TransitionDuration);

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
        //todo
        public override void OnAnimationTransitionEvent()
        {
            stateMachine.ChangeState(stateMachine.IdlingState);
        }
        #endregion
    }
}
