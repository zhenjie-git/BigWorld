using UnityEngine;

namespace BigWorldClient
{
    public enum StoppingIntensity
    {
        Light,
        Medium,
        Hard
    }

    public class PlayerStoppingState : PlayerGroundedState
    {
        public StoppingIntensity Intensity { get; }

        public override PlayerMovementStateType Type => Intensity switch
        {
            StoppingIntensity.Light => PlayerMovementStateType.LightStopping,
            StoppingIntensity.Medium => PlayerMovementStateType.MediumStopping,
            _ => PlayerMovementStateType.HardStopping
        };

        public override float MovementSpeedModifier => 0f;

        private int animationHash;

        private readonly DisplacementCurveAsset displacementCurveX;
        private readonly DisplacementCurveAsset displacementCurveZ;

        private float startTime;
        private float stopDuration;
        private int enterFrameCount;

        public PlayerStoppingState(PlayerMoveMentStateMachine playerMoveMentStateMachine, StoppingIntensity intensity) : base(playerMoveMentStateMachine)
        {
            Intensity = intensity;

            var stopData = PlayerConfig.Instance.GroundedData.StopData;

            animationHash = intensity switch
            {
                StoppingIntensity.Medium => PlayerConfig.Instance.AnimationData.MediumStopAnimationHash,
                StoppingIntensity.Hard => PlayerConfig.Instance.AnimationData.HardStopAnimationHash,
                _ => PlayerConfig.Instance.AnimationData.LightStopAnimationHash
            };

            switch (intensity)
            {
                case StoppingIntensity.Light:
                    displacementCurveX = stopData.LightDisplacementCurveX;
                    displacementCurveZ = stopData.LightDisplacementCurveZ;
                    break;
                case StoppingIntensity.Medium:
                    displacementCurveX = stopData.MediumDisplacementCurveX;
                    displacementCurveZ = stopData.MediumDisplacementCurveZ;
                    break;
                default:
                    displacementCurveX = stopData.HardDisplacementCurveX;
                    displacementCurveZ = stopData.HardDisplacementCurveZ;
                    break;
            }
        }

        public override void Enter()
        {
            base.Enter();
            Debug.LogError($"Enter {GetType().Name}, Intensity: {Intensity}");
            stateMachine.Controller.ReportMoveStart();

            stateMachine.Controller.CrossFade(animationHash, PlayerConfig.Instance.AnimationData.TransitionDuration);

            startTime = Time.time;
            stopDuration = 0f;
            enterFrameCount = Time.frameCount;

            stateMachine.Controller.BeginDisplacementMovement();
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void PhysicsUpdate()
        {
            // Displacement curves drive movement via ApplyTimedDisplacementMovement in Update().
        }

        public override void Update()
        {
            base.Update();

            if (Time.frameCount == enterFrameCount)
            {
                // Animator has not processed CrossFade yet; wait one frame
                return;
            }

            if (displacementCurveX == null && displacementCurveZ == null)
            {
                return;
            }
            stateMachine.Controller.RotateTowardTargetRotation();
            if (stopDuration == 0f)
            {
                var animStateInfo = stateMachine.Controller.Animator.GetCurrentAnimatorStateInfo(0);
                stopDuration = animStateInfo.length > 0f ? animStateInfo.length : 0.5f;
            }

            float elapsed = Time.time - startTime;
            float normalizedTime = Mathf.Clamp01(elapsed / stopDuration);

            stateMachine.Controller.ApplyTimedDisplacementMovement(displacementCurveX, displacementCurveZ, normalizedTime);
        }

        public override void OnAnimationTransitionEvent()
        {
            stateMachine.ChangeState(stateMachine.IdlingState);
        }
    }
}
