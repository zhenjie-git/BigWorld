using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace BigWorldClient.UI.Framework
{
    public class UIManager : MonoBehaviour
    {
        private static UIManager _instance;
        private static bool _searched;

        static UIManager()
        {
            SceneManager.sceneLoaded += (_, __) => _searched = false;
        }

        public static UIManager Instance
        {
            get
            {
                if (_instance == null && !_searched)
                {
                    _searched = true;
                    _instance = FindObjectOfType<UIManager>();
                    if (_instance == null)
                    {
                        var prefab = Resources.Load<GameObject>("UI/UIManager");
                        if (prefab != null)
                        {
                            var go = Instantiate(prefab);
                            _instance = go.GetComponent<UIManager>();
                        }
                    }
                }
                return _instance;
            }
        }

        [SerializeField] private UILayerController[] _layerControllers;
        [SerializeField] private Transform _cacheRoot;

        private readonly Dictionary<string, List<BasePanel>> _panelCache = new Dictionary<string, List<BasePanel>>();
        private readonly HashSet<string> _pendingLoads = new HashSet<string>();
        private readonly List<BasePanel> _pausedByModal = new List<BasePanel>();

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                gameObject.SetActive(false);
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            foreach (var lc in _layerControllers)
            {
                if (lc == null) continue;
                lc.OnPanelHidden += HandlePanelHidden;
                lc.StackChanged += UpdateModalPause;
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                foreach (var lc in _layerControllers)
                {
                    if (lc == null) continue;
                    lc.OnPanelHidden -= HandlePanelHidden;
                    lc.StackChanged -= UpdateModalPause;
                }
                _instance = null;
            }
        }

        private void UpdateModalPause()
        {
            int modalIndex = -1;
            for (int i = 0; i < _layerControllers.Length; i++)
            {
                var lc = _layerControllers[i];
                if (lc != null && lc.DimBackground && lc.PanelCount > 0)
                    modalIndex = i;
            }

            if (modalIndex < 0)
            {
                for (int i = 0; i < _pausedByModal.Count; i++)
                {
                    if (_pausedByModal[i] != null) _pausedByModal[i].Internal_Resume();
                }
                _pausedByModal.Clear();
                return;
            }

            if (_pausedByModal.Count > 0) return;

            for (int i = 0; i < modalIndex; i++)
            {
                var lc = _layerControllers[i];
                if (lc == null) continue;
                var top = lc.Peek();
                if (top == null) continue;
                top.Internal_Pause();
                _pausedByModal.Add(top);
            }
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                CloseTopPanel();
        }

        public bool CloseTopPanel()
        {
            if (_layerControllers == null) return false;
            for (int i = _layerControllers.Length - 1; i >= 0; i--)
            {
                var lc = _layerControllers[i];
                if (lc == null) continue;
                var top = lc.Peek();
                if (top == null) continue;
                if (top.RegistryEntry != null && !top.RegistryEntry.closeByBack) continue;
                HidePanel(top.PanelId);
                return true;
            }
            return false;
        }

        public void OpenPanel(string panelId, object args = null)
        {
            var entry = GetEntry(panelId);
            if (entry == null)
            {
                Debug.LogError($"[UI] 未注册的面板: {panelId}（检查 GameConfig/UI/panel_config.xlsx）");
                return;
            }
            var layer = GetLayerController(entry.layer);
            if (layer == null)
            {
                Debug.LogError($"[UI] 面板 {panelId} 的层 {entry.layer} 没有配置 UILayerController");
                return;
            }
            if (IsPanelOpen(panelId))
            {
                Debug.Log($"[UI] 面板 {panelId} 已打开，忽略重复打开");
                return;
            }
            if (_pendingLoads.Contains(panelId)) return;

            LoadAndShow(panelId, entry, layer, args);
        }

        public void HidePanel(string panelId)
        {
            var entry = GetEntry(panelId);
            if (entry == null)
            {
                Debug.LogError($"[UI] 未注册的面板: {panelId}（检查 GameConfig/UI/panel_config.xlsx）");
                return;
            }
            var layer = GetLayerController(entry.layer);
            if (layer == null) return;
            var panel = layer.GetPanelById(panelId);
            if (panel == null) return;
            layer.HidePanel(panel);
        }

        public T GetPanel<T>(string panelId = null) where T : BasePanel
        {
            foreach (var lc in _layerControllers)
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
            foreach (var lc in _layerControllers)
            {
                if (lc == null) continue;
                if (lc.GetPanelById(panelId) != null) return true;
            }
            return false;
        }

        public bool HasPanel(string panelId)
        {
            return PanelConfigTable.Instance.HasPanel(panelId);
        }

        private PanelEntry GetEntry(string panelId)
        {
            return PanelConfigTable.Instance.GetEntry(panelId);
        }

        private UILayerController GetLayerController(UILayer layer)
        {
            int index = (int)layer;
            if (_layerControllers == null || index >= _layerControllers.Length || _layerControllers[index] == null) return null;
            return _layerControllers[index];
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
            _pendingLoads.Add(panelId);
            request.completed += _ =>
            {
                _pendingLoads.Remove(panelId);
                if (this == null) return;
                if (request.asset == null)
                {
                    Debug.LogError($"[UI] 面板资源不存在: {entry.resourcePath}（面板 {panelId}）");
                    return;
                }
                if (IsPanelOpen(panelId))
                {
                    Debug.Log($"[UI] 面板 {panelId} 已打开，丢弃迟到的加载实例");
                    return;
                }
                var go = Instantiate(request.asset as GameObject);
                var panel = go.GetComponent<BasePanel>();
                if (panel == null)
                {
                    Debug.LogError($"[UI] 预制体缺少 BasePanel 组件: {entry.resourcePath}（面板 {panelId}）");
                    Destroy(go);
                    return;
                }
                panel.RegistryEntry = entry;
                layer.PushWithArgs(panel, args);
            };
        }

        private BasePanel GetCachedPanel(string panelId)
        {
            if (!_panelCache.TryGetValue(panelId, out var list) || list.Count == 0)
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
                if (!_panelCache.TryGetValue(panel.PanelId, out var list))
                    _panelCache[panel.PanelId] = list = new List<BasePanel>();
                if (list.Count < maxCache)
                {
                    panel.Internal_Cleanup();
                    panel.RectTransform.SetParent(_cacheRoot != null ? _cacheRoot : transform, false);
                    panel.gameObject.SetActive(false);
                    list.Add(panel);
                    return;
                }
            }
            panel.Internal_Cleanup();
            Destroy(panel.gameObject);
        }
    }
}
