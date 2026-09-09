using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{

    public class SceneMgr
    {
        private static SceneMgr _instance;
        public static SceneMgr Instance => _instance ??= new SceneMgr();

        private readonly Dictionary<string, GameScene> _scenes = new();
        private readonly Dictionary<string, int> _instanceCounters = new();

        private MonoBehaviour _runner;

        public void Initialize(MonoBehaviour coroutineRunner)
        {
            _runner = coroutineRunner;
        }

        public GameScene CreateScene(string templateId, float maxStepHeight)
        {
            _instanceCounters.TryGetValue(templateId, out int count);
            count++;
            _instanceCounters[templateId] = count;

            string sceneId = $"{templateId}_{count}";
            var scene = new GameScene(sceneId, templateId, _runner, maxStepHeight);
            _scenes[sceneId] = scene;

            {}
            return scene;
        }

        public GameScene GetScene(string sceneId)
        {
            _scenes.TryGetValue(sceneId, out var scene);
            return scene;
        }

        public void RemoveScene(string sceneId)
        {
            _scenes.Remove(sceneId);
        }
    }
}
