using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BigWorldClient.UI.Framework
{
    public class UILayerController : MonoBehaviour
    {
        [SerializeField] private UILayer _layerType;
        [SerializeField] private int _baseSortOrder = 0;
        [SerializeField] private bool _dimBackground = false;
        [SerializeField] private float _dimAlpha = 0.45f;

        public UILayer LayerType { get { return _layerType; } }
        public bool DimBackground { get { return _dimBackground; } }
        public Canvas Canvas { get; private set; }
        public int PanelCount { get { return _panelStack.Count; } }

        private readonly List<BasePanel> _panelStack = new List<BasePanel>();
        private GameObject _blocker;

        public event Action<BasePanel> OnPanelHidden;
        public event Action StackChanged;

        private void Awake()
        {
            Canvas = GetComponent<Canvas>();
            if (Canvas == null)
                Canvas = gameObject.AddComponent<Canvas>();
            Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            Canvas.sortingOrder = _baseSortOrder;

            var scaler = GetComponent<CanvasScaler>();
            if (scaler == null)
            {
                scaler = gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
            }

            if (GetComponent<GraphicRaycaster>() == null)
                gameObject.AddComponent<GraphicRaycaster>();
        }

        public void Push(BasePanel panel)
        {
            PushWithArgs(panel, null);
        }

        public void PushWithArgs(BasePanel panel, object args)
        {
            if (panel == null) return;

            _panelStack.Add(panel);
            panel.RectTransform.SetParent(transform, false);
            panel.RectTransform.SetAsLastSibling();
            UpdateBlocker();
            RaiseStackChanged();
            panel.Internal_Show(args);
        }

        public void Pop()
        {
            if (_panelStack.Count == 0) return;

            var topPanel = _panelStack[_panelStack.Count - 1];
            _panelStack.RemoveAt(_panelStack.Count - 1);
            UpdateBlocker();
            RaiseStackChanged();

            topPanel.Internal_Hide(() =>
            {
                if (OnPanelHidden != null) OnPanelHidden(topPanel);
            });
        }

        public void PopTo(BasePanel target)
        {
            if (target == null) return;
            int targetIndex = _panelStack.IndexOf(target);
            if (targetIndex < 0) return;
            while (_panelStack.Count > targetIndex + 1)
                Pop();
        }

        public void PopAll()
        {
            while (_panelStack.Count > 0)
                Pop();
        }

        public void HidePanel(BasePanel panel)
        {
            if (panel == null) return;
            int index = _panelStack.IndexOf(panel);
            if (index < 0) return;

            if (index == _panelStack.Count - 1)
            {
                Pop();
                return;
            }

            _panelStack.RemoveAt(index);
            UpdateBlocker();
            RaiseStackChanged();
            panel.Internal_Hide(() =>
            {
                if (OnPanelHidden != null) OnPanelHidden(panel);
            });
        }

        private void RaiseStackChanged()
        {
            if (StackChanged != null) StackChanged();
        }

        public BasePanel Peek()
        {
            return _panelStack.Count > 0 ? _panelStack[_panelStack.Count - 1] : null;
        }

        public T GetPanel<T>() where T : BasePanel
        {
            foreach (var panel in _panelStack)
            {
                if (panel is T) return (T)panel;
            }
            return null;
        }

        public BasePanel GetPanelById(string panelId)
        {
            foreach (var panel in _panelStack)
            {
                if (panel.PanelId == panelId) return panel;
            }
            return null;
        }

        private void UpdateBlocker()
        {
            bool need = _dimBackground && _panelStack.Count > 0;
            if (!need)
            {
                if (_blocker != null) _blocker.SetActive(false);
                return;
            }

            if (_blocker == null)
            {
                _blocker = new GameObject("Blocker");
                var image = _blocker.AddComponent<Image>();
                image.color = new Color(0f, 0f, 0f, _dimAlpha);
                image.raycastTarget = true;
                var rt = _blocker.GetComponent<RectTransform>();
                rt.SetParent(transform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
            _blocker.SetActive(true);
            _blocker.transform.SetAsFirstSibling();
        }
    }
}
