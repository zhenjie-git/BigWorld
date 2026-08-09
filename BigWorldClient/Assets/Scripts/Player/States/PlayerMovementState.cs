using UnityEngine;

namespace BigWorldClient
{
    public class PlayerMovementState : IState
    {
        protected PlayerMoveMentStateMachine stateMachine;

        public virtual PlayerMovementStateType Type { get; }
        public virtual float MovementSpeedModifier => 0f;
        public virtual float TimeToReachTargetRotationY => PlayerConfig.Instance.GroundedData.BaseRotationData.TargetRotationReachTime.y;
        public PlayerMovementState(PlayerMoveMentStateMachine playerMoveMentStateMachine)
        {
            stateMachine = playerMoveMentStateMachine;
        }

        public virtual void Enter()
        {

        }

        public virtual void Exit()
        {

        }

        public virtual void PhysicsUpdate()
        {
            stateMachine.Controller.Move();
        }

        public virtual void Update()
        {
        }

        public virtual void OnAnimationEnterEvent() { }
        public virtual void OnAnimationExitEvent() { }
        public virtual void OnAnimationTransitionEvent() { }

    }
}
