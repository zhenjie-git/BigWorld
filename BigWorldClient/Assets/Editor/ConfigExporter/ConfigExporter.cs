using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// 客户端与服务器共享配置的导出/导入工具。
    /// 唯一数据源在 <see cref="GameConfigDir"/>（D:/UnityStudy/BigWorld/GameConfig）。
    ///   - 导出：读 Player.asset / StateTransitionTable.asset -> 写 player_config.json / state_transition_table.json
    ///   - 导入：读共享 JSON -> 原地重建两个 .asset（GUID 不变，场景/预制体引用不断）
    /// 运行时代码不受影响。
    /// </summary>
    public static class ConfigExporter
    {
        // ------------------------------------------------------------------ paths
        const string PlayerAssetPath = "Assets/Data/Player.asset";
        const string TransitionAssetPath = "Assets/Data/StateTransitionTable.asset";
        const string PlayerJsonFile = "player_config.json";
        const string TransitionJsonFile = "state_transition_table.json";

        // Assets = .../BigWorldClient/Assets -> ../../GameConfig
        static string GameConfigDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "GameConfig"));

        // ------------------------------------------------------------------ menu
        [MenuItem("BigWorld/Config/导出到共享文件夹")]
        public static void ExportToShared()
        {
            if (!EnsureAssets()) return;
            try
            {
                Directory.CreateDirectory(GameConfigDir);
                WritePlayerConfig();
                WriteTransitionTable();
                Debug.Log($"[ConfigExporter] 已导出到 {GameConfigDir}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ConfigExporter] 导出失败: {e}");
            }
        }

        [MenuItem("BigWorld/Config/从共享文件夹导入")]
        public static void ImportFromShared()
        {
            if (!EnsureAssets()) return;
            try
            {
                ReadPlayerConfig();
                ReadTransitionTable();
                AssetDatabase.SaveAssets();
                Debug.Log($"[ConfigExporter] 已从 {GameConfigDir} 导入并重建 .asset");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ConfigExporter] 导入失败: {e}");
            }
        }

        // ------------------------------------------------------------------ JSON DTOs (snake_case, 与共享 JSON 一一对应)
        [Serializable] public class JsonVec3 { public float x; public float y; public float z; }
        [Serializable] public class JsonKey { public float time; public float value; public float in_tangent; public float out_tangent; public int tangent_mode; public int weighted_mode; public float in_weight; public float out_weight; }
        [Serializable] public class JsonCurve { public int pre_infinity; public int post_infinity; public JsonKey[] keys; }
        [Serializable] public class JsonRotation { public JsonVec3 target_rotation_reach_time; }
        [Serializable] public class JsonWalk { public float speed_modifier; public string curve_x; public string curve_z; }
        [Serializable] public class JsonRun { public float speed_modifier; public string curve_x; public string curve_z; }
        [Serializable] public class JsonDash { public float speed_modifier; public JsonRotation rotation; public float time_to_be_considered_consecutive; public int consecutive_dashed_limit_amount; public float dash_limit_reached_cooldown; public string curve_x; public string curve_z; }
        [Serializable] public class JsonSprint { public float speed_modifier; public float sprint_to_run_time; public float run_to_walk_time; }
        [Serializable] public class JsonStop
        {
            public float light_deceleration_force; public float medium_deceleration_force; public float hard_deceleration_force;
            public string light_curve_x; public string light_curve_z;
            public string medium_curve_x; public string medium_curve_z;
            public string hard_curve_x; public string hard_curve_z;
        }
        [Serializable] public class JsonRoll { public float speed_modifier; }
        [Serializable] public class JsonGrounded
        {
            public float base_speed; public float ground_to_fall_ray_distance;
            public JsonCurve slope_speed_angles; public JsonRotation base_rotation;
            public JsonWalk walk; public JsonRun run; public JsonDash dash;
            public JsonSprint sprint; public JsonStop stop; public JsonRoll roll;
        }
        [Serializable] public class JsonJump
        {
            public JsonRotation rotation;
            public string jump_up_curve_x; public string jump_up_curve_y; public string jump_up_curve_z;
            public string jump_down_curve_x; public string jump_down_curve_y; public string jump_down_curve_z;
        }
        [Serializable] public class JsonFall { public float fall_speed_limit; public float minimum_distance_to_be_considered_hard_fall; public float gravity; }
        [Serializable] public class JsonAirborne { public JsonJump jump; public JsonFall fall; }
        [Serializable] public class JsonCollider { public float height; public float center_y; public float radius; }
        [Serializable] public class JsonSlope { public float step_height_percentage; public float float_ray_distance; public float step_reach_force; }
        [Serializable] public class JsonLayers { public int ground_layer_bits; }
        [Serializable] public class JsonAnimation
        {
            public float transition_duration;
            public string idle; public string walk; public string run; public string sprint; public string dash;
            public string light_stop; public string medium_stop; public string hard_stop;
            public string light_land; public string hard_land; public string roll;
            public string jump_up; public string jump_down; public string fall;
        }
        [Serializable] public class PlayerConfigJson
        {
            public JsonGrounded grounded; public JsonAirborne airborne; public JsonCollider collider;
            public JsonSlope slope; public JsonLayers layers;
            public float voxel_max_step_height; public JsonAnimation animation;
        }
        [Serializable] public class StateTransitionJsonEntry { public string source; public string[] allowed_targets; }
        [Serializable] public class StateTransitionTableJson { public StateTransitionJsonEntry[] entries; }

        // ------------------------------------------------------------------ helpers
        static bool EnsureAssets()
        {
            if (AssetDatabase.LoadAssetAtPath<PlayerConfig>(PlayerAssetPath) == null)
            {
                Debug.LogError($"[ConfigExporter] 找不到 {PlayerAssetPath}");
                return false;
            }
            if (AssetDatabase.LoadAssetAtPath<PlayerStateTransitionTable>(TransitionAssetPath) == null)
            {
                Debug.LogError($"[ConfigExporter] 找不到 {TransitionAssetPath}");
                return false;
            }
            return true;
        }

        static string CurvePath(SerializedProperty p) =>
            p.objectReferenceValue != null ? AssetDatabase.GetAssetPath(p.objectReferenceValue) : "";

        static void SetCurveRef(SerializedProperty p, string path)
        {
            p.objectReferenceValue = string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAssetAtPath<DisplacementCurveAsset>(path);
        }

        static JsonCurve ReadCurve(SerializedProperty prop)
        {
            if (prop == null) return null;
            var json = new JsonCurve();
            json.pre_infinity = prop.FindPropertyRelative("m_PreInfinity").intValue;
            json.post_infinity = prop.FindPropertyRelative("m_PostInfinity").intValue;
            var arr = prop.FindPropertyRelative("m_Curve");
            json.keys = new JsonKey[arr.arraySize];
            for (int i = 0; i < arr.arraySize; i++)
            {
                var k = arr.GetArrayElementAtIndex(i);
                json.keys[i] = new JsonKey
                {
                    time = k.FindPropertyRelative("time").floatValue,
                    value = k.FindPropertyRelative("value").floatValue,
                    in_tangent = k.FindPropertyRelative("inSlope").floatValue,
                    out_tangent = k.FindPropertyRelative("outSlope").floatValue,
                    tangent_mode = k.FindPropertyRelative("tangentMode").intValue,
                    weighted_mode = k.FindPropertyRelative("weightedMode").intValue,
                    in_weight = k.FindPropertyRelative("inWeight").floatValue,
                    out_weight = k.FindPropertyRelative("outWeight").floatValue,
                };
            }
            return json;
        }

        static void WriteCurve(SerializedProperty prop, JsonCurve json)
        {
            if (prop == null || json == null) return;
            prop.FindPropertyRelative("m_PreInfinity").intValue = json.pre_infinity;
            prop.FindPropertyRelative("m_PostInfinity").intValue = json.post_infinity;
            var arr = prop.FindPropertyRelative("m_Curve");
            int n = json.keys != null ? json.keys.Length : 0;
            arr.arraySize = n;
            for (int i = 0; i < n; i++)
            {
                var k = arr.GetArrayElementAtIndex(i);
                var jk = json.keys[i];
                k.FindPropertyRelative("time").floatValue = jk.time;
                k.FindPropertyRelative("value").floatValue = jk.value;
                k.FindPropertyRelative("inSlope").floatValue = jk.in_tangent;
                k.FindPropertyRelative("outSlope").floatValue = jk.out_tangent;
                k.FindPropertyRelative("tangentMode").intValue = jk.tangent_mode;
                k.FindPropertyRelative("weightedMode").intValue = jk.weighted_mode;
                k.FindPropertyRelative("inWeight").floatValue = jk.in_weight;
                k.FindPropertyRelative("outWeight").floatValue = jk.out_weight;
            }
        }

        static JsonVec3 ReadVec3(SerializedProperty p) =>
            new JsonVec3 { x = p.vector3Value.x, y = p.vector3Value.y, z = p.vector3Value.z };

        static void WriteVec3(SerializedProperty p, JsonVec3 v) =>
            p.vector3Value = new Vector3(v.x, v.y, v.z);

        static JsonRotation ReadRotation(SerializedProperty p) =>
            new JsonRotation { target_rotation_reach_time = ReadVec3(p.FindPropertyRelative("<TargetRotationReachTime>k__BackingField")) };

        static void WriteRotation(SerializedProperty p, JsonRotation v)
        {
            if (v == null) return;
            WriteVec3(p.FindPropertyRelative("<TargetRotationReachTime>k__BackingField"), v.target_rotation_reach_time);
        }

        // 确保 [Serializable] 嵌套对象都存在（Unity 对 null 的类字段不序列化，路径会查不到）
        static void SetObject(object target, string field, object value)
        {
            var f = target.GetType().GetField(field,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) f.SetValue(target, value);
        }

        static void EnsureNested(PlayerConfig cfg)
        {
            if (cfg.GroundedData == null) SetObject(cfg, "<GroundedData>k__BackingField", new PlayerGroundedData());
            if (cfg.AirborneData == null) SetObject(cfg, "<AirborneData>k__BackingField", new PlayerAirborneData());
            if (cfg.DefaultColliderData == null) SetObject(cfg, "<DefaultColliderData>k__BackingField", new DefaultColliderData());
            if (cfg.SlopeData == null) SetObject(cfg, "<SlopeData>k__BackingField", new SlopeData());
            if (cfg.LayerData == null) SetObject(cfg, "<LayerData>k__BackingField", new PlayerLayerData());
            if (cfg.AnimationData == null) SetObject(cfg, "<AnimationData>k__BackingField", new PlayerAnimationData());

            var g = cfg.GroundedData;
            if (g.BaseRotationData == null) SetObject(g, "<BaseRotationData>k__BackingField", new PlayerRotationData());
            if (g.IdleData == null) SetObject(g, "<IdleData>k__BackingField", new PlayerIdleData());
            if (g.WalkData == null) SetObject(g, "<WalkData>k__BackingField", new PlayerWalkData());
            if (g.RunData == null) SetObject(g, "<RunData>k__BackingField", new PlayerRunData());
            if (g.DashData == null) SetObject(g, "<DashData>k__BackingField", new PlayerDashData());
            if (g.SprintData == null) SetObject(g, "<SprintData>k__BackingField", new PlayerSprintData());
            if (g.StopData == null) SetObject(g, "<StopData>k__BackingField", new PlayerStopData());
            if (g.RolllData == null) SetObject(g, "<RolllData>k__BackingField", new PlayerRollData());

            if (g.DashData.RotationData == null) SetObject(g.DashData, "<RotationData>k__BackingField", new PlayerRotationData());

            var a = cfg.AirborneData;
            if (a.JumpData == null) SetObject(a, "<JumpData>k__BackingField", new PlayerJumpData());
            if (a.FallData == null) SetObject(a, "<FallData>k__BackingField", new PlayerFallData());
            if (a.JumpData.RotationData == null) SetObject(a.JumpData, "<RotationData>k__BackingField", new PlayerRotationData());
        }

        // ------------------------------------------------------------------ player config
        static string PlayerJsonPath => Path.Combine(GameConfigDir, PlayerJsonFile);

        static void WritePlayerConfig()
        {
            var cfg = AssetDatabase.LoadAssetAtPath<PlayerConfig>(PlayerAssetPath);
            var so = new SerializedObject(cfg);

            var g = so.FindProperty("<GroundedData>k__BackingField");
            var a = so.FindProperty("<AirborneData>k__BackingField");
            var col = so.FindProperty("<DefaultColliderData>k__BackingField");
            var slo = so.FindProperty("<SlopeData>k__BackingField");
            var lay = so.FindProperty("<LayerData>k__BackingField");
            var anim = so.FindProperty("<AnimationData>k__BackingField");

            var dto = new PlayerConfigJson
            {
                grounded = new JsonGrounded
                {
                    base_speed = g.FindPropertyRelative("<BaseSpeed>k__BackingField").floatValue,
                    ground_to_fall_ray_distance = g.FindPropertyRelative("<GroundToFallRayDistance>k__BackingField").floatValue,
                    slope_speed_angles = ReadCurve(g.FindPropertyRelative("<SlopSpeedAngles>k__BackingField")),
                    base_rotation = ReadRotation(g.FindPropertyRelative("<BaseRotationData>k__BackingField")),
                    walk = new JsonWalk
                    {
                        speed_modifier = g.FindPropertyRelative("<WalkData>k__BackingField.<SpeedModifier>k__BackingField").floatValue,
                        curve_x = CurvePath(g.FindPropertyRelative("<WalkData>k__BackingField.<DisplacementCurveX>k__BackingField")),
                        curve_z = CurvePath(g.FindPropertyRelative("<WalkData>k__BackingField.<DisplacementCurveZ>k__BackingField")),
                    },
                    run = new JsonRun
                    {
                        speed_modifier = g.FindPropertyRelative("<RunData>k__BackingField.<SpeedModifier>k__BackingField").floatValue,
                        curve_x = CurvePath(g.FindPropertyRelative("<RunData>k__BackingField.<DisplacementCurveX>k__BackingField")),
                        curve_z = CurvePath(g.FindPropertyRelative("<RunData>k__BackingField.<DisplacementCurveZ>k__BackingField")),
                    },
                    dash = new JsonDash
                    {
                        speed_modifier = g.FindPropertyRelative("<DashData>k__BackingField.<SpeedModifier>k__BackingField").floatValue,
                        rotation = ReadRotation(g.FindPropertyRelative("<DashData>k__BackingField.<RotationData>k__BackingField")),
                        time_to_be_considered_consecutive = g.FindPropertyRelative("<DashData>k__BackingField.<TimeToBeConsideredConsecutive>k__BackingField").floatValue,
                        consecutive_dashed_limit_amount = g.FindPropertyRelative("<DashData>k__BackingField.<ConsecutiveDashedLimitAmount>k__BackingField").intValue,
                        dash_limit_reached_cooldown = g.FindPropertyRelative("<DashData>k__BackingField.<DashLimitReachedCooldown>k__BackingField").floatValue,
                        curve_x = CurvePath(g.FindPropertyRelative("<DashData>k__BackingField.<DisplacementCurveX>k__BackingField")),
                        curve_z = CurvePath(g.FindPropertyRelative("<DashData>k__BackingField.<DisplacementCurveZ>k__BackingField")),
                    },
                    sprint = new JsonSprint
                    {
                        speed_modifier = g.FindPropertyRelative("<SprintData>k__BackingField.<SpeedModifier>k__BackingField").floatValue,
                        sprint_to_run_time = g.FindPropertyRelative("<SprintData>k__BackingField.<SprintToRunTime>k__BackingField").floatValue,
                        run_to_walk_time = g.FindPropertyRelative("<SprintData>k__BackingField.<RunToWalkTime>k__BackingField").floatValue,
                    },
                    stop = new JsonStop
                    {
                        light_deceleration_force = g.FindPropertyRelative("<StopData>k__BackingField.<LightDecelerationForce>k__BackingField").floatValue,
                        medium_deceleration_force = g.FindPropertyRelative("<StopData>k__BackingField.<MediumDecelerationForce>k__BackingField").floatValue,
                        hard_deceleration_force = g.FindPropertyRelative("<StopData>k__BackingField.<HardDecelerationForce>k__BackingField").floatValue,
                        light_curve_x = CurvePath(g.FindPropertyRelative("<StopData>k__BackingField.<LightDisplacementCurveX>k__BackingField")),
                        light_curve_z = CurvePath(g.FindPropertyRelative("<StopData>k__BackingField.<LightDisplacementCurveZ>k__BackingField")),
                        medium_curve_x = CurvePath(g.FindPropertyRelative("<StopData>k__BackingField.<MediumDisplacementCurveX>k__BackingField")),
                        medium_curve_z = CurvePath(g.FindPropertyRelative("<StopData>k__BackingField.<MediumDisplacementCurveZ>k__BackingField")),
                        hard_curve_x = CurvePath(g.FindPropertyRelative("<StopData>k__BackingField.<HardDisplacementCurveX>k__BackingField")),
                        hard_curve_z = CurvePath(g.FindPropertyRelative("<StopData>k__BackingField.<HardDisplacementCurveZ>k__BackingField")),
                    },
                    roll = new JsonRoll
                    {
                        speed_modifier = g.FindPropertyRelative("<RolllData>k__BackingField.<SpeedModifier>k__BackingField").floatValue,
                    },
                },
                airborne = new JsonAirborne
                {
                    jump = new JsonJump
                    {
                        rotation = ReadRotation(a.FindPropertyRelative("<JumpData>k__BackingField.<RotationData>k__BackingField")),
                        jump_up_curve_x = CurvePath(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpUpCurveX>k__BackingField")),
                        jump_up_curve_y = CurvePath(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpUpCurveY>k__BackingField")),
                        jump_up_curve_z = CurvePath(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpUpCurveZ>k__BackingField")),
                        jump_down_curve_x = CurvePath(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpDownCurveX>k__BackingField")),
                        jump_down_curve_y = CurvePath(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpDownCurveY>k__BackingField")),
                        jump_down_curve_z = CurvePath(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpDownCurveZ>k__BackingField")),
                    },
                    fall = new JsonFall
                    {
                        fall_speed_limit = a.FindPropertyRelative("<FallData>k__BackingField.<FallSpeedLimit>k__BackingField").floatValue,
                        minimum_distance_to_be_considered_hard_fall = a.FindPropertyRelative("<FallData>k__BackingField.<MinimumDistanceToBeConsideredHardFall>k__BackingField").floatValue,
                        gravity = a.FindPropertyRelative("<FallData>k__BackingField.<Gravity>k__BackingField").floatValue,
                    },
                },
                collider = new JsonCollider
                {
                    height = col.FindPropertyRelative("<Height>k__BackingField").floatValue,
                    center_y = col.FindPropertyRelative("<CenterY>k__BackingField").floatValue,
                    radius = col.FindPropertyRelative("<Radius>k__BackingField").floatValue,
                },
                slope = new JsonSlope
                {
                    step_height_percentage = slo.FindPropertyRelative("<StepHeightPercentage>k__BackingField").floatValue,
                    float_ray_distance = slo.FindPropertyRelative("<FloatRayDistance>k__BackingField").floatValue,
                    step_reach_force = slo.FindPropertyRelative("<StepReachForce>k__BackingField").floatValue,
                },
                layers = new JsonLayers { ground_layer_bits = LayerBits(lay.FindPropertyRelative("<GroundLayer>k__BackingField")) },
                voxel_max_step_height = so.FindProperty("<VoxelMaxStepHeight>k__BackingField").floatValue,
                animation = new JsonAnimation
                {
                    transition_duration = anim.FindPropertyRelative("transitionDuration").floatValue,
                    idle = anim.FindPropertyRelative("idleAnimationName").stringValue,
                    walk = anim.FindPropertyRelative("walkAnimationName").stringValue,
                    run = anim.FindPropertyRelative("runAnimationName").stringValue,
                    sprint = anim.FindPropertyRelative("sprintAnimationName").stringValue,
                    dash = anim.FindPropertyRelative("dashAnimationName").stringValue,
                    light_stop = anim.FindPropertyRelative("lightStopAnimationName").stringValue,
                    medium_stop = anim.FindPropertyRelative("mediumStopAnimationName").stringValue,
                    hard_stop = anim.FindPropertyRelative("hardStopAnimationName").stringValue,
                    light_land = anim.FindPropertyRelative("lightLandAnimationName").stringValue,
                    hard_land = anim.FindPropertyRelative("hardLandAnimationName").stringValue,
                    roll = anim.FindPropertyRelative("rollAnimationName").stringValue,
                    jump_up = anim.FindPropertyRelative("jumpUpAnimationName").stringValue,
                    jump_down = anim.FindPropertyRelative("jumpDownAnimationName").stringValue,
                    fall = anim.FindPropertyRelative("fallAnimationName").stringValue,
                },
            };

            File.WriteAllText(PlayerJsonPath, JsonUtility.ToJson(dto, true));
        }

        static int LayerBits(SerializedProperty p)
        {
            var bits = p.FindPropertyRelative("m_Bits");
            return bits != null ? bits.intValue : p.intValue;
        }

        static void SetLayerBits(SerializedProperty p, int v)
        {
            var bits = p.FindPropertyRelative("m_Bits");
            if (bits != null) bits.intValue = v; else p.intValue = v;
        }

        static void ReadPlayerConfig()
        {
            var json = JsonUtility.FromJson<PlayerConfigJson>(File.ReadAllText(PlayerJsonPath));
            if (json == null || json.grounded == null) throw new InvalidDataException($"{PlayerJsonPath} 解析失败");

            var cfg = AssetDatabase.LoadAssetAtPath<PlayerConfig>(PlayerAssetPath);
            EnsureNested(cfg);

            var so = new SerializedObject(cfg);
            var g = so.FindProperty("<GroundedData>k__BackingField");
            var a = so.FindProperty("<AirborneData>k__BackingField");
            var col = so.FindProperty("<DefaultColliderData>k__BackingField");
            var slo = so.FindProperty("<SlopeData>k__BackingField");
            var lay = so.FindProperty("<LayerData>k__BackingField");
            var anim = so.FindProperty("<AnimationData>k__BackingField");

            var jg = json.grounded;
            g.FindPropertyRelative("<BaseSpeed>k__BackingField").floatValue = jg.base_speed;
            g.FindPropertyRelative("<GroundToFallRayDistance>k__BackingField").floatValue = jg.ground_to_fall_ray_distance;
            WriteCurve(g.FindPropertyRelative("<SlopSpeedAngles>k__BackingField"), jg.slope_speed_angles);
            WriteRotation(g.FindPropertyRelative("<BaseRotationData>k__BackingField"), jg.base_rotation);
            g.FindPropertyRelative("<WalkData>k__BackingField.<SpeedModifier>k__BackingField").floatValue = jg.walk.speed_modifier;
            SetCurveRef(g.FindPropertyRelative("<WalkData>k__BackingField.<DisplacementCurveX>k__BackingField"), jg.walk.curve_x);
            SetCurveRef(g.FindPropertyRelative("<WalkData>k__BackingField.<DisplacementCurveZ>k__BackingField"), jg.walk.curve_z);
            g.FindPropertyRelative("<RunData>k__BackingField.<SpeedModifier>k__BackingField").floatValue = jg.run.speed_modifier;
            SetCurveRef(g.FindPropertyRelative("<RunData>k__BackingField.<DisplacementCurveX>k__BackingField"), jg.run.curve_x);
            SetCurveRef(g.FindPropertyRelative("<RunData>k__BackingField.<DisplacementCurveZ>k__BackingField"), jg.run.curve_z);
            g.FindPropertyRelative("<DashData>k__BackingField.<SpeedModifier>k__BackingField").floatValue = jg.dash.speed_modifier;
            WriteRotation(g.FindPropertyRelative("<DashData>k__BackingField.<RotationData>k__BackingField"), jg.dash.rotation);
            g.FindPropertyRelative("<DashData>k__BackingField.<TimeToBeConsideredConsecutive>k__BackingField").floatValue = jg.dash.time_to_be_considered_consecutive;
            g.FindPropertyRelative("<DashData>k__BackingField.<ConsecutiveDashedLimitAmount>k__BackingField").intValue = jg.dash.consecutive_dashed_limit_amount;
            g.FindPropertyRelative("<DashData>k__BackingField.<DashLimitReachedCooldown>k__BackingField").floatValue = jg.dash.dash_limit_reached_cooldown;
            SetCurveRef(g.FindPropertyRelative("<DashData>k__BackingField.<DisplacementCurveX>k__BackingField"), jg.dash.curve_x);
            SetCurveRef(g.FindPropertyRelative("<DashData>k__BackingField.<DisplacementCurveZ>k__BackingField"), jg.dash.curve_z);
            g.FindPropertyRelative("<SprintData>k__BackingField.<SpeedModifier>k__BackingField").floatValue = jg.sprint.speed_modifier;
            g.FindPropertyRelative("<SprintData>k__BackingField.<SprintToRunTime>k__BackingField").floatValue = jg.sprint.sprint_to_run_time;
            g.FindPropertyRelative("<SprintData>k__BackingField.<RunToWalkTime>k__BackingField").floatValue = jg.sprint.run_to_walk_time;
            g.FindPropertyRelative("<StopData>k__BackingField.<LightDecelerationForce>k__BackingField").floatValue = jg.stop.light_deceleration_force;
            g.FindPropertyRelative("<StopData>k__BackingField.<MediumDecelerationForce>k__BackingField").floatValue = jg.stop.medium_deceleration_force;
            g.FindPropertyRelative("<StopData>k__BackingField.<HardDecelerationForce>k__BackingField").floatValue = jg.stop.hard_deceleration_force;
            SetCurveRef(g.FindPropertyRelative("<StopData>k__BackingField.<LightDisplacementCurveX>k__BackingField"), jg.stop.light_curve_x);
            SetCurveRef(g.FindPropertyRelative("<StopData>k__BackingField.<LightDisplacementCurveZ>k__BackingField"), jg.stop.light_curve_z);
            SetCurveRef(g.FindPropertyRelative("<StopData>k__BackingField.<MediumDisplacementCurveX>k__BackingField"), jg.stop.medium_curve_x);
            SetCurveRef(g.FindPropertyRelative("<StopData>k__BackingField.<MediumDisplacementCurveZ>k__BackingField"), jg.stop.medium_curve_z);
            SetCurveRef(g.FindPropertyRelative("<StopData>k__BackingField.<HardDisplacementCurveX>k__BackingField"), jg.stop.hard_curve_x);
            SetCurveRef(g.FindPropertyRelative("<StopData>k__BackingField.<HardDisplacementCurveZ>k__BackingField"), jg.stop.hard_curve_z);
            g.FindPropertyRelative("<RolllData>k__BackingField.<SpeedModifier>k__BackingField").floatValue = jg.roll.speed_modifier;

            var ja = json.airborne;
            WriteRotation(a.FindPropertyRelative("<JumpData>k__BackingField.<RotationData>k__BackingField"), ja.jump.rotation);
            SetCurveRef(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpUpCurveX>k__BackingField"), ja.jump.jump_up_curve_x);
            SetCurveRef(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpUpCurveY>k__BackingField"), ja.jump.jump_up_curve_y);
            SetCurveRef(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpUpCurveZ>k__BackingField"), ja.jump.jump_up_curve_z);
            SetCurveRef(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpDownCurveX>k__BackingField"), ja.jump.jump_down_curve_x);
            SetCurveRef(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpDownCurveY>k__BackingField"), ja.jump.jump_down_curve_y);
            SetCurveRef(a.FindPropertyRelative("<JumpData>k__BackingField.<JumpDownCurveZ>k__BackingField"), ja.jump.jump_down_curve_z);
            a.FindPropertyRelative("<FallData>k__BackingField.<FallSpeedLimit>k__BackingField").floatValue = ja.fall.fall_speed_limit;
            a.FindPropertyRelative("<FallData>k__BackingField.<MinimumDistanceToBeConsideredHardFall>k__BackingField").floatValue = ja.fall.minimum_distance_to_be_considered_hard_fall;
            a.FindPropertyRelative("<FallData>k__BackingField.<Gravity>k__BackingField").floatValue = ja.fall.gravity;

            col.FindPropertyRelative("<Height>k__BackingField").floatValue = json.collider.height;
            col.FindPropertyRelative("<CenterY>k__BackingField").floatValue = json.collider.center_y;
            col.FindPropertyRelative("<Radius>k__BackingField").floatValue = json.collider.radius;

            slo.FindPropertyRelative("<StepHeightPercentage>k__BackingField").floatValue = json.slope.step_height_percentage;
            slo.FindPropertyRelative("<FloatRayDistance>k__BackingField").floatValue = json.slope.float_ray_distance;
            slo.FindPropertyRelative("<StepReachForce>k__BackingField").floatValue = json.slope.step_reach_force;

            SetLayerBits(lay.FindPropertyRelative("<GroundLayer>k__BackingField"), json.layers.ground_layer_bits);

            so.FindProperty("<VoxelMaxStepHeight>k__BackingField").floatValue = json.voxel_max_step_height;

            var jn = json.animation;
            anim.FindPropertyRelative("transitionDuration").floatValue = jn.transition_duration;
            anim.FindPropertyRelative("idleAnimationName").stringValue = jn.idle;
            anim.FindPropertyRelative("walkAnimationName").stringValue = jn.walk;
            anim.FindPropertyRelative("runAnimationName").stringValue = jn.run;
            anim.FindPropertyRelative("sprintAnimationName").stringValue = jn.sprint;
            anim.FindPropertyRelative("dashAnimationName").stringValue = jn.dash;
            anim.FindPropertyRelative("lightStopAnimationName").stringValue = jn.light_stop;
            anim.FindPropertyRelative("mediumStopAnimationName").stringValue = jn.medium_stop;
            anim.FindPropertyRelative("hardStopAnimationName").stringValue = jn.hard_stop;
            anim.FindPropertyRelative("lightLandAnimationName").stringValue = jn.light_land;
            anim.FindPropertyRelative("hardLandAnimationName").stringValue = jn.hard_land;
            anim.FindPropertyRelative("rollAnimationName").stringValue = jn.roll;
            anim.FindPropertyRelative("jumpUpAnimationName").stringValue = jn.jump_up;
            anim.FindPropertyRelative("jumpDownAnimationName").stringValue = jn.jump_down;
            anim.FindPropertyRelative("fallAnimationName").stringValue = jn.fall;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(cfg);
        }

        // ------------------------------------------------------------------ state transition table
        static string TransitionJsonPath => Path.Combine(GameConfigDir, TransitionJsonFile);

        static void WriteTransitionTable()
        {
            var tbl = AssetDatabase.LoadAssetAtPath<PlayerStateTransitionTable>(TransitionAssetPath);
            var so = new SerializedObject(tbl);
            var entries = so.FindProperty("Entries");

            var dto = new StateTransitionTableJson { entries = new StateTransitionJsonEntry[entries.arraySize] };
            for (int i = 0; i < entries.arraySize; i++)
            {
                var e = entries.GetArrayElementAtIndex(i);
                var targets = e.FindPropertyRelative("AllowedTargets");
                var names = new string[targets.arraySize];
                for (int j = 0; j < targets.arraySize; j++)
                    names[j] = Enum.GetName(typeof(PlayerMovementStateType), targets.GetArrayElementAtIndex(j).intValue);
                dto.entries[i] = new StateTransitionJsonEntry
                {
                    source = Enum.GetName(typeof(PlayerMovementStateType), e.FindPropertyRelative("SourceState").intValue),
                    allowed_targets = names,
                };
            }

            File.WriteAllText(TransitionJsonPath, JsonUtility.ToJson(dto, true));
        }

        static void ReadTransitionTable()
        {
            var json = JsonUtility.FromJson<StateTransitionTableJson>(File.ReadAllText(TransitionJsonPath));
            if (json == null || json.entries == null) throw new InvalidDataException($"{TransitionJsonPath} 解析失败");

            var tbl = AssetDatabase.LoadAssetAtPath<PlayerStateTransitionTable>(TransitionAssetPath);
            var so = new SerializedObject(tbl);
            var entries = so.FindProperty("Entries");
            entries.arraySize = json.entries.Length;
            for (int i = 0; i < json.entries.Length; i++)
            {
                var e = entries.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("SourceState").intValue =
                    (int)Enum.Parse(typeof(PlayerMovementStateType), json.entries[i].source);
                var targets = e.FindPropertyRelative("AllowedTargets");
                targets.arraySize = json.entries[i].allowed_targets.Length;
                for (int j = 0; j < targets.arraySize; j++)
                    targets.GetArrayElementAtIndex(j).intValue =
                        (int)Enum.Parse(typeof(PlayerMovementStateType), json.entries[i].allowed_targets[j]);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(tbl);
        }
    }
}
