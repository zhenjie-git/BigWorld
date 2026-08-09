using System;
using UnityEngine;
using UnityEngine.UI;

namespace BigWorldClient.UI.Common
{
    public class GenericConfirmPopup : Framework.BasePanel
    {
        [Header("UI References")]
        [SerializeField] private Text titleText;
        [SerializeField] private Text messageText;
        [SerializeField] private Button confirmButton;
        [SerializeField] private Text confirmButtonText;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Text cancelButtonText;

        private Action onConfirm;
        private Action onCancel;

        protected override void OnInit()
        {
            if (confirmButton != null)
                confirmButton.onClick.AddListener(OnConfirmClicked);
            if (cancelButton != null)
                cancelButton.onClick.AddListener(OnCancelClicked);
        }

        protected override void OnShow(object args)
        {
            if (args is ConfirmPopupArgs)
            {
                var popupArgs = (ConfirmPopupArgs)args;
                if (titleText != null) titleText.text = popupArgs.Title;
                if (messageText != null) messageText.text = popupArgs.Message;
                if (confirmButtonText != null) confirmButtonText.text = popupArgs.ConfirmText;
                if (cancelButtonText != null) cancelButtonText.text = popupArgs.CancelText;
                onConfirm = popupArgs.OnConfirm;
                onCancel = popupArgs.OnCancel;
                if (cancelButton != null) cancelButton.gameObject.SetActive(popupArgs.ShowCancel);
            }
        }

        protected override void OnHide()
        {
            onConfirm = null;
            onCancel = null;
        }

        protected override void OnCleanup()
        {
            if (confirmButton != null) confirmButton.onClick.RemoveListener(OnConfirmClicked);
            if (cancelButton != null) cancelButton.onClick.RemoveListener(OnCancelClicked);
        }

        private void OnConfirmClicked()
        {
            if (onConfirm != null) onConfirm();
            if (Framework.UIManager.Instance != null)
                Framework.UIManager.Instance.HidePanel(PanelId);
        }

        private void OnCancelClicked()
        {
            if (onCancel != null) onCancel();
            if (Framework.UIManager.Instance != null)
                Framework.UIManager.Instance.HidePanel(PanelId);
        }
    }

    public class ConfirmPopupArgs
    {
        public string Title = "Confirm";
        public string Message = "Are you sure?";
        public string ConfirmText = "OK";
        public string CancelText = "Cancel";
        public bool ShowCancel = true;
        public Action OnConfirm;
        public Action OnCancel;
    }
}
