using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// Generic player skill driven by a <see cref="SkillConfig"/> ScriptableObject.
    /// Handles consecutive-use tracking, cooldown, and state transition automatically —
    /// no need to subclass for each new skill.
    ///
    /// If a skill truly needs custom logic beyond what the config provides, override
    /// <see cref="CanRelease"/> and / or <see cref="Release"/> in a derived class.
    /// </summary>
    public class Skill
    {
        public string Name => Config.SkillName;
        public SkillConfig Config { get; }
        protected Player Player { get; }

        private int _consecutiveUses;
        private float _lastUseTime = float.MinValue;
        private float _lastLimitReachedTime = float.MinValue;

        public Skill(Player player, SkillConfig config)
        {
            Player = player;
            Config = config;
        }

        /// <summary>
        /// Whether the skill can be released right now.
        /// Returns false when the consecutive-use limit has been reached and the
        /// cooldown has not yet elapsed.
        /// </summary>
        public virtual bool CanRelease()
        {
            if (_consecutiveUses >= Config.MaxConsecutiveUses
                && Time.time < _lastLimitReachedTime + Config.Cooldown)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Release the skill. Pre-condition: <see cref="CanRelease"/> must have returned true.
        /// Tracks consecutive usage, resets the counter when the time window expires,
        /// and transitions the state machine to <see cref="SkillConfig.TargetState"/>.
        /// </summary>
        public virtual void Release()
        {
            // Reset consecutive counter if the time window between uses has passed
            if (Time.time > _lastUseTime + Config.ConsecutiveTimeWindow)
            {
                _consecutiveUses = 0;
            }

            _consecutiveUses++;
            _lastUseTime = Time.time;

            if (_consecutiveUses >= Config.MaxConsecutiveUses)
            {
                _lastLimitReachedTime = Time.time;
            }

            // Transition to the configured target state
            var targetState = Player.StateMachine.GetStateByType(Config.TargetState);
            if (targetState != null)
            {
                Player.StateMachine.ChangeState(targetState);
            }
            else
            {
                Debug.LogError(
                    $"Skill '{Name}': target state '{Config.TargetState}' could not be resolved " +
                    "by the state machine.");
            }
        }
    }
}
