using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using BigWorldClient;

/// <summary>
/// 体素生成器编辑器窗口
/// 在 Tools > Voxel Generator 下打开
/// </summary>
public class VoxelGeneratorWindow : EditorWindow
{
    private VoxelGeneratorConfig config;
    private VoxelGridData result;
    private Vector2 scrollPosition;
    private bool showResult = false;

    // 二进制文件保存路径：默认输出到共享 GameConfig（服务器从这里读取），
    // 生成时会同步一份到客户端 Resources/VoxelData 供运行时打包。
    private string binarySaveFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "GameConfig"));
    private string lastSavedBinaryPath;

    [MenuItem("Tools/Voxel Generator")]
    static void Init()
    {
        var window = GetWindow<VoxelGeneratorWindow>();
        window.titleContent = new GUIContent("Voxel Generator");
        window.minSize = new Vector2(420, 560);
        window.Show();
    }

    private void OnEnable()
    {
        if (config == null)
            config = new VoxelGeneratorConfig();
    }

    private void OnGUI()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        GUILayout.Label("体素生成配置", EditorStyles.boldLabel);
        EditorGUILayout.Space(5);

        // === 体素尺寸 ===
        GUILayout.Label("体素尺寸", EditorStyles.boldLabel);
        config.voxelSize = EditorGUILayout.Vector3Field("最小体素尺寸 (m)", config.voxelSize);
        if (config.voxelSize.x <= 0) config.voxelSize.x = 0.1f;
        if (config.voxelSize.y <= 0) config.voxelSize.y = 0.1f;
        if (config.voxelSize.z <= 0) config.voxelSize.z = 0.1f;

        EditorGUILayout.Space(5);

        // === 角色高度 ===
        GUILayout.Label("合并参数", EditorStyles.boldLabel);
        config.characterHeight = EditorGUILayout.FloatField("角色高度 (m)", config.characterHeight);
        if (config.characterHeight < 0.1f) config.characterHeight = 0.1f;
        EditorGUILayout.HelpBox(
            "垂直相邻体素间空隙 < 角色高度 → 合并为一个体素",
            MessageType.Info);

        EditorGUILayout.Space(5);

        // === 空间范围 ===
        GUILayout.Label("空间范围", EditorStyles.boldLabel);
        config.autoComputeBounds = EditorGUILayout.Toggle("自动计算范围", config.autoComputeBounds);
        if (!config.autoComputeBounds)
        {
            EditorGUI.indentLevel++;
            config.boundsMin = EditorGUILayout.Vector3Field("最小坐标", config.boundsMin);
            config.boundsMax = EditorGUILayout.Vector3Field("最大坐标", config.boundsMax);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(5);

        // === Layer和类型 ===
        GUILayout.Label("场景过滤", EditorStyles.boldLabel);
        config.includeMeshRenderers = EditorGUILayout.Toggle("包含 MeshRenderer", config.includeMeshRenderers);
        config.includeSkinnedMeshRenderers = EditorGUILayout.Toggle("包含 SkinnedMeshRenderer", config.includeSkinnedMeshRenderers);

        EditorGUI.BeginChangeCheck();
        int layerMaskValue = EditorGUILayout.MaskField(
            "目标 Layers",
            UnityEditorInternal.InternalEditorUtility.LayerMaskToConcatenatedLayersMask(config.targetLayers),
            UnityEditorInternal.InternalEditorUtility.layers);
        if (EditorGUI.EndChangeCheck())
        {
            config.targetLayers = UnityEditorInternal.InternalEditorUtility.ConcatenatedLayersMaskToLayerMask(layerMaskValue);
        }

        EditorGUILayout.Space(5);

        // === 保存路径 ===
        GUILayout.Label("输出设置", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        binarySaveFolder = EditorGUILayout.TextField("二进制保存目录", binarySaveFolder);
        if (GUILayout.Button("...", GUILayout.Width(30)))
        {
            string selected = EditorUtility.OpenFolderPanel("选择保存目录", "Assets", "");
            if (!string.IsNullOrEmpty(selected))
            {
                // 转换为相对路径
                if (selected.StartsWith(Application.dataPath))
                    binarySaveFolder = "Assets" + selected.Substring(Application.dataPath.Length);
            }
        }
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(lastSavedBinaryPath))
        {
            EditorGUILayout.HelpBox($"上次保存: {lastSavedBinaryPath}", MessageType.None);
        }

        EditorGUILayout.Space(10);

        // === 生成按钮 ===
        GUI.backgroundColor = Color.green;
        if (GUILayout.Button("生成体素并保存二进制", GUILayout.Height(40)))
        {
            GenerateVoxels();
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.Space(5);

        // === 加载已有二进制 ===
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("从二进制加载已有体素", GUILayout.Height(25)))
        {
            LoadExistingBinary();
        }
        if (GUILayout.Button("可视化当前结果", GUILayout.Height(25)))
        {
            VisualizeResult();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(10);

        // === 结果显示 ===
        if (result != null)
        {
            showResult = EditorGUILayout.Foldout(showResult, "生成结果", true);
            if (showResult)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("网格维度(X×Z)", $"{result.gridDimX} × {result.gridDimZ}");
                EditorGUILayout.LabelField("体素尺寸", result.voxelSize.ToString("F2"));
                EditorGUILayout.LabelField("原点偏移", result.originOffset.ToString("F2"));
                EditorGUILayout.LabelField("体素总数", result.TotalVoxelCount.ToString());
                EditorGUILayout.LabelField("有体素的列数", $"{result.OccupiedColumnCount} / {result.gridDimX * result.gridDimZ}");

                EditorGUILayout.Space(5);

                if (result.voxels != null && result.voxels.Length > 0)
                {
                    float minH = float.MaxValue, maxH = float.MinValue, totalH = 0;
                    foreach (var v in result.voxels)
                    {
                        if (v.Height < minH) minH = v.Height;
                        if (v.Height > maxH) maxH = v.Height;
                        totalH += v.Height;
                    }
                    EditorGUILayout.LabelField("体素最小高度", $"{minH:F3} m");
                    EditorGUILayout.LabelField("体素最大高度", $"{maxH:F3} m");
                    EditorGUILayout.LabelField("体素平均高度", $"{totalH / result.voxels.Length:F3} m");

                    // 高度分布直方图简述
                    EditorGUILayout.LabelField("场景最低点", $"{result.originOffset.y + GetMinVoxelY():F2} m (世界)");
                    EditorGUILayout.LabelField("场景最高点", $"{result.originOffset.y + GetMaxVoxelY():F2} m (世界)");
                }

                EditorGUILayout.Space(5);

                if (GUILayout.Button("另存为 ScriptableObject (.asset)", GUILayout.Height(25)))
                {
                    SaveAsAsset();
                }

                EditorGUI.indentLevel--;
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private float GetMinVoxelY()
    {
        if (result?.voxels == null || result.voxels.Length == 0) return 0;
        float min = float.MaxValue;
        foreach (var v in result.voxels) { if (v.minY < min) min = v.minY; }
        return min;
    }

    private float GetMaxVoxelY()
    {
        if (result?.voxels == null || result.voxels.Length == 0) return 0;
        float max = float.MinValue;
        foreach (var v in result.voxels) { if (v.maxY > max) max = v.maxY; }
        return max;
    }

    // ==================== 生成逻辑 ====================

    private void GenerateVoxels()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            EditorUtility.DisplayDialog("错误", "没有打开的场景", "OK");
            return;
        }

        Debug.Log("========== 开始体素生成 ==========");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            result = VoxelGenerator.Generate(config, scene);
            sw.Stop();

            if (result != null)
            {
                Debug.Log($"========== 体素生成完成，耗时 {sw.ElapsedMilliseconds}ms ==========");
                showResult = true;

                // 自动保存为二进制
                AutoSaveBinary(scene.name);
            }
            else
            {
                EditorUtility.DisplayDialog("警告", "体素生成返回空结果，请检查场景中是否有mesh", "OK");
            }
        }
        catch (System.Exception e)
        {
            sw.Stop();
            Debug.LogError($"体素生成失败: {e.Message}\n{e.StackTrace}");
            EditorUtility.DisplayDialog("错误", $"体素生成失败:\n{e.Message}", "OK");
        }
    }

    private void AutoSaveBinary(string sceneName)
    {
        // 确保目录存在
        if (!Directory.Exists(binarySaveFolder))
            Directory.CreateDirectory(binarySaveFolder);

        string fileName = $"{sceneName}_voxels.bytes";
        string filePath = Path.Combine(binarySaveFolder, fileName);
        result.SaveToBinary(filePath);
        lastSavedBinaryPath = filePath;

        // 客户端运行时通过 Resources/VoxelData 加载体素，且只有客户端工程内的
        // 文件才会打包 —— 共享 GameConfig 是唯一数据源，这里同步一份进工程。
        string clientDir = Path.Combine(Application.dataPath, "Resources", "VoxelData");
        Directory.CreateDirectory(clientDir);
        string clientCopy = Path.Combine(clientDir, fileName);
        File.Copy(filePath, clientCopy, true);

        AssetDatabase.Refresh();
        Debug.Log($"[VoxelGeneratorWindow] 已自动保存二进制文件: {filePath}");
        Debug.Log($"[VoxelGeneratorWindow] 已同步客户端副本: {clientCopy}");
    }

    private void LoadExistingBinary()
    {
        string filePath = EditorUtility.OpenFilePanel("选择体素二进制文件", binarySaveFolder, "bytes");
        if (string.IsNullOrEmpty(filePath))
            return;

        try
        {
            result = VoxelGridData.LoadFromBinary(filePath);
            lastSavedBinaryPath = filePath;
            showResult = true;
            Debug.Log($"[VoxelGeneratorWindow] 已加载: {filePath}, {result.TotalVoxelCount} 个体素");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"加载二进制失败: {e.Message}");
            EditorUtility.DisplayDialog("错误", $"加载失败:\n{e.Message}", "OK");
        }
    }

    private void VisualizeResult()
    {
        if (result == null)
        {
            EditorUtility.DisplayDialog("提示", "请先生成或加载体素数据", "OK");
            return;
        }

        // 在场景中查找或创建 VoxelGridVisualizer
        var visualizer = FindObjectOfType<VoxelGridVisualizer>();
        if (visualizer == null)
        {
            var go = new GameObject("VoxelGridVisualizer");
            visualizer = go.AddComponent<VoxelGridVisualizer>();
            Undo.RegisterCreatedObjectUndo(go, "Create Voxel Visualizer");
        }

        Undo.RecordObject(visualizer, "Update Voxel Data");
        visualizer.SetGridData(result);
        EditorUtility.SetDirty(visualizer);

        Debug.Log($"[VoxelGeneratorWindow] 已更新 VoxelGridVisualizer，共 {result.TotalVoxelCount} 个体素");
    }

    private void SaveAsAsset()
    {
        string path = EditorUtility.SaveFilePanelInProject(
            "保存体素网格数据",
            "VoxelGridData",
            "asset",
            "选择保存路径");

        if (string.IsNullOrEmpty(path))
            return;

        var wrapper = ScriptableObject.CreateInstance<VoxelGridDataAsset>();
        wrapper.gridData = result;
        AssetDatabase.CreateAsset(wrapper, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("成功", $"已保存到:\n{path}", "OK");
    }
}
