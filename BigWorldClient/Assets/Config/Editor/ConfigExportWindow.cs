using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{

    public class ConfigExportWindow : EditorWindow
    {
        [MenuItem("BigWorld/Config/配置导出中心")]
        static void Open()
        {
            GetWindow<ConfigExportWindow>("配置导出");
        }

        Vector2 scroll;
        string logText = "";

        void OnGUI()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("一键导出全部", GUILayout.Height(28)))
                    ExportAll();
                if (GUILayout.Button("刷新", GUILayout.Height(28), GUILayout.Width(60)))
                    Repaint();
            }

            string gameConfigDir = ConfigTableRegistry.GameConfigDir;
            bool dirExists = Directory.Exists(gameConfigDir);
            EditorGUILayout.LabelField(
                dirExists ? $"GameConfig: {gameConfigDir}" : $"GameConfig 目录不存在: {gameConfigDir}",
                dirExists ? EditorStyles.miniLabel : ErrorStyle);
            EditorGUILayout.Space();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (ConfigTableEntry entry in ConfigTableRegistry.Entries)
                DrawEntry(entry);
            DrawVoxelCard();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            GUILayout.Label("日志", EditorStyles.boldLabel);
            logText = EditorGUILayout.TextArea(logText, GUILayout.MinHeight(90));
        }

        void DrawEntry(ConfigTableEntry entry)
        {
            string excelPath = Path.Combine(ConfigTableRegistry.GameConfigDir, entry.ExcelRelativePath);
            bool excelExists = File.Exists(excelPath);
            string ok = SessionState.GetString(StatusKey(entry, "ok"), "");
            string time = SessionState.GetString(StatusKey(entry, "time"), "");

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(entry.Name, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (ok == "")
                        GUILayout.Label("未导出", GreyStyle, GUILayout.Width(90));
                    else if (ok == "1")
                        GUILayout.Label($"● 成功 {time}", GreenStyle, GUILayout.Width(110));
                    else
                        GUILayout.Label($"✖ 失败 {time}", ErrorStyle, GUILayout.Width(110));
                }

                GUILayout.Label($"{entry.Description}\n{entry.ExcelRelativePath}", EditorStyles.miniLabel);

                if (!excelExists)
                    EditorGUILayout.HelpBox($"Excel 不存在：{excelPath}", MessageType.Warning);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!excelExists))
                    {
                        if (GUILayout.Button("打开Excel"))
                            EditorUtility.RevealInFinder(excelPath);
                        if (GUILayout.Button("打开生成目录"))
                            EditorUtility.RevealInFinder(
                                Path.Combine(ConfigTableRegistry.GameConfigDir, entry.GameConfigFolder));
                    }
                    GUILayout.FlexibleSpace();
                    if (entry.ToggleLabel != null)
                    {
                        bool toggle = SessionState.GetBool(StatusKey(entry, "toggle"), false);
                        bool newToggle = GUILayout.Toggle(toggle, entry.ToggleLabel, GUILayout.Width(110));
                        if (newToggle != toggle)
                            SessionState.SetBool(StatusKey(entry, "toggle"), newToggle);
                    }
                    if (GUILayout.Button("导出", GUILayout.Width(70)))
                        ExportOne(entry);
                }
            }
            EditorGUILayout.Space(4);
        }

        bool ExportOne(ConfigTableEntry entry)
        {
            bool withCurves = entry.ToggleLabel != null && SessionState.GetBool(StatusKey(entry, "toggle"), false);
            ExportResult result = entry.Export(withCurves);
            return LogResult(entry.Name, result, "ConfigExport." + entry.Name);
        }

        bool LogResult(string name, ExportResult result, string statusPrefix)
        {
            SessionState.SetString(statusPrefix + ".ok", result.Success ? "1" : "0");
            SessionState.SetString(statusPrefix + ".time", System.DateTime.Now.ToString("HH:mm:ss"));
            SessionState.SetString(statusPrefix + ".msg", result.Message);

            string head = $"[{System.DateTime.Now:HH:mm:ss}] {name}：{(result.Success ? "成功" : "失败")}";
            logText = head + "\n" + result.Message + "\n\n" + logText;
            if (logText.Length > 8000)
                logText = logText.Substring(0, 8000);

            if (result.Success)
                Debug.Log($"[Config] {head}\n{result.Message}");
            else
                Debug.LogError($"[Config] {head}\n{result.Message}");
            return result.Success;
        }

        const string VoxelScenePrefKey = "ConfigExport.VoxelSceneId";
        const string VoxelStatusPrefix = "ConfigExport.体素生成";

        void DrawVoxelCard()
        {
            string selected = EditorPrefs.GetString(VoxelScenePrefKey, "");
            string ok = SessionState.GetString(VoxelStatusPrefix + ".ok", "");
            string time = SessionState.GetString(VoxelStatusPrefix + ".time", "");

            List<VoxelSceneExporter.SceneRow> sceneRows = null;
            string sceneError = null;
            try
            {
                sceneRows = VoxelSceneExporter.ReadSceneRows();
            }
            catch (Exception e)
            {
                sceneError = e.Message;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("体素生成", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (ok == "")
                        GUILayout.Label("未生成", GreyStyle, GUILayout.Width(90));
                    else if (ok == "1")
                        GUILayout.Label($"● 成功 {time}", GreenStyle, GUILayout.Width(110));
                    else
                        GUILayout.Label($"✖ 失败 {time}", ErrorStyle, GUILayout.Width(110));
                }

                GUILayout.Label(
                    "按 scene_config.xlsx 行生成体素，输出 GameConfig/{scene_id}_voxels.bytes 与 Resources/VoxelData 双份；\n连通性步高单源取 player_config 的 voxel_max_step_height",
                    EditorStyles.miniLabel);

                if (sceneError != null)
                {
                    EditorGUILayout.HelpBox(sceneError, MessageType.Warning);
                }
                else if (sceneRows.Count == 0)
                {
                    EditorGUILayout.HelpBox("scene_config.xlsx 没有数据行", MessageType.Warning);
                }
                else
                {
                    if (sceneRows.Find(s => s.SceneId == selected) == null)
                        selected = sceneRows[0].SceneId;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string[] ids = new string[sceneRows.Count];
                        for (int i = 0; i < sceneRows.Count; i++)
                            ids[i] = sceneRows[i].SceneId;
                        int index = EditorGUILayout.Popup("场景", Array.IndexOf(ids, selected), ids);
                        if (index < 0) index = 0;
                        if (ids[index] != selected)
                        {
                            selected = ids[index];
                            EditorPrefs.SetString(VoxelScenePrefKey, selected);
                        }

                        using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                        {
                            if (GUILayout.Button("生成体素", GUILayout.Width(70)))
                            {
                                ExportResult result = VoxelSceneExporter.GenerateForScene(selected);
                                LogResult("体素生成", result, VoxelStatusPrefix);
                            }
                            if (GUILayout.Button("全部生成", GUILayout.Width(70)))
                            {
                                ExportResult result = VoxelSceneExporter.GenerateAll();
                                LogResult("体素生成", result, VoxelStatusPrefix);
                            }
                            if (GUILayout.Button("可视化", GUILayout.Width(60)))
                            {
                                ExportResult result = VoxelSceneExporter.Visualize(selected);
                                LogResult("体素生成", result, VoxelStatusPrefix);
                            }
                        }
                    }
                }
            }
            EditorGUILayout.Space(4);
        }

        void ExportAll()
        {
            var entries = ConfigTableRegistry.Entries;
            int failed = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                EditorUtility.DisplayProgressBar("配置导出",
                    $"导出 {entries[i].Name}（{i + 1}/{entries.Length}）", (float)i / entries.Length);
                if (!ExportOne(entries[i]))
                    failed++;
            }
            EditorUtility.ClearProgressBar();

            string summary = failed == 0
                ? $"全部导出成功（{entries.Length} 张表）"
                : $"导出完成：{entries.Length - failed} 成功 / {failed} 失败";
            logText = $"[{System.DateTime.Now:HH:mm:ss}] {summary}\n\n" + logText;
            ShowNotification(new GUIContent(summary));
        }

        static string StatusKey(ConfigTableEntry entry, string field)
        {
            return $"ConfigExport.{entry.Name}.{field}";
        }

        static GUIStyle _greenStyle;
        static GUIStyle GreenStyle => _greenStyle ??= new GUIStyle(EditorStyles.miniLabel)
            { normal = { textColor = new Color(0.2f, 0.7f, 0.2f) } };

        static GUIStyle _greyStyle;
        static GUIStyle GreyStyle => _greyStyle ??= new GUIStyle(EditorStyles.miniLabel)
            { normal = { textColor = Color.grey } };

        static GUIStyle _errorStyle;
        static GUIStyle ErrorStyle => _errorStyle ??= new GUIStyle(EditorStyles.miniLabel)
            { normal = { textColor = new Color(0.85f, 0.25f, 0.2f) } };
    }
}
