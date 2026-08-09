using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using BigWorldClient;

public class AnimationCurveExporter : EditorWindow
{
    public AnimationClip sourceClip;
    public bool exportX = true;
    public bool exportY = true;
    public bool exportZ = true;
    public string saveFolder = "Assets/Resources/Animations/DisplacementCurves";
    public bool clearSourceCurvesAfterExport = true;

    [MenuItem("Tools/Export Displacement Curve")]
    static void Init()
    {
        var window = GetWindow<AnimationCurveExporter>();
        window.titleContent = new GUIContent("Curve Exporter");
        window.Show();
    }

    void OnGUI()
    {
        GUILayout.Label("导出动画位移曲线（实际位移值）", EditorStyles.boldLabel);
        sourceClip = (AnimationClip)EditorGUILayout.ObjectField("源动画剪辑", sourceClip, typeof(AnimationClip), false);
        saveFolder = EditorGUILayout.TextField("保存目录", saveFolder);

        GUILayout.Label("选择导出轴：", EditorStyles.boldLabel);
        GUILayout.BeginHorizontal();
        exportX = EditorGUILayout.Toggle("X轴", exportX);
        exportY = EditorGUILayout.Toggle("Y轴", exportY);
        exportZ = EditorGUILayout.Toggle("Z轴", exportZ);
        GUILayout.EndHorizontal();

        clearSourceCurvesAfterExport = EditorGUILayout.Toggle("导出后清除源曲线中已导出轴位移", clearSourceCurvesAfterExport);

        if (GUILayout.Button("导出"))
        {
            if (sourceClip == null)
            {
                EditorUtility.DisplayDialog("错误", "请先拖入动画剪辑", "OK");
                return;
            }
            ExportDisplacementCurve();
        }
    }

    void ExportDisplacementCurve()
    {
        var bindings = AnimationUtility.GetCurveBindings(sourceClip);

        string[] selectedAxes = GetSelectedAxes();
        if (selectedAxes.Length == 0)
        {
            EditorUtility.DisplayDialog("错误", "请至少选择一个轴", "OK");
            return;
        }

        string clipFolder = System.IO.Path.Combine(saveFolder, sourceClip.name);
        if (!System.IO.Directory.Exists(clipFolder))
            System.IO.Directory.CreateDirectory(clipFolder);
        AssetDatabase.Refresh();

        int successCount = 0;
        foreach (string axis in selectedAxes)
        {
            string propertyName = "m_LocalPosition." + axis.ToLower();
            if (TryExportSingleCurve(bindings, propertyName, axis))
                successCount++;
        }

        if (successCount > 0)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (clearSourceCurvesAfterExport)
                ClearSourceCurves();

            EditorUtility.DisplayDialog("成功", $"已导出 {successCount} 条位移曲线", "OK");
        }
    }

    bool TryExportSingleCurve(EditorCurveBinding[] bindings, string propertyName, string axis)
    {
        EditorCurveBinding? targetBinding = null;

        foreach (var binding in bindings)
        {
            if (binding.propertyName == propertyName)
            {
                targetBinding = binding;
                break;
            }
        }

        if (targetBinding == null)
        {
            Debug.LogWarning($"未找到绑定属性：{propertyName}");
            return false;
        }

        AnimationCurve sourceCurve = AnimationUtility.GetEditorCurve(sourceClip, targetBinding.Value);
        if (sourceCurve == null || sourceCurve.keys.Length == 0)
        {
            Debug.LogWarning($"源曲线为空：{propertyName}");
            return false;
        }

        float totalTime = sourceClip.length;
        int samples = Mathf.RoundToInt(totalTime * 60);
        if (samples < 2) samples = 2;

        Keyframe[] keyframes = new Keyframe[samples];

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / (samples - 1) * totalTime;
            float displacement = sourceCurve.Evaluate(t);
            float normalizedTime = t / totalTime;
            keyframes[i] = new Keyframe(normalizedTime, displacement);
        }

        AnimationCurve curve = new(keyframes);
        for (int i = 0; i < curve.keys.Length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
        }

        string axisSavePath = GenerateAxisSavePath(axis);
        var asset = ScriptableObject.CreateInstance<DisplacementCurveAsset>();
        asset.curve = curve;
        AssetDatabase.CreateAsset(asset, axisSavePath);
        Debug.Log($"已导出曲线：{propertyName} -> {axisSavePath}");
        return true;
    }

    string[] GetSelectedAxes()
    {
        var list = new List<string>();
        if (exportX) list.Add("x");
        if (exportY) list.Add("y");
        if (exportZ) list.Add("z");
        return list.ToArray();
    }

    void ClearSourceCurves()
    {
        float totalTime = sourceClip.length;
        string[] axesToClear = GetSelectedAxes();
        int cleared = 0;

        foreach (string axis in axesToClear)
        {
            string propertyName = "m_LocalPosition." + axis;
            EditorCurveBinding? binding = null;
            foreach (var b in AnimationUtility.GetCurveBindings(sourceClip))
            {
                if (b.propertyName == propertyName)
                {
                    binding = b;
                    break;
                }
            }

            if (binding == null)
                continue;

            AnimationCurve sourceCurve = AnimationUtility.GetEditorCurve(sourceClip, binding.Value);
            // Mixamo Hips position curves store ABSOLUTE heights (e.g. standing Y ~0.93), not
            // relative displacements from 0. Replacing them with a 0 constant drops the whole
            // skeleton to y=0 on playback. Instead flatten to the baseline (first-key value):
            // the variation is removed (now driven by the exported curve + Rigidbody), while the
            // hips keep their standing height.
            float baseline = (sourceCurve != null && sourceCurve.keys.Length > 0)
                ? sourceCurve.keys[0].value
                : 0f;

            AnimationCurve baselineCurve = new(
                new Keyframe(0f, baseline),
                new Keyframe(totalTime, baseline)
            );

            Undo.RecordObject(sourceClip, "Clear " + axis.ToUpper() + " Displacement");
            AnimationUtility.SetEditorCurve(sourceClip, binding.Value, baselineCurve);
            cleared++;
        }

        if (cleared > 0)
        {
            EditorUtility.SetDirty(sourceClip);
            AssetDatabase.SaveAssets();
            var clearedAxes = new List<string>();
            foreach (var a in axesToClear) clearedAxes.Add(a.ToUpper());
            Debug.Log($"已清除源动画中 {cleared} 条位移曲线（{string.Join("/", clearedAxes)}轴归基准值，位移由曲线驱动）");
        }
    }

    string GenerateAxisSavePath(string axis)
    {
        string clipName = sourceClip != null ? sourceClip.name : "DisplacementCurve";
        string fileName = clipName + "_" + axis + ".asset";
        return saveFolder + "/" + clipName + "/" + fileName;
    }
}
