using System.Collections.Generic;
using System.Collections;
using System;
using UnityEngine;

namespace BigWorldClient
{

    public class GameScene
    {

        public int Template { get; }

        public SceneID ServerScene { get; private set; }

        public string MapName { get; }

        public float MaxStepHeight { get; }

        public Vector3 ServerSpawn { get; }

        public bool Loaded { get; private set; }

        public string PlayerPrefabPath { get; set; }

        public Action<GameScene, PlayerController> SceneEntered { get; set; }

        public List<VoxelGridData> VoxelGrids { get; } = new();

        readonly MonoBehaviour _runner;
        readonly Dictionary<string, VoxelGridData> _voxelByZone = new();
        SceneStreamer _streamer;
        bool _unloadStarted;

        public GameScene(SceneID serverScene, Vector3 serverSpawn, MonoBehaviour coroutineRunner, float maxStepHeight)
        {
            ServerScene = serverScene;
            Template = serverScene.Template();
            MapName = ZoneTable.MapNameOf(serverScene);
            ServerSpawn = serverSpawn;
            _runner = coroutineRunner;
            MaxStepHeight = maxStepHeight;
            _streamer = new SceneStreamer(Template, MapName, _runner);
        }

        public void LoadAsync()
        {
            if (_runner == null) return;
            _runner.StartCoroutine(LoadRoutine());
        }

        IEnumerator LoadRoutine()
        {
            EnsureVoxelGrid(ServerScene);
            if (ZoneTable.TryGetZone(ServerScene, out ZoneInfo center))
            {
                foreach (ZoneInfo zone in NeighborZones(center))
                    EnsureVoxelGrid(zone.SceneID);
            }

            yield return _streamer.LoadInitial(ServerScene, true);
            if (_unloadStarted) yield break;

            WorldPortalVisuals.EnsureForScene(MapName);
            Loaded = true;

            var prefab = Resources.Load<GameObject>(PlayerPrefabPath);
            SceneEntered?.Invoke(this, SpawnPlayer(prefab));
        }

        public PlayerController SpawnPlayer(GameObject prefab)
        {
            var existing = UnityEngine.Object.FindObjectOfType<PlayerController>();
            if (existing != null)
                return existing;

            if (prefab == null)
                return null;

            var playerGO = UnityEngine.Object.Instantiate(prefab, ServerSpawn, Quaternion.identity);
            playerGO.name = "Player";
            return playerGO.GetComponent<PlayerController>();
        }

        public void ActivatePlayer(PlayerController controller, string worldId)
        {
            controller.Player.SceneTemplate = Template;
            controller.Player.WorldId = worldId;
            controller.InitPrediction(ServerSpawn.x, ServerSpawn.z);

            var cameraController = UnityEngine.Object.FindObjectOfType<CameraController>();
            if (cameraController != null)
            {
                var cameraLookPoint = controller.transform.Find("CameraLookPoint");
                cameraController.SetTarget(cameraLookPoint != null
                    ? cameraLookPoint.transform
                    : controller.transform);
            }
        }

        public void UpdateCenterZone(SceneID sceneID)
        {
            EnsureVoxelGrid(sceneID);
            _streamer.SetCenterZone(sceneID);
            ServerScene = sceneID;
        }

        public void UpdateCenterFromPosition(double x, double z)
        {
            _streamer.SetCenterFromPosition(x, z);
        }

        public void UnloadZones()
        {
            if (_unloadStarted) return;
            _unloadStarted = true;
            foreach (string sceneName in _streamer.LoadedZoneIds())
            {
                if (UnityEngine.SceneManagement.SceneManager.GetSceneByName(sceneName).IsValid())
                    _ = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(sceneName);
            }
        }

        List<ZoneInfo> NeighborZones(ZoneInfo center)
        {
            if (!ZoneTable.TryGetMap(Template, out ZoneMap map))
                return new List<ZoneInfo>();
            return map.NeighborsWithin(center, SceneStreamer.LoadRadius);
        }

        void EnsureVoxelGrid(SceneID sceneID)
        {
            if (!ZoneTable.TryGetZone(sceneID, out ZoneInfo zone))
                return;
            if (_voxelByZone.ContainsKey(zone.Name))
                return;
            VoxelGridData grid = VoxelGridData.LoadFromResources($"VoxelData/{zone.Name}_voxels");
            if (grid == null)
            {
                Debug.LogWarning($"[GameScene] 缺少体素数据 VoxelData/{zone.Name}_voxels");
                return;
            }
            _voxelByZone[zone.Name] = grid;
            VoxelGrids.Add(grid);
        }
    }
}
