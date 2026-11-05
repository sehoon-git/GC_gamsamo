using System.Linq;
using Gamsamo.UI.Presentation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI.UiNext
{
    public sealed class EndingListView : MonoBehaviour
    {
        public UiFeaturePorts ports;
        public EndingPresentationView player;
        public Button closeButton;
        public Button[] playButtons;
        public TMP_Text[] stateLabels;
        public TMP_Text feedback;
        public static readonly string[] PatientIds = { "seoyun", "dohyeon", "taejun", "yeonwoo", "harin" };
        public static readonly string[] PatientNames = { "서윤", "도현", "태준", "연우", "하린" };
        bool pending, connectionMessage;
        string context;
        void Awake()
        {
            for (int i = 0; playButtons != null && i < playButtons.Length && i < PatientIds.Length; i++)
            { string patient = PatientIds[i]; playButtons[i].onClick.AddListener(() => Play(patient)); }
        }
        void OnEnable()
        {
            if (ports == null) return;
            ports.Changed += Refresh; ports.EndingReplayFinished += Ready;
            context = ports.ContextId; pending = false; Set(feedback, "해금된 후일담을 다시 볼 수 있습니다.");
            ports.QueryEndings(); Refresh();
        }
        void OnDisable()
        {
            if (ports != null) { ports.Changed -= Refresh; ports.EndingReplayFinished -= Ready; ports.CancelReplayRequest(); }
            pending = false;
        }
        public void Play(string patient)
        {
            if (pending || ports == null) return;
            pending = true; Set(feedback, "재생 가능 여부를 확인하고 있습니다."); Refresh();
            if (!ports.RequestReplay(patient)) { pending = false; Set(feedback, "후일담을 재생할 수 없습니다. 해금 상태와 연결을 확인해 주세요."); Refresh(); }
        }
        void Ready(string patient, EndingReplayGrant grant, string error)
        {
            pending = false;
            if (grant == null || player == null || !player.TryOpenApprovedReplay(grant.ContextId, patient, grant.Unlocked, grant.PhotoUnlocked, grant.Catalog))
                Set(feedback, string.IsNullOrWhiteSpace(error) ? "후일담을 열지 못했습니다. 잠시 후 다시 시도해 주세요." : error);
            else Set(feedback, "다시 보기는 보상과 사진 지급을 변경하지 않습니다.");
            Refresh();
        }
        public void Refresh()
        {
            if (ports == null) return;
            if (context != ports.ContextId) { context = ports.ContextId; pending = false; player?.CloseReplay(); }
            for (int i = 0; playButtons != null && i < playButtons.Length && i < PatientIds.Length; i++)
            {
                var item = ports.Endings.FirstOrDefault(e => e.PatientId == PatientIds[i]);
                bool playable = item != null && item.Unlocked && item.Approved && item.ContentReady;
                string state = !ports.EndingsKnown || item == null ? "해금 상태 확인 전"
                    : !item.Unlocked ? "잠김" : !item.Approved || !item.ContentReady ? "해금됨 · 콘텐츠 준비 중" : "해금됨 · 다시 보기";
                if (stateLabels != null && i < stateLabels.Length) Set(stateLabels[i], state);
                playButtons[i].interactable = !pending && playable && ports.CanRequestReplay;
            }
            if (!ports.CanRequestReplay && !pending) { connectionMessage = true; Set(feedback, "후일담 조회·재생 연결 준비 중입니다. 상담은 계속할 수 있습니다."); }
            else if (connectionMessage && !pending) { connectionMessage = false; Set(feedback, "해금된 후일담을 다시 볼 수 있습니다."); }
        }
        static void Set(TMP_Text text, string value) { if (text != null) { text.richText = false; text.text = value; } }
    }
}
