using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient.UI.Framework
{
    public class UILayerController : MonoBehaviour
    {
        [SerializeField] private UILayer layerType;
        [SerializeField] private int baseSortOrder = 0;

        public UILayer LayerType { get { return layerType; } }
        public Canvas Canvas { get; private set; }
        public int PanelCount { get { return panelStack.Count; } }

        private readonly List<BasePanel> panelStack = new List<BasePanel>();

        public event Action<BasePanel> OnPanelHidden;

        private void Awake()
        {
            Canvas = GetComponent<Canvas>();
            if (Canvas == null)
                Canvas = gameObject.AddComponent<Canvas>();
            Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            Canvas.sortingOrder = baseSortOrder;

            var scaler = GetComponent<UnityEngine.UI.CanvasScaler>();
            if (scaler == null)
            {
                scaler = gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
            }

            if (GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
                gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        }

        public void Push(BasePanel panel)
        {
            PushWithArgs(panel, null);
        }

        public void PushWithArgs(BasePanel panel, object args)
        {
            if (panel == null) return;

            if (panelStack.Count > 0)
            {
                var currentTop = panelStack[panelStack.Count - 1];
                currentTop.Internal_Pause();
            }

            panelStack.Add(panel);
            panel.RectTransform.SetParent(transform, false);
            ApplySorting(panel);
            panel.Internal_Show(args);
        }

        public void Pop()
        {
            if (panelStack.Count == 0) return;

            var topPanel = panelStack[panelStack.Count - 1];
            panelStack.RemoveAt(panelStack.Count - 1);

            topPanel.Internal_Hide(() =>
            {
                if (OnPanelHidden != null) OnPanelHidden(topPanel);
            });

            if (panelStack.Count > 0)
            {
                var newTop = panelStack[panelStack.Count - 1];
                newTop.Internal_Resume();
            }
        }

        public void PopTo(BasePanel target)
        {
            if (target == null) return;
            int targetIndex = panelStack.IndexOf(target);
            if (targetIndex < 0) return;
            while (panelStack.Count > targetIndex + 1)
                Pop();
        }

        public void PopAll()
        {
            while (panelStack.Count > 0)
                Pop();
        }

        /// <summary>
        /// Hide a specific panel. If it's the top of stack, pop normally (resumes panel beneath).
        /// If it's not the top, silently hide and remove it without affecting other panels.
        /// </summary>
        public void HidePanel(BasePanel panel)
        {
            if (panel == null) return;
            int index = panelStack.IndexOf(panel);
            if (index < 0) return;

            if (index == panelStack.Count - 1)
            {
                Pop();
            }
            else
            {
                panelStack.RemoveAt(index);
                panel.Internal_Hide(() =>
                {
                    if (OnPanelHidden != null) OnPanelHidden(panel);
                });
            }
        }

        public BasePanel Peek()
        {
            return panelStack.Count > 0 ? panelStack[panelStack.Count - 1] : null;
        }

        public T GetPanel<T>() where T : BasePanel
        {
            foreach (var panel in panelStack)
            {
                if (panel is T) return (T)panel;
            }
            return null;
        }

        public BasePanel GetPanelById(string panelId)
        {
            foreach (var panel in panelStack)
            {
                if (panel.PanelId == panelId) return panel;
            }
            return null;
        }

        private void ApplySorting(BasePanel panel)
        {
            int priorityValue = (int)panel.Priority;
            int insertIndex = panelStack.Count - 1;
            for (int i = 0; i < panelStack.Count - 1; i++)
            {
                if ((int)panelStack[i].Priority > priorityValue)
                {
                    insertIndex = i;
                    break;
                }
            }
            panel.RectTransform.SetSiblingIndex(insertIndex);
        }
    }
}
