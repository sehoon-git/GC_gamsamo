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
        public AiSaveStore(string directory) { this.directory = Path.GetFullPath(directory); }
        public string DirectoryPath => directory;
        internal Action<string> BeforeWrite { private get; set; }

        public static JObject NewSnapshot() => new JObject
        {
            ["local_revision"] = 0, ["state"] = AiProtocol.EmptyState(), ["pending_request"] = null,
            ["end_requested"] = false, ["draft"] = "", ["visit_completed"] = false
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
            AiProtocol.Require(AiProtocol.Number(index, "local_revision") == AiProtocol.Number(snapshot, "local_revision"), "저장 버전이 일치하지 않습니다.");
            var raw = AiProtocol.Parse(File.ReadAllText(Path.Combine(root, "transcript.json"), Encoding.UTF8));
            AiProtocol.Require(JToken.DeepEquals(raw, Transcript(snapshot)), "원문과 상태가 일치하지 않습니다. 기존 기록은 보존됩니다.");
            return snapshot;
        }

        public void Save(JObject snapshot, int expectedRevision)
        {
            ValidateSnapshot(snapshot);
            Directory.CreateDirectory(directory);
            using (var guard = new FileStream(Path.Combine(directory, ".save.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                JObject previous = Load();
                if (JToken.DeepEquals(previous, snapshot)) return;
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
            return new JObject { ["case_id"] = state["case_id"].DeepClone(), ["revision"] = state["revision"].DeepClone(),
                ["local_revision"] = snapshot["local_revision"].DeepClone(), ["records"] = AiProtocol.Records(state) };
        }

        public static void ValidateSnapshot(JObject snapshot)
        {
            var state = AiProtocol.Object(snapshot, "state");
            AiProtocol.ValidateState(state);
            AiProtocol.Require(AiProtocol.Number(snapshot, "local_revision") >= 0, "저장 순서가 올바르지 않습니다.");
            AiProtocol.Flag(snapshot, "end_requested"); AiProtocol.Flag(snapshot, "visit_completed"); AiProtocol.Text(snapshot, "draft");
            if (snapshot["pending_request"] is JObject request)
            {
                string action = AiProtocol.Text(request, "action");
                var body = AiProtocol.Object(request, "body");
                AiProtocol.Id(body, action == "turn" ? "turn_id" : "request_id");
                AiProtocol.Require(new[] { "start", "open", "turn", "close", "evaluate", "revisit" }.Contains(action), "저장된 요청 종류가 올바르지 않습니다.");
                if (action == "start") AiProtocol.Require(AiProtocol.Id(body, "case_id") == AiProtocol.Id(state, "case_id")
                    && AiProtocol.Id(body, "session_id") == AiProtocol.Id(AiProtocol.Current(state), "id"), "저장된 시작 요청이 다릅니다.");
                else AiProtocol.Require(JToken.DeepEquals(body["state"], state), "저장된 재시도 요청이 현재 상태와 다릅니다.");
            }
        }
    }
}
