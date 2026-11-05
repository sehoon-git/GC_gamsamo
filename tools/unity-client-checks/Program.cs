using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Gamsamo.Networking;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { checks++; return; }
        throw new Exception(message);
    }
    private static JObject Read(string root, string name) => AiProtocol.Parse(File.ReadAllText(Path.Combine(root, "contracts", name + ".example.json")));
    private static JObject RequestFromExample(string root, string name, string action) => new JObject { ["action"] = action, ["body"] = Read(root, name + ".request") };

    private static void StaticChecks(string root)
    {
        Check(AiProtocol.ValidateHealth(Read(root, "health.response")) == "mock", "health mode must be top-level");
        var notReady = Read(root, "health.response"); notReady["inference"]["ready"] = false;
        Reject(() => AiProtocol.ValidateHealth(notReady), "unready server accepted");
        foreach (string path in Directory.GetFiles(Path.Combine(root, "contracts"), "*.example.json"))
        {
            var value = AiProtocol.Parse(File.ReadAllText(path));
            if (value["state"] is JObject state) AiProtocol.ValidateState(state);
            checks++;
        }
        string[] labels = { "start", "open-retry", "greeting", "compound", "turn", "question-2", "question-3", "question-4", "fifth", "sixth", "retry", "close", "close-retry", "early-close", "empty-close", "opening-only-close", "evaluate", "evaluate-retry", "empty-evaluate", "opening-only-evaluate", "revisit" };
        foreach (string label in labels)
        {
            string action = label == "start" ? "start" : label == "open-retry" ? "open" : label == "revisit" ? "revisit" : label.Contains("evaluate") ? "evaluate" : label.Contains("close") ? "close" : "turn";
            var request = RequestFromExample(root, label, action);
            var before = action == "start" ? Read(root, "initial-state") : AiProtocol.Object(AiProtocol.Object(request, "body"), "state");
            var response = Read(root, label + ".response");
            var accepted = AiProtocol.Accept(before, request, response);
            Check((int)accepted["revision"] == (int)response["state"]["revision"], label);
        }
        var sampleRequest = RequestFromExample(root, "turn", "turn");
        var stateBefore = AiProtocol.Object(AiProtocol.Object(sampleRequest, "body"), "state");
        var good = Read(root, "turn.response");
        foreach (string field in new[] { "case_id", "session_id", "request_id" })
        {
            var bad = AiProtocol.Copy(good); bad[field] = "other";
            Reject(() => AiProtocol.Accept(stateBefore, sampleRequest, bad), "wrong identity accepted");
        }
        var wrongRevision = AiProtocol.Copy(good); wrongRevision["base_revision"] = 99;
        Reject(() => AiProtocol.Accept(stateBefore, sampleRequest, wrongRevision), "late revision accepted");
        var rewritten = AiProtocol.Copy(good); AiProtocol.Current((JObject)rewritten["state"])["opening"]["patient_text"] = "changed";
        Reject(() => AiProtocol.Accept(stateBefore, sampleRequest, rewritten), "rewritten opening accepted");
        var extra = AiProtocol.Copy(good); var turns = AiProtocol.Array(AiProtocol.Current((JObject)extra["state"]), "turns");
        var appended = turns.Last.DeepClone(); appended["id"] = "extra"; turns.Add(appended);
        Reject(() => AiProtocol.Accept(stateBefore, sampleRequest, extra), "extra turn accepted");
        var arbitraryReveal = AiProtocol.Copy(good); arbitraryReveal["disclosure_candidates"] = new JArray("friend_envy");
        Reject(() => AiProtocol.Accept(stateBefore, sampleRequest, arbitraryReveal), "invented candidate accepted");
        var wrongEnding = Read(root, "close.response"); AiProtocol.Current((JObject)wrongEnding["state"])["closing_text"] = "다른 종료 문구";
        Reject(() => AiProtocol.ValidateState((JObject)wrongEnding["state"]), "variable closing text accepted");
        var legacy = Read(root, "initial-state"); legacy["format_version"] = 3;
        Reject(() => AiProtocol.ValidateState(legacy), "old state accepted");
    }

    private static void StorageChecks(string root, string folder)
    {
        var store = new AiSaveStore(folder);
        var session = new AiSession(store);
        session.Schedule("start");
        string first = AiProtocol.Encode(session.PendingRequest);
        session = new AiSession(store);
        Check(AiProtocol.Encode(session.PendingRequest) == first, "restart lost same-ID request");
        session.RequestEnd();
        Check(AiProtocol.Encode(session.PendingRequest) == first && session.EndRequested, "end changed in-flight request");
        // Adapt the synthetic opening to this case, keeping the wire response invariants.
        var response = Read(root, "start.response");
        var pending = session.PendingRequest; var body = (JObject)pending["body"];
        response["case_id"] = body["case_id"].DeepClone(); response["session_id"] = body["session_id"].DeepClone(); response["request_id"] = body["request_id"].DeepClone();
        var state = (JObject)response["state"]; state["case_id"] = body["case_id"].DeepClone(); state["created_at"] = body["created_at"].DeepClone();
        var current = AiProtocol.Current(state); current["id"] = body["session_id"].DeepClone(); current["started_at"] = body["created_at"].DeepClone(); current["opening"]["id"] = body["request_id"].DeepClone();
        var before = store.Load();
        store.BeforeWrite = name => { if (name == "current.json") throw new IOException("simulated pointer failure"); };
        try { session.Receive(pending, response); throw new Exception("write failure did not occur"); } catch (IOException) { checks++; }
        Check(session.HasPendingSave && JToken.DeepEquals(store.Load(), before), "failed save altered committed pair");
        Reject(() => session.Schedule("turn", "추가 입력"), "new input accepted before save");
        store.BeforeWrite = null; session.RetrySave();
        Check(!session.HasPendingSave && session.PendingRequest == null && session.EndRequested, "save retry lost result or end request");
        Reject(() => session.Receive(pending, response), "duplicate response accepted");
        var index = AiProtocol.Parse(File.ReadAllText(Path.Combine(folder, "current.json")));
        string raw = Path.Combine(folder, (string)index["snapshot"], "transcript.json");
        File.WriteAllText(raw, "{}"); Reject(() => store.Load(), "corrupt raw accepted");
    }

    private static void RecoveryChecks(string root, string folder)
    {
        var store = new AiSaveStore(folder);
        var snapshot = AiSaveStore.NewSnapshot();
        snapshot["state"] = Read(root, "turn.request")["state"].DeepClone();
        store.Save(snapshot, -1);
        var session = new AiSession(store);
        string original = "  여러 줄 초안\n카페에서는 어떠세요?  ";
        session.Schedule("turn", original);
        string request = AiProtocol.Encode(session.PendingRequest);
        session.RequestEnd();
        Check(AiProtocol.Encode(session.PendingRequest) == request, "queued end rewrote pending turn");
        // A definitive context_overflow rejects the turn, but must still honor a queued user end.
        session.ReturnRejectedInputToDraft();
        Check(session.Draft == original && session.NextAction == "close", "rejected turn stranded queued end");
        session = new AiSession(store);
        Check(session.EndRequested && session.NextAction == "close", "restart lost queued end");
        session.Schedule("close");
        var pending = session.PendingRequest; var state = session.State;
        var current = AiProtocol.Current(state);
        current["phase"] = "pending_evaluation"; current["closed_at"] = AiProtocol.Timestamp();
        current["closing_request_id"] = pending["body"]["request_id"].DeepClone(); current["closing_text"] = AiProtocol.ClosingText;
        int previous = AiProtocol.Number(state, "revision"); state["revision"] = previous + 1;
        var response = new JObject { ["state"] = state, ["case_id"] = state["case_id"].DeepClone(),
            ["session_id"] = current["id"].DeepClone(), ["request_id"] = pending["body"]["request_id"].DeepClone(),
            ["base_revision"] = previous, ["replayed"] = false, ["disclosure_candidates"] = new JArray() };
        session.Receive(pending, response);
        session = new AiSession(store);
        Check(session.NextAction == "evaluate", "restart did not resume evaluation");
        Reject(() => session.Schedule("turn", "종료 이후"), "closed visit accepted a new turn");
        Reject(() => AiConnection.FromSettings("http://example.invalid", "test"), "remote HTTP accepted");
        Reject(() => AiConnection.FromSettings("https://example.invalid", ""), "remote missing key accepted");
        Reject(() => AiConnection.FromSettings("https://example.invalid", "bad\nkey"), "invalid auth header accepted");
    }

    private static async Task LiveChecks(string file, string folder)
    {
        var connection = AiConnection.Load(file);
        using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(connection.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(180) })
        {
            if (!string.IsNullOrEmpty(connection.ApiKey)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
            var health = AiProtocol.Parse(await http.GetStringAsync("health"));
            Check(AiProtocol.ValidateHealth(health) != "", "health mismatch");
            var store = new AiSaveStore(folder); var session = new AiSession(store);
            async Task Execute(string action, string text = null)
            {
                session.Schedule(action, text);
                JObject request = session.PendingRequest;
                using (var content = new StringContent(AiProtocol.Encode(request["body"]), Encoding.UTF8, "application/json"))
                using (var response = await http.PostAsync("v1/dialogue/" + action, content))
                {
                    Check(response.IsSuccessStatusCode, "HTTP failed: " + (int)response.StatusCode + " (URL/key omitted)");
                    session.Receive(request, AiProtocol.Parse(await response.Content.ReadAsStringAsync()));
                }
                Check(JToken.DeepEquals(session.State, store.Load()["state"]), "live state did not save");
            }
            await Execute("start");
            await Execute("turn", "그동안 많이 지치셨겠어요. 지금 어떤 마음이 드시나요? 카페에서는 어떻게 지내시는지도 궁금해요.");
            session.RequestEnd(); await Execute("close");
            Check((string)AiProtocol.Current(session.State)["closing_text"] == AiProtocol.ClosingText, "closing mismatch");
            session = new AiSession(store); Check(session.Phase == "pending_evaluation", "resume phase mismatch");
            await Execute("evaluate"); Check(session.Phase == "evaluated", "evaluation missing");
            if (!(bool)AiProtocol.Current(session.State)["evaluation"]["cleared"]) await Execute("revisit");
            Console.WriteLine("Live C# state/save/API checks passed; mode=" + (string)health["mode"] + ". This is not a Unity Play check.");
        }
    }

    public static async Task<int> Main(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        string temp = Path.Combine(Path.GetTempPath(), "gamsamo-csharp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            StaticChecks(root); StorageChecks(root, Path.Combine(temp, "save"));
            RecoveryChecks(root, Path.Combine(temp, "recovery"));
            if (args.Length > 1) await LiveChecks(args[1], Path.Combine(temp, "live"));
            Console.WriteLine("Passed " + checks + " C# contract/storage checks. Unity Editor UI execution is separate.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
        finally { Directory.Delete(temp, true); }
    }
}
