using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// player_config 的 Excel 导表工具。
    ///
    /// Excel 只管理服务器实际读取的 9 个数值字段：
    ///   GameConfig/Player/player_config.xlsx（sheet: server_params）
    /// 其余字段（曲线、旋转、动画名、Unity 资产路径等）仍由 Unity Player.asset 维护。
    ///
    /// 导出时合并规则：
    ///   Unity Player.asset 生成完整 JSON → 用 Excel 的 9 个字段覆盖 →
    ///   写入 GameConfig/Player/player_config.json → 刷新客户端 Player.asset。
    /// </summary>
    public static class PlayerConfigExcelExporter
    {
        const string ExcelFile = "Player/player_config.xlsx";
        const string JsonFile = "Player/player_config.json";
        const string SheetName = "server_params";

        static readonly string[] ExcelManagedFieldPaths =
        {
            "grounded.base_speed",
            "grounded.sprint.speed_modifier",
            "grounded.sprint.sprint_to_run_time",
            "grounded.roll.speed_modifier",
            "airborne.fall.fall_speed_limit",
            "airborne.fall.gravity",
            "collider.height",
            "collider.center_y",
            "voxel_max_step_height",
        };

        static string GameConfigDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "GameConfig"));

        static string ExcelPath => Path.Combine(GameConfigDir, ExcelFile);
        static string JsonPath => Path.Combine(GameConfigDir, JsonFile);

        [MenuItem("BigWorld/Config/从Excel导出玩家配置")]
        public static void ExportFromExcel()
        {
            try
            {
                ExportCore();
                Debug.Log($"[Config] 玩家配置已合并导出：\n" +
                          $"  Excel: {ExcelPath}\n" +
                          $"  JSON:  {JsonPath}");
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog("玩家配置", "已从 Excel 合并导出到 GameConfig，并刷新客户端 Player.asset。", "确定");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Config] 从 Excel 导出玩家配置失败: {e.Message}\n{e.StackTrace}");
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog("玩家配置导出失败", e.Message, "确定");
                else
                    EditorApplication.Exit(1);
            }
        }

        static void ExportCore()
        {
            if (!File.Exists(JsonPath))
                throw new FileNotFoundException(
                    "完整 player_config.json 不存在。请先执行 BigWorld/Config/导出到共享文件夹，生成 Unity 侧字段的完整 JSON", JsonPath);

            var json = JsonUtility.FromJson<ConfigExporter.PlayerConfigJson>(File.ReadAllText(JsonPath));
            if (json == null)
                throw new InvalidDataException($"{JsonPath} 解析失败");

            ApplyExcelServerParams(json);
            WriteJson(json);

            // 刷新客户端 Player.asset，使客户端数值与合并后的 JSON 一致
            ConfigExporter.ReadPlayerConfig();
            AssetDatabase.SaveAssets();
        }

        /// <summary>用 Excel 里的服务器字段覆盖 dto 对应字段。导出到共享文件夹也会调用。</summary>
        public static void ApplyExcelServerParams(ConfigExporter.PlayerConfigJson dto)
        {
            if (dto == null) throw new ArgumentNullException(nameof(dto));
            var values = ReadServerParams();

            EnsureJsonNested(dto);
            dto.grounded.base_speed = ToFloat(values, "grounded.base_speed");
            dto.grounded.sprint.speed_modifier = ToFloat(values, "grounded.sprint.speed_modifier");
            dto.grounded.sprint.sprint_to_run_time = ToFloat(values, "grounded.sprint.sprint_to_run_time");
            dto.grounded.roll.speed_modifier = ToFloat(values, "grounded.roll.speed_modifier");
            dto.airborne.fall.fall_speed_limit = ToFloat(values, "airborne.fall.fall_speed_limit");
            dto.airborne.fall.gravity = ToFloat(values, "airborne.fall.gravity");
            dto.collider.height = ToFloat(values, "collider.height");
            dto.collider.center_y = ToFloat(values, "collider.center_y");
            dto.voxel_max_step_height = ToFloat(values, "voxel_max_step_height");
        }

        static Dictionary<string, double> ReadServerParams()
        {
            if (!File.Exists(ExcelPath))
                throw new FileNotFoundException("玩家配置 Excel 源文件不存在，请确认路径", ExcelPath);

            var cells = LightExcel.ReadSheetCells(ExcelPath, SheetName);

            // 表头必须是 A1=字段路径、B1=值、C1=说明
            if (GetCell(cells, 1, 1).Trim() != "字段路径" ||
                GetCell(cells, 1, 2).Trim() != "值" ||
                GetCell(cells, 1, 3).Trim() != "说明")
            {
                throw new InvalidDataException($"{ExcelPath} 的 {SheetName} 表头必须为 A1=字段路径、B1=值、C1=说明");
            }

            var rows = new HashSet<int>();
            foreach (var pair in cells)
            {
                var loc = ParseCellKey(pair.Key);
                if (loc.col < 1 || loc.col > 3)
                    throw new InvalidDataException($"单元格 {ToExcelRef(loc.row, loc.col)} 超出 {SheetName} 的 A/B/C 三列");
                if (loc.row > 1) rows.Add(loc.row);
            }

            var rowList = new List<int>(rows);
            rowList.Sort();

            var expected = new HashSet<string>(ExcelManagedFieldPaths, StringComparer.Ordinal);
            var values = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (int row in rowList)
            {
                string path = GetCell(cells, row, 1).Trim();
                string rawValue = GetCell(cells, row, 2).Trim();

                if (path.Length == 0)
                    throw new InvalidDataException($"{SheetName} 第 {row} 行缺少字段路径（A{row}）");
                if (rawValue.Length == 0)
                    throw new InvalidDataException($"{SheetName} 第 {row} 行缺少值（B{row}）");
                if (!expected.Contains(path))
                    throw new InvalidDataException($"{SheetName} 第 {row} 行字段路径 \"{path}\" 不是服务器字段，不能加入本表");
                if (values.ContainsKey(path))
                    throw new InvalidDataException($"{SheetName} 字段路径 \"{path}\" 重复出现");
                if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                    double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new InvalidDataException($"{SheetName} 第 {row} 行的值 \"{rawValue}\" 不是有效数字");
                }

                values[path] = number;
            }

            foreach (string path in ExcelManagedFieldPaths)
            {
                if (!values.ContainsKey(path))
                    throw new InvalidDataException($"{SheetName} 缺少字段：{path}");
            }

            ValidateRanges(values);
            return values;
        }

        static void ValidateRanges(Dictionary<string, double> v)
        {
            RequireAtLeast(v, "grounded.base_speed", 0.0);
            RequireAtLeast(v, "grounded.sprint.speed_modifier", 0.0);
            RequireAtLeast(v, "grounded.sprint.sprint_to_run_time", 0.0);
            RequireAtLeast(v, "grounded.roll.speed_modifier", 0.0);
            RequirePositive(v, "airborne.fall.fall_speed_limit");
            RequirePositive(v, "airborne.fall.gravity");
            RequirePositive(v, "collider.height");
            RequirePositive(v, "collider.center_y");
            RequireAtLeast(v, "voxel_max_step_height", 0.0);

            if (v["collider.center_y"] > v["collider.height"])
                throw new InvalidDataException("collider.center_y 不能大于 collider.height");
        }

        static void RequirePositive(Dictionary<string, double> v, string key)
        {
            if (v[key] <= 0.0)
                throw new InvalidDataException($"{key} 必须大于 0，当前值 {v[key]}");
        }

        static void RequireAtLeast(Dictionary<string, double> v, string key, double min)
        {
            if (v[key] < min)
                throw new InvalidDataException($"{key} 不能小于 {min}，当前值 {v[key]}");
        }

        static float ToFloat(Dictionary<string, double> v, string key) => (float)v[key];

        static void EnsureJsonNested(ConfigExporter.PlayerConfigJson dto)
        {
            if (dto.grounded == null) dto.grounded = new ConfigExporter.JsonGrounded();
            if (dto.grounded.sprint == null) dto.grounded.sprint = new ConfigExporter.JsonSprint();
            if (dto.grounded.roll == null) dto.grounded.roll = new ConfigExporter.JsonRoll();
            if (dto.airborne == null) dto.airborne = new ConfigExporter.JsonAirborne();
            if (dto.airborne.fall == null) dto.airborne.fall = new ConfigExporter.JsonFall();
            if (dto.collider == null) dto.collider = new ConfigExporter.JsonCollider();
        }

        static void WriteJson(ConfigExporter.PlayerConfigJson dto)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JsonPath) ?? GameConfigDir);
            File.WriteAllText(JsonPath, JsonUtility.ToJson(dto, true), new UTF8Encoding(false));
        }

        static string GetCell(Dictionary<string, string> cells, int row, int col)
        {
            return cells.TryGetValue($"{row}:{col}", out string value) ? value : "";
        }

        static (int row, int col) ParseCellKey(string key)
        {
            int sep = key.IndexOf(':');
            if (sep < 0 || !int.TryParse(key.Substring(0, sep), out int row) || !int.TryParse(key.Substring(sep + 1), out int col))
                throw new InvalidDataException($"内部单元格坐标异常: {key}");
            return (row, col);
        }

        static string ToExcelRef(int row, int col)
        {
            string letters = "";
            int n = col;
            while (n > 0)
            {
                n--;
                letters = (char)('A' + n % 26) + letters;
                n /= 26;
            }
            return letters + row;
        }
    }
}
