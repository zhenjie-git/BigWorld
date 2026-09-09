using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace BigWorldClient
{

    public static class MessageIdGenerator
    {
        static readonly Regex NamePattern = new Regex(@"^[A-Za-z0-9]+2[A-Za-z0-9]+_[A-Za-z0-9_]+$", RegexOptions.Compiled);

        const string ExcelRelativePath = "Protocol/message_types.xlsx";

        static string ClientGenPath => Path.Combine(Application.dataPath, "Scripts", "Network", "MessageTypes.gen.cs");
        static string ServerGenPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "BigWorldServer", "Common", "MessageType.go"));

        public static void Generate()
        {
            string xlsxPath = Path.Combine(ConfigTableRegistry.GameConfigDir, ExcelRelativePath);
            if (!File.Exists(xlsxPath))
                throw new FileNotFoundException("消息号表不存在，请确认 GameConfig/Protocol/message_types.xlsx", xlsxPath);

            List<(string Name, int Id)> entries = ReadEntries(xlsxPath);
            WriteClient(entries);
            WriteServer(entries);
            EnsureMeta();
            Debug.Log($"[Config] 消息号已生成（{entries.Count} 条）:\n{ClientGenPath}\n{ServerGenPath}");
        }

        static List<(string, int)> ReadEntries(string xlsxPath)
        {
            List<string[]> rows = ConfigTableExporter.ReadSheetRows(xlsxPath, "message_types");
            var entries = new List<(string, int)>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<int>();

            for (int r = 1; r < rows.Count; r++)
            {
                string[] row = rows[r];
                string name = row.Length > 0 ? row[0].Trim() : "";
                string idText = row.Length > 1 ? row[1].Trim() : "";
                if (name.Length == 0 && idText.Length == 0) continue;

                if (name.Length == 0)
                    throw new FormatException($"message_types 第 {r + 1} 行缺少常量名");
                if (!NamePattern.IsMatch(name))
                    throw new FormatException($"message_types 第 {r + 1} 行 \"{name}\" 不符合 源2目标_ 命名");
                if (!int.TryParse(idText, out int id) || id < 1 || id > 65535)
                    throw new FormatException($"message_types 第 {r + 1} 行 \"{name}\" 的消息号 \"{idText}\" 无效（须为 1~65535）");
                if (!names.Add(name))
                    throw new FormatException($"message_types 常量名重复：{name}");
                if (!ids.Add(id))
                    throw new FormatException($"message_types 消息号 {id} 重复（{name}）");

                entries.Add((name, id));
            }

            if (entries.Count == 0)
                throw new InvalidOperationException("message_types 没有数据行");
            return entries;
        }

        static void WriteClient(List<(string Name, int Id)> entries)
        {
            var sb = new StringBuilder();
            sb.Append("namespace BigWorldClient.Network\n{\n");
            sb.Append("    public static class MessageTypes\n    {\n");
            foreach (var (name, id) in entries)
                sb.Append($"        public const int {name} = {id};\n");
            sb.Append("    }\n}\n");
            File.WriteAllText(ClientGenPath, sb.ToString(), new UTF8Encoding(false));
        }

        static void WriteServer(List<(string Name, int Id)> entries)
        {
            int width = 0;
            foreach (var (name, _) in entries)
                width = Math.Max(width, name.Length);

            var sb = new StringBuilder();
            sb.Append("package common\n\n");
            sb.Append("type MessageType uint16\n\n");
            sb.Append("const (\n");
            foreach (var (name, id) in entries)
                sb.Append($"\t{name.PadRight(width)} MessageType = {id}\n");
            sb.Append(")\n");
            File.WriteAllText(ServerGenPath, sb.ToString(), new UTF8Encoding(false));
        }

        static void EnsureMeta()
        {
            string metaPath = ClientGenPath + ".meta";
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
