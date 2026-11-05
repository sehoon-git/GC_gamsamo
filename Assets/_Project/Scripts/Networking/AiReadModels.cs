using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Gamsamo.Networking
{
    /// <summary>B allocates and persists these IDs before starting an operation. Revision is not a settlement key.</summary>
    public sealed class AiConversationKey
    {
        public string GameProgressId { get; }
        public string PatientId { get; }
        public string CaseId { get; }
        public string SessionId { get; }
        public string SettlementId => string.Join("/", GameProgressId, PatientId, CaseId, SessionId);
        public AiConversationKey(string gameProgressId, string patientId, string caseId, string sessionId)
        {
            var ids = new JObject { ["progress"] = gameProgressId, ["patient"] = patientId, ["case"] = caseId, ["session"] = sessionId };
            GameProgressId = AiProtocol.Id(ids, "progress"); PatientId = AiProtocol.Id(ids, "patient");
            CaseId = AiProtocol.Id(ids, "case"); SessionId = AiProtocol.Id(ids, "session");
        }
        public bool SameCase(AiConversationKey other) => other != null && GameProgressId == other.GameProgressId
            && PatientId == other.PatientId && CaseId == other.CaseId;
        public AiConversationKey WithSession(string id) => new AiConversationKey(GameProgressId, PatientId, CaseId, id);
        public static AiConversationKey FromState(JObject state) => new AiConversationKey(AiProtocol.ProgressId(state),
            AiProtocol.Id(state, "patient_id"), AiProtocol.Id(state, "case_id"), AiProtocol.Id(AiProtocol.Current(state), "id"));
    }

    public enum AiServicePhase { Idle, Ready, PreparingOpening, GeneratingReply, Saving, Closing, Evaluating, SavingEvaluation, Completed, Error }
    public sealed class AiServiceStatus
    {
        public AiServicePhase Phase { get; }
        public AiConversationKey Key { get; }
        public string PendingAction { get; }
        public string Error { get; }
        public bool CanRetry { get; }
        public bool HasSavedEvaluation { get; }
        internal AiServiceStatus(AiSession session, string error, bool networkBusy)
        {
            Error = error ?? ""; Key = session == null ? null : AiConversationKey.FromState(session.State);
            PendingAction = session?.PendingRequest == null ? "" : AiProtocol.Text(session.PendingRequest, "action");
            if (PendingAction == "revisit") Key = Key.WithSession(AiProtocol.Id(AiProtocol.Object(session.PendingRequest, "body"), "session_id"));
            CanRetry = !networkBusy && (Error.Length > 0 || (session != null && (session.HasPendingSave || session.PendingRequest != null)));
            HasSavedEvaluation = session != null && session.TryGetSavedEvaluation(out _);
            if (session != null && session.IsWriting) Phase = session.SavingEvaluation ? AiServicePhase.SavingEvaluation : AiServicePhase.Saving;
            else if (Error.Length > 0) Phase = AiServicePhase.Error;
            else if (session == null) Phase = AiServicePhase.Idle;
            else if (session.HasPendingSave) Phase = session.SavingEvaluation ? AiServicePhase.SavingEvaluation : AiServicePhase.Saving;
            else if (PendingAction == "start" || PendingAction == "open" || PendingAction == "revisit") Phase = AiServicePhase.PreparingOpening;
            else if (PendingAction == "turn") Phase = AiServicePhase.GeneratingReply;
            else if (PendingAction == "close" || session.EndRequested && session.Phase == "active") Phase = AiServicePhase.Closing;
            else if (session.Phase == "pending_evaluation" || PendingAction == "evaluate") Phase = AiServicePhase.Evaluating;
            else if (session.Phase == "evaluated") Phase = AiServicePhase.Completed;
            else if (!(AiProtocol.Current(session.State)["opening"] is JObject)) Phase = AiServicePhase.PreparingOpening;
            else Phase = AiServicePhase.Ready;
        }
    }

    /// <summary>A's durable evaluation only. B must still validate progress, ask C, and save before D displays settlement.</summary>
    public sealed class AiSavedEvaluation
    {
        public AiConversationKey Key { get; }
        public int PatientScore { get; }
        public int CounselorScore { get; }
        public bool Cleared { get; }
        public int SourceRevision { get; }
        public string EvaluationJson { get; }
        private AiSavedEvaluation(JObject state, JObject session)
        {
            Key = AiConversationKey.FromState(state).WithSession(AiProtocol.Id(session, "id"));
            var evaluation = AiProtocol.Object(session, "evaluation");
            PatientScore = AiProtocol.Number(evaluation, "patient_score"); CounselorScore = AiProtocol.Number(evaluation, "counselor_score");
            Cleared = AiProtocol.Flag(evaluation, "cleared"); SourceRevision = AiProtocol.Number(state, "revision");
            EvaluationJson = AiProtocol.Encode(evaluation);
        }
        internal static bool TryRead(JObject snapshot, string sessionId, out AiSavedEvaluation result)
        {
            result = null;
            AiSaveStore.ValidateSnapshot(snapshot);
            var state = AiProtocol.Object(snapshot, "state");
            if (AiProtocol.ProgressId(state) == "legacy" || snapshot["pending_request"] is JObject
                || (bool?)snapshot["model_only"] != true || (string)snapshot["evaluation_sources"]?[sessionId] != "model") return false;
            var session = AiProtocol.Array(state, "sessions").Cast<JObject>().FirstOrDefault(s => (string)s["id"] == sessionId);
            if (session == null || (string)session["phase"] != "evaluated") return false;
            result = new AiSavedEvaluation(state, session);
            return true;
        }
    }

    public sealed class AiTranscriptEntry
    {
        public AiConversationKey Key { get; }
        public string MessageId { get; }
        public int Position { get; }
        public string Speaker { get; }
        public string Kind { get; }
        public string Text { get; }
        internal AiTranscriptEntry(AiConversationKey key, JObject record, int position)
        {
            Key = key.WithSession(AiProtocol.Id(record, "session_id")); MessageId = AiProtocol.Text(record, "id"); Position = position;
            Speaker = AiProtocol.Text(record, "speaker"); Kind = AiProtocol.Text(record, "kind"); Text = AiProtocol.Text(record, "text");
        }
        /// <summary>Ordinal UTF-16 positions in the unchanged Text. Empty means the quote is not evidence here.</summary>
        public IReadOnlyList<int> QuoteOffsets(string quote)
        {
            var offsets = new List<int>();
            if (!string.IsNullOrEmpty(quote))
                for (int start = 0; start <= Text.Length - quote.Length;)
                {
                    int index = Text.IndexOf(quote, start, StringComparison.Ordinal);
                    if (index < 0) break;
                    offsets.Add(index); start = index + 1;
                }
            return offsets.AsReadOnly();
        }
    }
    public enum AiDisclosureState { Undisclosed, Candidate, Confirmed }
    public sealed class AiDisclosure
    {
        public string Id { get; }
        public AiDisclosureState State { get; }
        public string Text { get; }
        internal AiDisclosure(string id, AiDisclosureState state, string text) { Id = id; State = state; Text = text; }
    }
    public sealed class AiCaseRecord
    {
        public AiConversationKey Key { get; }
        public IReadOnlyList<AiTranscriptEntry> Transcript { get; }
        public IReadOnlyList<AiDisclosure> Disclosures { get; }
        public IReadOnlyList<string> SessionIds { get; }
        public int Revision { get; }
        internal AiCaseRecord(JObject state)
        {
            AiProtocol.ValidateState(state);
            Key = AiConversationKey.FromState(state); Revision = AiProtocol.Number(state, "revision");
            Transcript = System.Array.AsReadOnly(AiProtocol.Records(state).Cast<JObject>().Select((r, i) => new AiTranscriptEntry(Key, r, i)).ToArray());
            SessionIds = System.Array.AsReadOnly(AiProtocol.Array(state, "sessions").Select(s => (string)s["id"]).ToArray());
            var confirmed = new HashSet<string>(AiProtocol.Array(state, "revealed_fact_ids").Select(v => (string)v));
            var candidates = new HashSet<string>();
            foreach (JObject session in AiProtocol.Array(state, "sessions"))
            {
                var speech = AiProtocol.Array(session, "turns").Cast<JObject>().ToList();
                if (session["opening"] is JObject opening) speech.Add(opening);
                foreach (var turn in speech) foreach (var id in AiProtocol.Array(turn, "candidate_fact_ids")) candidates.Add((string)id);
            }
            Disclosures = System.Array.AsReadOnly(AiProtocol.Array(AiProtocol.Patient(Key.PatientId), "private_facts").Cast<JObject>().Select(f =>
            {
                string id = AiProtocol.Id(f, "id");
                var status = confirmed.Contains(id) ? AiDisclosureState.Confirmed : candidates.Contains(id) ? AiDisclosureState.Candidate : AiDisclosureState.Undisclosed;
                return new AiDisclosure(id, status, status == AiDisclosureState.Confirmed ? AiProtocol.Text(f, "text") : "");
            }).ToArray());
        }
        public IReadOnlyList<AiTranscriptEntry> SessionTranscript(string sessionId)
        {
            AiProtocol.Require(SessionIds.Contains(sessionId), "해당 회차의 원문이 없습니다.");
            return System.Array.AsReadOnly(Transcript.Where(r => r.Key.SessionId == sessionId).ToArray());
        }
    }
}
