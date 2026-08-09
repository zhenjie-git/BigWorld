using UnityEngine;

namespace BigWorldClient
{
    public class PlayerDashingState : PlayerGroundedState
    {
        public PlayerDashData DashData;

        public override PlayerMovementStateType Type => PlayerMovementStateType.Dashing;
        public override float MovementSpeedModifier => 0f;
        public override float TimeToReachTargetRotationY => DashData.RotationData.TargetRotationReachTime.y;

        private float StartTime;

        private float dashDuration;
        public PlayerDashingState(PlayerMoveMentStateMachine playerMoveMentStateMachine) : base(playerMoveMentStateMachine)
        {
            DashData = PlayerConfig.Instance.GroundedData.DashData;
        }

        #region IState Methods
        public override void Enter()
        {
            base.Enter();
            Debug.LogError($"Enter {GetType().Name}");
            stateMachine.Controller.ReportMoveStart();

            var animData = PlayerConfig.Instance.AnimationData;
            stateMachine.Controller.CrossFade(animData.DashAnimationHash, 0);

            StartTime = Time.time;
            stateMachine.Controller.BeginDisplacementMovement();
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void PhysicsUpdate()
        {
            base.PhysicsUpdate();

            stateMachine.Controller.RotateTowardTargetRotation();
        }

        public override void Update()
        {
            base.Update();

            if (stateMachine.Controller.ShouldSkipDisplacementFrame())
            {
                // Animator has not processed CrossFade yet; wait one frame
                return;
            }

            if (dashDuration == 0f)
            {
                var animStateInfo = stateMachine.Controller.Animator.GetCurrentAnimatorStateInfo(0);
                dashDuration = animStateInfo.length > 0f ? animStateInfo.length : 0.5f;
            }

            stateMachine.Controller.ApplyDisplacementRotation();

            float elapsed = Time.time - StartTime;
            float normalizedTime = Mathf.Clamp01(elapsed / dashDuration);

            stateMachine.Controller.ApplyTimedDisplacementMovement(DashData.DisplacementCurveX, DashData.DisplacementCurveZ, normalizedTime);

            if (normalizedTime < 1f)
            {
                return;
            }

            stateMachine.ChangeState(stateMachine.IdlingState);
        }

        public override void OnAnimationTransitionEvent()
        {
            if (stateMachine.Controller.MovementInput == Vector2.zero)
            {
                stateMachine.ChangeState(stateMachine.HardStoppingState);

                return;
            }

            stateMachine.ChangeState(stateMachine.SprintingState);
        }
        #endregion

    }
}
