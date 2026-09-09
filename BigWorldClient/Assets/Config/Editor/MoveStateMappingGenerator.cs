using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BigWorldClient.Network.Protocol;
using Google.Protobuf.Reflection;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{

    public static class MoveStateMappingGenerator
    {
        const string ExcelRelativePath = "StateConfig/state_config.xlsx";
        const string ClientGenPath = "Assets/Config/MoveStateMappings.gen.cs";
        static string ServerGenPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "BigWorldServer", "World", "MoveStateMapping.gen.go"));

        public static void Generate()
        {
            string xlsxPath = Path.Combine(ConfigTableRegistry.GameConfigDir, ExcelRelativePath);
            if (!File.Exists(xlsxPath))
                throw new FileNotFoundException($"状态配置表不存在: {xlsxPath}", xlsxPath);

            List<string[]> rows = ConfigTableExporter.ReadSheetRows(xlsxPath, "state_config");
            if (rows.Count < 4)
                throw new InvalidOperationException("state_config 需要 3 行表头 + 至少一条数据");

            string[] header = rows[0];
            int nameColumn = Array.IndexOf(header, "state");
            int enumColumn = Array.IndexOf(header, "state_enum");
            if (nameColumn < 0 || enumColumn < 0)
                throw new FormatException("state_config 缺少 state 或 state_enum 列");

            Dictionary<string, string> protoByCs = BuildProtoNames();
            var entries = new List<(string StateName, string CsName, string ProtoName)>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var seenEnums = new HashSet<string>(StringComparer.Ordinal);

            for (int r = 3; r < rows.Count; r++)
            {
                string[] row = rows[r];
                string stateName = nameColumn < row.Length ? (row[nameColumn] ?? "").Trim() : "";
                string protoName = enumColumn < row.Length ? (row[enumColumn] ?? "").Trim() : "";
                if (stateName.Length == 0 && protoName.Length == 0) continue;
                string where = $"state_config 第 {r + 1} 行";

                if (stateName.Length == 0)
                    throw new FormatException($"{where} 缺少 state");
                if (protoName.Length == 0)
                    throw new FormatException($"{where} 缺少 state_enum");
                if (!protoByCs.ContainsValue(protoName))
                    throw new FormatException($"{where} 的 state_enum \"{protoName}\" 不在 MoveState 枚举中");
                if (!seenNames.Add(stateName))
                    throw new FormatException($"{where} 状态名重复: {stateName}");
                if (!seenEnums.Add(protoName))
                    throw new FormatException($"{where} 状态枚举重复: {protoName}");

                string csName = null;
                foreach (var (cs, proto) in protoByCs)
                {
                    if (proto == protoName) { csName = cs; break; }
                }
                entries.Add((stateName, csName, protoName));
            }

            if (entries.Count == 0)
                throw new InvalidOperationException("state_config 没有数据行");

            WriteClient(entries);
            WriteServer(entries);
            Debug.Log($"[Config] MoveState 映射已生成（{entries.Count} 条）:\n{ClientGenPath}\n{ServerGenPath}");
        }

        static Dictionary<string, string> BuildProtoNames()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (FieldInfo field in typeof(MoveState).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = field.GetCustomAttribute<OriginalNameAttribute>();
                if (attr != null)
                    result[field.Name] = attr.Name;
            }
            return result;
        }

        static void WriteClient(List<(string StateName, string CsName, string ProtoName)> entries)
        {
            var sb = new StringBuilder();
            sb.Append("using BigWorldClient.Network.Protocol;\n\n");
            sb.Append("namespace BigWorldClient\n{\n");
            sb.Append("    public static class MoveStateMappings\n    {\n");
            sb.Append("        public static readonly (string Name, MoveState State)[] Mappings =\n        {\n");
            foreach (var e in entries)
                sb.Append($"            (\"{e.StateName}\", MoveState.{e.CsName}),\n");
            sb.Append("        };\n\n");
            sb.Append("        public static readonly string[] Names =\n        {\n");
            foreach (var e in entries)
                sb.Append($"            \"{e.StateName}\",\n");
            sb.Append("        };\n");
            sb.Append("    }\n}\n");
            WriteIfChanged(ClientGenPath, sb.ToString());
            EnsureMeta();
        }

        static void WriteServer(List<(string StateName, string CsName, string ProtoName)> entries)
        {
            var sb = new StringBuilder();
            sb.Append("package main\n\n");
            sb.Append("import pb \"bigworld/common/pb\"\n\n");
            sb.Append("func StateNameToMoveState(name string) (pb.MoveState, bool) {\n");
            sb.Append("\tswitch name {\n");
            foreach (var e in entries)
                sb.Append($"\tcase \"{e.StateName}\":\n\t\treturn pb.MoveState_{e.ProtoName}, true\n");
            sb.Append("\t}\n");
            sb.Append("\treturn 0, false\n");
            sb.Append("}\n");
            WriteIfChanged(ServerGenPath, sb.ToString());
        }

        static void WriteIfChanged(string path, string content)
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            AssetDatabase.Refresh();
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
