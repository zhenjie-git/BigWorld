using System.Collections.Generic;
using BigWorldClient.Network.Protocol;
using Google.FlatBuffers;
using UnityEngine;

namespace BigWorldClient
{

    public sealed class PlayerConfigTable
    {
        private static PlayerConfigTable _cached;

        public float SprintToRunTime { get; private set; } = 1f;
        public float FallSpeedLimit { get; private set; } = 15f;
        public float Gravity { get; private set; } = 1f;
        public float ColliderHeight { get; private set; } = 1.8f;
        public float ColliderCenterY { get; private set; } = 0.9f;
        public float ColliderRadius { get; private set; } = 0.2f;
        public float StepHeightPercentage { get; private set; } = 0.25f;
        public float VoxelMaxStepHeight { get; private set; } = 0.5f;

        public List<CameraRecenteringEntry> BackwardsRecenteringData { get; private set; } = new();
        public List<CameraRecenteringEntry> SidewaysRecenteringData { get; private set; } = new();

        public static PlayerConfigTable Instance => _cached ??= Load();

        static PlayerConfigTable Load()
        {
            var table = new PlayerConfigTable();
            TextAsset asset = Resources.Load<TextAsset>("Config/PlayerConfig");
            if (asset != null)
            {
                PlayerConfigMsg msg = PlayerConfigMsg.GetRootAsPlayerConfigMsg(new ByteBuffer(asset.bytes));
                table.SprintToRunTime = msg.SprintToRunTime;
                table.FallSpeedLimit = msg.FallSpeedLimit;
                table.Gravity = msg.Gravity;
                table.ColliderHeight = msg.ColliderHeight;
                table.ColliderCenterY = msg.ColliderCenterY;
                table.ColliderRadius = msg.ColliderRadius;
                table.StepHeightPercentage = msg.StepHeightPercentage;
                table.VoxelMaxStepHeight = msg.VoxelMaxStepHeight;
                var backwards = new List<CameraRecenteringEntry>(msg.CameraBackwardsLength);
                for (int i = 0; i < msg.CameraBackwardsLength; i++)
                {
                    var e = msg.CameraBackwards(i);
                    if (e.HasValue) backwards.Add(e.Value);
                }
                table.BackwardsRecenteringData = backwards;
                var sideways = new List<CameraRecenteringEntry>(msg.CameraSidewaysLength);
                for (int i = 0; i < msg.CameraSidewaysLength; i++)
                {
                    var e = msg.CameraSideways(i);
                    if (e.HasValue) sideways.Add(e.Value);
                }
                table.SidewaysRecenteringData = sideways;
            }

            return table;
        }
    }
}
