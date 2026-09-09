using BigWorldClient.Network.Protocol;

namespace BigWorldClient
{
    public static class MoveStateMappings
    {
        public static readonly (string Name, MoveState State)[] Mappings =
        {
            ("Idling", MoveState.MoveIdle),
            ("Walking", MoveState.MoveWalk),
            ("Running", MoveState.MoveRun),
            ("Sprinting", MoveState.MoveSprint),
            ("LightStopping", MoveState.MoveStopLight),
            ("MediumStopping", MoveState.MoveStopMed),
            ("HardStopping", MoveState.MoveStopHard),
            ("LightLanding", MoveState.MoveLandLight),
            ("Rolling", MoveState.MoveRoll),
            ("Dashing", MoveState.MoveDash),
            ("JumpUp", MoveState.MoveJumpUp),
            ("Falling", MoveState.MoveFall),
            ("JumpDown", MoveState.MoveJumpDown),
        };

        public static readonly string[] Names =
        {
            "Idling",
            "Walking",
            "Running",
            "Sprinting",
            "LightStopping",
            "MediumStopping",
            "HardStopping",
            "LightLanding",
            "Rolling",
            "Dashing",
            "JumpUp",
            "Falling",
            "JumpDown",
        };
    }
}
