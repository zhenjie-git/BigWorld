using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BigWorldClient.UI.Framework;
using BigWorldClient.UI.Panels;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BigWorldClient
{
    public class SceneStreamer
    {
        public const int LoadRadius = 1;
        public const int UnloadRadius = 2;

        readonly MonoBehaviour _runner;
        readonly int _template;
        readonly ZoneMap _map;
        readonly Dictionary<string, ZoneInfo> _zones = new();
        readonly HashSet<string> _loaded = new();
        readonly HashSet<string> _loading = new();
        readonly List<string> _pendingUnload = new();

        ZoneInfo _center;
        public bool InitialReady { get; private set; }

        public ZoneInfo Center => _center;

        public List<string> LoadedZoneIds()
        {
            return new List<string>(_loaded);
        }

        public SceneStreamer(int template, string mapName, MonoBehaviour runner)
        {
            _template = template;
            _runner = runner;
            _map = ZoneTable.TryGetMap(template, out ZoneMap map) ? map : null;
            if (_map != null)
            {
                foreach (ZoneInfo z in _map.Zones)
                    _zones[z.Name] = z;
            }
        }

        public IEnumerator LoadInitial(SceneID centerSceneID, bool withLoadingPanel)
        {
            _center = ResolveZone(centerSceneID);
            var ui = UIManager.Instance;
            bool hasLoading = withLoadingPanel && ui != null && ui.HasPanel(PanelIds.Loading);
            if (hasLoading)
            {
                ui.OpenPanel(PanelIds.Loading, $"Loading {_map?.MapName ?? centerSceneID.ToString()}...");
                yield return null;
            }

            var queue = new List<ZoneInfo> { _center };
            queue.AddRange(NeighborsOf(_center));

            int done = 0;
            for (int i = 0; i < queue.Count; i++)
            {
                ZoneInfo zone = queue[i];
                AsyncOperation op = LoadZone(zone, i == 0 ? LoadSceneMode.Single : LoadSceneMode.Additive);
                if (op == null)
                    continue;
                while (!op.isDone)
                    yield return null;
                _loaded.Add(zone.Name);
                if (i > 0)
                    DisableDuplicateSceneEssentials();
                done++;

                if (hasLoading)
                {
                    var loading = ui.GetPanel<LoadingPanel>(PanelIds.Loading);
                    if (loading != null)
                        loading.SetProgress(done / (float)queue.Count);
                }
            }

            if (hasLoading)
                ui.HidePanel(PanelIds.Loading);
            InitialReady = true;
        }

        public void SetCenterFromPosition(double x, double z)
        {
            if (!InitialReady)
                return;
            if (!ZoneTable.TryZoneAt(_template, x, z, out ZoneInfo zone))
                return;
            if (zone.SceneID == _center.SceneID)
                return;
            _center = zone;
            RefreshAroundCenter();
        }

        public void SetCenterZone(SceneID sceneID)
        {
            if (!InitialReady)
                return;
            ZoneInfo zone = ResolveZone(sceneID);
            if (zone.SceneID == _center.SceneID)
                return;
            _center = zone;
            RefreshAroundCenter();
        }

        void RefreshAroundCenter()
        {
            foreach (ZoneInfo n in NeighborsOf(_center))
            {
                if (_loaded.Contains(n.Name) || _loading.Contains(n.Name))
                    continue;
                _runner.StartCoroutine(LoadAdditiveRoutine(n));
            }

            foreach (string sceneName in _loaded)
            {
                ZoneInfo z = _zones.TryGetValue(sceneName, out ZoneInfo info) ? info : default;
                int dx = System.Math.Abs(z.GridX - _center.GridX);
                int dz = System.Math.Abs(z.GridZ - _center.GridZ);
                if (System.Math.Max(dx, dz) >= UnloadRadius)
                    _pendingUnload.Add(sceneName);
            }
            foreach (string sceneName in _pendingUnload)
            {
                _loaded.Remove(sceneName);
                AsyncOperation op = SceneManager.UnloadSceneAsync(sceneName);
                if (op != null)
                    _runner.StartCoroutine(EnsureEssentialsAfterUnload(op));
            }
            _pendingUnload.Clear();
        }

        IEnumerator EnsureEssentialsAfterUnload(AsyncOperation op)
        {
            while (!op.isDone)
                yield return null;
            RestoreEssentials();
        }

        IEnumerator LoadAdditiveRoutine(ZoneInfo zone)
        {
            _loading.Add(zone.Name);
            AsyncOperation op = LoadZone(zone, LoadSceneMode.Additive);
            if (op != null)
            {
                while (!op.isDone)
                    yield return null;
                DisableDuplicateSceneEssentials();
            }
            _loading.Remove(zone.Name);
            _loaded.Add(zone.Name);
        }

        AsyncOperation LoadZone(ZoneInfo zone, LoadSceneMode mode)
        {
            if (_loaded.Contains(zone.Name))
                return null;
            AsyncOperation op = SceneManager.LoadSceneAsync(zone.Name, mode);
            if (op == null)
            {
                Debug.LogError($"[SceneStreamer] 场景 {zone.Name} 不在 Build Settings，无法加载");
                return null;
            }
            if (mode == LoadSceneMode.Single)
                _loaded.Add(zone.Name);
            return op;
        }

        ZoneInfo ResolveZone(SceneID sceneID)
        {
            if (ZoneTable.TryGetZone(sceneID, out ZoneInfo zone))
                return zone;
            return default;
        }

        List<ZoneInfo> NeighborsOf(ZoneInfo center)
        {
            if (center.SceneID == default || _map == null)
                return new List<ZoneInfo>();
            return _map.NeighborsWithin(center, LoadRadius);
        }

        public static void DisableExtraDirectionalLights()
        {
            DisableDuplicateSceneEssentials();
        }

        public static void DisableDuplicateSceneEssentials()
        {
            Camera firstCamera = null;
            Light firstDirectional = null;
            foreach (Camera camera in Object.FindObjectsOfType<Camera>())
            {
                if (firstCamera == null)
                {
                    firstCamera = camera;
                    continue;
                }
                if (camera.gameObject.activeSelf)
                    camera.gameObject.SetActive(false);
            }
            foreach (AudioListener listener in Object.FindObjectsOfType<AudioListener>())
            {
                if (firstCamera != null && listener.gameObject == firstCamera.gameObject)
                    continue;
                if (listener.gameObject.activeSelf)
                    listener.enabled = false;
            }
            foreach (Light light in Object.FindObjectsOfType<Light>())
            {
                if (light.type != LightType.Directional)
                    continue;
                if (firstDirectional == null)
                {
                    firstDirectional = light;
                    continue;
                }
                if (light.gameObject.activeSelf)
                    light.gameObject.SetActive(false);
            }
        }

        public static void RestoreEssentials()
        {
            Camera camera = Object.FindObjectsOfType<Camera>().FirstOrDefault(c => c != null && c.isActiveAndEnabled);
            if (camera == null)
            {
                Camera fallback = Object.FindObjectsOfType<Camera>().FirstOrDefault(c => c != null);
                if (fallback != null)
                    fallback.gameObject.SetActive(true);
            }
            Light light = Object.FindObjectsOfType<Light>().FirstOrDefault(l => l != null && l.type == LightType.Directional && l.isActiveAndEnabled);
            if (light == null)
            {
                Light fallback = Object.FindObjectsOfType<Light>().FirstOrDefault(l => l != null && l.type == LightType.Directional);
                if (fallback != null)
                    fallback.gameObject.SetActive(true);
            }
            AudioListener listener = Object.FindObjectsOfType<AudioListener>().FirstOrDefault(l => l != null && l.isActiveAndEnabled);
            if (listener == null)
            {
                AudioListener fallback = Object.FindObjectsOfType<AudioListener>().FirstOrDefault(l => l != null);
                if (fallback != null)
                    fallback.enabled = true;
            }
        }
    }
}
