using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{

    public class SceneMgr
    {
        private static SceneMgr _instance;
        public static SceneMgr Instance => _instance ??= new SceneMgr();

        private readonly Dictionary<int, GameScene> _scenes = new();

        private MonoBehaviour _runner;

        public void Initialize(MonoBehaviour coroutineRunner)
        {
            _runner = coroutineRunner;
        }

        public GameScene CreateScene(SceneID serverScene, Vector3 serverSpawn, float maxStepHeight)
        {
            int template = serverScene.Template();
            var scene = new GameScene(serverScene, serverSpawn, _runner, maxStepHeight);
            _scenes[template] = scene;
            return scene;
        }

        public GameScene GetScene(int template)
        {
            _scenes.TryGetValue(template, out var scene);
            return scene;
        }

        public void RemoveScene(int template)
        {
            _scenes.Remove(template);
        }
    }
}
