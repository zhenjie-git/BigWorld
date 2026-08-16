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
        public List<Skill> Skills { get; } = new();

        // ── Scene binding ──
        public string SceneId { get; set; }

        // ── Runtime rotation target (input/camera direction, not animation) ──
        private Vector3 currentTargetRotation;

        public ref Vector3 CurrentTargetRotation => ref currentTargetRotation;

        public Player(PlayerController controller, List<SkillConfig> skillConfigs)
        {
            Controller = controller;

            foreach (var config in skillConfigs)
            {
                if (config != null)
                    Skills.Add(new Skill(this, config));
            }
        }

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

        public Skill GetSkill(SkillConfig config)
        {
            return Skills.FirstOrDefault(s => s.Config == config);
        }

        public Skill GetSkill(string skillName)
        {
            return Skills.FirstOrDefault(s => s.Name == skillName);
        }
    }
}