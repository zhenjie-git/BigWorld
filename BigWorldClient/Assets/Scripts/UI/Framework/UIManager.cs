using System;
using System.Collections.Generic;
using BigWorldClient.UI.Panels;
using UnityEngine;

namespace BigWorldClient.UI.Framework
{
    public class UIManager : MonoBehaviour
    {
        private static UIManager instance;

        public static UIManager Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = FindObjectOfType<UIManager>();
                    if (instance == null)
                    {
                        var prefab = Resources.Load<GameObject>("UI/UIManager");
                        if (prefab != null)
                        {
                            var go = Instantiate(prefab);
                            instance = go.GetComponent<UIManager>();
                        }
                        else
                        {
                            Debug.LogError("[UIManager] Prefab not found at Resources/UI/UIManager");
                            return null;
                        }
                    }
                }
                return instance;
            }
        }

        [SerializeField] private UIPanelRegistry panelRegistry;
        [SerializeField] private UILayerController[] layerControllers;
        [SerializeField] private Transform cacheRoot;

        private Dictionary<string, List<BasePanel>> panelCache = new Dictionary<string, List<BasePanel>>();

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                gameObject.SetActive(false);
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);

            if (panelRegistry != null) panelRegistry.BuildLookup();

            foreach (var lc in layerControllers)
            {
                if (lc != null) lc.OnPanelHidden += HandlePanelHidden;
            }
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                foreach (var lc in layerControllers)
                {
                    if (lc != null) lc.OnPanelHidden -= HandlePanelHidden;
                }
                instance = null;
            }
        }

        public void OpenPanel(string panelId, object args = null)
        {
            var entry = GetEntry(panelId);
            if (entry == null) return;
            var layer = GetLayerController(entry.layer);
            if (layer == null) return;
            LoadAndShow(panelId, entry, layer, args);
        }

        public void HidePanel(string panelId)
        {
            var entry = GetEntry(panelId);
            if (entry == null) return;
            var layer = GetLayerController(entry.layer);
            if (layer == null) return;
            var panel = layer.GetPanelById(panelId);
            if (panel == null) return;
            layer.HidePanel(panel);
        }

        public T GetPanel<T>(string panelId = null) where T : BasePanel
        {
            foreach (var lc in layerControllers)
            {
                if (lc == null) continue;
                if (!string.IsNullOrEmpty(panelId))
                {
                    var byId = lc.GetPanelById(panelId);
                    if (byId is T) return (T)byId;
                }
                else
                {
                    var byType = lc.GetPanel<T>();
                    if (byType != null) return byType;
                }
            }
            return null;
        }

        public bool IsPanelOpen(string panelId)
        {
            foreach (var lc in layerControllers)
            {
                if (lc == null) continue;
                if (lc.GetPanelById(panelId) != null) return true;
            }
            return false;
        }

        private PanelEntry GetEntry(string panelId)
        {
            if (panelRegistry == null) { Debug.LogError("[UIManager] PanelRegistry is null"); return null; }
            var entry = panelRegistry.GetEntry(panelId);
            if (entry == null) Debug.LogError("[UIManager] Panel not found: " + panelId);
            return entry;
        }

        private UILayerController GetLayerController(UILayer layer)
        {
            int index = (int)layer;
            if (layerControllers == null || index >= layerControllers.Length || layerControllers[index] == null)
            {
                Debug.LogError("[UIManager] Layer controller missing: " + layer);
                return null;
            }
            return layerControllers[index];
        }

        private void LoadAndShow(string panelId, PanelEntry entry, UILayerController layer, object args)
        {
            var cached = GetCachedPanel(panelId);
            if (cached != null)
            {
                layer.PushWithArgs(cached, args);
                return;
            }

            var request = Resources.LoadAsync<GameObject>(entry.resourcePath);
            request.completed += _ =>
            {
                if (request.asset == null)
                {
                    Debug.LogError("[UIManager] Load failed: " + entry.resourcePath);
                    return;
                }
                var go = Instantiate(request.asset as GameObject);
                var panel = go.GetComponent<BasePanel>();
                if (panel == null)
                {
                    Debug.LogError("[UIManager] Prefab missing BasePanel: " + entry.resourcePath);
                    Destroy(go);
                    return;
                }
                panel.RegistryEntry = entry;
                layer.PushWithArgs(panel, args);
            };
        }

        private BasePanel GetCachedPanel(string panelId)
        {
            if (!panelCache.TryGetValue(panelId, out var list) || list.Count == 0)
                return null;
            var panel = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);
            return panel;
        }

        private void HandlePanelHidden(BasePanel panel)
        {
            if (panel == null) return;
            if (panel.IsCacheable)
            {
                var entry = panel.RegistryEntry;
                int maxCache = entry != null ? entry.maxCacheCount : 1;
                if (!panelCache.TryGetValue(panel.PanelId, out var list))
                    panelCache[panel.PanelId] = list = new List<BasePanel>();
                if (list.Count < maxCache)
                {
                    panel.Internal_Cleanup();
                    panel.RectTransform.SetParent(cacheRoot != null ? cacheRoot : transform, false);
                    panel.gameObject.SetActive(false);
                    list.Add(panel);
                    return;
                }
            }
            panel.Internal_Cleanup();
            Destroy(panel.gameObject);
        }

        // ===== Loading UI helpers (used by SceneMgr) =====

        public bool HasLoadingPanel()
        {
            return panelRegistry != null && panelRegistry.HasPanel("loading");
        }

        public void SetLoadingProgress(float progress)
        {
            var panel = GetPanel<LoadingPanel>("loading");
            if (panel != null)
                panel.SetProgress(progress);
        }

    }
}
