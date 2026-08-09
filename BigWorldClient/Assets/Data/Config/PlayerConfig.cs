using UnityEngine;

namespace BigWorldClient
{
    [CreateAssetMenu(fileName = "PlayerConfig", menuName = "Custom/Characters/Player Config")]
    public class PlayerConfig : ScriptableObject
    {
        public static PlayerConfig Instance { get; private set; }

        [field: Header("Movement")]
        [field: SerializeField] public PlayerGroundedData GroundedData { get; private set; }
        [field: SerializeField] public PlayerAirborneData AirborneData { get; private set; }

        [field: Header("Colliders")]
        [field: SerializeField] public DefaultColliderData DefaultColliderData { get; private set; }
        [field: SerializeField] public SlopeData SlopeData { get; private set; }

        [field: Header("Layers")]
        [field: SerializeField] public PlayerLayerData LayerData { get; private set; }

        [field: Header("Voxel")]
        [field: SerializeField] [field: Range(0.1f, 3f)] public float VoxelMaxStepHeight { get; private set; } = 0.5f;

        [field: Header("Animations")]
        [field: SerializeField] public PlayerAnimationData AnimationData { get; private set; }

        private void OnEnable()
        {
            Instance = this;
            AnimationData.Initialize();
        }

        private void OnDisable()
        {
            if (Instance == this)
                Instance = null;
        } 
    }
}
