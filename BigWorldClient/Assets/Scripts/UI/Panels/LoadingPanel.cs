using UnityEngine;
using UnityEngine.UI;

namespace BigWorldClient.UI.Panels
{

    public class LoadingPanel : Framework.BasePanel
    {
        [Header("UI References")]
        [SerializeField] private Text _loadingText;
        [SerializeField] private Image _spinnerImage;

        [Header("Settings")]
        [SerializeField] private float _spinnerSpeed = 180f;

        private bool _isSpinning;

        protected override void OnInit()
        {
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

        protected override void OnShow(object args)
        {
            _isSpinning = true;

            if (args is string msg && _loadingText != null)
                _loadingText.text = msg;
            else if (_loadingText != null)
                _loadingText.text = "Loading...";
        }

        protected override void OnHide()
        {
            _isSpinning = false;
        }

        private void Update()
        {
            if (!_isSpinning || _spinnerImage == null) return;
            _spinnerImage.rectTransform.Rotate(0f, 0f, -_spinnerSpeed * Time.deltaTime);
        }

        public void SetMessage(string message)
        {
            if (_loadingText != null)
                _loadingText.text = message;
        }

        public void SetProgress(float progress)
        {
            if (_loadingText != null)
                _loadingText.text = "Loading... " + (int)(Mathf.Clamp01(progress) * 100) + "%";
        }
    }
}
