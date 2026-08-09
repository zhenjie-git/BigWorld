using UnityEngine;

namespace BigWorldClient
{
    public class PlayerIdlingState : PlayerGroundedState
    {
        public override PlayerMovementStateType Type => PlayerMovementStateType.Idling;
        public PlayerIdlingState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
        }

        #region IState Methods
        public override void Enter()
        {
            base.Enter();
            Debug.LogError($"Enter {GetType().Name}");
            stateMachine.Controller.ReportMoveStart();

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.CrossFade(animData.IdleAnimationHash, animData.TransitionDuration);

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
            {
                return;
            }

            stateMachine.Controller.ResetVelocity();
        }
        #endregion
    }
}
