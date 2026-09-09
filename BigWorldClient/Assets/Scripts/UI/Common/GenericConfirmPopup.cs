using System;
using UnityEngine;
using UnityEngine.UI;

namespace BigWorldClient.UI.Common
{
    public class GenericConfirmPopup : Framework.BasePanel
    {
        [Header("UI References")]
        [SerializeField] private Text _titleText;
        [SerializeField] private Text _messageText;
        [SerializeField] private Button _confirmButton;
        [SerializeField] private Text _confirmButtonText;
        [SerializeField] private Button _cancelButton;
        [SerializeField] private Text _cancelButtonText;

        private Action _onConfirm;
        private Action _onCancel;

        protected override void OnInit()
        {
            if (_confirmButton != null)
                _confirmButton.onClick.AddListener(OnConfirmClicked);
            if (_cancelButton != null)
                _cancelButton.onClick.AddListener(OnCancelClicked);
        }

        protected override void OnShow(object args)
        {
            if (args is ConfirmPopupArgs)
            {
                var popupArgs = (ConfirmPopupArgs)args;
                if (_titleText != null) _titleText.text = popupArgs.Title;
                if (_messageText != null) _messageText.text = popupArgs.Message;
                if (_confirmButtonText != null) _confirmButtonText.text = popupArgs.ConfirmText;
                if (_cancelButtonText != null) _cancelButtonText.text = popupArgs.CancelText;
                _onConfirm = popupArgs.OnConfirm;
                _onCancel = popupArgs.OnCancel;
                if (_cancelButton != null) _cancelButton.gameObject.SetActive(popupArgs.ShowCancel);
            }
        }

        protected override void OnHide()
        {
            _onConfirm = null;
            _onCancel = null;
        }

        protected override void OnCleanup()
        {
            if (_confirmButton != null) _confirmButton.onClick.RemoveListener(OnConfirmClicked);
            if (_cancelButton != null) _cancelButton.onClick.RemoveListener(OnCancelClicked);
        }

        private void OnConfirmClicked()
        {
            if (_onConfirm != null) _onConfirm();
            if (Framework.UIManager.Instance != null)
                Framework.UIManager.Instance.HidePanel(PanelId);
        }

        private void OnCancelClicked()
        {
            if (_onCancel != null) _onCancel();
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
