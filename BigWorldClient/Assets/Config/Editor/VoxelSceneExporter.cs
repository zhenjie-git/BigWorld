using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BigWorldClient
{
    public static class VoxelSceneExporter
    {
        const string PlayerConfigExcel = "Player/player_config.xlsx";
        const string PlayerConfigSheet = "server_params";
        const string StepHeightColumn = "voxel_max_step_height";
        const string SceneExcel = "Scene/scene_config.xlsx";
        const string SceneSheet = "scene_config";
        const string SceneAssetDir = "Assets/Scenes";

        public sealed class SceneRow
        {
            public string SceneId;
            public Vector3 VoxelSize;
            public float CharacterHeight;
            public bool FillInteriorCavities;
            public LayerMask TargetLayers;
        }

        static List<SceneRow> _rowCache;
        static DateTime _rowCacheTime;

        public static List<SceneRow> ReadSceneRows()
        {
            string xlsxPath = Path.Combine(ConfigTableRegistry.GameConfigDir, SceneExcel);
            if (!File.Exists(xlsxPath))
                throw new FileNotFoundException("找不到 Scene/scene_config.xlsx，请先在 GameConfig/Scene 下建表", xlsxPath);

            DateTime writeTime = File.GetLastWriteTimeUtc(xlsxPath);
            if (_rowCache != null && _rowCacheTime == writeTime)
                return _rowCache;

            List<string[]> rows = ConfigTableExporter.ReadSheetRows(xlsxPath, SceneSheet);
            if (rows.Count < 4)
                throw new InvalidDataException("scene_config 缺少表头或数据行");

            string[] header = rows[0];
            int Col(string name) => Array.IndexOf(header, name);
            int colId = Col("scene_id");
            int colSx = Col("voxel_size_x");
            int colSy = Col("voxel_size_y");
            int colSz = Col("voxel_size_z");
            int colCh = Col("character_height");
            int colFill = Col("fill_interior_cavities");
            int colLayers = Col("target_layers");
            if (colId < 0 || colSx < 0 || colSy < 0 || colSz < 0 || colCh < 0 || colFill < 0)
                throw new InvalidDataException("scene_config 表头缺少必需列（scene_id/voxel_size_x/y/z/character_height/fill_interior_cavities）");

            var result = new List<SceneRow>();
            for (int r = 3; r < rows.Count; r++)
            {
                string[] row = rows[r];
                string Cell(int col) => col >= 0 && col < row.Length ? (row[col] ?? "").Trim() : "";
                string id = Cell(colId);
                if (id.Length == 0) continue;

                result.Add(new SceneRow
                {
                    SceneId = id,
                    VoxelSize = new Vector3(
                        ParseFloat(Cell(colSx), 0.1f, "voxel_size_x"),
                        ParseFloat(Cell(colSy), 0.1f, "voxel_size_y"),
                        ParseFloat(Cell(colSz), 0.1f, "voxel_size_z")),
                    CharacterHeight = ParseFloat(Cell(colCh), 1.8f, "character_height"),
                    FillInteriorCavities = IsTrue(Cell(colFill), true),
                    TargetLayers = ParseLayerMask(Cell(colLayers)),
                });
            }

            _rowCache = result;
            _rowCacheTime = writeTime;
            return result;
        }

        public static ExportResult GenerateForScene(string sceneId)
        {
            SceneRow row;
            try
            {
                row = ReadSceneRows().Find(s => s.SceneId == sceneId);
            }
            catch (Exception e)
            {
                return ExportResult.Fail(e.Message);
            }
            if (row == null)
                return ExportResult.Fail($"scene_config 里没有场景 {sceneId}");
            return GenerateCore(row);
        }

        public static ExportResult GenerateAll()
        {
            List<SceneRow> rows;
            try
            {
                rows = ReadSceneRows();
            }
            catch (Exception e)
            {
                return ExportResult.Fail(e.Message);
            }
            if (rows.Count == 0)
                return ExportResult.Fail("scene_config 没有数据行");

            var outputs = new List<string>();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < rows.Count; i++)
            {
                EditorUtility.DisplayProgressBar("体素生成", $"生成 {rows[i].SceneId}（{i + 1}/{rows.Count}）",
                    (float)i / rows.Count);
                ExportResult result = GenerateCore(rows[i]);
                if (!result.Success)
                {
                    EditorUtility.ClearProgressBar();
                    return ExportResult.Fail($"场景 {rows[i].SceneId} 失败：{result.Message}");
                }
                outputs.AddRange(result.Outputs);
            }
            EditorUtility.ClearProgressBar();
            sw.Stop();
            outputs.Add($"共 {rows.Count} 个场景，耗时 {sw.ElapsedMilliseconds / 1000f:F1}s");
            return ExportResult.Ok(outputs.ToArray());
        }

        public static ExportResult Visualize(string sceneId)
        {
            string path = Path.Combine(ConfigTableRegistry.GameConfigDir, sceneId + "_voxels.bytes");
            if (!File.Exists(path))
                return ExportResult.Fail($"体素文件不存在: {path}（先执行生成）");
            try
            {
                VoxelGridData grid = VoxelGridData.LoadFromBinary(path);
                var visualizer = UnityEngine.Object.FindObjectOfType<VoxelGridVisualizer>();
                if (visualizer == null)
                {
                    var go = new GameObject("VoxelGridVisualizer");
                    visualizer = go.AddComponent<VoxelGridVisualizer>();
                }
                visualizer.SetGridData(grid);
                Selection.activeGameObject = visualizer.gameObject;
                return ExportResult.Ok(new[] { $"已可视化 {sceneId}（{grid.gridDimX}×{grid.gridDimZ} 列 / {grid.TotalVoxelCount} 个体素）" });
            }
            catch (Exception e)
            {
                return ExportResult.Fail($"可视化失败: {e.Message}");
            }
        }

        static ExportResult GenerateCore(SceneRow row)
        {
            if (EditorApplication.isPlaying || EditorApplication.isPaused)
                return ExportResult.Fail("Play 模式下不能生成体素，请先退出 Play 模式");

            string sceneAssetPath = $"{SceneAssetDir}/{row.SceneId}.unity";
            if (!File.Exists(Path.GetFullPath(sceneAssetPath)))
                return ExportResult.Fail($"场景文件不存在: {sceneAssetPath}（约定 scene_id == 场景名）");

            string stepError;
            float maxStepHeight = ReadMaxStepHeight(out stepError);
            if (stepError != null)
                return ExportResult.Fail(stepError);

            var config = new VoxelGeneratorConfig
            {
                voxelSize = row.VoxelSize,
                characterHeight = row.CharacterHeight,
                fillInteriorCavities = row.FillInteriorCavities,
                targetLayers = row.TargetLayers,
                maxStepHeight = maxStepHeight,
            };

            var sw = Stopwatch.StartNew();
            Scene scene = SceneManager.GetSceneByPath(sceneAssetPath);
            bool openedByUs = false;
            try
            {
                if (!scene.isLoaded)
                {
                    EditorUtility.DisplayProgressBar("体素生成", $"打开场景 {sceneAssetPath}", 0.1f);
                    scene = EditorSceneManager.OpenScene(sceneAssetPath, OpenSceneMode.Additive);
                    openedByUs = true;
                }
                if (!scene.IsValid() || !scene.isLoaded)
                    return ExportResult.Fail($"场景打开失败: {sceneAssetPath}");

                EditorUtility.DisplayProgressBar("体素生成", $"体素栅格化 {row.SceneId}", 0.3f);
                VoxelGridData grid = VoxelGenerator.Generate(config, scene);
                if (grid == null)
                    return ExportResult.Fail($"场景 {row.SceneId} 中没有可用 mesh，请检查场景内容与 Layer");

                EditorUtility.DisplayProgressBar("体素生成", "写出体素文件", 0.8f);
                string fileName = row.SceneId + "_voxels.bytes";
                string[] outputs = WriteVoxelOutputs(fileName, grid, ConfigTableRegistry.GameConfigDir);

                sw.Stop();
                return ExportResult.Ok(new[]
                {
                    $"场景 {row.SceneId}：{grid.gridDimX}×{grid.gridDimZ} 列 / {grid.TotalVoxelCount} 个体素 / 步高 {maxStepHeight} / 耗时 {sw.ElapsedMilliseconds / 1000f:F1}s",
                    outputs[0],
                    outputs[1],
                });
            }
            catch (Exception e)
            {
                return ExportResult.Fail($"体素生成失败: {e.Message}");
            }
            finally
            {
                if (openedByUs && scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
                EditorUtility.ClearProgressBar();
            }
        }

        public static string[] WriteVoxelOutputs(string fileName, VoxelGridData grid, string primaryFolder)
        {
            if (!Directory.Exists(primaryFolder))
                Directory.CreateDirectory(primaryFolder);
            string primaryPath = Path.Combine(primaryFolder, fileName);
            grid.SaveToBinary(primaryPath);

            string clientDir = Path.Combine(Application.dataPath, "Resources", "VoxelData");
            Directory.CreateDirectory(clientDir);
            string clientCopy = Path.Combine(clientDir, fileName);
            File.Copy(primaryPath, clientCopy, true);

            string clientAssetPath = "Assets/Resources/VoxelData/" + fileName;
            AssetDatabase.Refresh();
            return new[] { Path.GetFullPath(primaryPath), clientAssetPath };
        }

        static float ParseFloat(string value, float fallback, string field)
        {
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && parsed > 0f)
                return parsed;
            return fallback;
        }

        static bool IsTrue(string value, bool fallback)
        {
            if (value.Length == 0) return fallback;
            return value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        static LayerMask ParseLayerMask(string names)
        {
            if (string.IsNullOrWhiteSpace(names)) return ~0;
            int mask = 0;
            foreach (string name in names.Split(','))
            {
                int layer = LayerMask.NameToLayer(name.Trim());
                if (layer < 0)
                    throw new InvalidDataException($"target_layers 里的层名 \"{name.Trim()}\" 不存在");
                mask |= 1 << layer;
            }
            return mask;
        }

        static float ReadMaxStepHeight(out string error)
        {
            error = null;
            string xlsxPath = Path.Combine(ConfigTableRegistry.GameConfigDir, PlayerConfigExcel);
            try
            {
                var rows = ConfigTableExporter.ReadSheetRows(xlsxPath, PlayerConfigSheet);
                if (rows.Count < 4)
                {
                    error = $"{PlayerConfigExcel} 缺少数据行";
                    return 0f;
                }
                int col = Array.IndexOf(rows[0], StepHeightColumn);
                if (col < 0)
                {
                    error = $"{PlayerConfigExcel} 缺少 {StepHeightColumn} 列";
                    return 0f;
                }
                string value = rows[3].Length > col ? rows[3][col] : "";
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float step) || step <= 0f)
                {
                    error = $"{PlayerConfigExcel} 的 {StepHeightColumn} 无效: \"{value}\"";
                    return 0f;
                }
                return step;
            }
            catch (Exception e)
            {
                error = $"读取 {PlayerConfigExcel} 失败: {e.Message}";
                return 0f;
            }
        }
    }
}
