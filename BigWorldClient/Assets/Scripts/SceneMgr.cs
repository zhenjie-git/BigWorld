using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// Central scene registry. Manages GameScene instances by sceneId.
    /// sceneId = templateId + "_" + instanceId.
    /// Not a MonoBehaviour — initialized with a coroutine runner.
    /// </summary>
    public class SceneMgr
    {
        private static SceneMgr instance;
        public static SceneMgr Instance => instance ??= new SceneMgr();

        private readonly Dictionary<string, GameScene> scenes = new();
        private readonly Dictionary<string, int> instanceCounters = new();

        private MonoBehaviour runner;

        /// <summary>Must be called before creating any scenes.</summary>
        public void Initialize(MonoBehaviour coroutineRunner)
        {
            runner = coroutineRunner;
        }

        /// <summary>
        /// Create a new scene instance with unique sceneId.
        /// Call scene.LoadAsync() to start voxel + Unity scene loading.
        /// </summary>
        public GameScene CreateScene(string templateId, float maxStepHeight)
        {
            instanceCounters.TryGetValue(templateId, out int count);
            count++;
            instanceCounters[templateId] = count;

            string sceneId = $"{templateId}_{count}";
            var scene = new GameScene(sceneId, templateId, runner, maxStepHeight);
            scenes[sceneId] = scene;

            {}
            return scene;
        }

        /// <summary>Get scene by its full sceneId.</summary>
        public GameScene GetScene(string sceneId)
        {
            scenes.TryGetValue(sceneId, out var scene);
            return scene;
        }

        /// <summary>Remove a scene instance.</summary>
        public void RemoveScene(string sceneId)
        {
            scenes.Remove(sceneId);
        }
    }
}
