using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using BigWorldClient.UI.Events;
using BigWorldClient.UI.Framework;

namespace BigWorldClient
{
    /// <summary>
    /// Runtime scene data. Identified by sceneId = templateId + "_" + instanceId.
    /// Owns voxel loading and async Unity scene loading.
    /// Managed by SceneMgr — not a MonoBehaviour.
    /// </summary>
    public class GameScene
    {
        /// <summary>Unique scene identifier: "{templateId}_{instanceId}"</summary>
        public string SceneId { get; }

        /// <summary>Template name (e.g. "MainCity", "Dungeon_001")</summary>
        public string TemplateId { get; }

        /// <summary>Voxel grid loaded from binary. Available after LoadAsync completes.</summary>
        public VoxelGridData VoxelGridData { get; private set; }

        /// <summary>Max step height from config.</summary>
        public float MaxStepHeight { get; }

        private readonly MonoBehaviour runner;

        public GameScene(string sceneId, string templateId, MonoBehaviour coroutineRunner, float maxStepHeight)
        {
            SceneId = sceneId;
            TemplateId = templateId;
            runner = coroutineRunner;
            MaxStepHeight = maxStepHeight;
        }

        /// <summary>
        /// Load voxel binary from Resources/VoxelData/{templateId}_voxels.bytes,
        /// then async load the Unity scene with loading progress UI.
        /// </summary>
        public void LoadAsync()
        {
            if (runner == null) return;
            runner.StartCoroutine(LoadRoutine());
        }

        private IEnumerator LoadRoutine()
        {
            // ── 1. Load voxel data from Resources binary ──
            string resourcePath = $"VoxelData/{TemplateId}_voxels";
            VoxelGridData = VoxelGridData.LoadFromResources(resourcePath);

            // ── 2. Async load Unity scene with loading UI ──
            var ui = UIManager.Instance;
            bool hasLoading = ui != null && ui.HasLoadingPanel();

            if (hasLoading)
            {
                ui.OpenPanel("loading", $"Loading {TemplateId}...");
                yield return null; // one frame for loading UI
            }

            var op = SceneManager.LoadSceneAsync(TemplateId, LoadSceneMode.Single);
            if (op == null)
            {
                if (hasLoading) ui.HidePanel("loading");
                yield break;
            }

            op.allowSceneActivation = true;

            float displayProgress = 0f;
            while (!op.isDone || displayProgress < 1f)
            {
                float actual = op.isDone ? 1f : Mathf.Clamp01(op.progress / 0.9f);
                displayProgress = Mathf.MoveTowards(displayProgress, actual, Time.deltaTime * 1.5f);
                if (!op.isDone && displayProgress < actual)
                    displayProgress = Mathf.Min(displayProgress + Time.deltaTime * 0.2f, actual);

                if (hasLoading) ui.SetLoadingProgress(displayProgress);
                yield return null;
            }

            if (hasLoading) ui.HidePanel("loading");
            UIEventBus.Publish(new SceneLoadCompleteEvent { SceneName = TemplateId });
        }
    }
}
