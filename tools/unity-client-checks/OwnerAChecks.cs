// Test-only fixtures exercise A's real protocol/storage. They are never used by Unity gameplay.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gamsamo.Networking;
using Newtonsoft.Json.Linq;

internal static class OwnerAChecks
{
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { checks++; return; }
        throw new Exception(message);
    }
    private static JObject Example(string root, string name) => AiProtocol.Parse(File.ReadAllText(Path.Combine(root, "contracts", name + ".example.json")));
    private static JObject ScopedFixture(string root, string name, AiConversationKey key)
    {
        var state = (JObject)Example(root, name)["state"];
        Check((string)state["patient_id"] == key.PatientId, "bad test patient fixture");
        state["game_progress_id"] = key.GameProgressId; state["case_id"] = key.CaseId;
        AiProtocol.Current(state)["id"] = key.SessionId;
        return state;
    }
    private static JObject Response(AiSession session, JObject after)
    {
        var pending = session.PendingRequest;
        var body = AiProtocol.Object(pending, "body");
        string idField = (string)pending["action"] == "turn" ? "turn_id" : "request_id";
        return new JObject { ["state"] = after, ["case_id"] = after["case_id"].DeepClone(),
            ["session_id"] = body["session_id"].DeepClone(), ["request_id"] = body[idField].DeepClone(),
            ["base_revision"] = session.State["revision"].DeepClone(), ["replayed"] = false, ["disclosure_candidates"] = new JArray() };
    }
    private static JObject OpeningResponse(string root, AiSession session)
    {
        var state = session.State;
        var key = AiConversationKey.FromState(state);
        var body = AiProtocol.Object(session.PendingRequest, "body");
        bool revisit = (string)session.PendingRequest["action"] == "revisit";
        if (revisit)
        {
            var fresh = AiProtocol.EmptyState(key.PatientId, key.WithSession((string)body["session_id"]));
            AiProtocol.Current(fresh)["started_at"] = body["created_at"].DeepClone();
            AiProtocol.Array(state, "sessions").Add(AiProtocol.Current(fresh));
        }
        string label = key.PatientId == "seoyun" ? "start.response" : key.PatientId + "-start.response";
        var opening = (JObject)Example(root, label)["state"]["sessions"][0]["opening"];
        opening["id"] = body["request_id"].DeepClone();
        AiProtocol.Current(state)["opening"] = opening;
        state["revision"] = (int)state["revision"] + 1;
        return Response(session, state);
    }
    private static void Seed(AiCaseRepository repo, AiConversationKey key, JObject state, string mode = "model")
    {
        var snapshot = AiSaveStore.NewSnapshot(key.PatientId, key); snapshot["state"] = state;
        if (AiProtocol.Phase(state) == "evaluated") snapshot["evaluation_sources"] = new JObject { [key.SessionId] = mode };
        repo.Store(key).Save(snapshot, -1);
    }

    public static int Run(string root, string folder)
    {
        checks = 0;
        var catalog = new AiPatientCatalog(Path.Combine(root, "Assets", "StreamingAssets", "GameData"));
        var repo = new AiCaseRepository(folder, catalog);
        Check(catalog.Patients.Count == 5 && catalog.Patients.All(p => p.Ready), "final roster is not five ready patients");
        Check(catalog.Get("jieun").Id == "yeonwoo", "legacy character alias changed");
        Reject(() => catalog.Get("minseok"), "removed patient was accepted");
        foreach (var patient in catalog.Patients)
        {
            Check(catalog.Register(patient.Id) == patient.Id, "formal content registration failed");
            Check((string)AiProtocol.Patient(patient.Id)["content_version"] == patient.ContentVersion, "catalog version mismatch");
        }
        Reject(() => new AiConversationKey("../game", "seoyun", "case", "one"), "unsafe progress path accepted");
        Reject(() => repo.StartNewCase(new AiConversationKey("game", "jieun", "case", "one")), "alias stored as a second patient");
        Reject(() => repo.StartNewCase(new AiConversationKey("legacy", "seoyun", "case", "one")), "legacy scope awarded game identity");

        var a = new AiConversationKey("game_a", "seoyun", "same_case", "one");
        var b = new AiConversationKey("game_a", "harin", "same_case", "one");
        var c = new AiConversationKey("game_b", "seoyun", "same_case", "one");
        var sessions = new[] { repo.StartNewCase(a), repo.StartNewCase(b), repo.StartNewCase(c) };
        foreach (var session in sessions)
        {
            session.Schedule("start");
            Check((string)session.PendingRequest["body"]["game_progress_id"] == AiProtocol.ProgressId(session.State), "start omitted progress");
        }
        foreach (var key in new[] { b, c, a }) // Interleaved resume, with identical case/session/utterance namespaces allowed.
        {
            var session = repo.Resume(key);
            var request = session.PendingRequest;
            var response = OpeningResponse(root, session);
            var bad = AiProtocol.Copy(response); bad["state"]["game_progress_id"] = "another_game";
            Reject(() => session.Receive(request, bad, "model"), "cross-progress response accepted");
            Check(JToken.DeepEquals(request, repo.Resume(key).PendingRequest), "rejection changed the durable request");
            session.Receive(request, response, "model");
            Reject(() => session.Receive(request, response, "model"), "duplicate callback accepted");
            Check(repo.Read(key).Transcript.Count == 1 && repo.Read(key).Key.SettlementId == key.SettlementId, "case content leaked across scopes");
        }
        Check(repo.Read(b).Transcript[0].Text.Contains("하린"), "patient-specific opening lost");
        Check(repo.ListCases("game_a", "seoyun").Count == 1, "other progress/patient cases leaked into listing");
        Reject(() => repo.Resume(a.WithSession("missing")), "resume silently created a new session");
        Reject(() => repo.Resume(new AiConversationKey("game_a", "seoyun", "absent", "one")), "resume created a missing case");
        var initial = repo.Read(a).Transcript[0];
        Check(initial.QuoteOffsets(initial.Text).SequenceEqual(new[] { 0 }), "evidence offset is not the exact original text");
        Check(initial.QuoteOffsets("없는 근거").Count == 0, "fabricated evidence location returned");
        var unicodeEntry = new AiTranscriptEntry(a, new JObject { ["session_id"] = a.SessionId, ["id"] = "unicode:patient", ["speaker"] = "patient", ["kind"] = "dialogue", ["text"] = "가😊나 나" }, 0);
        Check(unicodeEntry.QuoteOffsets("나").SequenceEqual(new[] { 3, 5 }), "quote offsets are not ordinal UTF-16 positions");
        Check(repo.Read(a).Disclosures.All(d => d.State == AiDisclosureState.Undisclosed && d.Text == ""), "private facts exposed by read API");
        var copy = repo.Resume(a).State; copy["case_id"] = "changed";
        Check(repo.Read(a).Key.CaseId == a.CaseId, "read copy mutated committed state");
        var newKey = new AiConversationKey("game_a", "seoyun", "separate", "one");
        Check(repo.StartNewCase(newKey).State["memory"]["items"].Count() == 0 && repo.ListCases("game_a", "seoyun").Count == 2, "new case reused memory");
        Reject(() => repo.Store(c).Save(repo.Store(a).Load(), 1), "wrong scope written to another progress");

        var evaluationKey = new AiConversationKey("game_evaluation", "seoyun", "case", "one");
        Seed(repo, evaluationKey, ScopedFixture(root, "close.response", evaluationKey));
        var store = repo.Store(evaluationKey);
        var evaluating = new AiSession(store, "seoyun", requireExisting: true);
        evaluating.Schedule("evaluate");
        string originalRequest = AiProtocol.Encode(evaluating.PendingRequest);
        Check(AiProtocol.Encode(repo.Resume(evaluationKey).PendingRequest) == originalRequest, "restart changed pending evaluation");
        Check(!evaluating.TryGetSavedEvaluation(out _), "pending evaluation emitted settlement");
        var evaluated = ScopedFixture(root, "evaluate.response", evaluationKey);
        var reply = Response(evaluating, evaluated);
        var pending = evaluating.PendingRequest;
        var phases = new List<AiServicePhase>();
        evaluating.PersistenceChanged += () => phases.Add(new AiServiceStatus(evaluating, "", false).Phase);
        bool rejectedReentrantWrite = false;
        evaluating.PersistenceChanged += () =>
        {
            if (!evaluating.IsWriting) return;
            try { evaluating.RetrySave(); }
            catch (InvalidDataException) { rejectedReentrantWrite = true; }
        };
        store.BeforeWrite = file => { if (file == "current.json") throw new IOException("test-only disk failure"); };
        try { evaluating.Receive(pending, reply, "model"); throw new Exception("test did not fail persistence"); } catch (IOException) { checks++; }
        Check(evaluating.HasPendingSave && evaluating.SavingEvaluation, "received evaluation was lost on save error");
        Check(rejectedReentrantWrite, "persistence observer reentered a write transaction");
        Reject(() => evaluating.Receive(pending, reply, "model"), "late callback replaced the received evaluation awaiting save");
        Check(!evaluating.TryGetSavedEvaluation(out _) && !repo.TryReadSavedEvaluation(evaluationKey, out _), "unsaved evaluation became settlement-ready");
        Check(new AiServiceStatus(evaluating, "저장 실패", false).Phase == AiServicePhase.Error, "save error displayed as complete");
        Reject(() => evaluating.Schedule("revisit"), "revisit accepted before evaluation save");
        store.BeforeWrite = null; evaluating.RetrySave();
        Check(evaluating.TryGetSavedEvaluation(out var receipt) && receipt.Key.SettlementId == evaluationKey.SettlementId
            && receipt.PatientScore == 0 && receipt.CounselorScore == 0 && !receipt.Cleared, "saved two-axis result/key missing");
        Check(phases.Contains(AiServicePhase.SavingEvaluation) && phases.Last() == AiServicePhase.Completed, "evaluation save phase is not observable");
        Check(repo.TryReadSavedEvaluation(evaluationKey, out var restored) && restored.Key.SettlementId == receipt.Key.SettlementId, "restart lost saved receipt");
        Reject(() => evaluating.Receive(pending, reply, "model"), "late evaluation response accepted twice");

        foreach (string mode in new[] { "mock", "unknown" })
        {
            var key = new AiConversationKey("game_" + mode, "seoyun", "case", "one");
            Seed(repo, key, ScopedFixture(root, "evaluate.response", key), mode);
            Check(!repo.TryReadSavedEvaluation(key, out _), "mock/unknown result can award game rewards");
        }
        var taintedKey = new AiConversationKey("game_tainted", "seoyun", "case", "one");
        var tainted = repo.StartNewCase(taintedKey); tainted.Schedule("start");
        tainted.Receive(tainted.PendingRequest, OpeningResponse(root, tainted), "mock");
        Check((bool)tainted.Snapshot["model_only"] == false, "mock conversation provenance lost");
        var taintedSnapshot = tainted.Snapshot;
        taintedSnapshot["state"] = ScopedFixture(root, "evaluate.response", taintedKey);
        taintedSnapshot["local_revision"] = (int)taintedSnapshot["local_revision"] + 1;
        taintedSnapshot["evaluation_sources"] = new JObject { [taintedKey.SessionId] = "model" };
        repo.Store(taintedKey).Save(taintedSnapshot, (int)taintedSnapshot["local_revision"] - 1);
        Check(!repo.TryReadSavedEvaluation(taintedKey, out _), "model evaluation made earlier mock dialogue reward-eligible");

        // Test-only authored-fixture state with a real original-message location, remembered across revisit.
        var revisitKey = new AiConversationKey("game_revisit", "seoyun", "case", "one");
        var remembered = ScopedFixture(root, "evaluate.response", revisitKey);
        var opening = AiProtocol.Object(AiProtocol.Current(remembered), "opening");
        opening["candidate_fact_ids"] = new JArray("avoidance_relief");
        remembered["revealed_fact_ids"] = new JArray("avoidance_relief");
        remembered["memory"] = new JObject { ["through_turn_id"] = opening["id"].DeepClone(), ["items"] = new JArray(new JObject {
            ["category"] = "fact", ["speaker"] = "patient", ["text"] = "테스트 원문에 있는 첫 이야기", ["source_ids"] = new JArray((string)opening["id"] + ":patient") }) };
        Seed(repo, revisitKey, remembered);
        var revisit = repo.StartRevisit(revisitKey, "two");
        string revisitBody = AiProtocol.Encode(revisit.PendingRequest);
        Check(new AiServiceStatus(revisit, "", false).Key.SessionId == "two", "opening status does not identify the pending revisit");
        Check(AiProtocol.Encode(repo.StartRevisit(revisitKey, "two").PendingRequest) == revisitBody, "same revisit generated duplicate request");
        Check(AiProtocol.Encode(repo.Resume(revisitKey.WithSession("two")).PendingRequest) == revisitBody, "resume lost an opening in progress");
        Reject(() => repo.StartRevisit(revisitKey, "three"), "another revisit abandoned pending session");
        var previousState = revisit.State;
        revisit.Receive(revisit.PendingRequest, OpeningResponse(root, revisit), "model");
        Check(AiProtocol.Array(revisit.State, "sessions").Count == 2 && JToken.DeepEquals(revisit.State["sessions"][0], previousState["sessions"][0]), "revisit rewrote previous original/evaluation");
        Check(JToken.DeepEquals(revisit.State["memory"], previousState["memory"]) && JToken.DeepEquals(revisit.State["revealed_fact_ids"], previousState["revealed_fact_ids"]), "revisit discarded memory/disclosures");
        Check(AiProtocol.Array(repo.StartRevisit(revisitKey, "two").State, "sessions").Count == 2, "completed revisit duplicated session");
        Reject(() => repo.StartRevisit(revisitKey, "one"), "resume and revisit accepted identical IDs");
        Check(repo.Read(revisitKey).SessionTranscript("one").Count > 1 && repo.Read(revisitKey).SessionTranscript("two").Count == 1, "session transcript filtering mixed visits");
        Check(repo.Read(revisitKey).Disclosures.Single(d => d.Id == "avoidance_relief").State == AiDisclosureState.Confirmed, "confirmed disclosure lost");
        Check(repo.TryReadSavedEvaluation(revisitKey, out var earlier) && earlier.Key.SessionId == "one", "past saved evaluation cannot be read separately");
        Check(!repo.TryReadSavedEvaluation(revisitKey.WithSession("two"), out _), "active revisit exposed prior result as current");
        Reject(() => repo.StartNewCase(revisitKey), "new-case retry jumped back to prior session");
        return checks;
    }
}
