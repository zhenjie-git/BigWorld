using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// 状态迁移表的 Excel 导表工具。
    ///
    /// 唯一录入源：GameConfig/StateTransitionTable/state_transition_table.xlsx
    /// 生成物：  GameConfig/StateTransitionTable/state_transition_table.json（服务器读）
    ///           Assets/Resources/Config/StateTransitionTable.json（客户端读）
    ///
    /// Excel 约定：
    ///   - 工作表名必须为 state_transition_table
    ///   - 第 1 行 B 列起是目标状态名，A 列第 2 行起是源状态名
    ///   - 状态名必须与 MoveTransitionTable.StateNames 完全一致，一个都不能少
    ///   - 交叉格填 1（或 x/X/y/Y/√/是/true/yes）表示允许迁移；留空或 0/false/no/否/-/× 表示禁止
    ///   - 其余单元格值视为笔误，导表会直接失败
    /// </summary>
    public static class StateTransitionTableExcelExporter
    {
        const string ExcelFileName = "state_transition_table.xlsx";
        const string GameConfigJsonFileName = "state_transition_table.json";
        const string ClientJsonAssetPath = "Assets/Resources/Config/StateTransitionTable.json";
        const string SheetName = "state_transition_table";

        static readonly string[] CanonicalStates = MoveTransitionTable.StateNames;

        static readonly HashSet<string> AllowedMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "x", "y", "√", "是", "true", "yes",
        };

        static readonly HashSet<string> DisallowedMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "false", "no", "否", "-", "×",
        };

        [Serializable]
        public class Entry
        {
            public string source;
            public string[] allowed_targets;
        }

        [Serializable]
        public class StateTransitionTable
        {
            public Entry[] entries;
        }

        static string GameConfigDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "GameConfig"));

        static string ExcelPath => Path.Combine(GameConfigDir, "StateTransitionTable", ExcelFileName);
        static string SharedJsonPath => Path.Combine(GameConfigDir, "StateTransitionTable", GameConfigJsonFileName);
        static string ClientJsonPath => Path.Combine(Application.dataPath, "Resources", "Config", "StateTransitionTable.json");

        [MenuItem("BigWorld/Config/从Excel导出状态迁移表")]
        public static void ExportFromExcel()
        {
            try
            {
                ExportCore();
                Debug.Log($"[Config] 状态迁移表已从 {ExcelPath} 导出：\n" +
                          $"  -> {SharedJsonPath}\n" +
                          $"  -> {ClientJsonAssetPath}");
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog("状态迁移表", "已从 Excel 导出到 GameConfig 与客户端 Resources。", "确定");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Config] 从 Excel 导出状态迁移表失败: {e.Message}\n{e.StackTrace}");
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog("状态迁移表导出失败", e.Message, "确定");
                else
                    EditorApplication.Exit(1);
            }
        }

        static void ExportCore()
        {
            if (!File.Exists(ExcelPath))
                throw new FileNotFoundException("Excel 源文件不存在，请确认路径", ExcelPath);

            if (CanonicalStates == null || CanonicalStates.Length == 0)
                throw new InvalidOperationException("MoveTransitionTable.StateNames 为空，请先修复客户端状态映射");

            var cells = LightExcel.ReadSheetCells(ExcelPath, SheetName);

            // ── 表头：第 1 行 B 列起 = 目标状态 ──
            var targets = new Dictionary<int, string>(); // col -> name
            foreach (var pair in cells)
            {
                var loc = SplitLocation(pair.Key);
                if (loc.row == 1 && loc.col >= 2)
                    targets[loc.col] = pair.Value.Trim();
            }

            // ── 行表头：A 列第 2 行起 = 源状态 ──
            var sources = new Dictionary<int, string>(); // row -> name
            foreach (var pair in cells)
            {
                var loc = SplitLocation(pair.Key);
                if (loc.col == 1 && loc.row >= 2)
                    sources[loc.row] = pair.Value.Trim();
            }

            var targetValues = targets.OrderBy(p => p.Key).Select(p => p.Value).ToArray();
            var sourceValues = sources.OrderBy(p => p.Key).Select(p => p.Value).ToArray();
            ValidateHeaders(targetValues, "目标状态");
            ValidateHeaders(sourceValues, "源状态");
            ValidateNoOrphanCells(cells, sources, targets);

            var sourceRowByName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in sources)
                sourceRowByName[row.Value] = row.Key;

            var targetColByName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var col in targets)
                targetColByName[col.Value] = col.Key;

            var entries = new List<Entry>();
            foreach (string source in CanonicalStates)
            {
                int row = sourceRowByName[source];
                var allowedTargets = new List<string>();

                foreach (string target in CanonicalStates)
                {
                    int col = targetColByName[target];
                    string key = $"{row}:{col}";
                    if (!cells.TryGetValue(key, out string raw) || raw == null)
                        continue; // 空单元格 = 禁止

                    if (IsAllowed(raw, key))
                        allowedTargets.Add(target);
                }

                entries.Add(new Entry { source = source, allowed_targets = allowedTargets.ToArray() });
            }

            var json = JsonUtility.ToJson(new StateTransitionTable { entries = entries.ToArray() }, true);
            var utf8NoBom = new UTF8Encoding(false);

            Directory.CreateDirectory(Path.GetDirectoryName(SharedJsonPath) ?? GameConfigDir);
            File.WriteAllText(SharedJsonPath, json, utf8NoBom);

            Directory.CreateDirectory(Path.GetDirectoryName(ClientJsonPath) ?? Application.dataPath);
            File.WriteAllText(ClientJsonPath, json, utf8NoBom);

            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(ClientJsonAssetPath, ImportAssetOptions.ForceUpdate);
        }

        static void ValidateHeaders(IEnumerable<string> values, string role)
        {
            var list = values.ToList();
            var actual = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = new List<string>();

            for (int i = 0; i < list.Count; i++)
            {
                string value = list[i];
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidDataException($"{role}第 {i + 1} 项是空白单元格，请填写状态名");

                if (!actual.Add(value)) duplicates.Add(value);
            }

            var expected = new HashSet<string>(CanonicalStates, StringComparer.Ordinal);
            if (duplicates.Count > 0)
                throw new InvalidDataException($"{role}出现重复：{string.Join(", ", duplicates.Distinct())}");

            if (!actual.SetEquals(expected))
            {
                var missing = expected.Except(actual).OrderBy(s => s).ToArray();
                var unknown = actual.Except(expected).OrderBy(s => s).ToArray();
                throw new InvalidDataException(
                    $"{role}与 MoveTransitionTable.StateNames 不一致（该列表与服务器 StateNameToMoveState 同步维护）。" +
                    (missing.Length > 0 ? $" 缺少：{string.Join(", ", missing)}。" : "") +
                    (unknown.Length > 0 ? $" 未知：{string.Join(", ", unknown)}。" : ""));
            }
        }

        static void ValidateNoOrphanCells(
            Dictionary<string, string> cells,
            Dictionary<int, string> sources,
            Dictionary<int, string> targets)
        {
            var sourceRows = new HashSet<int>(sources.Keys);
            var targetCols = new HashSet<int>(targets.Keys);

            foreach (var pair in cells)
            {
                var loc = SplitLocation(pair.Key);
                bool isLabel = loc.row == 1 && loc.col == 1;
                bool isTargetHeader = loc.row == 1 && loc.col >= 2;
                bool isSourceHeader = loc.col == 1 && loc.row >= 2;
                bool isMatrixCell = loc.row >= 2 && loc.col >= 2
                                    && sourceRows.Contains(loc.row)
                                    && targetCols.Contains(loc.col);
                if (isLabel || isTargetHeader || isSourceHeader || isMatrixCell) continue;

                throw new InvalidDataException(
                    $"单元格 {ToExcelRef(loc.row, loc.col)} 不在声明的状态矩阵内（A1 标签、第 1 行表头、A 列表头或合法交叉格之外的内容都会被拒绝）");
            }
        }

        static bool IsAllowed(string raw, string cellLocation)
        {
            string value = raw.Trim();
            if (value.Length == 0) return false;

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                if (Math.Abs(number - 1.0) < 0.000001) return true;
                if (Math.Abs(number) < 0.000001) return false;
                throw MarkerError(raw, cellLocation);
            }

            if (DisallowedMarkers.Contains(value)) return false;
            if (AllowedMarkers.Contains(value)) return true;

            throw MarkerError(raw, cellLocation);
        }

        static InvalidDataException MarkerError(string raw, string cellLocation)
        {
            var loc = ParseLocation(cellLocation);
            return new InvalidDataException(
                $"单元格 {ToExcelRef(loc.row, loc.col)} 的值 \"{raw}\" 无法识别。" +
                $"允许：1、{string.Join("/", AllowedMarkers)}；禁止：留空、0、{string.Join("/", DisallowedMarkers)}");
        }

        static (int row, int col) SplitLocation(string key)
        {
            int sep = key.IndexOf(':');
            if (sep < 0 || !int.TryParse(key.Substring(0, sep), out int row) || !int.TryParse(key.Substring(sep + 1), out int col))
                throw new InvalidDataException($"内部单元格坐标异常: {key}");
            return (row, col);
        }

        static (int row, int col) ParseLocation(string key) => SplitLocation(key);

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
