using System.IO;
using UnityEngine;

namespace BigWorldClient
{

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
        public Color lowColor = new Color(0.2f, 0.3f, 0.9f);

        [Tooltip("中间颜色")]
        public Color midColor = new Color(0.1f, 0.8f, 0.2f);

        [Tooltip("高处颜色 (Y最大)")]
        public Color highColor = new Color(1f, 0.85f, 0.1f);

        [Header("调试")]
        public bool showWireframe = false;
        public bool regenerate;

        private VoxelGridData _gridData;
        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Material _voxelMaterial;

        public VoxelGridData GridData => _gridData;
        public bool HasData => _gridData != null && _gridData.voxels != null && _gridData.voxels.Length > 0;

        private void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
            CreateMaterial();
        }

        private void OnValidate()
        {
            if (regenerate)
            {
                regenerate = false;
                if (binaryFile != null)
                    LoadFromTextAsset(binaryFile);
                else if (_gridData != null)
                    RegenerateMesh();
            }
        }

        private void Start()
        {
            if (binaryFile != null && _gridData == null)
                LoadFromTextAsset(binaryFile);
        }

        public void SetGridData(VoxelGridData data)
        {
            _gridData = data;
            if (data != null)
                RegenerateMesh();
            else
                ClearMesh();
        }

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

        public void LoadFromResources(string resourcePath)
        {
            var data = VoxelGridData.LoadFromResources(resourcePath);
            SetGridData(data);
        }

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

        public void ClearMesh()
        {
            if (_meshFilter != null && _meshFilter.sharedMesh != null)
            {
                if (Application.isPlaying)
                    Destroy(_meshFilter.sharedMesh);
                else
                    DestroyImmediate(_meshFilter.sharedMesh);
                _meshFilter.sharedMesh = null;
            }
        }

        public void RegenerateMesh()
        {
            if (!HasData)
            {
                ClearMesh();
                return;
            }

            if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
            if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
            CreateMaterial();

            float yMin, yMax;
            ComputeYRange(out yMin, out yMax);

            int voxelCount = _gridData.TotalVoxelCount;
            int vertexCount = voxelCount * 24;
            int indexCount = voxelCount * 36;

            Vector3[] vertices = new Vector3[vertexCount];
            Color[] colors = new Color[vertexCount];
            int[] triangles = new int[indexCount];

            Vector3 halfVoxelXZ = new Vector3(_gridData.voxelSize.x * 0.5f, 0f, _gridData.voxelSize.z * 0.5f);

            int vi = 0;
            int ti = 0;

            int colCount = _gridData.gridDimX * _gridData.gridDimZ;
            for (int colIdx = 0; colIdx < colCount; colIdx++)
            {
                int count = _gridData.voxelCounts[colIdx];
                if (count == 0) continue;

                int z = colIdx / _gridData.gridDimX;
                int x = colIdx - z * _gridData.gridDimX;

                float cx = _gridData.originOffset.x + (x + 0.5f) * _gridData.voxelSize.x;
                float cz = _gridData.originOffset.z + (z + 0.5f) * _gridData.voxelSize.z;

                int start = _gridData.startIndices[colIdx];
                for (int j = 0; j < count; j++)
                {
                    var voxel = _gridData.voxels[start + j];
                    float cy = _gridData.originOffset.y + (voxel.minY + voxel.maxY) * 0.5f;
                    float hy = (voxel.maxY - voxel.minY) * 0.5f;

                    Vector3 center = new Vector3(cx, cy, cz);
                    Vector3 halfExtents = new Vector3(halfVoxelXZ.x, hy, halfVoxelXZ.z);

                    Color color = EvaluateColor(voxel.minY, voxel.maxY, yMin, yMax);

                    AddBox(vertices, colors, triangles, ref vi, ref ti, center, halfExtents, color);
                }
            }

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

            ClearMesh();
            _meshFilter.sharedMesh = mesh;

            {}
        }

        private void ComputeYRange(out float yMin, out float yMax)
        {
            yMin = float.MaxValue;
            yMax = float.MinValue;
            for (int i = 0; i < _gridData.voxels.Length; i++)
            {
                if (_gridData.voxels[i].minY < yMin) yMin = _gridData.voxels[i].minY;
                if (_gridData.voxels[i].maxY > yMax) yMax = _gridData.voxels[i].maxY;
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

        private static readonly (Vector3 n, Vector3 u, Vector3 r)[] BOX_FACES = new[]
        {
            (new Vector3( 0,  0,  1), new Vector3( 0,  1,  0), new Vector3( 1,  0,  0)),
            (new Vector3( 0,  0, -1), new Vector3( 0,  1,  0), new Vector3(-1,  0,  0)),
            (new Vector3( 1,  0,  0), new Vector3( 0,  1,  0), new Vector3( 0,  0, -1)),
            (new Vector3(-1,  0,  0), new Vector3( 0,  1,  0), new Vector3( 0,  0,  1)),
            (new Vector3( 0,  1,  0), new Vector3( 0,  0, -1), new Vector3( 1,  0,  0)),
            (new Vector3( 0, -1,  0), new Vector3( 0,  0,  1), new Vector3( 1,  0,  0)),
        };

        private static void AddBox(Vector3[] verts, Color[] colors, int[] tris,
            ref int vi, ref int ti, Vector3 center, Vector3 half, Color color)
        {
            for (int f = 0; f < BOX_FACES.Length; f++)
            {
                AddFace(verts, colors, tris, ref vi, ref ti, center, half,
                    BOX_FACES[f].n, BOX_FACES[f].u, BOX_FACES[f].r, color);
            }
        }

        private static void AddFace(Vector3[] verts, Color[] colors, int[] tris,
            ref int vi, ref int ti, Vector3 center, Vector3 half,
            Vector3 normal, Vector3 up, Vector3 right, Color color)
        {

            Vector3 faceCenter = center + Vector3.Scale(normal, half);

            Vector3 hUp = Vector3.Scale(up, half);
            Vector3 hRight = Vector3.Scale(right, half);

            Vector3 v0 = faceCenter - hRight - hUp;
            Vector3 v1 = faceCenter + hRight - hUp;
            Vector3 v2 = faceCenter - hRight + hUp;
            Vector3 v3 = faceCenter + hRight + hUp;

            int i0 = vi; vi++;
            int i1 = vi; vi++;
            int i2 = vi; vi++;
            int i3 = vi; vi++;

            verts[i0] = v0; colors[i0] = color;
            verts[i1] = v1; colors[i1] = color;
            verts[i2] = v2; colors[i2] = color;
            verts[i3] = v3; colors[i3] = color;

            tris[ti++] = i0;
            tris[ti++] = i1;
            tris[ti++] = i2;

            tris[ti++] = i1;
            tris[ti++] = i3;
            tris[ti++] = i2;
        }

        private void CreateMaterial()
        {
            if (_voxelMaterial != null) return;

            Shader shader = Shader.Find("Voxel/UnlitVertexColor");
            if (shader == null)
            {

                shader = Shader.Find("Mobile/VertexLit");
                if (shader == null)
                    shader = Shader.Find("Standard");
            }

            _voxelMaterial = new Material(shader);
            _voxelMaterial.SetFloat("_Alpha", alpha);
            _meshRenderer.sharedMaterial = _voxelMaterial;
        }

        private void OnDestroy()
        {
            ClearMesh();
            if (_voxelMaterial != null)
            {
                if (Application.isPlaying) Destroy(_voxelMaterial);
                else DestroyImmediate(_voxelMaterial);
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!HasData) return;

            Gizmos.color = Color.yellow;
            Vector3 center = new Vector3(
                _gridData.originOffset.x + _gridData.gridDimX * _gridData.voxelSize.x * 0.5f,
                _gridData.originOffset.y + GetSceneHeight() * 0.5f,
                _gridData.originOffset.z + _gridData.gridDimZ * _gridData.voxelSize.z * 0.5f);
            Vector3 size = new Vector3(
                _gridData.gridDimX * _gridData.voxelSize.x,
                GetSceneHeight(),
                _gridData.gridDimZ * _gridData.voxelSize.z);
            Gizmos.DrawWireCube(center, size);
        }

        private float GetSceneHeight()
        {
            if (_gridData?.voxels == null || _gridData.voxels.Length == 0) return 1f;
            float min = float.MaxValue, max = float.MinValue;
            foreach (var v in _gridData.voxels)
            {
                if (v.minY < min) min = v.minY;
                if (v.maxY > max) max = v.maxY;
            }
            return max - min;
        }
#endif
    }
}
