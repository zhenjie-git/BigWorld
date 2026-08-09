using System;
using System.Collections;
using UnityEngine;

namespace BigWorldClient.UI.Framework
{
    [RequireComponent(typeof(CanvasGroup))]
    public abstract class BasePanel : MonoBehaviour
    {
        /// <summary>
        /// Set by UIManager when the panel is loaded. Provides all panel metadata
        /// (id, layer, priority, cache settings) from the PanelRegistry ScriptableObject.
        /// </summary>
        internal PanelEntry RegistryEntry { get; set; }

        public string PanelId { get { return RegistryEntry?.panelId; } }
        public UILayer DefaultLayer { get { return RegistryEntry?.layer ?? UILayer.Normal; } }
        public PanelPriority Priority { get { return RegistryEntry?.priority ?? PanelPriority.Medium; } }
        public bool IsCacheable { get { return RegistryEntry?.cacheable ?? false; } }
        public PanelState CurrentState { get; private set; }
        public CanvasGroup CanvasGroup { get; private set; }
        public RectTransform RectTransform { get; private set; }

        private UIEffectBase effect;
        private bool initialized;

        protected virtual void Awake()
        {
            CanvasGroup = GetComponent<CanvasGroup>();
            RectTransform = GetComponent<RectTransform>();
            effect = GetComponent<UIEffectBase>();
            CurrentState = PanelState.Closed;
        }

        public void Internal_Show(object args)
        {
            if (CurrentState == PanelState.Opening || CurrentState == PanelState.Opened)
                return;

            gameObject.SetActive(true);

            if (!initialized)
            {
                OnInit();
                initialized = true;
            }

            CurrentState = PanelState.Opening;
            StartCoroutine(ShowRoutine(args));
        }

        public void Internal_Hide(Action onComplete = null)
        {
            if (CurrentState == PanelState.Closed || CurrentState == PanelState.Closing)
            {
                if (onComplete != null) onComplete();
                return;
            }

            CurrentState = PanelState.Closing;
            StartCoroutine(HideRoutine(onComplete));
        }

        public void Internal_Pause()
        {
            if (CurrentState != PanelState.Opened) return;
            CurrentState = PanelState.Paused;
            OnPause();
        }

        public void Internal_Resume()
        {
            if (CurrentState != PanelState.Paused) return;
            CurrentState = PanelState.Opened;
            OnResume();
        }

        public void Internal_Cleanup()
        {
            OnCleanup();
        }

        private IEnumerator ShowRoutine(object args)
        {
            if (effect != null)
                yield return StartCoroutine(effect.PlayShowEffect());

            if (CurrentState == PanelState.Opening)
            {
                CurrentState = PanelState.Opened;
                OnShow(args);
            }
        }

        private IEnumerator HideRoutine(Action onComplete)
        {
            if (effect != null)
                yield return StartCoroutine(effect.PlayHideEffect());

            CurrentState = PanelState.Closed;
            OnHide();
            gameObject.SetActive(false);
            if (onComplete != null) onComplete();
        }

        protected virtual void OnInit() { }
        protected virtual void OnShow(object args) { }
        protected virtual void OnPause() { }
        protected virtual void OnResume() { }
        protected virtual void OnHide() { }
        protected virtual void OnCleanup() { }
    }
}
