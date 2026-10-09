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
            TableStructType = typeof(PlayerConfigMsg),
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
            TableStructType = typeof(StateConfigMsg),
            EntryObjectType = typeof(StateConfigEntryT),
            Validate = ValidateStateConfig,
            PreProcess = GenerateCurves,
        };

        public static readonly TableDef StateTransitionTable = new TableDef
        {
            Name = "状态迁移表",
            Description = "移动区状态×事件策略矩阵，0=KEEP/1=BLOCK/2=INTERRUPT（服务器/客户端共用）",
            ExcelRelativePath = "StateTransitionTable/state_transition_table.xlsx",
            SheetName = "state_transition_table",
            ServerBytesRelativePath = "StateTransitionTable/state_transition_table.bytes",
            ClientBytesAssetPath = "Assets/Resources/Config/StateTransitionTable.bytes",
            GameConfigFolder = "StateTransitionTable",
            RowsAsEntries = true,
            TableObjectType = typeof(TransitionTableMsgT),
            TableStructType = typeof(TransitionTableMsg),
            EntryObjectType = typeof(TransitionEntryT),
            Validate = ValidateTransitionTable,
        };

        public static readonly TableDef PhaseTable = new TableDef
        {
            Name = "实体阶段表",
            Description = "phase 对各系统策略 + forces_move_idle 进入动作 + 合法跳变（服务器）",
            ExcelRelativePath = "StateTransitionTable/state_transition_table.xlsx",
            SheetName = "phase_table",
            ServerBytesRelativePath = "StateTransitionTable/phase_table.bytes",
            ClientBytesAssetPath = null,
            GameConfigFolder = "StateTransitionTable",
            RowsAsEntries = true,
            TableObjectType = typeof(PhaseTableMsgT),
            TableStructType = typeof(PhaseTableMsg),
            EntryObjectType = typeof(PhaseEntryT),
            Validate = ValidatePhaseTable,
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
            TableStructType = typeof(PanelConfigMsg),
            EntryObjectType = typeof(PanelConfigEntryT),
            Validate = ValidatePanelConfig,
        };

        public static readonly TableDef SceneConfig = new TableDef
        {
            Name = "场景配置",
            Description = "场景/zone 注册与体素生成参数，scene_id == Unity 场景名 == 体素文件名（服务器 + 客户端 zone 表）",
            ExcelRelativePath = "Scene/scene_config.xlsx",
            SheetName = "scene_config",
            ServerBytesRelativePath = "Scene/scene_config.bytes",
            ClientBytesAssetPath = "Assets/Resources/Config/SceneConfig.bytes",
            GameConfigFolder = "Scene",
            RowsAsEntries = true,
            TableObjectType = typeof(SceneConfigMsgT),
            TableStructType = typeof(SceneConfigMsg),
            EntryObjectType = typeof(SceneConfigEntryT),
            Validate = ValidateSceneConfig,
        };

        public static void ExportPlayerConfig() => ConfigTableExporter.BatchExport(PlayerConfig);
        public static void ExportStateConfig(bool withCurves) => ConfigTableExporter.BatchExport(StateConfig, withCurves);
        public static void ExportStateTransitionTable() => ConfigTableExporter.BatchExport(StateTransitionTable);
        public static void ExportPhaseTable() => ConfigTableExporter.BatchExport(PhaseTable);
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
            var templateByMap = new Dictionary<string, long>(StringComparer.Ordinal);
            var mapByTemplate = new Dictionary<long, string>();
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

                long template = (long)table.GetFloat(row, "template_id");
                if (template < 1 || template > 65535)
                    throw new InvalidOperationException($"scene_config 第 {row.RowNo} 行 template_id={template} 越界（1~65535）");
                string mapId = TrimZoneSuffix(id);
                if (mapByTemplate.TryGetValue(template, out string owner) && owner != mapId)
                    throw new InvalidOperationException(
                        $"scene_config template_id={template} 同时用于地图 {owner} 和 {mapId}");
                mapByTemplate[template] = mapId;
                if (templateByMap.TryGetValue(mapId, out long existing) && existing != template)
                    throw new InvalidOperationException(
                        $"scene_config 地图 {mapId} 的 template_id 不一致（{existing} vs {template}）");
                templateByMap[mapId] = template;
            }
            if (defaults != 1)
                throw new InvalidOperationException($"scene_config 需要恰好一行 is_default=TRUE，当前 {defaults} 行");
        }

        static string TrimZoneSuffix(string name)
        {
            int us1 = name.LastIndexOf('_');
            if (us1 <= 0)
                return name;
            int us2 = name.LastIndexOf('_', us1 - 1);
            if (us2 <= 0)
                return name;
            return name.Substring(0, us2);
        }

        static string ToSnakeCase(string name) =>
            Regex.Replace(name, @"([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();

        static bool IsSchemaColumn<TEntry>(string column) where TEntry : class
        {
            foreach (var prop in typeof(TEntry).GetProperties())
            {
                if (column == prop.Name || column == ToSnakeCase(prop.Name)) return true;
            }
            return false;
        }

        static List<string> MissingSchemaColumns<TEntry>(GenericTable table) where TEntry : class
        {
            var missing = new List<string>();
            foreach (var prop in typeof(TEntry).GetProperties())
            {
                if (table.Columns.Any(c => c.Name == prop.Name || c.Name == ToSnakeCase(prop.Name))) continue;
                missing.Add(prop.Name);
            }
            return missing;
        }

        static List<string> PhaseTransitionColumns(GenericTable table)
        {
            var columns = new List<string>();
            foreach (var prop in typeof(PhaseEntryT).GetProperties())
            {
                if (prop.Name == "Phase") continue;
                bool verbatim = table.Columns.Any(c => c.Name == prop.Name);
                bool snake = table.Columns.Any(c => c.Name == ToSnakeCase(prop.Name));
                if (verbatim && !snake)
                    columns.Add(prop.Name);
            }
            return columns;
        }

        static void ValidateTransitionTable(GenericTable table)
        {
            var names = new HashSet<string>(MoveStateMappings.Names, StringComparer.Ordinal);
            foreach (ConfigColumn column in table.Columns)
            {
                if (column.Name == "source") continue;
                if (!IsSchemaColumn<TransitionEntryT>(column.Name))
                    throw new InvalidOperationException($"策略矩阵的列名 \"{column.Name}\" 不在 TransitionEntry schema 字段内（事件列名即 schema 字段名）");
            }
            foreach (string missing in MissingSchemaColumns<TransitionEntryT>(table))
                throw new InvalidOperationException($"策略矩阵缺少 schema 字段对应的列：{missing}");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigRowData row in table.Rows)
            {
                string source = table.GetString(row, "source");
                if (!names.Contains(source))
                    throw new InvalidOperationException($"迁移表第 {row.RowNo} 行源状态 \"{source}\" 不在状态名列表内");
                if (!seen.Add(source))
                    throw new InvalidOperationException($"迁移表源状态重复：{source}");

                foreach (ConfigColumn column in table.Columns)
                {
                    if (column.Type != "policy") continue;
                    long policy = (long)table.GetFloat(row, column.Name);
                    if (policy < 0 || policy > 2)
                        throw new InvalidOperationException($"迁移表第 {row.RowNo} 行事件 {column.Name} 的格值 {policy} 非法（0/1/2）");
                }
            }

            foreach (string name in MoveStateMappings.Names)
                if (!seen.Contains(name))
                    throw new InvalidOperationException($"迁移表缺少源状态行：{name}");
        }

        static void ValidatePhaseTable(GenericTable table)
        {
            foreach (ConfigColumn column in table.Columns)
            {
                if (column.Name == "phase") continue;
                if (!IsSchemaColumn<PhaseEntryT>(column.Name))
                    throw new InvalidOperationException($"phase 表的列名 \"{column.Name}\" 不在 PhaseEntry schema 字段内");
            }
            foreach (string missing in MissingSchemaColumns<PhaseEntryT>(table))
                throw new InvalidOperationException($"phase 表缺少 schema 字段对应的列：{missing}");

            var phaseCols = new HashSet<string>(PhaseTransitionColumns(table), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigRowData row in table.Rows)
            {
                string phase = table.GetString(row, "phase");
                if (!phaseCols.Contains(phase))
                    throw new InvalidOperationException($"phase 表第 {row.RowNo} 行 phase \"{phase}\" 不在跳变矩阵列名内");
                if (!seen.Add(phase))
                    throw new InvalidOperationException($"phase 表 phase 重复：{phase}");
            }

            foreach (string name in phaseCols)
                if (!seen.Contains(name))
                    throw new InvalidOperationException($"phase 表缺少 phase 行：{name}");
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
