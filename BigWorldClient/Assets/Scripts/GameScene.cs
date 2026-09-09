using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using BigWorldClient.UI.Events;
using BigWorldClient.UI.Framework;
using BigWorldClient.UI.Panels;

namespace BigWorldClient
{

    public class GameScene
    {

        public string SceneId { get; }

        public string TemplateId { get; }

        public VoxelGridData VoxelGridData { get; private set; }

        public float MaxStepHeight { get; }

        private readonly MonoBehaviour _runner;

        public GameScene(string sceneId, string templateId, MonoBehaviour coroutineRunner, float maxStepHeight)
        {
            SceneId = sceneId;
            TemplateId = templateId;
            _runner = coroutineRunner;
            MaxStepHeight = maxStepHeight;
        }

        public void LoadAsync()
        {
            if (_runner == null) return;
            _runner.StartCoroutine(LoadRoutine());
        }

        private IEnumerator LoadRoutine()
        {

            string resourcePath = $"VoxelData/{TemplateId}_voxels";
            VoxelGridData = VoxelGridData.LoadFromResources(resourcePath);

            var ui = UIManager.Instance;
            bool hasLoading = ui != null && ui.HasPanel(PanelIds.Loading);

            if (hasLoading)
            {
                ui.OpenPanel(PanelIds.Loading, $"Loading {TemplateId}...");
                yield return null;
            }

            var op = SceneManager.LoadSceneAsync(TemplateId, LoadSceneMode.Single);
            if (op == null)
            {
                if (hasLoading) ui.HidePanel(PanelIds.Loading);
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

                if (hasLoading)
                {
                    var loading = ui.GetPanel<LoadingPanel>(PanelIds.Loading);
                    if (loading != null) loading.SetProgress(displayProgress);
                }
                yield return null;
            }

            if (hasLoading) ui.HidePanel(PanelIds.Loading);
            UIEventBus.Publish(new SceneLoadCompleteEvent { SceneName = TemplateId });
        }
    }
}
