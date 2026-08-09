using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// 把客户端位移曲线导出成服务器用的 displacement_curves.json，直接写入共享
    /// GameConfig 文件夹（服务器唯一数据源）。替代原 BigWorldServer/tools 下的
    /// export_displacement Go 工具，schema 完全一致：
    ///   { fixed_delta, margin, states: { "MOVE_*": { duration, curves:{x/z/y:[[t,v]...]} } | { max_per_frame } } }
    /// </summary>
    public static class DisplacementCurveExporter
    {
        const string CurvesDir = "Assets/Resources/Animations/DisplacementCurves";
        const string AnimsDir = "Assets/Resources/Animations/Player";
        const string OutFileName = "displacement_curves.json";

        const double FixedDelta = 0.02;
        const double Margin = 2.0;

        // 与旧 Go 工具的 specs 保持一致：有曲线的状态带 curves+anim，无曲线的用 max_per_frame。
        static readonly (string name, string[] curves, string anim, double maxFrame)[] Specs =
        {
            ("MOVE_IDLE",       null,                          null,  0.02),
            ("MOVE_WALK",       new[]{"Walk/Walk_x.asset", "Walk/Walk_z.asset"}, "Walk", 0),
            ("MOVE_RUN",        new[]{"Run/Run_x.asset", "Run/Run_z.asset"}, "Run", 0),
            ("MOVE_SPRINT",     null,                          null,  0.2),
            ("MOVE_STOP_LIGHT", new[]{"LightStop/LightStop_x.asset", "LightStop/LightStop_z.asset"}, "LightStop", 0),
            ("MOVE_STOP_MED",   new[]{"MediumStop/MediumStop_x.asset", "MediumStop/MediumStop_z.asset"}, "MediumStop", 0),
            ("MOVE_STOP_HARD",  null,                          null,  0.05),
            ("MOVE_LAND_LIGHT", null,                          null,  0.05),
            ("MOVE_LAND_HARD",  null,                          null,  0.05),
            ("MOVE_ROLL",       null,                          null,  0.15),
            ("MOVE_DASH",       new[]{"Dash/Dash_x.asset", "Dash/Dash_z.asset"}, "Dash", 0),
            ("MOVE_JUMP_UP",    new[]{"JumpUp/JumpUp_x.asset", "JumpUp/JumpUp_y.asset", "JumpUp/JumpUp_z.asset"}, "JumpUp", 0),
            ("MOVE_FALL",       null,                          null,  0.1),
            ("MOVE_JUMP_DOWN",  new[]{"JumpDown/JumpDown_x.asset", "JumpDown/JumpDown_y.asset", "JumpDown/JumpDown_z.asset"}, "JumpDown", 0),
        };

        static string GameConfigDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "GameConfig"));

        [MenuItem("BigWorld/Config/导出位移曲线到共享文件夹")]
        public static void ExportDisplacement()
        {
            try
            {
                Directory.CreateDirectory(GameConfigDir);
                string json = BuildJson();
                string outPath = Path.Combine(GameConfigDir, OutFileName);
                File.WriteAllText(outPath, json);
                Debug.Log($"[DisplacementCurveExporter] 已导出位移曲线到 {outPath}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DisplacementCurveExporter] 导出失败: {e.Message}");
            }
        }

        static string BuildJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"fixed_delta\": {Num(FixedDelta)},");
            sb.AppendLine($"  \"margin\": {Num(Margin)},");
            sb.AppendLine("  \"states\": {");

            for (int i = 0; i < Specs.Length; i++)
            {
                var spec = Specs[i];
                bool last = i == Specs.Length - 1;
                sb.AppendLine($"    \"{spec.name}\": {{");

                if (spec.curves == null)
                {
                    sb.AppendLine($"      \"max_per_frame\": {Num(spec.maxFrame)}");
                }
                else
                {
                    double dur = ParseStopTime(Path.Combine(AnimsDir, spec.anim + ".anim"));
                    sb.AppendLine($"      \"duration\": {Num(dur)},");
                    sb.AppendLine("      \"curves\": {");
                    for (int c = 0; c < spec.curves.Length; c++)
                    {
                        string axis = spec.curves[c].Substring(spec.curves[c].LastIndexOf('_') + 1);
                        axis = axis.Substring(0, axis.IndexOf('.'));
                        sb.AppendLine($"        \"{axis}\": [");
                        var keys = ReadCurveKeys(Path.Combine(CurvesDir, spec.curves[c]));
                        for (int k = 0; k < keys.Count; k++)
                        {
                            bool kLast = k == keys.Count - 1;
                            sb.AppendLine($"          [{Num(keys[k].x)}, {Num(keys[k].y)}]" + (kLast ? "" : ","));
                        }
                        sb.AppendLine("        ]" + (c == spec.curves.Length - 1 ? "" : ","));
                    }
                    sb.AppendLine("      }");
                }
                sb.AppendLine("    }" + (last ? "" : ","));
            }

            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // 读 DisplacementCurveAsset 的 AnimationCurve 键 (time, value)
        static System.Collections.Generic.List<Vector2> ReadCurveKeys(string assetPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<DisplacementCurveAsset>(assetPath);
            if (asset == null || asset.curve == null)
                throw new System.IO.FileNotFoundException($"曲线资产缺失: {assetPath}");
            var keys = new System.Collections.Generic.List<Vector2>();
            foreach (var kf in asset.curve.keys)
                keys.Add(new Vector2(kf.time, kf.value));
            return keys;
        }

        // 解析 .anim 的 m_StopTime: 作为状态时长（与旧工具一致）
        static double ParseStopTime(string animPath)
        {
            if (!File.Exists(animPath))
                throw new System.IO.FileNotFoundException($"动画文件缺失: {animPath}");
            foreach (string rawLine in File.ReadAllLines(animPath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("m_StopTime:"))
                {
                    string v = line.Substring("m_StopTime:".Length).Trim();
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        return d;
                }
            }
            throw new System.Exception($"m_StopTime 未找到: {animPath}");
        }

        // 数字格式化：整数不带小数位，其余最多 8 位小数去尾零
        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            if (v == System.Math.Floor(v) && System.Math.Abs(v) < 9e15)
                return v.ToString("0", CultureInfo.InvariantCulture);
            return v.ToString("0.########", CultureInfo.InvariantCulture);
        }
    }
}
