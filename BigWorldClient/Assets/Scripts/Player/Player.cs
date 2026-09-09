using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BigWorldClient
{

    public class Player
    {
        public PlayerController Controller { get; }
        public List<Skill> Skills { get; } = new();

        public string SceneId { get; set; }

        private Vector3 _currentTargetRotation;

        public ref Vector3 CurrentTargetRotation => ref _currentTargetRotation;

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
