using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// 单个合并后的体素数据
    /// </summary>
    [Serializable]
    public struct VoxelData
    {
        /// <summary>底面Y坐标（相对原点偏移后）</summary>
        public float minY;
        /// <summary>顶面Y坐标（相对原点偏移后）</summary>
        public float maxY;

        /// <summary>
        /// 8方向连通性信息，每个方向2 bit，共16 bit。
        /// 00=同k层可通行, 01=k+1层可通行, 10=k-1层可通行, 11=需遍历相邻列全部体素。
        /// 未计算时默认为 0xFFFF（全部阻塞）。
        /// </summary>
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

    /// <summary>
    /// 体素连通性编码/解码工具
    /// 8个方向（上下左右 + 四对角），每方向2 bit，共16 bit 打包为一个 ushort。
    ///
    /// 方向顺序：上(+Z) 下(-Z) 左(-X) 右(+X) 左上(-X,+Z) 左下(-X,-Z) 右上(+X,+Z) 右下(+X,-Z)
    ///
    /// 每方向2 bit含义：
    ///   00 (SameLayer)  — 可走到相邻列同一k层（索引相同）的体素
    ///   01 (LayerAbove) — 可走到相邻列k+1层的体素
    ///   10 (LayerBelow) — 可走到相邻列k-1层的体素
    ///   11 (Blocked)    — 相邻列k/k+1/k-1均不可达，需遍历该列全部体素
    /// </summary>
    public static class VoxelConnectivity
    {
        // ── 方向索引 ──
        public const int DirUp         = 0; // +Z
        public const int DirDown       = 1; // -Z
        public const int DirLeft       = 2; // -X
        public const int DirRight      = 3; // +X
        public const int DirUpperLeft  = 4; // -X, +Z
        public const int DirLowerLeft  = 5; // -X, -Z
        public const int DirUpperRight = 6; // +X, +Z
        public const int DirLowerRight = 7; // +X, -Z

        public const int DirectionCount = 8;

        // ── 2 bit 标志值 ──
        public const byte FlagSameLayer  = 0; // 00
        public const byte FlagLayerAbove = 1; // 01
        public const byte FlagLayerBelow = 2; // 10
        public const byte FlagBlocked    = 3; // 11

        /// <summary>所有方向均为阻塞的默认值</summary>
        public const ushort ALL_BLOCKED = 0xFFFF;

        // ── 各方向在XZ网格上的偏移 ──
        public static readonly (int dx, int dz)[] Offsets = new (int, int)[]
        {
            ( 0,  1), // Up          (+Z)
            ( 0, -1), // Down        (-Z)
            (-1,  0), // Left        (-X)
            ( 1,  0), // Right       (+X)
            (-1,  1), // UpperLeft   (-X, +Z)
            (-1, -1), // LowerLeft   (-X, -Z)
            ( 1,  1), // UpperRight  (+X, +Z)
            ( 1, -1), // LowerRight  (+X, -Z)
        };

        /// <summary>设置某个方向的连通性标志</summary>
        public static void SetFlag(ref ushort connectivity, int dirIndex, byte flag)
        {
            int shift = dirIndex * 2;
            connectivity &= (ushort)(~(3 << shift));
            connectivity |= (ushort)((flag & 3) << shift);
        }

        /// <summary>获取某个方向的连通性标志</summary>
        public static byte GetFlag(ushort connectivity, int dirIndex)
        {
            return (byte)((connectivity >> (dirIndex * 2)) & 3);
        }

        /// <summary>某个方向是否被阻塞（需全遍历相邻列）</summary>
        public static bool IsBlocked(ushort connectivity, int dirIndex)
        {
            return GetFlag(connectivity, dirIndex) == FlagBlocked;
        }

        /// <summary>获取某个方向对应的目标层偏移量（-1/0/+1），阻塞时返回0</summary>
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

    /// <summary>
    /// 完整的体素网格数据
    /// </summary>
    [Serializable]
    public class VoxelGridData
    {
        /// <summary>最小体素尺寸（世界单位）</summary>
        public Vector3 voxelSize;
        /// <summary>原点偏移量（世界空间中所有mesh光栅化后xyz最小的体素坐标）</summary>
        public Vector3 originOffset;
        /// <summary>X方向格子数量</summary>
        public int gridDimX;
        /// <summary>Z方向格子数量</summary>
        public int gridDimZ;

        /// <summary>
        /// 扁平化2D数组: [x + z * dimX] = 该(x,z)列在voxels数组中的起始索引
        /// 与voxelCounts配合使用：给定(gridX, gridZ)，该列的体素为
        /// voxels[startIndices[idx] .. startIndices[idx] + voxelCounts[idx] - 1]
        /// </summary>
        public int[] startIndices;
        /// <summary>扁平化2D数组: [x + z * dimX] = 该(x,z)列的体素数量</summary>
        public int[] voxelCounts;
        /// <summary>一维数组：所有体素，按列连续存储，每列内按minY升序排列</summary>
        public VoxelData[] voxels;

        public int GridIndex(int x, int z) => x + z * gridDimX;

        /// <summary>获取指定(x,z)格子的所有体素</summary>
        public IEnumerable<VoxelData> GetVoxelsAt(int x, int z)
        {
            int idx = GridIndex(x, z);
            if (idx < 0 || idx >= startIndices.Length) yield break;
            int start = startIndices[idx];
            int count = voxelCounts[idx];
            for (int i = 0; i < count; i++)
                yield return voxels[start + i];
        }

        /// <summary>体素总数</summary>
        public int TotalVoxelCount => voxels?.Length ?? 0;

        /// <summary>有体素的列数</summary>
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

        // ==================== 二进制序列化 ====================

        private const uint BINARY_MAGIC = 0x4C584F56; // "VOXL" (little-endian)
        private const int BINARY_VERSION = 2;
        private const int HEADER_SIZE = 48; // 字节

        /// <summary>保存为二进制文件</summary>
        public void SaveToBinary(string filePath)
        {
            byte[] data = ToBytes();
            File.WriteAllBytes(filePath, data);
            Debug.Log($"[VoxelGridData] 已保存二进制文件: {filePath} ({data.Length} bytes)");
        }

        /// <summary>从二进制文件加载</summary>
        public static VoxelGridData LoadFromBinary(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            return FromBytes(data);
        }

        /// <summary>从Resources中的TextAsset加载</summary>
        public static VoxelGridData LoadFromResources(string resourcePath)
        {
            TextAsset asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null)
            {
                Debug.LogError($"[VoxelGridData] 找不到资源: Resources/{resourcePath}");
                return null;
            }
            return FromBytes(asset.bytes);
        }

        /// <summary>序列化为字节数组</summary>
        public byte[] ToBytes()
        {
            int totalColumns = gridDimX * gridDimZ;
            int totalVoxels = voxels?.Length ?? 0;

            using (var ms = new MemoryStream(HEADER_SIZE + totalColumns * 4 + totalVoxels * 10))
            using (var bw = new BinaryWriter(ms))
            {
                // --- Header: 48 bytes ---
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
                bw.Write(0); // reserved
                bw.Write(0); // reserved

                // --- voxelCounts ---
                for (int i = 0; i < totalColumns; i++)
                    bw.Write(voxelCounts?[i] ?? 0);

                // --- voxels ---
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

        /// <summary>从字节数组反序列化</summary>
        public static VoxelGridData FromBytes(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                // --- Header ---
                uint magic = br.ReadUInt32();
                if (magic != BINARY_MAGIC)
                    throw new InvalidDataException($"无效的体素文件格式 (Magic: 0x{magic:X8}, 期望: 0x{BINARY_MAGIC:X8})");

                int version = br.ReadInt32();
                if (version != BINARY_VERSION)
                    Debug.LogWarning($"[VoxelGridData] 文件版本 {version} 与当前版本 {BINARY_VERSION} 不匹配，尝试加载...");

                var gridData = new VoxelGridData
                {
                    gridDimX = br.ReadInt32(),
                    gridDimZ = br.ReadInt32(),
                    voxelSize = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    originOffset = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                };

                int totalVoxels = br.ReadInt32();
                br.ReadInt32(); // reserved
                br.ReadInt32(); // reserved

                int totalColumns = gridData.gridDimX * gridData.gridDimZ;

                // --- voxelCounts + 重建 startIndices ---
                gridData.voxelCounts = new int[totalColumns];
                gridData.startIndices = new int[totalColumns];
                int runningIndex = 0;
                for (int i = 0; i < totalColumns; i++)
                {
                    gridData.voxelCounts[i] = br.ReadInt32();
                    gridData.startIndices[i] = runningIndex;
                    runningIndex += gridData.voxelCounts[i];
                }

                if (runningIndex != totalVoxels)
                    Debug.LogWarning($"[VoxelGridData] 体素数量不一致: header={totalVoxels}, 实际={runningIndex}");

                // --- voxels ---
                gridData.voxels = new VoxelData[totalVoxels];
                for (int i = 0; i < totalVoxels; i++)
                {
                    float minY = br.ReadSingle();
                    float maxY = br.ReadSingle();
                    ushort connectivity = (version >= 2) ? br.ReadUInt16() : VoxelConnectivity.ALL_BLOCKED;
                    gridData.voxels[i] = new VoxelData(minY, maxY, connectivity);
                }

                Debug.Log($"[VoxelGridData] 从二进制加载: {gridData.gridDimX}×{gridData.gridDimZ}, " +
                          $"{totalVoxels} 个体素, {gridData.OccupiedColumnCount} 个非空列");
                return gridData;
            }
        }
    }

    /// <summary>
    /// 体素生成器配置
    /// </summary>
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

        /// <summary>获取最终使用的世界空间包围盒</summary>
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
