using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{

    public static class PanelIdsGenerator
    {
        static readonly Regex IdPattern = new Regex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

        const string ExcelRelativePath = "UI/panel_config.xlsx";
        const string GenPath = "Assets/Scripts/UI/PanelIds.gen.cs";

        public static void Generate()
        {
            string xlsxPath = Path.Combine(ConfigTableRegistry.GameConfigDir, ExcelRelativePath);
            if (!File.Exists(xlsxPath))
                throw new FileNotFoundException($"面板配置表不存在: {xlsxPath}", xlsxPath);

            List<string[]> rows = ConfigTableExporter.ReadSheetRows(xlsxPath, "panel_config");
            if (rows.Count < 4)
                throw new InvalidOperationException("panel_config 需要 3 行表头 + 至少一条数据");

            string[] header = rows[0];
            int idColumn = Array.IndexOf(header, "panel_id");
            if (idColumn < 0)
                throw new FormatException("panel_config 缺少 panel_id 列");

            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int r = 3; r < rows.Count; r++)
            {
                string[] row = rows[r];
                string id = idColumn < row.Length ? (row[idColumn] ?? "").Trim() : "";
                if (id.Length == 0) continue;

                if (!IdPattern.IsMatch(id))
                    throw new FormatException($"panel_config 第 {r + 1} 行 panel_id \"{id}\" 只允许字母开头的字母/数字/下划线");
                if (!seen.Add(id))
                    throw new FormatException($"panel_config 面板 ID 重复: {id}");
                ids.Add(id);
            }

            if (ids.Count == 0)
                throw new InvalidOperationException("panel_config 没有数据行");

            var sb = new StringBuilder();
            sb.Append("namespace BigWorldClient.UI.Framework\n{\n");
            sb.Append("    public static class PanelIds\n    {\n");
            foreach (string id in ids)
                sb.Append($"        public const string {ToPascal(id)} = \"{id}\";\n");
            sb.Append("    }\n}\n");
            string content = sb.ToString();
            bool changed = !File.Exists(GenPath) || File.ReadAllText(GenPath) != content;
            if (changed)
            {
                File.WriteAllText(GenPath, content, new UTF8Encoding(false));
                EnsureMeta();
                AssetDatabase.Refresh();
            }
            Debug.Log($"[Config] PanelIds {(changed ? "已更新" : "无变化")}（{ids.Count} 条）: {GenPath}");
        }

        static string ToPascal(string id)
        {
            var sb = new StringBuilder();
            foreach (string part in id.Split('_', '-', ' '))
            {
                if (part.Length == 0) continue;
                sb.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
            }
            return sb.ToString();
        }

        static void EnsureMeta()
        {
            string metaPath = GenPath + ".meta";
            if (File.Exists(metaPath)) return;
            File.WriteAllText(metaPath,
                "fileFormatVersion: 2\n" +
                $"guid: {Guid.NewGuid():N}\n" +
                "MonoImporter:\n" +
                "  externalObjects: {}\n" +
                "  serializedVersion: 2\n" +
                "  defaultReferences: []\n" +
                "  executionOrder: 0\n" +
                "  icon: {instanceID: 0}\n" +
                "  userData: \n" +
                "  assetBundleName: \n" +
                "  assetBundleVariant: \n");
        }
    }
}
