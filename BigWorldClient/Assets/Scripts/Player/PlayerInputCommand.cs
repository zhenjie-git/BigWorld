using UnityEngine;

namespace BigWorldClient
{
    public enum PlayerInputCommandKind
    {
        MovementStarted,
        MovementPerformed,
        MovementCanceled,
        WalkToggle,
        Sprint,
        Jump,
        Dash,
    }

    public struct PlayerInputCommand
    {
        public PlayerInputCommandKind Kind;
        public Vector2 Movement;
        public long Tick;
    }
}
