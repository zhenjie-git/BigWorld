using UnityEngine;

namespace BigWorldClient
{
    public class PlayerMoveMentStateMachine : StateMachine
    {
        public PlayerController Controller { get; }
        public PlayerMovementState CurrentMovementState => currentState as PlayerMovementState;

        public PlayerIdlingState IdlingState { get; }
        public PlayerDashingState DashingState { get; }
        public PlayerWalkingState WalkingState { get; }
        public PlayerRunningState RunningState { get; }
        public PlayerSprintingState SprintingState { get; }
        public PlayerStoppingState LightStoppingState { get; }
        public PlayerStoppingState MediumStoppingState { get; }
        public PlayerStoppingState HardStoppingState { get; }
        public PlayerLightLandingState LightLandingState { get; }
        public PlayerRollingState RolllingState { get; }
        public PlayerHardLandingState HardLandingState { get; }
        public PlayerJumpUpState JumpUpState { get; }
        public PlayerJumpDownState JumpDownState { get; }
        public PlayerFallingState FallingState { get; }

        public PlayerMovementState ResumeAfterLandingState { get; set; }

        private Vector3 enterFallPosition;

        public void RecordEnterFallPosition()
        {
            enterFallPosition = Controller.transform.position;
        }

        public override void ChangeState(IState newState)
        {
            if (transitionTable != null && currentState != null && newState != null
                && !transitionTable.CanTransition(currentState.Type, newState.Type))
            {
                Debug.LogError($"Invalid transition: {currentState.Type} -> {newState.Type}, forcing to IdlingState");
                base.ChangeState(IdlingState);
                return;
            }

            base.ChangeState(newState);
        }

        public void OnGroundContact(Collider collider)
        {
            if (currentState is PlayerFallingState)
            {
                float fallDistance = Mathf.Abs(enterFallPosition.y - Controller.transform.position.y);
                var fallData = PlayerConfig.Instance.AirborneData.FallData;

                if (fallDistance < fallData.MinimumDistanceToBeConsideredHardFall)
                {
                    if (ResumeAfterLandingState != null && Controller.MovementInput != Vector2.zero)
                    {
                        var resumeState = ResumeAfterLandingState;
                        ResumeAfterLandingState = null;
                        ChangeState(resumeState);
                    }
                    else
                    {
                        ChangeState(LightLandingState);
                    }
                }
                else if (Controller.MovementInput == Vector2.zero)
                {
                    ResumeAfterLandingState = null;
                    ChangeState(HardLandingState);
                }
                else
                {
                    ChangeState(RolllingState);
                }
            }
            else if (currentState is PlayerJumpDownState)
            {
                if (ResumeAfterLandingState != null && Controller.MovementInput != Vector2.zero)
                {
                    var resumeState = ResumeAfterLandingState;
                    ResumeAfterLandingState = null;
                    ChangeState(resumeState);
                }
                else
                {
                    ChangeState(IdlingState);
                }
            }
        }

        public void OnGroundContactExited(Collider collider)
        {
            if (!(currentState is PlayerGroundedState))
                return;

            // If voxel system already triggered a fall, don't double-trigger via physics
            if (currentState is PlayerFallingState)
                return;

            if (Controller.IsThereGroundUnderneath())
                return;

            if (!Controller.IsGroundBelow(PlayerConfig.Instance.GroundedData.GroundToFallRayDistance))
            {
                if (currentState is PlayerWalkingState
                    || currentState is PlayerRunningState
                    || currentState is PlayerSprintingState)
                    ResumeAfterLandingState = CurrentMovementState;
                else
                    ResumeAfterLandingState = null;

                RecordEnterFallPosition();
                ChangeState(FallingState);
            }
        }

        public void OnJumpInput()
        {
            if (currentState is PlayerGroundedState && currentState is not PlayerHardLandingState && currentState is not PlayerDashingState)
            {
                if (currentState is PlayerWalkingState
                    || currentState is PlayerRunningState
                    || currentState is PlayerSprintingState)
                    ResumeAfterLandingState = CurrentMovementState;
                else
                    ResumeAfterLandingState = null;

                ChangeState(JumpUpState);
            }
        }

        public void StartMove()
        {
            ChangeState(WalkingState);
        }

        public void OnSprintInput()
        {
            if (currentState is PlayerSprintingState sprintingState)
                sprintingState.OnSprintInput();
        }

        public void OnWalkToggleInput()
        {
            if (currentState is PlayerWalkingState)
                ChangeState(RunningState);
        }

        public void OnMovementStartedInput()
        {
            if ((currentState is PlayerIdlingState
                || currentState is PlayerLightLandingState
                || currentState is PlayerRollingState
                || currentState is PlayerStoppingState)
                && currentState is not PlayerHardLandingState)
            {
                StartMove();
                return;
            }
        }

        public void OnMovementPerformedInput()
        {

        }

        public void OnMovementCanceledInput()
        {
            if (currentState is PlayerSprintingState)
            {
                ChangeState(HardStoppingState);
                return;
            }

            if (currentState is PlayerWalkingState)
            {
                ChangeState(LightStoppingState);
                return;
            }

            if (currentState is PlayerRunningState)
            {
                ChangeState(MediumStoppingState);
                return;
            }
        }

        public PlayerMoveMentStateMachine(PlayerController player)
        {
            Controller = player;
            IdlingState = new PlayerIdlingState(this);
            DashingState = new PlayerDashingState(this);
            WalkingState = new PlayerWalkingState(this);
            RunningState = new PlayerRunningState(this);
            SprintingState = new PlayerSprintingState(this);
            LightStoppingState = new PlayerStoppingState(this, StoppingIntensity.Light);
            MediumStoppingState = new PlayerStoppingState(this, StoppingIntensity.Medium);
            HardStoppingState = new PlayerStoppingState(this, StoppingIntensity.Hard);
            LightLandingState = new PlayerLightLandingState(this);
            RolllingState = new PlayerRollingState(this);
            HardLandingState = new PlayerHardLandingState(this);
            JumpUpState = new PlayerJumpUpState(this);
            JumpDownState = new PlayerJumpDownState(this);
            FallingState = new PlayerFallingState(this);
        }

        /// <summary>
        /// Resolve a <see cref="PlayerMovementStateType"/> enum value to its concrete state instance.
        /// Used by the generic <see cref="Skill"/> to transition without hardcoding state references.
        /// </summary>
        public IState GetStateByType(PlayerMovementStateType type)
        {
            return type switch
            {
                PlayerMovementStateType.Idling => IdlingState,
                PlayerMovementStateType.Walking => WalkingState,
                PlayerMovementStateType.Running => RunningState,
                PlayerMovementStateType.Sprinting => SprintingState,
                PlayerMovementStateType.LightStopping => LightStoppingState,
                PlayerMovementStateType.MediumStopping => MediumStoppingState,
                PlayerMovementStateType.HardStopping => HardStoppingState,
                PlayerMovementStateType.LightLanding => LightLandingState,
                PlayerMovementStateType.HardLanding => HardLandingState,
                PlayerMovementStateType.Rolling => RolllingState,
                PlayerMovementStateType.Dashing => DashingState,
                PlayerMovementStateType.JumpUp => JumpUpState,
                PlayerMovementStateType.Falling => FallingState,
                PlayerMovementStateType.JumpDown => JumpDownState,
                _ => null,
            };
        }
    }
}
