using System;
using System.Collections.Generic;
using BigWorldClient.Network.Protocol;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{

    public static class ConfigTableDefinitions
    {
        const string AnimsDir = "Assets/Resources/Animations/Player";
        const string CurvesPathPrefix = "Curves/";
        const int SampleRate = 60;
        static readonly string[] Axes = { "x", "y", "z" };
        static readonly Regex PanelIdPattern = new Regex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);
        static readonly string[] PanelLayerNames = { "Background", "Normal", "Popup", "Top" };
        const string PanelPrefabDir = "Assets/Resources/UI/Panels";

        public static readonly TableDef PlayerConfig = new TableDef
        {
            Name = "玩家配置",
            Description = "数值 + 相机回正（服务器/客户端共用）",
            ExcelRelativePath = "Player/player_config.xlsx",
            SheetName = "server_params",
            ServerBytesRelativePath = "Player/player_config.bytes",
            ClientBytesAssetPath = "Assets/Resources/Config/PlayerConfig.bytes",
            GameConfigFolder = "Player",
            RowsAsEntries = false,
            TableObjectType = typeof(PlayerConfigMsgT),
            Validate = ValidatePlayerConfig,
        };

        public static readonly TableDef StateConfig = new TableDef
        {
            Name = "状态配置",
            Description = "动画名 + 位移曲线路径 + 时长/tick/循环（客户端）",
            ExcelRelativePath = "StateConfig/state_config.xlsx",
            SheetName = "state_config",
            ServerBytesRelativePath = "StateConfig/state_config.bytes",
            ClientBytesAssetPath = "Assets/Resources/Config/StateConfig.bytes",
            GameConfigFolder = "StateConfig",
            RowsAsEntries = true,
            ToggleLabel = "生成位移曲线",
            TableObjectType = typeof(StateConfigMsgT),
            EntryObjectType = typeof(StateConfigEntryT),
            Validate = ValidateStateConfig,
            PreProcess = GenerateCurves,
        };

        public static readonly TableDef StateTransitionTable = new TableDef
        {
            Name = "状态迁移表",
            Description = "状态机迁移矩阵（服务器/客户端共用）",
            ExcelRelativePath = "StateTransitionTable/state_transition_table.xlsx",
            SheetName = "state_transition_table",
            ServerBytesRelativePath = "StateTransitionTable/state_transition_table.bytes",
            ClientBytesAssetPath = "Assets/Resources/Config/StateTransitionTable.bytes",
            GameConfigFolder = "StateTransitionTable",
            RowsAsEntries = true,
            TableObjectType = typeof(TransitionTableMsgT),
            EntryObjectType = typeof(TransitionEntryT),
            PostParse = MatrixToAllowedTargets,
            Validate = ValidateTransitionTable,
        };

        public static readonly TableDef PanelConfig = new TableDef
        {
            Name = "面板配置",
            Description = "面板注册与行为开关，预制体按约定放 Resources/UI/Panels/{panel_id}（客户端）",
            ExcelRelativePath = "UI/panel_config.xlsx",
            SheetName = "panel_config",
            ServerBytesRelativePath = null,
            ClientBytesAssetPath = "Assets/Resources/Config/PanelConfig.bytes",
            GameConfigFolder = "UI",
            RowsAsEntries = true,
            TableObjectType = typeof(PanelConfigMsgT),
            EntryObjectType = typeof(PanelConfigEntryT),
            Validate = ValidatePanelConfig,
        };

        public static readonly TableDef SceneConfig = new TableDef
        {
            Name = "场景配置",
            Description = "场景注册与体素生成参数，scene_id == Unity 场景名 == 体素文件名（服务器）",
            ExcelRelativePath = "Scene/scene_config.xlsx",
            SheetName = "scene_config",
            ServerBytesRelativePath = "Scene/scene_config.bytes",
            ClientBytesAssetPath = null,
            GameConfigFolder = "Scene",
            RowsAsEntries = true,
            TableObjectType = typeof(SceneConfigMsgT),
            EntryObjectType = typeof(SceneConfigEntryT),
            Validate = ValidateSceneConfig,
        };

        public static void ExportPlayerConfig() => ConfigTableExporter.BatchExport(PlayerConfig);
        public static void ExportStateConfig(bool withCurves) => ConfigTableExporter.BatchExport(StateConfig, withCurves);
        public static void ExportStateTransitionTable() => ConfigTableExporter.BatchExport(StateTransitionTable);
        public static void ExportPanelConfig() => ConfigTableExporter.BatchExport(PanelConfig);
        public static void ExportSceneConfig() => ConfigTableExporter.BatchExport(SceneConfig);

        static void ValidatePlayerConfig(GenericTable table)
        {
            ConfigRowData row = table.Rows[0];
            void CheckRange(string field, double min, double max)
            {
                double v = table.GetFloat(row, field);
                if (v < min || v > max)
                    throw new InvalidOperationException($"{field} 必须在 [{min}, {max}] 内，当前值 {v}");
            }

            CheckRange("sprint_to_run_time", 0, double.MaxValue);
            CheckRange("fall_speed_limit", 0.0001, double.MaxValue);
            CheckRange("gravity", 0.0001, double.MaxValue);
            CheckRange("collider_height", 0.0001, double.MaxValue);
            CheckRange("collider_center_y", 0.0001, double.MaxValue);
            CheckRange("collider_radius", 0, 5);
            CheckRange("step_height_percentage", 0, 1);
            CheckRange("voxel_max_step_height", 0, double.MaxValue);
            if (table.GetFloat(row, "collider_center_y") > table.GetFloat(row, "collider_height"))
                throw new InvalidOperationException("collider_center_y 不能大于 collider_height");
        }

        static void ValidateStateConfig(GenericTable table)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var protoNames = new HashSet<string>(StringComparer.Ordinal);
            var validEnums = new HashSet<string>();
            foreach (var field in typeof(Network.Protocol.MoveState).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var attr = field.GetCustomAttribute<Google.Protobuf.Reflection.OriginalNameAttribute>();
                if (attr != null) validEnums.Add(attr.Name);
            }

            foreach (ConfigRowData row in table.Rows)
            {
                string state = table.GetString(row, "state");
                if (state.Length == 0)
                    throw new InvalidOperationException($"state_config 第 {row.RowNo} 行缺少状态名");
                if (!names.Add(state))
                    throw new InvalidOperationException($"state_config 状态名重复：{state}");

                string stateEnum = table.GetString(row, "state_enum");
                if (stateEnum.Length == 0)
                    throw new InvalidOperationException($"state_config 第 {row.RowNo} 行缺少 state_enum");
                if (!validEnums.Contains(stateEnum))
                    throw new InvalidOperationException($"state_config 第 {row.RowNo} 行 state_enum \"{stateEnum}\" 不在 MoveState 枚举中");
                if (!protoNames.Add(stateEnum))
                    throw new InvalidOperationException($"state_config 第 {row.RowNo} 行 state_enum 重复：{stateEnum}");

                foreach (string axis in new[] { "curve_x", "curve_y", "curve_z" })
                {
                    string path = table.GetString(row, axis);
                    if (path.Length > 0 && !path.StartsWith(CurvesPathPrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"state_config 第 {row.RowNo} 行 {axis} 的路径 \"{path}\" 必须以 \"{CurvesPathPrefix}\" 开头");
                }

                if (table.GetFloat(row, "duration_seconds") < 0)
                    throw new InvalidOperationException($"state_config 第 {row.RowNo} 行 duration_seconds 不能为负");
                if (table.GetFloat(row, "total_ticks") < 1)
                    throw new InvalidOperationException($"state_config 第 {row.RowNo} 行 total_ticks 必须 ≥ 1");
                if (table.GetFloat(row, "loop") != 0 && table.GetFloat(row, "loop") != 1)
                    throw new InvalidOperationException($"state_config 第 {row.RowNo} 行 loop 必须是 0/1");
                if (table.GetFloat(row, "max_per_frame") < 0)
                    throw new InvalidOperationException($"state_config 第 {row.RowNo} 行 max_per_frame 不能为负");
            }

            foreach (string name in MoveStateMappings.Names)
                if (!names.Contains(name))
                    throw new InvalidOperationException($"state_config 缺少状态行：{name}");
        }

        static void ValidatePanelConfig(GenericTable table)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigRowData row in table.Rows)
            {
                string id = table.GetString(row, "panel_id");
                if (id.Length == 0)
                    throw new InvalidOperationException($"panel_config 第 {row.RowNo} 行缺少 panel_id");
                if (!PanelIdPattern.IsMatch(id))
                    throw new InvalidOperationException($"panel_config 第 {row.RowNo} 行 panel_id \"{id}\" 只允许字母开头的字母/数字/下划线（路径约定要求）");
                if (!ids.Add(id))
                    throw new InvalidOperationException($"panel_config 面板 ID 重复：{id}");

                string layer = table.GetString(row, "layer");
                if (Array.IndexOf(PanelLayerNames, layer) < 0)
                    throw new InvalidOperationException(
                        $"panel_config 第 {row.RowNo} 行 layer \"{layer}\" 无效（支持: {string.Join("/", PanelLayerNames)}）");
                if (table.GetFloat(row, "max_cache_count") < 1)
                    throw new InvalidOperationException($"panel_config 第 {row.RowNo} 行 max_cache_count 必须 ≥ 1");

                string prefabPath = Path.Combine(PanelPrefabDir, id + ".prefab");
                if (!File.Exists(prefabPath))
                    Debug.LogWarning($"[Config] 面板 {id} 的预制体不存在: {prefabPath}");
            }
        }

        static void ValidateSceneConfig(GenericTable table)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int defaults = 0;
            foreach (ConfigRowData row in table.Rows)
            {
                string id = table.GetString(row, "scene_id");
                if (id.Length == 0)
                    throw new InvalidOperationException($"scene_config 第 {row.RowNo} 行缺少 scene_id");
                if (!PanelIdPattern.IsMatch(id))
                    throw new InvalidOperationException($"scene_config 第 {row.RowNo} 行 scene_id \"{id}\" 只允许字母开头的字母/数字/下划线（需与 Unity 场景名一致）");
                if (!ids.Add(id))
                    throw new InvalidOperationException($"scene_config scene_id 重复：{id}");
                if (!File.Exists($"Assets/Scenes/{id}.unity"))
                    Debug.LogWarning($"[Config] 场景 {id} 的场景文件不存在: Assets/Scenes/{id}.unity");
                if (table.GetFloat(row, "is_default") != 0)
                    defaults++;
                if (table.GetFloat(row, "voxel_size_x") <= 0 || table.GetFloat(row, "voxel_size_y") <= 0 || table.GetFloat(row, "voxel_size_z") <= 0)
                    throw new InvalidOperationException($"scene_config 第 {row.RowNo} 行 voxel_size_x/y/z 必须全部 > 0");
                if (table.GetFloat(row, "character_height") < 0.1)
                    throw new InvalidOperationException($"scene_config 第 {row.RowNo} 行 character_height 必须 ≥ 0.1");
            }
            if (defaults != 1)
                throw new InvalidOperationException($"scene_config 需要恰好一行 is_default=TRUE，当前 {defaults} 行");
        }

        static void ValidateTransitionTable(GenericTable table)
        {
            var names = new HashSet<string>(MoveStateMappings.Names, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigRowData row in table.Rows)
            {
                string source = table.GetString(row, "source");
                if (!names.Contains(source))
                    throw new InvalidOperationException($"迁移表第 {row.RowNo} 行源状态 \"{source}\" 不在状态名列表内");
                if (!seen.Add(source))
                    throw new InvalidOperationException($"迁移表源状态重复：{source}");

                var targets = Get(row, "allowed_targets") as List<object>;
                if (targets == null) continue;
                foreach (object target in targets)
                {
                    if (!names.Contains((string)target))
                        throw new InvalidOperationException(
                            $"迁移表第 {row.RowNo} 行目标状态 \"{target}\" 不在状态名列表内");
                }
            }

            foreach (string name in MoveStateMappings.Names)
                if (!seen.Contains(name))
                    throw new InvalidOperationException($"迁移表缺少源状态行：{name}");
        }

        static object Get(ConfigRowData row, string field)
        {
            return row.Values.TryGetValue(field, out object value) ? value : null;
        }

        static GenericTable MatrixToAllowedTargets(GenericTable matrix)
        {
            var states = new HashSet<string>(MoveStateMappings.Names, StringComparer.Ordinal);

            var targetColumns = new List<ConfigColumn>();
            foreach (ConfigColumn column in matrix.Columns)
            {
                if (column.Name == "source") continue;
                if (!states.Contains(column.Name))
                    throw new InvalidOperationException($"迁移矩阵的列名 \"{column.Name}\" 不在状态名列表内（矩阵列名即目标状态名）");
                targetColumns.Add(column);
            }

            var expectedTargets = new HashSet<string>(states, StringComparer.Ordinal);
            var actualTargets = new HashSet<string>(targetColumns.Select(c => c.Name), StringComparer.Ordinal);
            foreach (string missing in expectedTargets.Except(actualTargets).OrderBy(s => s))
                throw new InvalidOperationException($"迁移矩阵缺少目标状态列：{missing}");

            var result = new GenericTable();
            result.Columns.Add(new ConfigColumn { Name = "source", Type = "string", Endpoint = "both" });
            result.Columns.Add(new ConfigColumn { Name = "allowed_targets", Type = "string_array", Endpoint = "both" });

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigRowData row in matrix.Rows)
            {
                string source = matrix.GetString(row, "source");
                if (!states.Contains(source))
                    throw new InvalidOperationException($"迁移矩阵第 {row.RowNo} 行源状态 \"{source}\" 不在状态名列表内");
                if (!seen.Add(source))
                    throw new InvalidOperationException($"迁移矩阵源状态重复：{source}");

                var allowed = new List<object>();
                foreach (ConfigColumn column in targetColumns)
                {
                    if (row.Values[column.Name] is true)
                        allowed.Add(column.Name);
                }

                var newRow = new ConfigRowData { RowNo = row.RowNo };
                newRow.Values["source"] = source;
                newRow.Values["allowed_targets"] = allowed;
                result.Rows.Add(newRow);
            }

            foreach (string name in states)
                if (!seen.Contains(name))
                    throw new InvalidOperationException($"迁移矩阵缺少源状态行：{name}");

            return result;
        }

        static List<string> GenerateCurves(TableDef def, List<string[]> rawRows)
        {
            string xlsxPath = Path.Combine(ConfigTableRegistry.GameConfigDir, def.ExcelRelativePath);
            var header = rawRows[0];
            int colState = Array.IndexOf(header, "state");
            int colAnim = Array.IndexOf(header, "animation_name");
            int[] colCurve = { Array.IndexOf(header, "curve_x"), Array.IndexOf(header, "curve_y"), Array.IndexOf(header, "curve_z") };
            if (colState < 0 || colAnim < 0 || colCurve.Any(c => c < 0))
                throw new InvalidOperationException("state_config 表头缺少 state/animation_name/curve_x/y/z 列");

            var generated = new List<string>();
            for (int r = 3; r < rawRows.Count; r++)
            {
                string[] row = rawRows[r];
                string animationName = Cell(row, colAnim);
                if (animationName.Length == 0) continue;

                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Path.Combine(AnimsDir, animationName + ".anim"));
                for (int i = 0; i < Axes.Length; i++)
                {
                    AnimationCurve curve = clip != null ? ExtractCurve(clip, Axes[i]) : null;
                    string relativePath = "";
                    if (curve != null)
                    {
                        relativePath = $"{CurvesPathPrefix}{animationName}/{animationName}_{Axes[i]}";
                        WriteCurveBytes(ConfigTableRegistry.GameConfigDir, relativePath, curve);
                        WriteCurveBytes(Application.dataPath + "/Resources", relativePath, curve);
                        generated.Add(relativePath + ".bytes");
                    }
                    rawRows[r] = SetCell(row, colCurve[i], relativePath);
                }
            }

            var notes = ConfigTableExporter.ReadSheetRows(xlsxPath, "说明");
            LightExcelWriter.Write(xlsxPath, new[] { (def.SheetName, rawRows), ("说明", notes) });
            AssetDatabase.Refresh();
            return generated;
        }

        static string Cell(string[] row, int index)
        {
            return row != null && index >= 0 && index < row.Length ? (row[index] ?? "").Trim() : "";
        }

        static string[] SetCell(string[] row, int index, string value)
        {
            if (index >= row.Length)
                Array.Resize(ref row, index + 1);
            row[index] = value;
            return row;
        }

        static AnimationCurve ExtractCurve(AnimationClip clip, string axis)
        {
            EditorCurveBinding? found = null;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.propertyName == "m_LocalPosition." + axis)
                {
                    found = binding;
                    break;
                }
            }
            if (found == null) return null;

            AnimationCurve source = AnimationUtility.GetEditorCurve(clip, found.Value);
            if (source == null || source.keys.Length == 0) return null;

            float totalTime = clip.length;
            int samples = Mathf.RoundToInt(totalTime * SampleRate);
            if (samples < 2) samples = 2;

            var keyframes = new Keyframe[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / (samples - 1) * totalTime;
                keyframes[i] = new Keyframe(t / totalTime, source.Evaluate(t));
            }

            var curve = new AnimationCurve(keyframes);
            for (int i = 0; i < curve.keys.Length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
            }
            return curve;
        }

        static void WriteCurveBytes(string rootDir, string relativePath, AnimationCurve curve)
        {
            string fullPath = Path.Combine(rootDir, relativePath + ".bytes");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? rootDir);

            var t = new CurveMsgT();
            foreach (Keyframe key in curve.keys)
            {
                t.T.Add(key.time);
                t.V.Add(key.value);
            }
            var builder = new Google.FlatBuffers.FlatBufferBuilder(1024);
            builder.Finish(CurveMsg.Pack(builder, t).Value);
            File.WriteAllBytes(fullPath, builder.SizedByteArray());
        }
    }
}
