using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BigWorldClient
{
    /// <summary>
    /// 场景体素生成器
    /// 将场景中所有mesh光栅化为体素，使用SAT三角形-体素相交测试，
    /// 基于角色高度合并垂直相邻体素，输出一维数组+indexArr结构
    /// </summary>
    public class VoxelGenerator
    {
        /// <summary>
        /// 三角形数据（世界空间，用于光栅化）
        /// </summary>
        private struct WorldTriangle
        {
            public Vector3 v0, v1, v2;
        }

        /// <summary>
        /// 从场景生成体素网格
        /// </summary>
        public static VoxelGridData Generate(VoxelGeneratorConfig config, Scene scene)
        {
            // Step 1: 收集场景中所有世界空间三角形
            List<WorldTriangle> triangles = CollectTriangles(config, scene);
            if (triangles.Count == 0)
            {
                {}
                return null;
            }
            {}

            // Step 2: 计算世界空间包围盒
            Bounds worldBounds;
            if (config.autoComputeBounds)
                worldBounds = ComputeTrianglesBounds(triangles);
            else
                worldBounds = new Bounds(
                    (config.boundsMin + config.boundsMax) * 0.5f,
                    config.boundsMax - config.boundsMin);

            // gridOrigin 必须精确对齐几何体最小顶点坐标。
            // Expand 会移动 worldBounds.min，使几何体顶面"陷入"相邻 cell
            // → SAT 边界接触判定相交 → 多算一个体素。
            Vector3 gridOrigin = worldBounds.min;

            // Step 3: 计算网格维度 (-1e-5f 防止浮点误差导致 CeilToInt 多算)
            const float kDimEps = 1e-5f;
            int dimX = Mathf.Max(1, Mathf.CeilToInt(worldBounds.size.x / config.voxelSize.x - kDimEps));
            int dimY = Mathf.Max(1, Mathf.CeilToInt(worldBounds.size.y / config.voxelSize.y - kDimEps));
            int dimZ = Mathf.Max(1, Mathf.CeilToInt(worldBounds.size.z / config.voxelSize.z - kDimEps));

            {}

            // Step 4: 光栅化 — 对每个三角形，测试其可能覆盖的体素
            bool[,,] occupied = new bool[dimX, dimY, dimZ];
            Vector3 voxelHalfExtents = config.voxelSize * 0.5f;

            int triIdx = 0;
            foreach (var tri in triangles)
            {
                RasterizeTriangle(tri, gridOrigin, config.voxelSize,
                    voxelHalfExtents, dimX, dimY, dimZ, occupied);
                triIdx++;
            }

            // 统计占用体素
            int occupiedCount = CountOccupied(occupied, dimX, dimY, dimZ);
            {}

            // Step 5: 填充内部封闭空腔（可选）
            if (config.fillInteriorCavities)
            {
                int filledCount = FillInteriorCavities(occupied, dimX, dimY, dimZ);
                if (filledCount > 0)
                {
                    {}
                    int newOccupied = CountOccupied(occupied, dimX, dimY, dimZ);
                    {}
                    occupiedCount = newOccupied;
                }
                else
                {
                    {}
                }
            }

            // Step 6: 垂直合并
            List<List<VoxelData>> columnVoxels = MergeVoxels(
                occupied, config, gridOrigin, dimX, dimY, dimZ);
            {}

            // Step 7: 计算原点偏移并构建输出
            VoxelGridData gridData = BuildGridData(columnVoxels, config.voxelSize, gridOrigin, dimX, dimZ);

            // Step 8: 计算体素8方向连通性
            ComputeConnectivity(gridData, config.maxStepHeight);

            return gridData;
        }

        // ========== 三角形收集 ==========

        private static List<WorldTriangle> CollectTriangles(VoxelGeneratorConfig config, Scene scene)
        {
            var triangles = new List<WorldTriangle>();
            var rootObjects = scene.GetRootGameObjects();

            foreach (var root in rootObjects)
            {
                CollectFromGameObject(root, config, triangles);
            }

            return triangles;
        }

        private static void CollectFromGameObject(GameObject go, VoxelGeneratorConfig config,
            List<WorldTriangle> triangles)
        {
            // 检查Layer
            if (((1 << go.layer) & config.targetLayers) == 0)
                return;

            // MeshRenderer + MeshFilter
            if (config.includeMeshRenderers)
            {
                var mf = go.GetComponent<MeshFilter>();
                var mr = go.GetComponent<MeshRenderer>();
                if (mf != null && mr != null && mr.enabled && mf.sharedMesh != null)
                {
                    AddMeshTriangles(mf.sharedMesh, go.transform.localToWorldMatrix, triangles);
                }
            }

            // SkinnedMeshRenderer
            if (config.includeSkinnedMeshRenderers)
            {
                var smr = go.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.enabled && smr.sharedMesh != null)
                {
                    Mesh baked = new Mesh();
                    smr.BakeMesh(baked);
                    AddMeshTriangles(baked, go.transform.localToWorldMatrix, triangles);
                    // 注意：BakeMesh已经将顶点变换到世界空间（相对于SkinnedMeshRenderer的Transform）
                    // 实际上BakeMesh输出的是世界空间的顶点
                    // 验证：Unity文档说BakeMesh将变形后的顶点烘焙到本地空间
                    // 本地空间 = 相对于transform，所以还需要localToWorldMatrix
#if UNITY_EDITOR
                    if (!Application.isPlaying)
                        Object.DestroyImmediate(baked);
                    else
                        Object.Destroy(baked);
#else
                    Object.Destroy(baked);
#endif
                }
            }

            // 递归子物体
            foreach (Transform child in go.transform)
            {
                CollectFromGameObject(child.gameObject, config, triangles);
            }
        }

        private static void AddMeshTriangles(Mesh mesh, Matrix4x4 localToWorld, List<WorldTriangle> triangles)
        {
            Vector3[] vertices = mesh.vertices;
            int[] indices = mesh.triangles;

            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                Vector3 v0 = localToWorld.MultiplyPoint3x4(vertices[indices[i]]);
                Vector3 v1 = localToWorld.MultiplyPoint3x4(vertices[indices[i + 1]]);
                Vector3 v2 = localToWorld.MultiplyPoint3x4(vertices[indices[i + 2]]);

                // 跳过退化三角形
                if (Vector3.Cross(v1 - v0, v2 - v0).sqrMagnitude < 1e-12f)
                    continue;

                triangles.Add(new WorldTriangle { v0 = v0, v1 = v1, v2 = v2 });
            }
        }

        // ========== 包围盒 ==========

        private static Bounds ComputeTrianglesBounds(List<WorldTriangle> triangles)
        {
            if (triangles.Count == 0)
                return new Bounds(Vector3.zero, Vector3.one);

            Vector3 min = triangles[0].v0;
            Vector3 max = triangles[0].v0;

            foreach (var tri in triangles)
            {
                min = Vector3.Min(min, tri.v0);
                min = Vector3.Min(min, tri.v1);
                min = Vector3.Min(min, tri.v2);
                max = Vector3.Max(max, tri.v0);
                max = Vector3.Max(max, tri.v1);
                max = Vector3.Max(max, tri.v2);
            }

            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            return new Bounds(center, size);
        }

        // ========== 三角形光栅化 ==========

        private static void RasterizeTriangle(
            WorldTriangle tri, Vector3 gridOrigin, Vector3 voxelSize,
            Vector3 voxelHalfExtents, int dimX, int dimY, int dimZ,
            bool[,,] occupied)
        {
            // 计算三角形的世界空间AABB，确定候选体素范围
            Vector3 triMin = Vector3.Min(Vector3.Min(tri.v0, tri.v1), tri.v2);
            Vector3 triMax = Vector3.Max(Vector3.Max(tri.v0, tri.v1), tri.v2);

            // 用微小 epsilon 修正边界，避免三角形顶点恰好落在体素边界时
            // SAT 测试因严格不等号(<)而判定"接触=相交"，导致多算相邻体素。
            // min/max 都减去 kEps，Clamp 并确保 min ≤ max。
            const float kEps = 1e-4f;
            int minX = Mathf.Clamp(Mathf.FloorToInt((triMin.x - gridOrigin.x) / voxelSize.x - kEps), 0, dimX - 1);
            int maxX = Mathf.Clamp(Mathf.FloorToInt((triMax.x - gridOrigin.x) / voxelSize.x - kEps), 0, dimX - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt((triMin.y - gridOrigin.y) / voxelSize.y - kEps), 0, dimY - 1);
            int maxY = Mathf.Clamp(Mathf.FloorToInt((triMax.y - gridOrigin.y) / voxelSize.y - kEps), 0, dimY - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt((triMin.z - gridOrigin.z) / voxelSize.z - kEps), 0, dimZ - 1);
            int maxZ = Mathf.Clamp(Mathf.FloorToInt((triMax.z - gridOrigin.z) / voxelSize.z - kEps), 0, dimZ - 1);
            if (minX > maxX) minX = maxX;
            if (minY > maxY) minY = maxY;
            if (minZ > maxZ) minZ = maxZ;

            // 对每个候选体素做SAT相交测试
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        if (occupied[x, y, z])
                            continue;

                        Vector3 voxelCenter = new Vector3(
                            gridOrigin.x + (x + 0.5f) * voxelSize.x,
                            gridOrigin.y + (y + 0.5f) * voxelSize.y,
                            gridOrigin.z + (z + 0.5f) * voxelSize.z);

                        if (SATIntersection.TriangleAABBIntersect(
                            tri.v0, tri.v1, tri.v2,
                            voxelCenter, voxelHalfExtents))
                        {
                            occupied[x, y, z] = true;
                        }
                    }
                }
            }
        }

        private static int CountOccupied(bool[,,] occupied, int dimX, int dimY, int dimZ)
        {
            int count = 0;
            for (int x = 0; x < dimX; x++)
                for (int y = 0; y < dimY; y++)
                    for (int z = 0; z < dimZ; z++)
                        if (occupied[x, y, z]) count++;
            return count;
        }

        // ========== 内部空腔填充 ==========

        /// <summary>
        /// 使用3D洪水填充（6邻域BFS）检测并填充封闭内部空腔。
        /// 从网格6个外表面的所有非占用体素出发做BFS，
        /// 能到达的=外部空间（保留），不能到达的=内部封闭空腔（填实）。
        /// </summary>
        /// <returns>被填充的体素数量</returns>
        private static int FillInteriorCavities(bool[,,] occupied, int dimX, int dimY, int dimZ)
        {
            bool[,,] visited = new bool[dimX, dimY, dimZ];
            var queue = new System.Collections.Generic.Queue<(int x, int y, int z)>();

            // 从6个边界面的所有非占用体素出发做BFS
            // X- 和 X+ 面
            for (int y = 0; y < dimY; y++)
            {
                for (int z = 0; z < dimZ; z++)
                {
                    TrySeed(occupied, visited, queue, 0,           y, z);
                    TrySeed(occupied, visited, queue, dimX - 1,    y, z);
                }
            }
            // Y- 和 Y+ 面 (底面和顶面)
            for (int x = 0; x < dimX; x++)
            {
                for (int z = 0; z < dimZ; z++)
                {
                    TrySeed(occupied, visited, queue, x, 0,           z);
                    TrySeed(occupied, visited, queue, x, dimY - 1,    z);
                }
            }
            // Z- 和 Z+ 面
            for (int x = 0; x < dimX; x++)
            {
                for (int y = 0; y < dimY; y++)
                {
                    TrySeed(occupied, visited, queue, x, y, 0);
                    TrySeed(occupied, visited, queue, x, y, dimZ - 1);
                }
            }

            // 6邻域BFS扩散
            while (queue.Count > 0)
            {
                var (x, y, z) = queue.Dequeue();

                // ±X
                if (x > 0 && !occupied[x - 1, y, z] && !visited[x - 1, y, z])
                { visited[x - 1, y, z] = true; queue.Enqueue((x - 1, y, z)); }
                if (x + 1 < dimX && !occupied[x + 1, y, z] && !visited[x + 1, y, z])
                { visited[x + 1, y, z] = true; queue.Enqueue((x + 1, y, z)); }

                // ±Y
                if (y > 0 && !occupied[x, y - 1, z] && !visited[x, y - 1, z])
                { visited[x, y - 1, z] = true; queue.Enqueue((x, y - 1, z)); }
                if (y + 1 < dimY && !occupied[x, y + 1, z] && !visited[x, y + 1, z])
                { visited[x, y + 1, z] = true; queue.Enqueue((x, y + 1, z)); }

                // ±Z
                if (z > 0 && !occupied[x, y, z - 1] && !visited[x, y, z - 1])
                { visited[x, y, z - 1] = true; queue.Enqueue((x, y, z - 1)); }
                if (z + 1 < dimZ && !occupied[x, y, z + 1] && !visited[x, y, z + 1])
                { visited[x, y, z + 1] = true; queue.Enqueue((x, y, z + 1)); }
            }

            // 将所有未被BFS访问到的非占用体素标记为占用（=内部封闭空腔）
            int filledCount = 0;
            for (int x = 0; x < dimX; x++)
            {
                for (int y = 0; y < dimY; y++)
                {
                    for (int z = 0; z < dimZ; z++)
                    {
                        if (!occupied[x, y, z] && !visited[x, y, z])
                        {
                            occupied[x, y, z] = true;
                            filledCount++;
                        }
                    }
                }
            }

            return filledCount;
        }

        private static void TrySeed(bool[,,] occupied, bool[,,] visited,
            System.Collections.Generic.Queue<(int, int, int)> queue,
            int x, int y, int z)
        {
            if (!occupied[x, y, z] && !visited[x, y, z])
            {
                visited[x, y, z] = true;
                queue.Enqueue((x, y, z));
            }
        }

        // ========== 垂直合并 ==========

        /// <summary>
        /// 对每个(x,z)列，从下往上扫描占用体素。
        /// 若两个上下相邻的占用体素之间的空隙高度 < characterHeight，则合并为一个。
        /// 返回每列的合并后体素列表。
        /// </summary>
        private static List<List<VoxelData>> MergeVoxels(
            bool[,,] occupied, VoxelGeneratorConfig config,
            Vector3 gridOrigin, int dimX, int dimY, int dimZ)
        {
            var columnVoxels = new List<List<VoxelData>>(dimX * dimZ);

            for (int z = 0; z < dimZ; z++)
            {
                for (int x = 0; x < dimX; x++)
                {
                    var voxels = MergeColumn(occupied, x, z, dimY, config);
                    columnVoxels.Add(voxels);
                }
            }

            return columnVoxels;
        }

        /// <summary>
        /// 合并单个(x,z)列的体素
        /// </summary>
        private static List<VoxelData> MergeColumn(
            bool[,,] occupied, int x, int z, int dimY,
            VoxelGeneratorConfig config)
        {
            var result = new List<VoxelData>();

            int y = 0;
            while (y < dimY)
            {
                // 跳过空体素
                while (y < dimY && !occupied[x, y, z]) y++;
                if (y >= dimY) break;

                // 开始一个占用段
                int segmentStart = y;

                // 找到连续占用段末尾
                while (y < dimY && occupied[x, y, z]) y++;

                int segmentEnd = y - 1; // 段末尾（含）

                // 检查后面的占用段是否可以合并
                while (y < dimY)
                {
                    // 跳过空隙
                    int gapStart = y;
                    while (y < dimY && !occupied[x, y, z]) y++;
                    if (y >= dimY) break; // 没有更多占用段了

                    int nextSegStart = y;

                    // 计算空隙高度：上一段顶面 到 下一段底面 的距离
                    float lowerTop = segmentEnd + 1;  // 每段顶面的体素索引+1 = 顶面Y（体素单位）
                    float upperBottom = nextSegStart;  // 下一段底面的体素索引 = 底面Y（体素单位）
                    float gapHeight = (upperBottom - lowerTop) * config.voxelSize.y;

                    // 继续找下一段的末尾
                    while (y < dimY && occupied[x, y, z]) y++;
                    int nextSegEnd = y - 1;

                    if (gapHeight < config.characterHeight)
                    {
                        // 空隙小于角色高度，合并
                        segmentEnd = nextSegEnd;
                        // 继续检查后面是否还有可合并的段
                    }
                    else
                    {
                        // 空隙太大，不能合并，回退y并退出
                        y = nextSegStart;
                        break;
                    }
                }

                // 输出合并后的体素
                float minY = segmentStart * config.voxelSize.y;
                float maxY = (segmentEnd + 1) * config.voxelSize.y;
                result.Add(new VoxelData(minY, maxY));
            }

            return result;
        }

        // ========== 构建输出数据 ==========

        /// <summary>
        /// 构建最终的VoxelGridData，计算原点偏移并填充一维数组
        /// </summary>
        private static VoxelGridData BuildGridData(
            List<List<VoxelData>> columnVoxels,
            Vector3 voxelSize, Vector3 gridOrigin,
            int dimX, int dimZ)
        {
            var gridData = new VoxelGridData
            {
                voxelSize = voxelSize,
                gridDimX = dimX,
                gridDimZ = dimZ,
            };

            int totalColumns = dimX * dimZ;
            gridData.startIndices = new int[totalColumns];
            gridData.voxelCounts = new int[totalColumns];

            // 先统计总体素数
            int totalVoxelCount = 0;
            for (int i = 0; i < totalColumns; i++)
            {
                gridData.voxelCounts[i] = columnVoxels[i].Count;
                gridData.startIndices[i] = totalVoxelCount;
                totalVoxelCount += columnVoxels[i].Count;
            }

            gridData.voxels = new VoxelData[totalVoxelCount];

            // 计算原点偏移：所有占用体素中xyz坐标最小的那个
            // x和z的最小值：找到第一个有体素的列
            Vector3 originOffset = ComputeOriginOffset(columnVoxels, voxelSize, gridOrigin, dimX, dimZ);
            gridData.originOffset = originOffset;

            // 填充体素数组（减去原点偏移）
            int voxelIdx = 0;
            for (int z = 0; z < dimZ; z++)
            {
                for (int x = 0; x < dimX; x++)
                {
                    int colIdx = x + z * dimX;
                    foreach (var voxel in columnVoxels[colIdx])
                    {
                        // minY/maxY from MergeColumn are relative to gridOrigin.y,
                        // but originOffset.y is world-space. Add gridOrigin.y
                        // so both are in the same coordinate space before subtracting.
                        float worldMinY = gridOrigin.y + voxel.minY;
                        float worldMaxY = gridOrigin.y + voxel.maxY;
                        gridData.voxels[voxelIdx] = new VoxelData(
                            worldMinY - originOffset.y,
                            worldMaxY - originOffset.y);
                        voxelIdx++;
                    }
                }
            }

            {}
            {}
            {}

            return gridData;
        }

        /// <summary>
        /// 计算原点偏移：所有光栅化后占用的最小体素的世界坐标
        /// 原点 = (minGridX * voxelSize.x + gridOrigin.x,
        ///         minOccupiedY,
        ///         minGridZ * voxelSize.z + gridOrigin.z)
        /// 其中minOccupiedY是所有占用体素底面中的最小值
        /// 但体素已经合并过，Y坐标直接就是minY的值（相对gridOrigin的）
        /// 实际原点 = gridOrigin + (minGridX*voxelSize.x, minOccupiedYOffset, minGridZ*voxelSize.z)
        /// 这里我们把所有体素位置减去这个原点
        /// </summary>
        private static Vector3 ComputeOriginOffset(
            List<List<VoxelData>> columnVoxels,
            Vector3 voxelSize, Vector3 gridOrigin,
            int dimX, int dimZ)
        {
            // 找到第一个有体素的列来确定最小x,z
            int minGridX = dimX;
            int minGridZ = dimZ;
            float minOccupiedY = float.MaxValue;

            for (int z = 0; z < dimZ; z++)
            {
                for (int x = 0; x < dimX; x++)
                {
                    int idx = x + z * dimX;
                    if (columnVoxels[idx].Count > 0)
                    {
                        if (x < minGridX) minGridX = x;
                        if (z < minGridZ) minGridZ = z;
                        // 该列第一个体素的minY就是最低的占用Y
                        float colMinY = columnVoxels[idx][0].minY;
                        if (colMinY < minOccupiedY)
                            minOccupiedY = colMinY;
                    }
                }
            }

            // 如果没有找到任何体素
            if (minGridX >= dimX || minGridZ >= dimZ)
                return gridOrigin;

            return new Vector3(
                gridOrigin.x + minGridX * voxelSize.x,
                gridOrigin.y + minOccupiedY,
                gridOrigin.z + minGridZ * voxelSize.z);
        }

        private static int TotalCount(List<List<VoxelData>> columnVoxels)
        {
            int count = 0;
            foreach (var col in columnVoxels)
                count += col.Count;
            return count;
        }

        // ========== 连通性计算 ==========

        /// <summary>
        /// 计算每个体素的8方向连通性信息。
        /// 对每个体素（列(x,z)的第k层），依次检查8个方向的相邻列：
        ///   1. 先检查同k层体素是否可跨步（00）
        ///   2. 再检查k+1层（01）
        ///   3. 再检查k-1层（10）
        ///   4. 都不通则标记为阻塞（11），运行时需要遍历相邻列全部体素
        /// </summary>
        private static void ComputeConnectivity(VoxelGridData gridData, float maxStepHeight)
        {
            int dimX = gridData.gridDimX;
            int dimZ = gridData.gridDimZ;

            for (int z = 0; z < dimZ; z++)
            {
                for (int x = 0; x < dimX; x++)
                {
                    int colIdx = gridData.GridIndex(x, z);
                    int voxelCount = gridData.voxelCounts[colIdx];
                    if (voxelCount == 0) continue;

                    int startIdx = gridData.startIndices[colIdx];

                    for (int k = 0; k < voxelCount; k++)
                    {
                        int voxelIdx = startIdx + k;
                        VoxelData current = gridData.voxels[voxelIdx];
                        ushort conn = 0;

                        for (int dir = 0; dir < VoxelConnectivity.DirectionCount; dir++)
                        {
                            var (dx, dz) = VoxelConnectivity.Offsets[dir];
                            int nx = x + dx;
                            int nz = z + dz;

                            byte flag = VoxelConnectivity.FlagBlocked; // 默认阻塞

                            if (nx >= 0 && nx < dimX && nz >= 0 && nz < dimZ)
                            {
                                int neighborColIdx = gridData.GridIndex(nx, nz);
                                int neighborCount = gridData.voxelCounts[neighborColIdx];

                                if (neighborCount > 0)
                                {
                                    int neighborStart = gridData.startIndices[neighborColIdx];

                                    // 尝试同k层
                                    if (k < neighborCount)
                                    {
                                        VoxelData neighbor = gridData.voxels[neighborStart + k];
                                        if (CanStepTo(current, neighbor, maxStepHeight))
                                            flag = VoxelConnectivity.FlagSameLayer;
                                    }

                                    // 尝试k+1层
                                    if (flag == VoxelConnectivity.FlagBlocked && k + 1 < neighborCount)
                                    {
                                        VoxelData neighbor = gridData.voxels[neighborStart + k + 1];
                                        if (CanStepTo(current, neighbor, maxStepHeight))
                                            flag = VoxelConnectivity.FlagLayerAbove;
                                    }

                                    // 尝试k-1层
                                    if (flag == VoxelConnectivity.FlagBlocked && k - 1 >= 0)
                                    {
                                        VoxelData neighbor = gridData.voxels[neighborStart + k - 1];
                                        if (CanStepTo(current, neighbor, maxStepHeight))
                                            flag = VoxelConnectivity.FlagLayerBelow;
                                    }
                                }
                            }

                            VoxelConnectivity.SetFlag(ref conn, dir, flag);
                        }

                        gridData.voxels[voxelIdx].connectivity = conn;
                    }
                }
            }

            // 统计连通性
            int blockedDirs = 0;
            int totalDirs = 0;
            foreach (var v in gridData.voxels)
            {
                for (int dir = 0; dir < VoxelConnectivity.DirectionCount; dir++)
                {
                    totalDirs++;
                    if (VoxelConnectivity.IsBlocked(v.connectivity, dir))
                        blockedDirs++;
                }
            }
            {}
        }

        /// <summary>
        /// 判断角色能否从from体素的顶面跨步到to体素的顶面
        /// </summary>
        private static bool CanStepTo(VoxelData from, VoxelData to, float maxStepHeight)
        {
            float heightDiff = Mathf.Abs(from.maxY - to.maxY);
            return heightDiff <= maxStepHeight;
        }
    }
}
