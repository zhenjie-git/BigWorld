using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BigWorldClient
{

    public class VoxelGenerator
    {

        private struct WorldTriangle
        {
            public Vector3 v0, v1, v2;
        }

        public static VoxelGridData Generate(VoxelGeneratorConfig config, Scene scene)
        {

            List<WorldTriangle> triangles = CollectTriangles(config, scene);
            if (triangles.Count == 0)
            {
                {}
                return null;
            }
            {}

            Bounds worldBounds;
            if (config.autoComputeBounds)
                worldBounds = ComputeTrianglesBounds(triangles);
            else
                worldBounds = new Bounds(
                    (config.boundsMin + config.boundsMax) * 0.5f,
                    config.boundsMax - config.boundsMin);

            Vector3 gridOrigin = worldBounds.min;

            const float kDimEps = 1e-5f;
            int dimX = Mathf.Max(1, Mathf.CeilToInt(worldBounds.size.x / config.voxelSize.x - kDimEps));
            int dimY = Mathf.Max(1, Mathf.CeilToInt(worldBounds.size.y / config.voxelSize.y - kDimEps));
            int dimZ = Mathf.Max(1, Mathf.CeilToInt(worldBounds.size.z / config.voxelSize.z - kDimEps));

            {}

            bool[,,] occupied = new bool[dimX, dimY, dimZ];
            Vector3 voxelHalfExtents = config.voxelSize * 0.5f;

            int triIdx = 0;
            foreach (var tri in triangles)
            {
                RasterizeTriangle(tri, gridOrigin, config.voxelSize,
                    voxelHalfExtents, dimX, dimY, dimZ, occupied);
                triIdx++;
            }

            int occupiedCount = CountOccupied(occupied, dimX, dimY, dimZ);
            {}

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

            List<List<VoxelData>> columnVoxels = MergeVoxels(
                occupied, config, gridOrigin, dimX, dimY, dimZ);
            {}

            VoxelGridData gridData = BuildGridData(columnVoxels, config.voxelSize, gridOrigin, dimX, dimZ);

            ComputeConnectivity(gridData, config.maxStepHeight);

            return gridData;
        }

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

            if (((1 << go.layer) & config.targetLayers) == 0)
                return;

            if (config.includeMeshRenderers)
            {
                var mf = go.GetComponent<MeshFilter>();
                var mr = go.GetComponent<MeshRenderer>();
                if (mf != null && mr != null && mr.enabled && mf.sharedMesh != null)
                {
                    AddMeshTriangles(mf.sharedMesh, go.transform.localToWorldMatrix, triangles);
                }
            }

            if (config.includeSkinnedMeshRenderers)
            {
                var smr = go.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.enabled && smr.sharedMesh != null)
                {
                    Mesh baked = new Mesh();
                    smr.BakeMesh(baked);
                    AddMeshTriangles(baked, go.transform.localToWorldMatrix, triangles);

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

                if (Vector3.Cross(v1 - v0, v2 - v0).sqrMagnitude < 1e-12f)
                    continue;

                triangles.Add(new WorldTriangle { v0 = v0, v1 = v1, v2 = v2 });
            }
        }

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

        private static void RasterizeTriangle(
            WorldTriangle tri, Vector3 gridOrigin, Vector3 voxelSize,
            Vector3 voxelHalfExtents, int dimX, int dimY, int dimZ,
            bool[,,] occupied)
        {

            Vector3 triMin = Vector3.Min(Vector3.Min(tri.v0, tri.v1), tri.v2);
            Vector3 triMax = Vector3.Max(Vector3.Max(tri.v0, tri.v1), tri.v2);

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

        private static int FillInteriorCavities(bool[,,] occupied, int dimX, int dimY, int dimZ)
        {
            bool[,,] visited = new bool[dimX, dimY, dimZ];
            var queue = new System.Collections.Generic.Queue<(int x, int y, int z)>();

            for (int y = 0; y < dimY; y++)
            {
                for (int z = 0; z < dimZ; z++)
                {
                    TrySeed(occupied, visited, queue, 0,           y, z);
                    TrySeed(occupied, visited, queue, dimX - 1,    y, z);
                }
            }

            for (int x = 0; x < dimX; x++)
            {
                for (int z = 0; z < dimZ; z++)
                {
                    TrySeed(occupied, visited, queue, x, 0,           z);
                    TrySeed(occupied, visited, queue, x, dimY - 1,    z);
                }
            }

            for (int x = 0; x < dimX; x++)
            {
                for (int y = 0; y < dimY; y++)
                {
                    TrySeed(occupied, visited, queue, x, y, 0);
                    TrySeed(occupied, visited, queue, x, y, dimZ - 1);
                }
            }

            while (queue.Count > 0)
            {
                var (x, y, z) = queue.Dequeue();

                if (x > 0 && !occupied[x - 1, y, z] && !visited[x - 1, y, z])
                { visited[x - 1, y, z] = true; queue.Enqueue((x - 1, y, z)); }
                if (x + 1 < dimX && !occupied[x + 1, y, z] && !visited[x + 1, y, z])
                { visited[x + 1, y, z] = true; queue.Enqueue((x + 1, y, z)); }

                if (y > 0 && !occupied[x, y - 1, z] && !visited[x, y - 1, z])
                { visited[x, y - 1, z] = true; queue.Enqueue((x, y - 1, z)); }
                if (y + 1 < dimY && !occupied[x, y + 1, z] && !visited[x, y + 1, z])
                { visited[x, y + 1, z] = true; queue.Enqueue((x, y + 1, z)); }

                if (z > 0 && !occupied[x, y, z - 1] && !visited[x, y, z - 1])
                { visited[x, y, z - 1] = true; queue.Enqueue((x, y, z - 1)); }
                if (z + 1 < dimZ && !occupied[x, y, z + 1] && !visited[x, y, z + 1])
                { visited[x, y, z + 1] = true; queue.Enqueue((x, y, z + 1)); }
            }

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

        private static List<VoxelData> MergeColumn(
            bool[,,] occupied, int x, int z, int dimY,
            VoxelGeneratorConfig config)
        {
            var result = new List<VoxelData>();

            int y = 0;
            while (y < dimY)
            {

                while (y < dimY && !occupied[x, y, z]) y++;
                if (y >= dimY) break;

                int segmentStart = y;

                while (y < dimY && occupied[x, y, z]) y++;

                int segmentEnd = y - 1;

                while (y < dimY)
                {

                    int gapStart = y;
                    while (y < dimY && !occupied[x, y, z]) y++;
                    if (y >= dimY) break;

                    int nextSegStart = y;

                    float lowerTop = segmentEnd + 1;
                    float upperBottom = nextSegStart;
                    float gapHeight = (upperBottom - lowerTop) * config.voxelSize.y;

                    while (y < dimY && occupied[x, y, z]) y++;
                    int nextSegEnd = y - 1;

                    if (gapHeight < config.characterHeight)
                    {

                        segmentEnd = nextSegEnd;

                    }
                    else
                    {

                        y = nextSegStart;
                        break;
                    }
                }

                float minY = segmentStart * config.voxelSize.y;
                float maxY = (segmentEnd + 1) * config.voxelSize.y;
                result.Add(new VoxelData(minY, maxY));
            }

            return result;
        }

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

            int totalVoxelCount = 0;
            for (int i = 0; i < totalColumns; i++)
            {
                gridData.voxelCounts[i] = columnVoxels[i].Count;
                gridData.startIndices[i] = totalVoxelCount;
                totalVoxelCount += columnVoxels[i].Count;
            }

            gridData.voxels = new VoxelData[totalVoxelCount];

            Vector3 originOffset = ComputeOriginOffset(columnVoxels, voxelSize, gridOrigin, dimX, dimZ);
            gridData.originOffset = originOffset;

            int voxelIdx = 0;
            for (int z = 0; z < dimZ; z++)
            {
                for (int x = 0; x < dimX; x++)
                {
                    int colIdx = x + z * dimX;
                    foreach (var voxel in columnVoxels[colIdx])
                    {

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

        private static Vector3 ComputeOriginOffset(
            List<List<VoxelData>> columnVoxels,
            Vector3 voxelSize, Vector3 gridOrigin,
            int dimX, int dimZ)
        {

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

                        float colMinY = columnVoxels[idx][0].minY;
                        if (colMinY < minOccupiedY)
                            minOccupiedY = colMinY;
                    }
                }
            }

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

                            byte flag = VoxelConnectivity.FlagBlocked;

                            if (nx >= 0 && nx < dimX && nz >= 0 && nz < dimZ)
                            {
                                int neighborColIdx = gridData.GridIndex(nx, nz);
                                int neighborCount = gridData.voxelCounts[neighborColIdx];

                                if (neighborCount > 0)
                                {
                                    int neighborStart = gridData.startIndices[neighborColIdx];

                                    if (k < neighborCount)
                                    {
                                        VoxelData neighbor = gridData.voxels[neighborStart + k];
                                        if (CanStepTo(current, neighbor, maxStepHeight))
                                            flag = VoxelConnectivity.FlagSameLayer;
                                    }

                                    if (flag == VoxelConnectivity.FlagBlocked && k + 1 < neighborCount)
                                    {
                                        VoxelData neighbor = gridData.voxels[neighborStart + k + 1];
                                        if (CanStepTo(current, neighbor, maxStepHeight))
                                            flag = VoxelConnectivity.FlagLayerAbove;
                                    }

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

        private static bool CanStepTo(VoxelData from, VoxelData to, float maxStepHeight)
        {
            float heightDiff = Mathf.Abs(from.maxY - to.maxY);
            return heightDiff <= maxStepHeight;
        }
    }
}
