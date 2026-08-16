using UnityEngine;
using BigWorldClient.Network.Protocol;

namespace BigWorldClient
{
    /// <summary>
    /// Generic skill configuration asset. Configure target state, consecutive-use rules,
    /// and cooldown — no need to write a new Skill subclass for each ability.
    /// </summary>
    [CreateAssetMenu(menuName = "Player/Skill Config")]
    public class SkillConfig : ScriptableObject
    {
        [field: Header("Identity")]
        [field: SerializeField]
        public string SkillName { get; private set; } = "New Skill";

        [field: Header("State Transition")]
        [field: SerializeField]
        [field: Tooltip("Which movement state to switch to when this skill is released.")]
        public MoveState TargetState { get; private set; }

        [field: Header("Consecutive Use (连击)")]
        [field: SerializeField]
        [field: Range(1, 10)]
        [field: Tooltip("How many times this skill can be used consecutively before cooldown kicks in.")]
        public int MaxConsecutiveUses { get; private set; } = 1;

        [field: SerializeField]
        [field: Range(0f, 5f)]
        [field: Tooltip("Time window (seconds) within which two uses are considered consecutive. " +
                        "If the player waits longer than this between uses, the counter resets.")]
        public float ConsecutiveTimeWindow { get; private set; } = 1f;

        [field: Header("Cooldown")]
        [field: SerializeField]
        [field: Range(0f, 10f)]
        [field: Tooltip("Cooldown in seconds after reaching MaxConsecutiveUses.")]
        public float Cooldown { get; private set; } = 0f;
    }
}