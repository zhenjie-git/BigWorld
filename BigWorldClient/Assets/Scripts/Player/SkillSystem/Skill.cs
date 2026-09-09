using UnityEngine;

namespace BigWorldClient
{

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

        public virtual bool CanRelease()
        {
            if (_consecutiveUses >= Config.MaxConsecutiveUses
                && Time.time < _lastLimitReachedTime + Config.Cooldown)
            {
                return false;
            }

            return true;
        }

        public virtual void Release()
        {

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

            var predictor = Player.Controller.Predictor;
            if (predictor == null || !predictor.Ready) return;

            Vector3 dir = Player.Controller.GetWorldMoveDirection();
            switch (Config.TargetState)
            {
                case BigWorldClient.Network.Protocol.MoveState.MoveDash:
                    predictor.EnqueueStart(BigWorldClient.Network.Protocol.MoveState.MoveDash, dir.x, dir.z);
                    break;
                case BigWorldClient.Network.Protocol.MoveState.MoveJumpUp:
                    predictor.EnqueueStart(BigWorldClient.Network.Protocol.MoveState.MoveJumpUp, dir.x, dir.z);
                    break;
                case BigWorldClient.Network.Protocol.MoveState.MoveRoll:
                    predictor.EnqueueStart(BigWorldClient.Network.Protocol.MoveState.MoveRoll, dir.x, dir.z);
                    break;
                default:
                    break;
            }
        }
    }
}
