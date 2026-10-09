using System;
using System.Collections.Generic;
using BigWorldClient.Network.Protocol;
using Google.FlatBuffers;
using UnityEngine;

namespace BigWorldClient
{
    public struct ZoneInfo
    {
        public SceneID SceneID;
        public string Name;
        public int GridX;
        public int GridZ;
        public double OriginX;
        public double OriginZ;
        public double ZoneSize;
    }

    public class ZoneMap
    {
        public int Template;
        public string MapName;
        public readonly List<ZoneInfo> Zones = new();
        public readonly Dictionary<long, ZoneInfo> ByGridKey = new();

        public bool TryGet(int gx, int gz, out ZoneInfo zone)
        {
            return ByGridKey.TryGetValue(GridKey(gx, gz), out zone);
        }

        public List<ZoneInfo> NeighborsWithin(ZoneInfo center, int radius)
        {
            var result = new List<ZoneInfo>();
            foreach (ZoneInfo z in Zones)
            {
                if (z.SceneID == center.SceneID)
                    continue;
                int dx = System.Math.Abs(z.GridX - center.GridX);
                int dz = System.Math.Abs(z.GridZ - center.GridZ);
                if (dx <= radius && dz <= radius)
                    result.Add(z);
            }
            return result;
        }

        public static long GridKey(int gx, int gz)
        {
            return ((long)gx << 32) | (uint)gz;
        }
    }

    public static class ZoneTable
    {
        static readonly Dictionary<int, ZoneMap> Maps = new();
        static readonly Dictionary<int, ZoneInfo> ByMain = new();
        static readonly Dictionary<string, ZoneInfo> ByName = new();
        static bool _loaded;

        public static bool TryGetZone(SceneID sceneID, out ZoneInfo zone)
        {
            EnsureLoaded();
            return ByMain.TryGetValue(sceneID.Main(), out zone);
        }

        public static bool TryGetMap(int template, out ZoneMap map)
        {
            EnsureLoaded();
            return Maps.TryGetValue(template, out map);
        }

        public static bool TryGetMapOf(SceneID sceneID, out ZoneMap map)
        {
            return TryGetMap(sceneID.Template(), out map);
        }

        public static int TemplateOf(SceneID sceneID) => sceneID.Template();

        public static string NameOf(SceneID sceneID)
        {
            EnsureLoaded();
            if (ByMain.TryGetValue(sceneID.Main(), out ZoneInfo zone))
                return zone.Name;
            if (Maps.TryGetValue(sceneID.Template(), out ZoneMap map))
                return map.MapName;
            return "unknown";
        }

        public static string MapNameOf(SceneID sceneID)
        {
            EnsureLoaded();
            if (Maps.TryGetValue(sceneID.Template(), out ZoneMap map))
                return map.MapName;
            return NameOf(sceneID);
        }

        public static bool TryZoneAt(int template, double x, double z, out ZoneInfo zone)
        {
            zone = default;
            if (!TryGetMap(template, out ZoneMap map))
                return false;
            foreach (ZoneInfo info in map.Zones)
            {
                if (info.ZoneSize <= 0)
                    continue;
                if (x >= info.OriginX && x < info.OriginX + info.ZoneSize && z >= info.OriginZ && z < info.OriginZ + info.ZoneSize)
                {
                    zone = info;
                    return true;
                }
            }
            return false;
        }

        static void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;
            TextAsset asset = Resources.Load<TextAsset>("Config/SceneConfig");
            if (asset == null)
                return;
            try
            {
                SceneConfigMsg msg = SceneConfigMsg.GetRootAsSceneConfigMsg(new ByteBuffer(asset.bytes));
                for (int i = 0; i < msg.EntriesLength; i++)
                {
                    SceneConfigEntry? entry = msg.Entries(i);
                    if (entry == null)
                        continue;
                    SceneConfigEntry e = entry.Value;
                    string name = e.SceneId;
                    if (string.IsNullOrEmpty(name))
                        continue;
                    ZoneInfo zone = new ZoneInfo
                    {
                        SceneID = SceneID.Make((int)e.TemplateId, (int)e.GridX, (int)e.GridZ, 0),
                        Name = name,
                        GridX = (int)e.GridX,
                        GridZ = (int)e.GridZ,
                        OriginX = e.OriginX,
                        OriginZ = e.OriginZ,
                        ZoneSize = e.ZoneSize,
                    };
                    if (!Maps.TryGetValue(zone.SceneID.Template(), out ZoneMap map))
                    {
                        map = new ZoneMap { Template = zone.SceneID.Template(), MapName = name };
                        Maps[zone.SceneID.Template()] = map;
                    }
                    map.Zones.Add(zone);
                    map.ByGridKey[ZoneMap.GridKey(zone.GridX, zone.GridZ)] = zone;
                    ByMain[zone.SceneID.Main()] = zone;
                    ByName[name] = zone;
                }
                foreach (ZoneMap map in Maps.Values)
                {
                    if (map.Zones.Count > 0)
                        map.MapName = TrimZoneSuffix(map.Zones[0].Name);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ZoneTable] 解析 SceneConfig.bytes 失败: {e.Message}");
            }
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
    }
}
