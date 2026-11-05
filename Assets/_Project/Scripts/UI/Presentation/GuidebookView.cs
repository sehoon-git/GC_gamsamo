using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI.Presentation
{
    public sealed class GuidebookView : PresentationModal
    {
        public Button openButton, closeButton, previousButton, nextButton;
        public TMP_Text titleText, bodyText, pageText;
        public int Page { get; private set; }
        public int PageCount => Titles.Length;
        private static readonly string[] Titles = { "상담실에 오신 것을 환영해요", "이야기를 듣고 답하기", "기록과 공개 정보", "상담 마무리와 다시 만나기", "보상과 사진", "저장과 다시 보기" };
        private static readonly string[] Pages =
        {
            "문을 눌러 현재 순서의 내담자를 맞이합니다.\n상담 중에는 다른 내담자가 들어오지 않아요.\n서류와 안내서는 대화 중에도 열어볼 수 있습니다.",
            "내담자가 먼저 이야기를 꺼냅니다. 공감, 제안, 질문을 자유롭게 적어 주세요. 질문 횟수 제한은 없습니다.\nEnter는 줄바꿈, 전송 버튼 또는 Ctrl+Enter는 보내기입니다.\n응답을 기다리는 동안 기존 대화와 초안은 유지됩니다.",
            "책상 위 서류에서 상담 메모, 대화 원문, 상세 평가를 확인합니다.\n미확인은 아직 확인하지 않은 정보, 해당 없음은 해당 사항이 없는 정보입니다. 미공개 정보의 내용은 확인할 수 없습니다.\n기록부의 사진 칸과 PC 사진첩은 별개입니다.",
            "원할 때 상담 그만하기를 선택하고 확인합니다. 진행 중인 답변과 기록 저장이 끝나면 평가합니다.\n결과 저장 후 환자 내보내기로 이번 방문을 마칩니다.\n기준 미달이면 다음 문 클릭 때 같은 내담자와 기억을 이어 새 회차로 상담하고, 클리어하면 다음 내담자로 넘어갑니다.",
            "보상과 사진은 진행 저장이 완료된 결과를 기준으로 안내합니다. 상세 점수와 정산 내역은 서류에서 확인하세요.\n인물별 엔딩을 건너뛰어도 이미 해금된 사진은 PC 사진첩에 남습니다. 다시 보기로 보상이나 사진을 추가 지급하지 않습니다.\n상품의 미리보기와 구매·소유는 서로 다릅니다.",
            "진행은 자동 저장됩니다. 불러오기는 중단한 회차와 처리 단계부터 이어갑니다. 새 게임은 별도 진행입니다.\n저장 실패나 평가 대기는 완료가 아닙니다. 실제 오류는 재시도로 이어갑니다.\n확정된 사진과 엔딩은 복원된 진행에서 다시 볼 수 있습니다."
        };

        private void Awake()
        {
            if (openButton != null) openButton.onClick.AddListener(Open);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            if (previousButton != null) previousButton.onClick.AddListener(Previous);
            if (nextButton != null) nextButton.onClick.AddListener(Next);
            if (panel != null) panel.SetActive(false);
            Refresh();
        }
        public bool TryOpen() { if (!OpenModal()) return false; Refresh(); return true; }
        private void Open() => TryOpen();
        public void Close() => CloseModal();
        public void Previous() => GoTo(Page - 1);
        public void Next() => GoTo(Page + 1);
        public void GoTo(int page)
        {
            if (!IsOpen) return;
            int next = Mathf.Clamp(page, 0, PageCount - 1);
            if (next != Page) PresentationAudio.Instance?.PlayPageSound();
            Page = next; Refresh();
        }
        private void Update()
        {
            if (!IsOpen) return;
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) Previous();
            else if (Input.GetKeyDown(KeyCode.RightArrow)) Next();
        }
        private void Refresh()
        {
            Set(titleText, Titles[Page]); Set(bodyText, Pages[Page]); Set(pageText, (Page + 1) + " / " + PageCount);
            if (previousButton != null) previousButton.interactable = Page > 0;
            if (nextButton != null) nextButton.interactable = Page < PageCount - 1;
        }
        internal static void Set(TMP_Text label, string value)
        {
            if (label == null) return;
            label.richText = false; label.text = value ?? "";
        }
    }
}
