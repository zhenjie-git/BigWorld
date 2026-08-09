using UnityEngine;
using UnityEngine.UI;

namespace BigWorldClient.UI.Panels
{
    /// <summary>
    /// Full-screen loading panel with a spinning indicator and message text.
    /// Expects args to be a string message (optional).
    /// </summary>
    public class LoadingPanel : Framework.BasePanel
    {
        [Header("UI References")]
        [SerializeField] private Text loadingText;
        [SerializeField] private Image spinnerImage;

        [Header("Settings")]
        [SerializeField] private float spinnerSpeed = 180f;

        private bool isSpinning;

        protected override void OnShow(object args)
        {
            isSpinning = true;

            if (args is string msg && loadingText != null)
                loadingText.text = msg;
            else if (loadingText != null)
                loadingText.text = "Loading...";

            // Ensure the background covers the full screen
            var bg = GetComponent<Image>();
            if (bg == null)
            {
                bg = gameObject.AddComponent<Image>();
                bg.color = new Color(0f, 0f, 0f, 0.6f);
            }

            var rect = GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
        }

        protected override void OnHide()
        {
            isSpinning = false;
        }

        private void Update()
        {
            if (!isSpinning || spinnerImage == null) return;
            spinnerImage.rectTransform.Rotate(0f, 0f, -spinnerSpeed * Time.deltaTime);
        }

        public void SetMessage(string message)
        {
            if (loadingText != null)
                loadingText.text = message;
        }

        public void SetProgress(float progress)
        {
            if (loadingText != null)
                loadingText.text = "Loading... " + (int)(Mathf.Clamp01(progress) * 100) + "%";
        }
    }
}
