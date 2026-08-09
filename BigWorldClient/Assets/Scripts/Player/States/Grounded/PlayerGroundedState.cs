using UnityEngine;

namespace BigWorldClient
{
    public abstract class PlayerGroundedState : PlayerMovementState
    {
        public PlayerGroundedState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
        }

        #region IState Methods
        public override void Enter()
        {
            base.Enter();
            stateMachine.Controller.SnapToVoxelSurface();
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void PhysicsUpdate()
        {
            base.PhysicsUpdate();
        }

        #endregion
    }
}
