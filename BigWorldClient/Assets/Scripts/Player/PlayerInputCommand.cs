using UnityEngine;

namespace BigWorldClient
{
    public enum PlayerInputCommandKind
    {
        MovementStarted,
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
