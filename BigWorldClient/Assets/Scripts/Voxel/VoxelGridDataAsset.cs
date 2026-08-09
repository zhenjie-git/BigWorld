using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// VoxelGridData 的 ScriptableObject 包装器，用于将体素网格数据保存为 .asset 文件
    /// </summary>
    [CreateAssetMenu(fileName = "VoxelGridData", menuName = "Voxel/VoxelGridData")]
    public class VoxelGridDataAsset : ScriptableObject
    {
        [SerializeReference]
        public VoxelGridData gridData;
    }
}
