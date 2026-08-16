using System.IO;
using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// 体素网格可视化组件
    /// 将VoxelGridData渲染为场景中的合并Mesh，使用顶点颜色按高度着色
    ///
    /// 使用方式:
    ///   1. 代码调用 SetGridData(data) / LoadFromBinary(path) / LoadFromResources(path)
    ///   2. 或在Inspector中拖入 .bytes 文件点击加载
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class VoxelGridVisualizer : MonoBehaviour
    {
        [Header("数据源")]
        [Tooltip("拖入 .bytes 二进制体素文件（可选）")]
        public TextAsset binaryFile;

        [Header("外观")]
        [Range(0f, 1f)]
        public float alpha = 0.7f;

        [Tooltip("低处颜色 (Y最小)")]
        public Color lowColor = new Color(0.2f, 0.3f, 0.9f);   // 蓝

        [Tooltip("中间颜色")]
        public Color midColor = new Color(0.1f, 0.8f, 0.2f);   // 绿

        [Tooltip("高处颜色 (Y最大)")]
        public Color highColor = new Color(1f, 0.85f, 0.1f);   // 金

        [Header("调试")]
        public bool showWireframe = false;
        public bool regenerate;

        // 内部数据
        private VoxelGridData gridData;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Material voxelMaterial;

        public VoxelGridData GridData => gridData;
        public bool HasData => gridData != null && gridData.voxels != null && gridData.voxels.Length > 0;

        private void Awake()
        {
            meshFilter = GetComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>();
            CreateMaterial();
        }

        private void OnValidate()
        {
            if (regenerate)
            {
                regenerate = false;
                if (binaryFile != null)
                    LoadFromTextAsset(binaryFile);
                else if (gridData != null)
                    RegenerateMesh();
            }
        }

        private void Start()
        {
            if (binaryFile != null && gridData == null)
                LoadFromTextAsset(binaryFile);
        }

        // ==================== 公开接口 ====================

        /// <summary>设置体素数据并生成Mesh</summary>
        public void SetGridData(VoxelGridData data)
        {
            gridData = data;
            if (data != null)
                RegenerateMesh();
            else
                ClearMesh();
        }

        /// <summary>从二进制文件加载</summary>
        public void LoadFromBinary(string filePath)
        {
            if (!File.Exists(filePath))
            {
                {}
                return;
            }
            var data = VoxelGridData.LoadFromBinary(filePath);
            SetGridData(data);
        }

        /// <summary>从Resources加载 (.bytes 文件，路径不含后缀)</summary>
        public void LoadFromResources(string resourcePath)
        {
            var data = VoxelGridData.LoadFromResources(resourcePath);
            SetGridData(data);
        }

        /// <summary>从TextAsset加载</summary>
        public void LoadFromTextAsset(TextAsset asset)
        {
            if (asset == null)
            {
                {}
                return;
            }
            var data = VoxelGridData.FromBytes(asset.bytes);
            SetGridData(data);
        }

        /// <summary>清除Mesh</summary>
        public void ClearMesh()
        {
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                if (Application.isPlaying)
                    Destroy(meshFilter.sharedMesh);
                else
                    DestroyImmediate(meshFilter.sharedMesh);
                meshFilter.sharedMesh = null;
            }
        }

        // ==================== Mesh生成 ====================

        /// <summary>重新生成合并体素Mesh</summary>
        public void RegenerateMesh()
        {
            if (!HasData)
            {
                ClearMesh();
                return;
            }

            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
            CreateMaterial();

            // 计算高度范围用于颜色映射
            float yMin, yMax;
            ComputeYRange(out yMin, out yMax);

            // 计算需要的顶点和索引数量
            int voxelCount = gridData.TotalVoxelCount;
            int vertexCount = voxelCount * 24;   // 每体素6面×4顶点
            int indexCount = voxelCount * 36;     // 每体素6面×6索引

            Vector3[] vertices = new Vector3[vertexCount];
            Color[] colors = new Color[vertexCount];
            int[] triangles = new int[indexCount];

            Vector3 halfVoxelXZ = new Vector3(gridData.voxelSize.x * 0.5f, 0f, gridData.voxelSize.z * 0.5f);

            int vi = 0; // 顶点索引
            int ti = 0; // 三角形索引

            int colCount = gridData.gridDimX * gridData.gridDimZ;
            for (int colIdx = 0; colIdx < colCount; colIdx++)
            {
                int count = gridData.voxelCounts[colIdx];
                if (count == 0) continue;

                int z = colIdx / gridData.gridDimX;
                int x = colIdx - z * gridData.gridDimX;

                float cx = gridData.originOffset.x + (x + 0.5f) * gridData.voxelSize.x;
                float cz = gridData.originOffset.z + (z + 0.5f) * gridData.voxelSize.z;

                int start = gridData.startIndices[colIdx];
                for (int j = 0; j < count; j++)
                {
                    var voxel = gridData.voxels[start + j];
                    float cy = gridData.originOffset.y + (voxel.minY + voxel.maxY) * 0.5f;
                    float hy = (voxel.maxY - voxel.minY) * 0.5f;

                    Vector3 center = new Vector3(cx, cy, cz);
                    Vector3 halfExtents = new Vector3(halfVoxelXZ.x, hy, halfVoxelXZ.z);

                    Color color = EvaluateColor(voxel.minY, voxel.maxY, yMin, yMax);

                    AddBox(vertices, colors, triangles, ref vi, ref ti, center, halfExtents, color);
                }
            }

            // 创建Mesh
            Mesh mesh = new Mesh();
            mesh.name = "VoxelGrid";
            mesh.indexFormat = voxelCount > 2700
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            // 替换旧Mesh
            ClearMesh();
            meshFilter.sharedMesh = mesh;

            {}
        }

        private void ComputeYRange(out float yMin, out float yMax)
        {
            yMin = float.MaxValue;
            yMax = float.MinValue;
            for (int i = 0; i < gridData.voxels.Length; i++)
            {
                if (gridData.voxels[i].minY < yMin) yMin = gridData.voxels[i].minY;
                if (gridData.voxels[i].maxY > yMax) yMax = gridData.voxels[i].maxY;
            }
            if (yMin > yMax) { yMin = 0; yMax = 1; }
        }

        private Color EvaluateColor(float minY, float maxY, float globalMinY, float globalMaxY)
        {
            float midY = (minY + maxY) * 0.5f;
            float range = globalMaxY - globalMinY;
            float t = range > 0.001f ? Mathf.Clamp01((midY - globalMinY) / range) : 0.5f;

            if (t < 0.5f)
                return Color.Lerp(lowColor, midColor, t * 2f);
            else
                return Color.Lerp(midColor, highColor, (t - 0.5f) * 2f);
        }

        // ==================== 单个体素盒Mesh ====================

        // 6个面的 (normal, up, right) 定义 — 均满足 cross(right, up) = normal (右手系)
        // 保证 AddFace 中的三角形 0-1-2, 1-3-2 为逆时针绕序（从面外侧看）
        private static readonly (Vector3 n, Vector3 u, Vector3 r)[] BOX_FACES = new[]
        {
            (new Vector3( 0,  0,  1), new Vector3( 0,  1,  0), new Vector3( 1,  0,  0)), // +Z
            (new Vector3( 0,  0, -1), new Vector3( 0,  1,  0), new Vector3(-1,  0,  0)), // -Z
            (new Vector3( 1,  0,  0), new Vector3( 0,  1,  0), new Vector3( 0,  0, -1)), // +X
            (new Vector3(-1,  0,  0), new Vector3( 0,  1,  0), new Vector3( 0,  0,  1)), // -X
            (new Vector3( 0,  1,  0), new Vector3( 0,  0, -1), new Vector3( 1,  0,  0)), // +Y
            (new Vector3( 0, -1,  0), new Vector3( 0,  0,  1), new Vector3( 1,  0,  0)), // -Y
        };

        /// <summary>
        /// 向顶点/颜色/索引数组添加一个盒体
        /// </summary>
        private static void AddBox(Vector3[] verts, Color[] colors, int[] tris,
            ref int vi, ref int ti, Vector3 center, Vector3 half, Color color)
        {
            for (int f = 0; f < BOX_FACES.Length; f++)
            {
                AddFace(verts, colors, tris, ref vi, ref ti, center, half,
                    BOX_FACES[f].n, BOX_FACES[f].u, BOX_FACES[f].r, color);
            }
        }

        /// <summary>
        /// 添加盒体的一个面
        /// </summary>
        /// <param name="normal">面法线方向</param>
        /// <param name="up">面"上方"方向（用于在面内定位顶点）</param>
        /// <param name="right">面"右方"方向</param>
        private static void AddFace(Vector3[] verts, Color[] colors, int[] tris,
            ref int vi, ref int ti, Vector3 center, Vector3 half,
            Vector3 normal, Vector3 up, Vector3 right, Color color)
        {
            // 面中心到盒中心的偏移
            Vector3 faceCenter = center + Vector3.Scale(normal, half);

            // 面的半尺寸（在面的两个切线方向上）
            Vector3 hUp = Vector3.Scale(up, half);
            Vector3 hRight = Vector3.Scale(right, half);

            // 4个角：左下、右下、左上、右上
            Vector3 v0 = faceCenter - hRight - hUp; // 左下
            Vector3 v1 = faceCenter + hRight - hUp; // 右下
            Vector3 v2 = faceCenter - hRight + hUp; // 左上
            Vector3 v3 = faceCenter + hRight + hUp; // 右上

            int i0 = vi; vi++;
            int i1 = vi; vi++;
            int i2 = vi; vi++;
            int i3 = vi; vi++;

            verts[i0] = v0; colors[i0] = color;
            verts[i1] = v1; colors[i1] = color;
            verts[i2] = v2; colors[i2] = color;
            verts[i3] = v3; colors[i3] = color;

            // 两个三角形: 0-1-2, 1-3-2 (逆时针 = 正面朝外)
            tris[ti++] = i0;
            tris[ti++] = i1;
            tris[ti++] = i2;

            tris[ti++] = i1;
            tris[ti++] = i3;
            tris[ti++] = i2;
        }

        // ==================== 材质 ====================

        private void CreateMaterial()
        {
            if (voxelMaterial != null) return;

            Shader shader = Shader.Find("Voxel/UnlitVertexColor");
            if (shader == null)
            {
                // 回退: 尝试标准shader
                shader = Shader.Find("Mobile/VertexLit");
                if (shader == null)
                    shader = Shader.Find("Standard");
            }

            voxelMaterial = new Material(shader);
            voxelMaterial.SetFloat("_Alpha", alpha);
            meshRenderer.sharedMaterial = voxelMaterial;
        }

        private void OnDestroy()
        {
            ClearMesh();
            if (voxelMaterial != null)
            {
                if (Application.isPlaying) Destroy(voxelMaterial);
                else DestroyImmediate(voxelMaterial);
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!HasData) return;

            // 绘制体素网格范围
            Gizmos.color = Color.yellow;
            Vector3 center = new Vector3(
                gridData.originOffset.x + gridData.gridDimX * gridData.voxelSize.x * 0.5f,
                gridData.originOffset.y + GetSceneHeight() * 0.5f,
                gridData.originOffset.z + gridData.gridDimZ * gridData.voxelSize.z * 0.5f);
            Vector3 size = new Vector3(
                gridData.gridDimX * gridData.voxelSize.x,
                GetSceneHeight(),
                gridData.gridDimZ * gridData.voxelSize.z);
            Gizmos.DrawWireCube(center, size);
        }

        private float GetSceneHeight()
        {
            if (gridData?.voxels == null || gridData.voxels.Length == 0) return 1f;
            float min = float.MaxValue, max = float.MinValue;
            foreach (var v in gridData.voxels)
            {
                if (v.minY < min) min = v.minY;
                if (v.maxY > max) max = v.maxY;
            }
            return max - min;
        }
#endif
    }
}
