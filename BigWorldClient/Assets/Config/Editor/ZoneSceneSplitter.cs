using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BigWorldClient
{
    public static class ZoneSceneSplitter
    {
        const string SceneAssetDir = "Assets/Scenes";

        public sealed class ZoneRow
        {
            public string SceneId;
            public string MapId;
            public int GridX;
            public int GridZ;
            public float OriginX;
            public float OriginZ;
            public float ZoneSize;
        }

        [MenuItem("BigWorld/Config/世界地图拆分")]
        public static void SplitAllMenu()
        {
            string error = SplitAll();
            if (error == null)
                EditorUtility.DisplayDialog("世界地图拆分", "zone 场景拆分完成。", "确定");
            else
                EditorUtility.DisplayDialog("世界地图拆分", $"拆分失败:\n{error}", "确定");
        }

        public static string SplitAll()
        {
            try
            {
                List<ZoneRow> zones = ReadZoneRows();
                var maps = zones.GroupBy(z => z.MapId).ToList();
                int created = 0;
                foreach (var group in maps)
                {
                    List<ZoneRow> mapZones = group.ToList();
                    if (mapZones.Count <= 1)
                        continue;
                    string sourcePath = $"{SceneAssetDir}/{group.Key}.unity";
                    if (!File.Exists(sourcePath))
                        return $"地图 {group.Key} 的源场景不存在: {sourcePath}";
                    string err = SplitMap(sourcePath, mapZones);
                    if (err != null)
                        return err;
                    created += mapZones.Count;
                }
                EnsureBuildSettings(zones);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log($"[ZoneSplitter] 拆分完成，生成 {created} 个 tile 场景");
                return null;
            }
            catch (Exception e)
            {
                return $"{e.Message}\n{e.StackTrace}";
            }
        }

        static List<ZoneRow> ReadZoneRows()
        {
            var rows = VoxelSceneExporter.ReadSceneRowsWithGrid();
            if (rows.Count == 0)
                throw new InvalidDataException("scene_config 没有数据行");
            var zones = new List<ZoneRow>(rows.Count);
            foreach (VoxelSceneExporter.SceneRow row in rows)
            {
                zones.Add(new ZoneRow
                {
                    SceneId = row.SceneId,
                    MapId = row.MapId,
                    GridX = row.GridX,
                    GridZ = row.GridZ,
                    OriginX = row.OriginX,
                    OriginZ = row.OriginZ,
                    ZoneSize = row.ZoneSize,
                });
            }
            return zones;
        }

        static string SplitMap(string sourcePath, List<ZoneRow> mapZones)
        {
            foreach (ZoneRow zone in mapZones)
            {
                string tilePath = $"{SceneAssetDir}/{zone.SceneId}.unity";
                File.Copy(sourcePath, tilePath, true);
            }
            AssetDatabase.Refresh();
            foreach (ZoneRow zone in mapZones)
                AssetDatabase.ImportAsset($"{SceneAssetDir}/{zone.SceneId}.unity");

            foreach (ZoneRow zone in mapZones)
            {
                string tilePath = $"{SceneAssetDir}/{zone.SceneId}.unity";
                Scene tile = EditorSceneManager.OpenScene(tilePath, OpenSceneMode.Additive);
                if (!tile.IsValid() || !tile.isLoaded)
                    return $"tile 场景打开失败: {tilePath}";

                int kept = 0;
                foreach (GameObject root in tile.GetRootGameObjects().ToList())
                {
                    if (IsSharedRoot(root))
                    {
                        kept++;
                        continue;
                    }
                    List<Transform> children = new();
                    for (int i = 0; i < root.transform.childCount; i++)
                        children.Add(root.transform.GetChild(i));
                    foreach (Transform child in children)
                    {
                        ZoneRow owner = OwnerZoneOf(child, mapZones);
                        if (owner == null || owner.SceneId == zone.SceneId)
                        {
                            kept++;
                            continue;
                        }
                        UnityEngine.Object.DestroyImmediate(child.gameObject);
                    }
                    if (OwnerZoneOf(root.transform, mapZones)?.SceneId != zone.SceneId && IsEmptyTransformRoot(root))
                        UnityEngine.Object.DestroyImmediate(root);
                }

                EditorSceneManager.SaveScene(tile);
                EditorSceneManager.CloseScene(tile, true);
                Debug.Log($"[ZoneSplitter] {zone.SceneId}: 保留 {kept} 个对象");
            }
            return null;
        }

        static bool IsEmptyTransformRoot(GameObject root)
        {
            if (root.transform.childCount > 0)
                return false;
            foreach (Component c in root.GetComponents<Component>())
            {
                if (!(c is Transform))
                    return false;
            }
            return true;
        }

        static bool IsSharedRoot(GameObject root)
        {
            if (root.GetComponent<Camera>() != null)
                return true;
            Light light = root.GetComponent<Light>();
            if (light != null && light.type == LightType.Directional)
                return true;
            return false;
        }

        static ZoneRow OwnerZoneOf(Transform t, List<ZoneRow> mapZones)
        {
            Renderer renderer = t.GetComponentInChildren<Renderer>();
            Vector3 center = renderer != null ? renderer.bounds.center : t.position;
            ZoneRow best = null;
            float bestDist = float.MaxValue;
            foreach (ZoneRow z in mapZones)
            {
                if (z.ZoneSize <= 0)
                    continue;
                int gx = Mathf.FloorToInt((center.x - MapOriginX(z)) / z.ZoneSize);
                int gz = Mathf.FloorToInt((center.z - MapOriginZ(z)) / z.ZoneSize);
                if (gx == z.GridX && gz == z.GridZ)
                    return z;
                float dx = Mathf.Abs(center.x - (z.OriginX + z.ZoneSize * 0.5f));
                float dz = Mathf.Abs(center.z - (z.OriginZ + z.ZoneSize * 0.5f));
                float dist = dx + dz;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = z;
                }
            }
            return best;
        }

        static float MapOriginX(ZoneRow z) => z.OriginX - z.GridX * z.ZoneSize;
        static float MapOriginZ(ZoneRow z) => z.OriginZ - z.GridZ * z.ZoneSize;

        static void EnsureBuildSettings(List<ZoneRow> zones)
        {
            var multiZoneMaps = new HashSet<string>(
                zones.GroupBy(z => z.MapId).Where(g => g.Count() > 1).Select(g => g.Key));
            var scenes = EditorBuildSettings.scenes.ToList();
            bool changed = false;
            for (int i = scenes.Count - 1; i >= 0; i--)
            {
                string fileName = Path.GetFileNameWithoutExtension(scenes[i].path);
                if (multiZoneMaps.Contains(fileName))
                {
                    scenes.RemoveAt(i);
                    changed = true;
                }
            }
            foreach (ZoneRow zone in zones)
            {
                string path = $"{SceneAssetDir}/{zone.SceneId}.unity";
                if (!File.Exists(path))
                    continue;
                if (scenes.Any(s => s.path == path))
                    continue;
                scenes.Add(new EditorBuildSettingsScene(path, true));
                changed = true;
            }
            if (changed)
                EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
