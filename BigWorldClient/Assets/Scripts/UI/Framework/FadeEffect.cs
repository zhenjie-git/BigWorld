using System.Collections;
using UnityEngine;

namespace BigWorldClient.UI.Framework
{
    public class FadeEffect : UIEffectBase
    {
        [SerializeField] private float showDuration = 0.25f;
        [SerializeField] private float hideDuration = 0.15f;

        private CanvasGroup canvasGroup;

        private void Awake()
        {
            canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = gameObject.AddComponent<CanvasGroup>();
        }

        public override IEnumerator PlayShowEffect()
        {
            if (canvasGroup == null) yield break;
            canvasGroup.alpha = 0f;
            float elapsed = 0f;
            while (elapsed < showDuration)
            {
                elapsed += Time.deltaTime;
                canvasGroup.alpha = Mathf.Lerp(0f, 1f, elapsed / showDuration);
                yield return null;
            }
            canvasGroup.alpha = 1f;
        }

        public override IEnumerator PlayHideEffect()
        {
            if (canvasGroup == null) yield break;
            float startAlpha = canvasGroup.alpha;
            float elapsed = 0f;
            while (elapsed < hideDuration)
            {
                elapsed += Time.deltaTime;
                canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, elapsed / hideDuration);
                yield return null;
            }
            canvasGroup.alpha = 0f;
        }
    }
}
