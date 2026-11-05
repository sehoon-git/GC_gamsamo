using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Gamsamo.Networking
{
    /// <summary>AI persistence only. B owns patient order and durable game progress; never infers a resume from a patient alone.</summary>
    public sealed class AiCaseRepository
    {
        private readonly string root;
        private readonly AiPatientCatalog catalog;
        public AiCaseRepository(string root, AiPatientCatalog catalog) { this.root = Path.GetFullPath(root); this.catalog = catalog; }
        public AiSaveStore Store(AiConversationKey key)
        {
            AiProtocol.Require(key.GameProgressId != "legacy", "새 연결에서는 게임 진행 ID를 지정하세요. legacy는 이전 독립 저장 전용입니다.");
            AiProtocol.Require(catalog.Register(key.PatientId) == key.PatientId, "이전 환자 ID는 목록의 정식 ID로 먼저 변환하세요.");
            return new AiSaveStore(Path.Combine(root, "progress", key.GameProgressId, "patients", key.PatientId, "cases", key.CaseId), key);
        }
        public AiSession StartNewCase(AiConversationKey key)
        {
            var session = new AiSession(Store(key), key.PatientId, key);
            AiProtocol.Require((string)AiProtocol.Array(session.State, "sessions")[0]["id"] == key.SessionId,
                "이미 다른 첫 회차로 시작한 사례입니다. 새 사례 ID를 사용하세요.");
            // Repeated start may resume the first session, but cannot jump back from a later revisit.
            AiProtocol.Require(AiConversationKey.FromState(session.State).SessionId == key.SessionId, "이미 재상담한 사례입니다. Resume으로 현재 회차를 지정하세요.");
            return session;
        }
        public AiSession Resume(AiConversationKey key)
        {
            var session = new AiSession(Store(key), key.PatientId, requireExisting: true);
            var current = AiConversationKey.FromState(session.State);
            var pending = session.PendingRequest;
            bool pendingRevisit = pending != null && (string)pending["action"] == "revisit" && (string)pending["body"]?["session_id"] == key.SessionId;
            AiProtocol.Require(current.SessionId == key.SessionId || pendingRevisit, "이어하기 회차가 현재 저장과 다릅니다.");
            return session;
        }
        public AiSession StartRevisit(AiConversationKey previous, string newSessionId)
        {
            var target = previous.WithSession(newSessionId); // Validate before any write.
            AiProtocol.Require(newSessionId != previous.SessionId, "재상담은 새 회차 ID를 사용해야 합니다.");
            var session = new AiSession(Store(previous), previous.PatientId, requireExisting: true);
            var state = session.State;
            string currentId = AiProtocol.Id(AiProtocol.Current(state), "id");
            var sessions = AiProtocol.Array(state, "sessions");
            if (currentId == target.SessionId)
            {
                AiProtocol.Require(sessions.Count >= 2 && (string)sessions[sessions.Count - 2]["id"] == previous.SessionId, "재상담의 이전 회차가 다릅니다.");
                return session; // Same operation after a lost callback/restart.
            }
            AiProtocol.Require(currentId == previous.SessionId, "다른 회차에서 재상담을 시작할 수 없습니다.");
            if (session.PendingRequest != null)
            {
                AiProtocol.Require((string)session.PendingRequest["action"] == "revisit" && (string)session.PendingRequest["body"]?["session_id"] == target.SessionId,
                    "이전에 저장한 요청을 먼저 복구하세요.");
                return session;
            }
            session.Schedule("revisit", revisitSessionId: target.SessionId);
            return session;
        }
        public AiCaseRecord Read(AiConversationKey key)
        {
            var snapshot = Store(key).Load();
            AiProtocol.Require(snapshot != null, "사례 저장이 없습니다.");
            var record = new AiCaseRecord(AiProtocol.Object(snapshot, "state"));
            AiProtocol.Require(record.SessionIds.Contains(key.SessionId), "회차 저장이 없습니다.");
            return record;
        }
        public bool TryReadSavedEvaluation(AiConversationKey key, out AiSavedEvaluation evaluation)
        {
            var snapshot = Store(key).Load(); evaluation = null;
            return snapshot != null && AiSavedEvaluation.TryRead(snapshot, key.SessionId, out evaluation);
        }
        public IReadOnlyList<AiConversationKey> ListCases(string gameProgressId, string patientId)
        {
            var path = Store(new AiConversationKey(gameProgressId, patientId, "probe", "probe")).DirectoryPath;
            var parent = Path.GetDirectoryName(path);
            if (!Directory.Exists(parent)) return System.Array.AsReadOnly(new AiConversationKey[0]);
            var result = new List<AiConversationKey>();
            foreach (string directory in Directory.GetDirectories(parent).OrderBy(p => p, StringComparer.Ordinal))
            {
                var key = new AiConversationKey(gameProgressId, patientId, Path.GetFileName(directory), "probe");
                var snapshot = Store(key).Load();
                if (snapshot != null) result.Add(AiConversationKey.FromState(AiProtocol.Object(snapshot, "state")));
            }
            return result.AsReadOnly();
        }
    }
}
