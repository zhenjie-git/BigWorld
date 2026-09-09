using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{

    public static class ConfigSchemaGenerator
    {
        static readonly Dictionary<string, string> TypeMap = new()
        {
            { "float", "float" },
            { "int", "long" },
            { "string", "string" },
            { "bool", "bool" },
            { "string_array", "[string]" },
            { "recentering", "[CameraRecenteringEntry]" },
        };

        [MenuItem("BigWorld/Config/生成配置协议代码")]
        public static void Generate()
        {
            try
            {
                string fbs = BuildSchema();
                string fbsPath = Path.Combine(FbsDir, "config.fbs");
                bool changed = !File.Exists(fbsPath) || File.ReadAllText(fbsPath) != fbs;
                File.WriteAllText(fbsPath, fbs, new UTF8Encoding(false));
                Debug.Log($"[Config] config.fbs {(changed ? "已更新" : "无变化")}：{fbsPath}");

                MessageIdGenerator.Generate();
                RunFlatc();
                AssetDatabase.Refresh();
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog("配置协议代码", "config.fbs 与两端生成代码已更新。", "确定");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Config] 生成配置协议代码失败:\n{e.Message}\n{e.StackTrace}");
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
            }
        }

        static string FbsDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "BigWorldServer", "Common", "proto"));
        static string FlatcPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "BigWorldServer", ".tools", "flatc", "flatc.exe"));
        static string ClientGenDir => Path.Combine(Application.dataPath, "Scripts", "Network", "Generated");
        static string ServerPbDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "BigWorldServer", "Common", "pb"));

        static string BuildSchema()
        {
            var player = ReadColumns(ConfigTableDefinitions.PlayerConfig);
            var state = ReadColumns(ConfigTableDefinitions.StateConfig);
            var trans = ReadColumns(ConfigTableDefinitions.StateTransitionTable);
            var panel = ReadColumns(ConfigTableDefinitions.PanelConfig);
            var scene = ReadColumns(ConfigTableDefinitions.SceneConfig);

            var sb = new StringBuilder();
            sb.Append("namespace BigWorldClient.Network.Protocol;\n\n");
            sb.Append("table CameraRecenteringEntry {\n");
            sb.Append("  minimum_angle: float;\n  maximum_angle: float;\n  wait_time: float;\n  recentering_time: float;\n}\n\n");
            EmitTable(sb, "PlayerConfigMsg", player);
            sb.Append("root_type PlayerConfigMsg;\n\n");
            EmitTable(sb, "StateConfigEntry", state);
            sb.Append("table StateConfigMsg {\n  entries: [StateConfigEntry];\n}\n");
            sb.Append("root_type StateConfigMsg;\n\n");

            bool isMatrix = trans.Count > 0 && trans[0].name == "source" && trans.Skip(1).All(c => c.type == "bool");
            if (isMatrix)
            {
                sb.Append("table TransitionEntry {\n  source: string (required);\n  allowed_targets: [string];\n}\n");
                sb.Append("table TransitionTableMsg {\n  entries: [TransitionEntry];\n}\n");
                sb.Append("root_type TransitionTableMsg;\n\n");
            }
            else
            {
                EmitTable(sb, "TransitionEntry", trans);
                sb.Append("table TransitionTableMsg {\n  entries: [TransitionEntry];\n}\n");
                sb.Append("root_type TransitionTableMsg;\n\n");
            }

            EmitTable(sb, "PanelConfigEntry", panel);
            sb.Append("table PanelConfigMsg {\n  entries: [PanelConfigEntry];\n}\n");
            sb.Append("root_type PanelConfigMsg;\n\n");

            EmitTable(sb, "SceneConfigEntry", scene);
            sb.Append("table SceneConfigMsg {\n  entries: [SceneConfigEntry];\n}\n");
            sb.Append("root_type SceneConfigMsg;\n\n");

            sb.Append("table CurveMsg {\n  t: [double];\n  v: [double];\n}\n");
            sb.Append("root_type CurveMsg;\n");
            return sb.ToString();
        }

        static void EmitTable(StringBuilder sb, string name, List<(string name, string type)> columns)
        {
            sb.Append($"table {name} {{\n");
            foreach (var (cname, ctype) in columns)
            {
                if (!TypeMap.TryGetValue(ctype, out string fbsType))
                    throw new InvalidOperationException($"列 {cname} 的类型 {ctype} 没有 FlatBuffers 映射");
                sb.Append($"  {cname}: {fbsType};\n");
            }
            sb.Append("}\n");
        }

        static List<(string name, string type)> ReadColumns(TableDef def)
        {
            string xlsxPath = Path.Combine(ConfigTableRegistry.GameConfigDir, def.ExcelRelativePath);
            List<string[]> rows = ConfigTableExporter.ReadSheetRows(xlsxPath, def.SheetName);
            if (rows.Count < 2)
                throw new InvalidOperationException($"{def.SheetName} 缺少表头行");

            string[] header = rows[0];
            string[] types = rows[1];
            string[] endpoints = rows.Count > 2 ? rows[2] : new string[header.Length];
            var columns = new List<(string, string)>();
            for (int c = 0; c < header.Length; c++)
            {
                string name = (header[c] ?? "").Trim();
                if (name.Length == 0) continue;
                string endpoint = c < endpoints.Length ? (endpoints[c] ?? "").Trim() : "";
                if (endpoint == "tool") continue;
                columns.Add((name, (types[c] ?? "").Trim()));
            }
            return columns;
        }

        static void RunFlatc()
        {
            if (!File.Exists(FlatcPath))
                throw new FileNotFoundException("flatc 不存在，请确认 BigWorldServer/.tools/flatc/flatc.exe", FlatcPath);

            string tmp = Path.Combine(Path.GetTempPath(), "fbs_gen");
            Directory.CreateDirectory(tmp);
            string fbsPath = Path.Combine(FbsDir, "config.fbs");

            Run(FlatcPath, $"--csharp --gen-object-api -o \"{tmp}\" \"{fbsPath}\"");
            foreach (string file in Directory.EnumerateFiles(tmp, "*.cs", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(ClientGenDir, Path.GetFileName(file)), true);

            string tmpGo = tmp + "_go";
            Directory.CreateDirectory(tmpGo);
            Run(FlatcPath, $"--go --gen-object-api --go-namespace pb -o \"{tmpGo}\" \"{fbsPath}\"");
            foreach (string file in Directory.EnumerateFiles(tmpGo, "*.go", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(ServerPbDir, Path.GetFileName(file)), true);

            Directory.Delete(tmp, true);
            Directory.Delete(tmpGo, true);
            Debug.Log($"[Config] flatc 生成完成 -> {ClientGenDir} + {ServerPbDir}");
        }

        static void Run(string exe, string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"flatc 失败:\n{stderr}");
        }
    }
}
