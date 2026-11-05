using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Gamsamo.Networking
{
    /// <summary>Client-owned transaction coordinator. Pending bodies survive restart; received results survive save failure.</summary>
    public sealed class AiSession
    {
        private readonly AiSaveStore store;
        private JObject snapshot;
        private JObject pendingSave;
        public JObject State => AiProtocol.Copy(AiProtocol.Object(snapshot, "state"));
        public JObject PendingRequest => snapshot["pending_request"] is JObject value ? AiProtocol.Copy(value) : null;
        public bool HasPendingSave => pendingSave != null;
        public bool EndRequested => AiProtocol.Flag(pendingSave ?? snapshot, "end_requested");
        public bool VisitCompleted => AiProtocol.Flag(snapshot, "visit_completed");
        public string Draft => AiProtocol.Text(pendingSave ?? snapshot, "draft");
        public string CaseId => AiProtocol.Id(AiProtocol.Object(snapshot, "state"), "case_id");
        public string Phase => AiProtocol.Phase(AiProtocol.Object(snapshot, "state"));
        public string NextAction
        {
            get
            {
                if (HasPendingSave || PendingRequest != null) return null;
                if ((Phase == "active" && EndRequested) || Phase == "closing") return "close";
                if (Phase == "pending_evaluation") return "evaluate";
                var state = AiProtocol.Object(snapshot, "state");
                if (Phase == "active" && !(AiProtocol.Current(state)["opening"] is JObject))
                    return AiProtocol.Number(state, "revision") == 0 ? "start" : "open";
                return null;
            }
        }
        public JObject Snapshot => AiProtocol.Copy(snapshot);

        public AiSession(AiSaveStore store)
        {
            this.store = store;
            snapshot = store.Load();
            if (snapshot == null)
            {
                snapshot = AiSaveStore.NewSnapshot();
                store.Save(snapshot, -1);
            }
        }

        private JObject Next()
        {
            var result = AiProtocol.Copy(pendingSave ?? snapshot);
            result["local_revision"] = AiProtocol.Number(snapshot, "local_revision") + 1;
            return result;
        }
        private void Save(JObject candidate)
        {
            pendingSave = candidate;
            store.Save(candidate, AiProtocol.Number(snapshot, "local_revision"));
            snapshot = candidate;
            pendingSave = null;
        }
        public void RetrySave()
        {
            if (pendingSave != null) Save(pendingSave);
        }

        public void Schedule(string action, string text = null)
        {
            AiProtocol.Require(pendingSave == null && PendingRequest == null, "이전 요청·자동 저장을 먼저 완료하세요.");
            var state = State;
            string phase = AiProtocol.Phase(state);
            if (action == "turn") AiProtocol.Require(phase == "active" && !EndRequested
                && AiProtocol.Current(state)["opening"] is JObject && !string.IsNullOrWhiteSpace(text), "현재는 새 발언을 보낼 수 없습니다.");
            if (action == "close") AiProtocol.Require((EndRequested && phase == "active") || phase == "closing", "종료 의도를 먼저 저장하세요.");
            if (action == "evaluate") AiProtocol.Require(phase == "pending_evaluation", "종료 안내 저장을 먼저 완료하세요.");
            if (action == "start" || action == "open") AiProtocol.Require(phase == "active" && !(AiProtocol.Current(state)["opening"] is JObject), "이미 첫 발화가 있습니다.");
            if (action == "revisit") AiProtocol.Require(phase == "evaluated" && !AiProtocol.Flag(AiProtocol.Object(AiProtocol.Current(state), "evaluation"), "cleared"), "기준 미달 상담만 재상담할 수 있습니다.");
            var next = Next();
            next["pending_request"] = AiProtocol.Request(state, action, text);
            if (action == "turn") next["draft"] = text;
            if (action == "revisit") { next["end_requested"] = false; next["visit_completed"] = false; }
            Save(next);
        }

        public void Receive(JObject request, JObject response)
        {
            AiProtocol.Require(PendingRequest != null && JToken.DeepEquals(PendingRequest, request), "이미 처리했거나 오래된 요청의 응답입니다.");
            var updated = AiProtocol.Accept(State, request, response);
            // Preserve an end request made while this network request was in flight, even if its disk write failed.
            var next = Next();
            next["state"] = updated;
            next["pending_request"] = null;
            if (AiProtocol.Text(request, "action") == "turn") next["draft"] = "";
            Save(next);
        }

        public void RequestEnd()
        {
            if (Phase != "active" && Phase != "closing") return;
            var next = Next();
            next["end_requested"] = true;
            Save(next);
        }

        public void ReturnRejectedInputToDraft()
        {
            AiProtocol.Require(pendingSave == null && PendingRequest != null && AiProtocol.Text(PendingRequest, "action") == "turn", "수정할 입력이 없습니다.");
            var next = Next();
            next["draft"] = AiProtocol.Text(AiProtocol.Object(PendingRequest, "body"), "text");
            next["pending_request"] = null;
            Save(next);
        }

        public void CompleteVisit()
        {
            AiProtocol.Require(Phase == "evaluated" && pendingSave == null && PendingRequest == null, "평가 저장을 먼저 완료하세요.");
            if (VisitCompleted) return;
            var next = Next(); next["visit_completed"] = true; Save(next);
        }

        public void SaveDraft(string text)
        {
            if (pendingSave != null || PendingRequest != null || EndRequested || Phase != "active" || text == Draft) return;
            var next = Next(); next["draft"] = text ?? ""; Save(next);
        }

        public void NewExperiment()
        {
            AiProtocol.Require(pendingSave == null && PendingRequest == null, "이전 요청·저장을 먼저 완료하세요.");
            var next = AiSaveStore.NewSnapshot();
            next["local_revision"] = AiProtocol.Number(snapshot, "local_revision") + 1;
            Save(next);
        }
    }
}
