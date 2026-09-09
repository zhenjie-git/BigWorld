using System.Collections;
using UnityEngine;

namespace BigWorldClient.UI.Framework
{
    public class FadeEffect : UIEffectBase
    {
        [SerializeField] private float _showDuration = 0.25f;
        [SerializeField] private float _hideDuration = 0.15f;

        private CanvasGroup _canvasGroup;

        private void Awake()
        {
            _canvasGroup = GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
                _canvasGroup = gameObject.AddComponent<CanvasGroup>();
        }

        public override IEnumerator PlayShowEffect()
        {
            if (_canvasGroup == null) yield break;
            _canvasGroup.alpha = 0f;
            float elapsed = 0f;
            while (elapsed < _showDuration)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(0f, 1f, elapsed / _showDuration);
                yield return null;
            }
            _canvasGroup.alpha = 1f;
        }

        public override IEnumerator PlayHideEffect()
        {
            if (_canvasGroup == null) yield break;
            float startAlpha = _canvasGroup.alpha;
            float elapsed = 0f;
            while (elapsed < _hideDuration)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, elapsed / _hideDuration);
                yield return null;
            }
            _canvasGroup.alpha = 0f;
        }
    }
}
