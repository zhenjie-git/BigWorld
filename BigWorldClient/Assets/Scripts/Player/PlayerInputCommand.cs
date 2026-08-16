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

    /// <summary>PlayerInput 只收集原始输入，所有意图解释都交给 MovePredictor。</summary>
    public struct PlayerInputCommand
    {
        public PlayerInputCommandKind Kind;
        public Vector2 Movement;
        public long Tick;
    }
}
