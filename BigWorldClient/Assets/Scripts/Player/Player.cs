using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// Non-MonoBehaviour Player object created at game start.
    /// Owns the skill list and serves as the central hub for skill operations.
    /// </summary>
    public class Player
    {
        public PlayerController Controller { get; }
        public PlayerMoveMentStateMachine StateMachine { get; }
        public List<Skill> Skills { get; } = new();

        // ── Scene binding ──
        public string SceneId { get; set; }

        // ── Runtime state (moved from PlayerStateReusableData) ──
        public float MovementOnSlopSpeedModifier { get; set; } = 1f;
        private Vector3 currentTargetRotation;
        private Vector3 dampedTargetRotationCurrentVelocity;
        private Vector3 dampedTargetRotationPassedTime;

        public ref Vector3 CurrentTargetRotation => ref currentTargetRotation;
        public ref Vector3 DampedTargetRotationCurrentVelocity => ref dampedTargetRotationCurrentVelocity;
        public ref Vector3 DampedTargetRotationPassedTime => ref dampedTargetRotationPassedTime;

        /// <summary>
        /// Create the player. Skills are built from the supplied <paramref name="skillConfigs"/> —
        /// one <see cref="Skill"/> instance per non-null config.
        /// </summary>
        public Player(PlayerController controller, PlayerMoveMentStateMachine stateMachine,
            List<SkillConfig> skillConfigs)
        {
            Controller = controller;
            StateMachine = stateMachine;

            foreach (var config in skillConfigs)
            {
                if (config != null)
                    Skills.Add(new Skill(this, config));
            }
        }

        /// <summary>
        /// Try to release the skill identified by <paramref name="config"/>.
        /// Returns true if the skill was released.
        /// </summary>
        public bool TryReleaseSkill(SkillConfig config)
        {
            var skill = Skills.FirstOrDefault(s => s.Config == config);
            if (skill != null && skill.CanRelease())
            {
                skill.Release();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Try to release the skill with the given <paramref name="skillName"/>.
        /// Returns true if the skill was released.
        /// </summary>
        public bool TryReleaseSkill(string skillName)
        {
            var skill = Skills.FirstOrDefault(s => s.Name == skillName);
            if (skill != null && skill.CanRelease())
            {
                skill.Release();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get the skill instance for the given config, or null if none exists.
        /// </summary>
        public Skill GetSkill(SkillConfig config)
        {
            return Skills.FirstOrDefault(s => s.Config == config);
        }

        /// <summary>
        /// Get the skill instance with the given name, or null if none exists.
        /// </summary>
        public Skill GetSkill(string skillName)
        {
            return Skills.FirstOrDefault(s => s.Name == skillName);
        }
    }
}
