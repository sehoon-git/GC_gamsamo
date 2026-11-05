using Gamsamo.Dialogue;
using Gamsamo.UI.Presentation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI.UiNext
{
    /// <summary>Scrollable copy of the real status label. Opening/closing never invokes the service.</summary>
    public sealed class UiStatusDetailsView : PresentationModal
    {
        public Button openButton, closeButton;
        public TMP_Text body;
        void Awake()
        {
            panel.SetActive(false); openButton.onClick.AddListener(Open); closeButton.onClick.AddListener(Close);
        }
        public void Open() { if (dialogue != null) { OpenModal(); Refresh(); } }
        public void Close() { CloseModal(); }
        void Update()
        {
            if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) Close();
            if (openButton != null) openButton.gameObject.SetActive(dialogue != null && dialogue.IsVisible
                && (dialogue.ProcessingState == DialogueProcessingState.Error || DialogueProcessingText.IsWaiting(dialogue.ProcessingState)));
            if (IsOpen) Refresh();
        }
        void Refresh()
        {
            if (body == null || dialogue == null) return;
            body.richText = false; body.text = dialogue.statusText != null ? dialogue.statusText.text : "상태 확인 중";
            body.text += "\n\n상담 내용과 입력 초안은 유지됩니다.\n재시도는 창을 닫은 뒤, 현재 상태에서 활성화된 재시도 버튼으로 진행해 주세요.";
        }
    }
}
