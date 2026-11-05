using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Gamsamo.Networking
{
    /// <summary>Immutable state/raw pairs. One atomic pointer commits a snapshot including pending requests.</summary>
    public sealed class AiSaveStore
    {
        private readonly string directory;
        private readonly AiConversationKey scope;
        public AiSaveStore(string directory, AiConversationKey scope = null)
        { this.directory = Path.GetFullPath(directory); this.scope = scope; }
        public string DirectoryPath => directory;
        internal Action<string> BeforeWrite { private get; set; }

        public static JObject NewSnapshot(string patientId = "seoyun", AiConversationKey key = null) => new JObject
        {
            ["local_revision"] = 0, ["state"] = AiProtocol.EmptyState(patientId, key), ["pending_request"] = null,
            ["end_requested"] = false, ["draft"] = "", ["visit_completed"] = false, ["model_only"] = true
        };

        public JObject Load()
        {
            string pointer = Path.Combine(directory, "current.json");
            if (!File.Exists(pointer)) return null;
            var index = AiProtocol.Parse(File.ReadAllText(pointer, Encoding.UTF8));
            string folder = AiProtocol.Text(index, "snapshot");
            AiProtocol.Require(Regex.IsMatch(folder, "^snapshot_[a-f0-9]{32}$"), "저장 포인터가 손상되었습니다.");
            string root = Path.Combine(directory, folder);
            var snapshot = AiProtocol.Parse(File.ReadAllText(Path.Combine(root, "state.json"), Encoding.UTF8));
            ValidateSnapshot(snapshot);
            var state = AiProtocol.Object(snapshot, "state");
            ValidateScope(state);
            AiProtocol.Require(AiProtocol.Number(index, "local_revision") == AiProtocol.Number(snapshot, "local_revision"), "저장 버전이 일치하지 않습니다.");
            var raw = AiProtocol.Parse(File.ReadAllText(Path.Combine(root, "transcript.json"), Encoding.UTF8));
            AiProtocol.Require(JToken.DeepEquals(raw, Transcript(snapshot)), "원문과 상태가 일치하지 않습니다. 기존 기록은 보존됩니다.");
            return snapshot;
        }

        public void Save(JObject snapshot, int expectedRevision)
        {
            ValidateSnapshot(snapshot);
            ValidateScope(AiProtocol.Object(snapshot, "state"));
            Directory.CreateDirectory(directory);
            using (var guard = new FileStream(Path.Combine(directory, ".save.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                JObject previous = Load();
                if (JToken.DeepEquals(previous, snapshot)) return;
                AiProtocol.Require(previous == null || AiProtocol.Id(AiProtocol.Object(previous, "state"), "patient_id")
                    == AiProtocol.Id(AiProtocol.Object(snapshot, "state"), "patient_id"), "다른 환자의 자동 저장을 덮어쓸 수 없습니다.");
                int actual = previous == null ? -1 : AiProtocol.Number(previous, "local_revision");
                AiProtocol.Require(actual == expectedRevision && AiProtocol.Number(snapshot, "local_revision") == actual + 1,
                    "다른 실행이 자동 저장을 변경했습니다. 게임을 다시 열어 저장을 확인하세요.");
                string folder = "snapshot_" + Guid.NewGuid().ToString("N");
                string root = Path.Combine(directory, folder);
                Directory.CreateDirectory(root);
                Write(Path.Combine(root, "state.json"), snapshot);
                Write(Path.Combine(root, "transcript.json"), Transcript(snapshot));
                Write(Path.Combine(directory, "current.json"), new JObject
                    { ["snapshot"] = folder, ["local_revision"] = AiProtocol.Number(snapshot, "local_revision") });
            }
        }

        private void ValidateScope(JObject state)
        {
            if (scope != null) AiProtocol.Require(scope.SameCase(AiConversationKey.FromState(state)), "저장 경로와 진행·환자·사례가 다릅니다.");
        }

        private void Write(string path, JObject value)
        {
            BeforeWrite?.Invoke(Path.GetFileName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(AiProtocol.Encode(value));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // Never delete the old pointer first: replacement either succeeds atomically or throws.
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static JObject Transcript(JObject snapshot)
        {
            var state = AiProtocol.Object(snapshot, "state");
            var result = new JObject { ["case_id"] = state["case_id"].DeepClone(), ["revision"] = state["revision"].DeepClone(),
                ["local_revision"] = snapshot["local_revision"].DeepClone(), ["records"] = AiProtocol.Records(state) };
            if (AiProtocol.ProgressId(state) != "legacy")
            { result["game_progress_id"] = AiProtocol.ProgressId(state); result["patient_id"] = state["patient_id"].DeepClone(); }
            return result;
        }

        public static void ValidateSnapshot(JObject snapshot)
        {
            var state = AiProtocol.Object(snapshot, "state");
            AiProtocol.ValidateState(state);
            AiProtocol.Require(AiProtocol.Number(snapshot, "local_revision") >= 0, "저장 순서가 올바르지 않습니다.");
            AiProtocol.Flag(snapshot, "end_requested"); AiProtocol.Flag(snapshot, "visit_completed"); AiProtocol.Text(snapshot, "draft");
            if (snapshot["model_only"] != null) AiProtocol.Flag(snapshot, "model_only");
            AiProtocol.Require(snapshot["evaluation_sources"] == null || snapshot["evaluation_sources"] is JObject, "평가 생성 출처 형식이 올바르지 않습니다.");
            if (snapshot["evaluation_sources"] is JObject sources)
                foreach (var source in sources.Properties())
                    AiProtocol.Require(AiProtocol.Array(state, "sessions").Cast<JObject>().Any(s => (string)s["id"] == source.Name && s["evaluation"] is JObject)
                        && new[] { "model", "mock", "unknown" }.Contains((string)source.Value), "평가 생성 출처가 올바르지 않습니다.");
            if (snapshot["pending_request"] is JObject request)
            {
                string action = AiProtocol.Text(request, "action");
                var body = AiProtocol.Object(request, "body");
                AiProtocol.Id(body, action == "turn" ? "turn_id" : "request_id");
                AiProtocol.Require(new[] { "start", "open", "turn", "close", "evaluate", "revisit" }.Contains(action), "저장된 요청 종류가 올바르지 않습니다.");
                if (action == "start") AiProtocol.Require(AiProtocol.Id(body, "case_id") == AiProtocol.Id(state, "case_id")
                    && AiProtocol.Id(body, "patient_id") == AiProtocol.Id(state, "patient_id") && AiProtocol.ProgressId(body) == AiProtocol.ProgressId(state)
                    && AiProtocol.Id(body, "session_id") == AiProtocol.Id(AiProtocol.Current(state), "id"), "저장된 시작 요청이 다릅니다.");
                else AiProtocol.Require(JToken.DeepEquals(body["state"], state), "저장된 재시도 요청이 현재 상태와 다릅니다.");
            }
        }
    }
}
