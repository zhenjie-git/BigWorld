using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
            public string MapId;
            public int GridX;
            public int GridZ;
            public float OriginX;
            public float OriginZ;
            public float ZoneSize;
            public Vector3 VoxelSize;
            public float CharacterHeight;
            public bool FillInteriorCavities;
            public LayerMask TargetLayers;
        }

        public const double ZoneVoxelMargin = 8.0;

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
            int colGx = Col("grid_x");
            int colGz = Col("grid_z");
            int colOx = Col("origin_x");
            int colOz = Col("origin_z");
            int colZone = Col("zone_size");
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

                string mapId = id;
                int gx = 0, gz = 0;
                int us1 = id.LastIndexOf('_');
                if (us1 > 0)
                {
                    int us2 = id.LastIndexOf('_', us1 - 1);
                    if (us2 > 0
                        && int.TryParse(id.Substring(us1 + 1), out gz)
                        && int.TryParse(id.Substring(us2 + 1, us1 - us2 - 1), out gx))
                    {
                        mapId = id.Substring(0, us2);
                    }
                }

                result.Add(new SceneRow
                {
                    SceneId = id,
                    MapId = mapId,
                    GridX = gx,
                    GridZ = gz,
                    OriginX = ParseFloatAny(Cell(colOx), 0f),
                    OriginZ = ParseFloatAny(Cell(colOz), 0f),
                    ZoneSize = ParseFloat(Cell(colZone), 0f, "zone_size"),
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

        public static List<SceneRow> ReadSceneRowsWithGrid() => ReadSceneRows();

        public static void ClearRowCache()
        {
            _rowCache = null;
            _rowCacheTime = DateTime.MinValue;
        }

        public static ExportResult GenerateForScene(string sceneId)
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
            SceneRow row = rows.Find(s => s.SceneId == sceneId);
            if (row == null)
                return ExportResult.Fail($"scene_config 里没有场景 {sceneId}");
            var group = rows.Where(s => s.MapId == row.MapId).ToList();
            return GenerateMapGroup(group);
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
            var maps = rows.GroupBy(s => s.MapId).ToList();
            for (int i = 0; i < maps.Count; i++)
            {
                List<SceneRow> group = maps[i].ToList();
                EditorUtility.DisplayProgressBar("体素生成", $"生成地图 {group[0].MapId}（{i + 1}/{maps.Count}）",
                    (float)i / maps.Count);
                ExportResult result = GenerateMapGroup(group);
                if (!result.Success)
                {
                    EditorUtility.ClearProgressBar();
                    return ExportResult.Fail($"地图 {group[0].MapId} 失败：{result.Message}");
                }
                outputs.AddRange(result.Outputs);
            }
            EditorUtility.ClearProgressBar();
            sw.Stop();
            outputs.Add($"共 {maps.Count} 张地图 {rows.Count} 个 zone，耗时 {sw.ElapsedMilliseconds / 1000f:F1}s");
            return ExportResult.Ok(outputs.ToArray());
        }

        static ExportResult GenerateMapGroup(List<SceneRow> group)
        {
            if (EditorApplication.isPlaying || EditorApplication.isPaused)
                return ExportResult.Fail("Play 模式下不能生成体素，请先退出 Play 模式");

            string mapId = group[0].MapId;
            bool multiZone = group.Count > 1;
            string sourceSceneName = multiZone ? mapId : group[0].SceneId;
            string sceneAssetPath = $"{SceneAssetDir}/{sourceSceneName}.unity";
            if (!File.Exists(Path.GetFullPath(sceneAssetPath)))
                return ExportResult.Fail($"场景文件不存在: {sceneAssetPath}（约定 scene_id == 场景名）");

            SceneRow proto = group[0];
            foreach (SceneRow row in group)
            {
                if (row.VoxelSize != proto.VoxelSize
                    || !Mathf.Approximately(row.CharacterHeight, proto.CharacterHeight)
                    || row.FillInteriorCavities != proto.FillInteriorCavities
                    || row.TargetLayers != proto.TargetLayers)
                {
                    return ExportResult.Fail(
                        $"地图 {mapId} 的 zone 行体素参数不一致（{row.SceneId} vs {proto.SceneId}），整图生成只取第一行参数，请统一后重试");
                }
            }
            string stepError;
            float maxStepHeight = ReadMaxStepHeight(out stepError);
            if (stepError != null)
                return ExportResult.Fail(stepError);

            var config = new VoxelGeneratorConfig
            {
                voxelSize = proto.VoxelSize,
                characterHeight = proto.CharacterHeight,
                fillInteriorCavities = proto.FillInteriorCavities,
                targetLayers = proto.TargetLayers,
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

                EditorUtility.DisplayProgressBar("体素生成", $"体素栅格化 {sourceSceneName}", 0.3f);
                VoxelGridData grid = VoxelGenerator.Generate(config, scene);
                if (grid == null)
                    return ExportResult.Fail($"场景 {sourceSceneName} 中没有可用 mesh，请检查场景内容与 Layer");

                var outputs = new List<string>();
                var notes = new List<string>();
                EditorUtility.DisplayProgressBar("体素生成", "写出体素文件", 0.8f);
                foreach (SceneRow row in group)
                {
                    VoxelGridData outGrid = grid;
                    if (multiZone)
                    {
                        double minX = row.OriginX - ZoneVoxelMargin;
                        double minZ = row.OriginZ - ZoneVoxelMargin;
                        double maxX = row.OriginX + row.ZoneSize + ZoneVoxelMargin;
                        double maxZ = row.OriginZ + row.ZoneSize + ZoneVoxelMargin;
                        outGrid = SliceGrid(grid, minX, minZ, maxX, maxZ);
                        if (outGrid == null)
                            return ExportResult.Fail($"zone {row.SceneId} 切片失败（origin/zone_size 配置异常？）");
                    }
                    string fileName = row.SceneId + "_voxels.bytes";
                    string[] paths = WriteVoxelOutputs(fileName, outGrid, ConfigTableRegistry.GameConfigDir);
                    outputs.AddRange(paths);
                    notes.Add($"zone {row.SceneId}：{outGrid.gridDimX}×{outGrid.gridDimZ} 列 / {outGrid.TotalVoxelCount} 体素");
                }

                sw.Stop();
                notes.Add($"源场景 {sourceSceneName}：{grid.gridDimX}×{grid.gridDimZ} 列 / {grid.TotalVoxelCount} 体素 / 步高 {maxStepHeight} / 耗时 {sw.ElapsedMilliseconds / 1000f:F1}s");
                notes.AddRange(outputs.Select(p => Path.GetFullPath(p)));
                return ExportResult.Ok(notes.ToArray());
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

        static VoxelGridData SliceGrid(VoxelGridData src, double minX, double minZ, double maxX, double maxZ)
        {
            if (src == null || src.voxelSize.x <= 0 || src.voxelSize.z <= 0)
                return null;
            int ColOf(double w, float origin, float size) => (int)Math.Floor((w - origin) / size);
            int ClampC(int v) => Math.Max(0, Math.Min(src.gridDimX - 1, v));
            int ClampZ(int v) => Math.Max(0, Math.Min(src.gridDimZ - 1, v));
            int c0x = ClampC(ColOf(minX, src.originOffset.x, src.voxelSize.x));
            int c1x = ClampC(ColOf(maxX - 0.001, src.originOffset.x, src.voxelSize.x));
            int c0z = ClampZ(ColOf(minZ, src.originOffset.z, src.voxelSize.z));
            int c1z = ClampZ(ColOf(maxZ - 0.001, src.originOffset.z, src.voxelSize.z));
            if (c1x < c0x || c1z < c0z)
                return null;

            int dimX = c1x - c0x + 1;
            int dimZ = c1z - c0z + 1;
            int columns = dimX * dimZ;
            var dst = new VoxelGridData
            {
                voxelSize = src.voxelSize,
                originOffset = new Vector3(
                    src.originOffset.x + c0x * src.voxelSize.x,
                    src.originOffset.y,
                    src.originOffset.z + c0z * src.voxelSize.z),
                gridDimX = dimX,
                gridDimZ = dimZ,
                startIndices = new int[columns],
                voxelCounts = new int[columns],
            };

            int total = 0;
            for (int z = 0; z < dimZ; z++)
            {
                for (int x = 0; x < dimX; x++)
                {
                    int srcIdx = (c0x + x) + (c0z + z) * src.gridDimX;
                    int count = src.voxelCounts[srcIdx];
                    dst.voxelCounts[x + z * dimX] = count;
                    dst.startIndices[x + z * dimX] = total;
                    total += count;
                }
            }
            dst.voxels = new VoxelData[total];
            int cursor = 0;
            for (int z = 0; z < dimZ; z++)
            {
                for (int x = 0; x < dimX; x++)
                {
                    int srcIdx = (c0x + x) + (c0z + z) * src.gridDimX;
                    int start = src.startIndices[srcIdx];
                    int count = src.voxelCounts[srcIdx];
                    Array.Copy(src.voxels, start, dst.voxels, cursor, count);
                    cursor += count;
                }
            }
            return dst;
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

        static float ParseFloatAny(string value, float fallback)
        {
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
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
