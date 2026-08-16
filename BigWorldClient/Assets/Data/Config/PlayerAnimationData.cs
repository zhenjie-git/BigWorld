using System;
using UnityEngine;

namespace BigWorldClient
{
    [Serializable]
    public class PlayerAnimationData
    {
        [Header("Crossfade Duration")]
        [SerializeField] private float transitionDuration = 0.1f;

        [Header("Animation State Names")]
        [SerializeField] private string idleAnimationName = "Idle";
        [SerializeField] private string walkAnimationName = "Walk";
        [SerializeField] private string runAnimationName = "Run";
        [SerializeField] private string sprintAnimationName = "Sprint";
        [SerializeField] private string dashAnimationName = "Dash";
        [SerializeField] private string lightStopAnimationName = "LightStop";
        [SerializeField] private string mediumStopAnimationName = "MediumStop";
        [SerializeField] private string hardStopAnimationName = "HardStop";
        [SerializeField] private string lightLandAnimationName = "LightLand";
        [SerializeField] private string hardLandAnimationName = "HardLand";
        [SerializeField] private string rollAnimationName = "Roll";
        [SerializeField] private string jumpUpAnimationName = "JumpUp";
        [SerializeField] private string jumpDownAnimationName = "JumpDown";
        [SerializeField] private string fallAnimationName = "Fall";

        public float TransitionDuration => transitionDuration;

        public string IdleAnimationName => idleAnimationName;
        public string WalkAnimationName => walkAnimationName;
        public string RunAnimationName => runAnimationName;
        public string SprintAnimationName => sprintAnimationName;
        public string DashAnimationName => dashAnimationName;
        public string LightStopAnimationName => lightStopAnimationName;
        public string MediumStopAnimationName => mediumStopAnimationName;
        public string HardStopAnimationName => hardStopAnimationName;
        public string LightLandAnimationName => lightLandAnimationName;
        public string HardLandAnimationName => hardLandAnimationName;
        public string RollAnimationName => rollAnimationName;
        public string JumpUpAnimationName => jumpUpAnimationName;
        public string JumpDownAnimationName => jumpDownAnimationName;
        public string FallAnimationName => fallAnimationName;
  
        public int IdleAnimationHash { get; private set; }
        public int WalkAnimationHash { get; private set; }
        public int RunAnimationHash { get; private set; }
        public int SprintAnimationHash { get; private set; }
        public int DashAnimationHash { get; private set; }
        public int LightStopAnimationHash { get; private set; }
        public int MediumStopAnimationHash { get; private set; }
        public int HardStopAnimationHash { get; private set; }
        public int LightLandAnimationHash { get; private set; }
        public int HardLandAnimationHash { get; private set; }
        public int RollAnimationHash { get; private set; }
        public int JumpUpAnimationHash { get; private set; }
        public int JumpDownAnimationHash { get; private set; }
        public int FallAnimationHash { get; private set; }

        public void Initialize()
        {
            IdleAnimationHash = Animator.StringToHash(idleAnimationName);
            WalkAnimationHash = Animator.StringToHash(walkAnimationName);
            RunAnimationHash = Animator.StringToHash(runAnimationName);
            SprintAnimationHash = Animator.StringToHash(sprintAnimationName);
            DashAnimationHash = Animator.StringToHash(dashAnimationName);
            LightStopAnimationHash = Animator.StringToHash(lightStopAnimationName);
            MediumStopAnimationHash = Animator.StringToHash(mediumStopAnimationName);
            HardStopAnimationHash = Animator.StringToHash(hardStopAnimationName);
            LightLandAnimationHash = Animator.StringToHash(lightLandAnimationName);
            HardLandAnimationHash = Animator.StringToHash(hardLandAnimationName);
            RollAnimationHash = Animator.StringToHash(rollAnimationName);
            JumpUpAnimationHash = Animator.StringToHash(jumpUpAnimationName);
            JumpDownAnimationHash = Animator.StringToHash(jumpDownAnimationName);
            FallAnimationHash = Animator.StringToHash(fallAnimationName);
        }
    }
}
