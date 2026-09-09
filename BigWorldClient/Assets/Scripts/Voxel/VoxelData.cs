using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BigWorldClient
{

    [Serializable]
    public struct VoxelData
    {

        public float minY;

        public float maxY;

        public ushort connectivity;

        public float Height => maxY - minY;

        public VoxelData(float minY, float maxY, ushort connectivity = VoxelConnectivity.ALL_BLOCKED)
        {
            this.minY = minY;
            this.maxY = maxY;
            this.connectivity = connectivity;
        }

        public override string ToString() => $"[{minY:F2}, {maxY:F2}] h={Height:F2} conn=0x{connectivity:X4}";
    }

    public static class VoxelConnectivity
    {

        public const int DirUp         = 0;
        public const int DirDown       = 1;
        public const int DirLeft       = 2;
        public const int DirRight      = 3;
        public const int DirUpperLeft  = 4;
        public const int DirLowerLeft  = 5;
        public const int DirUpperRight = 6;
        public const int DirLowerRight = 7;

        public const int DirectionCount = 8;

        public const byte FlagSameLayer  = 0;
        public const byte FlagLayerAbove = 1;
        public const byte FlagLayerBelow = 2;
        public const byte FlagBlocked    = 3;

        public const ushort ALL_BLOCKED = 0xFFFF;

        public static readonly (int dx, int dz)[] Offsets = new (int, int)[]
        {
            ( 0,  1),
            ( 0, -1),
            (-1,  0),
            ( 1,  0),
            (-1,  1),
            (-1, -1),
            ( 1,  1),
            ( 1, -1),
        };

        public static void SetFlag(ref ushort connectivity, int dirIndex, byte flag)
        {
            int shift = dirIndex * 2;
            connectivity &= (ushort)(~(3 << shift));
            connectivity |= (ushort)((flag & 3) << shift);
        }

        public static byte GetFlag(ushort connectivity, int dirIndex)
        {
            return (byte)((connectivity >> (dirIndex * 2)) & 3);
        }

        public static bool IsBlocked(ushort connectivity, int dirIndex)
        {
            return GetFlag(connectivity, dirIndex) == FlagBlocked;
        }

        public static int GetLayerOffset(ushort connectivity, int dirIndex)
        {
            byte flag = GetFlag(connectivity, dirIndex);
            switch (flag)
            {
                case FlagSameLayer:  return 0;
                case FlagLayerAbove: return 1;
                case FlagLayerBelow: return -1;
                default:             return 0;
            }
        }
    }

    [Serializable]
    public class VoxelGridData
    {

        public Vector3 voxelSize;

        public Vector3 originOffset;

        public int gridDimX;

        public int gridDimZ;

        public int[] startIndices;

        public int[] voxelCounts;

        public VoxelData[] voxels;

        public int GridIndex(int x, int z) => x + z * gridDimX;

        public IEnumerable<VoxelData> GetVoxelsAt(int x, int z)
        {
            int idx = GridIndex(x, z);
            if (idx < 0 || idx >= startIndices.Length) yield break;
            int start = startIndices[idx];
            int count = voxelCounts[idx];
            for (int i = 0; i < count; i++)
                yield return voxels[start + i];
        }

        public int TotalVoxelCount => voxels?.Length ?? 0;

        public int OccupiedColumnCount
        {
            get
            {
                int count = 0;
                if (voxelCounts != null)
                    for (int i = 0; i < voxelCounts.Length; i++)
                        if (voxelCounts[i] > 0) count++;
                return count;
            }
        }

        private const uint BINARY_MAGIC = 0x4C584F56;
        private const int BINARY_VERSION = 2;
        private const int HEADER_SIZE = 48;

        public void SaveToBinary(string filePath)
        {
            byte[] data = ToBytes();
            File.WriteAllBytes(filePath, data);
        }

        public static VoxelGridData LoadFromBinary(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            return FromBytes(data);
        }

        public static VoxelGridData LoadFromResources(string resourcePath)
        {
            TextAsset asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null)
            {
                return null;
            }
            return FromBytes(asset.bytes);
        }

        public byte[] ToBytes()
        {
            int totalColumns = gridDimX * gridDimZ;
            int totalVoxels = voxels?.Length ?? 0;

            using (var ms = new MemoryStream(HEADER_SIZE + totalColumns * 4 + totalVoxels * 10))
            using (var bw = new BinaryWriter(ms))
            {

                bw.Write(BINARY_MAGIC);
                bw.Write(BINARY_VERSION);
                bw.Write(gridDimX);
                bw.Write(gridDimZ);
                bw.Write(voxelSize.x);
                bw.Write(voxelSize.y);
                bw.Write(voxelSize.z);
                bw.Write(originOffset.x);
                bw.Write(originOffset.y);
                bw.Write(originOffset.z);
                bw.Write(totalVoxels);
                bw.Write(0);
                bw.Write(0);

                for (int i = 0; i < totalColumns; i++)
                    bw.Write(voxelCounts?[i] ?? 0);

                if (voxels != null)
                {
                    for (int i = 0; i < voxels.Length; i++)
                    {
                        bw.Write(voxels[i].minY);
                        bw.Write(voxels[i].maxY);
                        bw.Write(voxels[i].connectivity);
                    }
                }

                return ms.ToArray();
            }
        }

        public static VoxelGridData FromBytes(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {

                uint magic = br.ReadUInt32();
                if (magic != BINARY_MAGIC)
                    throw new InvalidDataException($"无效的体素文件格式 (Magic: 0x{magic:X8}, 期望: 0x{BINARY_MAGIC:X8})");

                int version = br.ReadInt32();

                var gridData = new VoxelGridData
                {
                    gridDimX = br.ReadInt32(),
                    gridDimZ = br.ReadInt32(),
                    voxelSize = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    originOffset = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                };

                int totalVoxels = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt32();

                int totalColumns = gridData.gridDimX * gridData.gridDimZ;

                gridData.voxelCounts = new int[totalColumns];
                gridData.startIndices = new int[totalColumns];
                int runningIndex = 0;
                for (int i = 0; i < totalColumns; i++)
                {
                    gridData.voxelCounts[i] = br.ReadInt32();
                    gridData.startIndices[i] = runningIndex;
                    runningIndex += gridData.voxelCounts[i];
                }

                gridData.voxels = new VoxelData[totalVoxels];
                for (int i = 0; i < totalVoxels; i++)
                {
                    float minY = br.ReadSingle();
                    float maxY = br.ReadSingle();
                    ushort connectivity = (version >= 2) ? br.ReadUInt16() : VoxelConnectivity.ALL_BLOCKED;
                    gridData.voxels[i] = new VoxelData(minY, maxY, connectivity);
                }
                return gridData;
            }
        }
    }

    [Serializable]
    public class VoxelGeneratorConfig
    {
        [Tooltip("最小体素尺寸 (世界单位)")]
        public Vector3 voxelSize = new Vector3(0.1f, 0.1f, 0.1f);

        [Tooltip("角色高度(m)，用于合并垂直相邻体素：若两体素间空隙小于此值则合并")]
        public float characterHeight = 1.8f;

        [Tooltip("最大跨步高度(m)，用于计算水平连通性：两个相邻体素顶面高度差小于此值视为可通行")]
        public float maxStepHeight = 0.5f;

        [Tooltip("是否填充内部封闭空腔：对空心封闭物体（如无门窗房间），将其内部空心体素填实，使地板天花板合并")]
        public bool fillInteriorCavities = true;

        [Tooltip("是否自动计算空间范围（基于场景中所有mesh的包围盒）")]
        public bool autoComputeBounds = true;

        [Tooltip("手动指定空间最小坐标 (autoComputeBounds=false时生效)")]
        public Vector3 boundsMin;

        [Tooltip("手动指定空间最大坐标 (autoComputeBounds=false时生效)")]
        public Vector3 boundsMax;

        [Tooltip("参与体素化的Layer (默认All)")]
        public LayerMask targetLayers = ~0;

        [Tooltip("是否包含SkinnedMeshRenderer")]
        public bool includeSkinnedMeshRenderers = true;

        [Tooltip("是否包含MeshRenderer（静态网格）")]
        public bool includeMeshRenderers = true;

        public Bounds GetWorldBounds(Bounds? sceneBounds = null)
        {
            if (autoComputeBounds && sceneBounds.HasValue)
                return sceneBounds.Value;
            Vector3 center = (boundsMin + boundsMax) * 0.5f;
            Vector3 size = boundsMax - boundsMin;
            return new Bounds(center, size);
        }
    }
}
