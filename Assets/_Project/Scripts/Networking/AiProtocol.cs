using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Gamsamo.Networking
{
    /// <summary>Lossless format-4 state validation shared by Unity and the standalone contract checks.</summary>
    public static class AiProtocol
    {
        public const int FormatVersion = 4;
        public const string ContentVersion = "seoyun-4.0";
        public const string ClosingText = "상담은 여기까지 하겠습니다. 오늘 수고하셨습니다!";
        private static readonly Regex IdPattern = new Regex("^[A-Za-z0-9_-]{1,80}$");
        private static readonly Dictionary<string, JObject> Patients = new Dictionary<string, JObject>(StringComparer.Ordinal);

        public static void RegisterPatient(JObject patient)
        {
            string id = Id(patient, "id");
            Require(Text(patient, "content_version").StartsWith(id + "-", StringComparison.Ordinal) && Number(patient, "clear_threshold") == 70, "환자 콘텐츠 버전·평가 기준 오류입니다.");
            Text(patient, "name");
            var facts = Array(patient, "private_facts").Cast<JObject>().Select(f => Id(f, "id")).ToList();
            Require(facts.Distinct().Count() == facts.Count, "공개 정보 ID가 중복되었습니다.");
            var rubric = Array(patient, "rubric").Cast<JObject>().ToList();
            Require(rubric.Count == 8 && rubric.Select(r => Id(r, "id")).Distinct().Count() == 8
                && rubric.Count(r => Text(r, "axis") == "patient") == 4
                && rubric.Count(r => Text(r, "axis") == "counselor") == 4
                && rubric.All(r => Number(r, "max_score") == 25), "환자별 평가 기준 오류입니다.");
            Patients[id] = Copy(patient);
        }
        public static bool SupportsPatient(string id) => id != null && Patients.ContainsKey(id);
        public static JObject Patient(string id)
        {
            Require(SupportsPatient(id), "지원하지 않거나 로드하지 않은 환자입니다.");
            return Copy(Patients[id]);
        }

        public static JObject Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.MaxDepth = 128;
                var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                Require(!reader.Read(), "JSON 뒤에 추가 내용이 있습니다.");
                return result;
            }
        }

        public static string Encode(JToken value) => value.ToString(Formatting.None);
        public static string ValidateHealth(JObject health, string patientId = "seoyun")
        {
            var entry = Array(health, "supported_patients").Cast<JObject>().FirstOrDefault(p => (string)p["id"] == patientId);
            string version = entry == null ? "" : (string)entry["content_version"] ?? (patientId == "seoyun" ? (string)health["content_version"] : "");
            Require(Number(health, "format_version") == FormatVersion && version == Text(Patient(patientId), "content_version")
                && Array(health, "supported_patient_ids").Any(v => (string)v == patientId)
                && Flag(Object(health, "inference"), "ready"), "지원하지 않거나 준비되지 않은 AI 서버입니다.");
            string mode = Text(health, "mode");
            Require(mode == "mock" || mode == "model", "알 수 없는 AI 실행 모드입니다.");
            return mode;
        }
        public static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");
        public static string Timestamp() => DateTimeOffset.UtcNow.ToString("o");
        public static JObject Copy(JObject value) => (JObject)value.DeepClone();
        public static JObject Current(JObject state) => (JObject)Array(state, "sessions").Last;
        public static string Phase(JObject state) => Text(Current(state), "phase");
        public static JObject Object(JObject value, string key) => value[key] as JObject ?? throw new InvalidDataException("객체 필드 오류: " + key);
        public static JArray Array(JObject value, string key) => value[key] as JArray ?? throw new InvalidDataException("목록 필드 오류: " + key);
        public static string Text(JObject value, string key)
        {
            Require(value[key]?.Type == JTokenType.String, "문자 필드 오류: " + key);
            return (string)value[key];
        }
        public static int Number(JObject value, string key)
        {
            Require(value[key]?.Type == JTokenType.Integer, "숫자 필드 오류: " + key);
            return (int)value[key];
        }
        public static bool Flag(JObject value, string key)
        {
            Require(value[key]?.Type == JTokenType.Boolean, "참/거짓 필드 오류: " + key);
            return (bool)value[key];
        }
        public static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        public static string Id(JObject value, string key)
        {
            string id = Text(value, key);
            Require(IdPattern.IsMatch(id), "ID 형식 오류: " + key);
            return id;
        }
        private static bool Empty(JToken value) => value == null || value.Type == JTokenType.Null;
        private static void Same(JToken a, JToken b, string message) => Require(JToken.DeepEquals(a, b), message);

        public static string ProgressId(JObject state) => state["game_progress_id"] == null ? "legacy" : Id(state, "game_progress_id");

        public static JObject EmptyState(string patientId = "seoyun", AiConversationKey key = null)
        {
            string time = Timestamp();
            var state = new JObject
            {
                ["format_version"] = FormatVersion, ["content_version"] = Text(Patient(patientId), "content_version"),
                ["case_id"] = NewId("case"), ["patient_id"] = patientId, ["revision"] = 0,
                ["created_at"] = time, ["revealed_fact_ids"] = new JArray(),
                ["memory"] = new JObject { ["items"] = new JArray(), ["through_turn_id"] = null },
                ["sessions"] = new JArray(new JObject
                {
                    ["id"] = NewId("session"), ["started_at"] = time, ["closed_at"] = null,
                    ["phase"] = "active", ["closing_request_id"] = null, ["closing_text"] = "",
                    ["closing_speaker"] = "counselor", ["closing_metrics"] = new JObject
                    { ["seconds"] = 0, ["calls"] = 0, ["prompt_tokens"] = 0, ["completion_tokens"] = 0, ["role_seconds"] = new JObject() },
                    ["opening"] = null, ["turns"] = new JArray(), ["evaluation"] = null
                })
            };
            if (key != null)
            {
                Require(patientId == key.PatientId, "환자 ID가 다릅니다.");
                state["game_progress_id"] = key.GameProgressId;
                state["case_id"] = key.CaseId;
                Current(state)["id"] = key.SessionId;
            }
            return state;
        }

        public static JArray Records(JObject state)
        {
            var records = new JArray();
            foreach (JObject session in Array(state, "sessions"))
            {
                string sessionId = Id(session, "id");
                if (session["opening"] is JObject opening)
                    records.Add(Record(Id(opening, "id") + ":patient", sessionId, "patient", Text(opening, "patient_text")));
                foreach (JObject turn in Array(session, "turns"))
                {
                    records.Add(Record(Id(turn, "id") + ":user", sessionId, "counselor", Text(turn, "user_text")));
                    records.Add(Record(Id(turn, "id") + ":patient", sessionId, "patient", Text(turn, "patient_text")));
                }
                if (!string.IsNullOrEmpty((string)session["closing_text"]))
                    records.Add(Record(Id(session, "closing_request_id") + ":closing", sessionId, "counselor", Text(session, "closing_text"), "closing"));
            }
            return records;
        }
        private static JObject Record(string id, string session, string speaker, string text, string kind = "dialogue") =>
            new JObject { ["id"] = id, ["session_id"] = session, ["speaker"] = speaker, ["text"] = text, ["kind"] = kind };

        public static void ValidateState(JObject state)
        {
            var patient = Patient(Id(state, "patient_id"));
            var facts = new HashSet<string>(Array(patient, "private_facts").Select(f => (string)f["id"]));
            Require(Number(state, "format_version") == FormatVersion && Text(state, "content_version") == Text(patient, "content_version"),
                "지원하지 않는 저장 형식입니다. 이전 기록은 보존됩니다.");
            Id(state, "case_id");
            ProgressId(state);
            Require(Number(state, "revision") >= 0, "지원하지 않는 환자 또는 상태입니다.");
            Text(state, "created_at");
            var sessions = Array(state, "sessions");
            Require(sessions.Count > 0, "상담 회차가 없습니다.");
            var sessionIds = new HashSet<string>();
            var utteranceIds = new HashSet<string>();
            var allCandidates = new HashSet<string>();
            foreach (JObject session in sessions)
            {
                Require(sessionIds.Add(Id(session, "id")), "상담 회차 ID가 중복되었습니다.");
                Text(session, "started_at");
                var turns = Array(session, "turns");
                var opening = session["opening"] as JObject;
                Require(turns.Count == 0 || opening != null, "환자 첫 발화가 없는 대화입니다.");
                IEnumerable<JObject> speech = turns.Cast<JObject>();
                if (opening != null) speech = new[] { opening }.Concat(speech);
                foreach (var turn in speech)
                {
                    Require(utteranceIds.Add(Id(turn, "id")), "발언 ID가 중복되었습니다.");
                    Require(!string.IsNullOrWhiteSpace(Text(turn, "patient_text")), "환자 발화가 비어 있습니다.");
                    if (turn != opening) Require(!string.IsNullOrWhiteSpace(Text(turn, "user_text")), "상담자 발화가 비어 있습니다.");
                    foreach (JToken fact in Array(turn, "candidate_fact_ids"))
                    {
                        Require(fact.Type == JTokenType.String && facts.Contains((string)fact), "알 수 없는 공개 후보입니다.");
                        allCandidates.Add((string)fact);
                    }
                }
                string phase = Text(session, "phase");
                Require(new[] { "active", "closing", "pending_evaluation", "evaluated" }.Contains(phase), "알 수 없는 상담 단계입니다.");
                bool closed = phase == "pending_evaluation" || phase == "evaluated";
                Require(closed == !Empty(session["closed_at"]) && closed == !Empty(session["closing_request_id"]), "종료 기록이 단계와 일치하지 않습니다.");
                Require(Text(session, "closing_text") == (closed ? ClosingText : "") && Text(session, "closing_speaker") == "counselor", "고정 종료 안내가 일치하지 않습니다.");
                Require((phase == "evaluated") == !Empty(session["evaluation"]), "평가 상태가 일치하지 않습니다.");
                Require(session == sessions.Last || phase == "evaluated", "이전 회차의 평가가 완료되지 않았습니다.");
            }
            foreach (JToken fact in Array(state, "revealed_fact_ids"))
                Require(fact.Type == JTokenType.String && allCandidates.Contains((string)fact), "후보가 아닌 정보를 공개했습니다.");
            var orderedRecords = Records(state).Cast<JObject>().Where(r => Text(r, "kind") == "dialogue").ToList();
            var raw = orderedRecords.ToDictionary(r => Text(r, "id"));
            var memory = Object(state, "memory");
            var covered = new HashSet<string>();
            if (!Empty(memory["through_turn_id"]))
            {
                string marker = Id(memory, "through_turn_id");
                Require(utteranceIds.Contains(marker), "기억의 원문 위치가 없습니다.");
                foreach (JObject record in orderedRecords)
                {
                    covered.Add(Text(record, "id"));
                    if (Text(record, "id") == marker + ":patient") break;
                }
            }
            foreach (JObject item in Array(memory, "items"))
                foreach (JToken id in Array(item, "source_ids"))
                {
                    Require(covered.Contains((string)id), "기억 출처가 요약 범위에 없습니다.");
                    Require(Text(item, "speaker") == "both" || Text(item, "speaker") == Text(raw[(string)id], "speaker"), "기억 발언자가 일치하지 않습니다.");
                }
            for (int index = 0; index < sessions.Count; index++)
                if (sessions[index]["evaluation"] is JObject evaluation)
                    ValidateEvaluation(patient, evaluation, (JObject)sessions[index], raw,
                        new HashSet<string>(sessions.Take(index + 1).Select(s => (string)s["id"])));
        }

        private static void ValidateEvaluation(JObject patientDefinition, JObject evaluation, JObject session, Dictionary<string, JObject> raw, HashSet<string> allowedSessions)
        {
            var patientCriteria = new HashSet<string>(Array(patientDefinition, "rubric").Cast<JObject>().Where(r => Text(r, "axis") == "patient").Select(r => Id(r, "id")));
            var counselorCriteria = new HashSet<string>(Array(patientDefinition, "rubric").Cast<JObject>().Where(r => Text(r, "axis") == "counselor").Select(r => Id(r, "id")));
            var scores = Array(evaluation, "scores");
            Require(scores.Count == 8, "평가 항목 수가 일치하지 않습니다.");
            var seen = new HashSet<string>();
            int patient = 0, counselor = 0;
            foreach (JObject score in scores)
            {
                string key = Text(score, "criterion_id");
                bool isPatient = patientCriteria.Contains(key);
                Require((isPatient || counselorCriteria.Contains(key)) && seen.Add(key), "평가 항목이 중복되거나 알 수 없습니다.");
                int number = Number(score, "score");
                Require(number >= 0 && number <= 25, "점수 범위가 올바르지 않습니다.");
                var citations = Array(score, "evidence");
                Require(number == 0 || citations.Cast<JObject>().Any(e => !Flag(e, "counter")), "양수 점수에 지지 근거가 없습니다.");
                foreach (JObject evidence in citations)
                {
                    string id = Text(evidence, "message_id"), quote = Text(evidence, "quote");
                    Require(raw.ContainsKey(id) && !string.IsNullOrWhiteSpace(quote), "평가 원문 근거가 없습니다.");
                    var record = raw[id];
                    Require(Text(record, "text").Contains(quote) && Text(record, "speaker") == (isPatient ? "patient" : "counselor")
                        && allowedSessions.Contains(Text(record, "session_id")), "평가 인용·발언자·회차가 일치하지 않습니다.");
                    Require(isPatient || Text(record, "session_id") == Id(session, "id"), "현재 회차가 아닌 상담자 근거입니다.");
                }
                if (isPatient) patient += number; else counselor += number;
            }
            Require(Number(evaluation, "patient_score") == patient && Number(evaluation, "counselor_score") == counselor
                && Flag(evaluation, "cleared") == (patient >= 70 && counselor >= 70), "평가 합산·클리어 판정이 일치하지 않습니다.");
        }

        public static JObject Request(JObject state, string action, string text = null, string revisitSessionId = null)
        {
            ValidateState(state);
            Require(new[] { "start", "open", "revisit", "turn", "close", "evaluate" }.Contains(action), "알 수 없는 요청입니다.");
            string requestId = NewId(action), sessionId = Id(Current(state), "id");
            JObject body;
            if (action == "start")
                body = new JObject { ["patient_id"] = Id(state, "patient_id"), ["case_id"] = Id(state, "case_id"), ["session_id"] = sessionId,
                    ["request_id"] = requestId, ["created_at"] = Text(state, "created_at") };
            else
            {
                if (action == "revisit") sessionId = revisitSessionId ?? NewId("session");
                body = new JObject { ["state"] = state.DeepClone(), ["session_id"] = sessionId };
                body[action == "turn" ? "turn_id" : "request_id"] = requestId;
                if (action == "turn") body["text"] = text;
                if (action == "revisit") body["created_at"] = Timestamp();
            }
            if (action == "start" && ProgressId(state) != "legacy") body["game_progress_id"] = ProgressId(state);
            Id(body, "session_id");
            return new JObject { ["action"] = action, ["body"] = body };
        }

        public static JObject Accept(JObject before, JObject request, JObject response)
        {
            ValidateState(before);
            string action = Text(request, "action");
            Require(new[] { "start", "open", "revisit", "turn", "close", "evaluate" }.Contains(action), "알 수 없는 요청입니다.");
            JObject body = Object(request, "body"), after = Object(response, "state");
            ValidateState(after);
            Same(before["patient_id"], after["patient_id"], "응답이 다른 환자로 변경되었습니다.");
            Require(ProgressId(before) == ProgressId(after), "응답이 다른 게임 진행으로 변경되었습니다.");
            Same(before["content_version"], after["content_version"], "응답의 콘텐츠 버전이 변경되었습니다.");
            string requestId = Id(body, action == "turn" ? "turn_id" : "request_id");
            Require(Id(response, "case_id") == Id(before, "case_id") && Id(after, "case_id") == Id(before, "case_id")
                && Id(response, "session_id") == Id(body, "session_id") && Id(Current(after), "id") == Id(body, "session_id")
                && Id(response, "request_id") == requestId && Number(response, "base_revision") == Number(before, "revision"),
                "응답의 실험·회차·요청·revision이 일치하지 않습니다.");
            bool replay = Flag(response, "replayed");
            Require(Number(after, "revision") == Number(before, "revision") + (replay ? 0 : 1), "응답 revision이 일치하지 않습니다.");
            Same(before["created_at"], after["created_at"], "실험 생성 정보가 변경되었습니다.");
            Same(before["revealed_fact_ids"], after["revealed_fact_ids"], "서버가 공개 확정 상태를 변경했습니다.");
            if (action != "start") Same(body["state"], before, "요청 이후 상태가 변경되었습니다.");
            if (replay) Same(before, after, "재전달 응답이 상태를 변경했습니다.");
            else
            {
                var previous = Array(before, "sessions"); var next = Array(after, "sessions");
                bool revisit = action == "revisit";
                Require(next.Count == previous.Count + (revisit ? 1 : 0), "회차 수가 일치하지 않습니다.");
                int preserve = revisit ? previous.Count : previous.Count - 1;
                for (int i = 0; i < preserve; i++) Same(previous[i], next[i], "이전 회차 원문이 변경되었습니다.");
                JObject old = Current(before), current = Current(after);
                if (!revisit) Same(old["started_at"], current["started_at"], "회차 생성 정보가 변경되었습니다.");
                else Same(body["created_at"], current["started_at"], "재상담 생성 정보가 변경되었습니다.");
                var oldTurns = Array(old, "turns"); var turns = Array(current, "turns");
                if (action == "start" || action == "open" || revisit)
                {
                    Require(turns.Count == 0 && Phase(after) == "active" && current["opening"] is JObject, "첫 발화 응답에 추가 대화가 섞였습니다.");
                    Require(Id(Object(current, "opening"), "id") == requestId, "첫 발화 요청 ID가 다릅니다.");
                    Require(revisit || (oldTurns.Count == 0 && Empty(old["opening"])), "기존 발화를 덮어쓸 수 없습니다.");
                }
                else
                {
                    Same(old["opening"], current["opening"], "환자 첫 발화가 변경되었습니다.");
                    Require(turns.Count == oldTurns.Count + (action == "turn" ? 1 : 0), "응답에 요청하지 않은 대화가 있습니다.");
                    for (int i = 0; i < oldTurns.Count; i++) Same(oldTurns[i], turns[i], "기존 대화 원문이 변경되었습니다.");
                    if (action == "turn")
                    {
                        var turn = (JObject)turns.Last;
                        Require(Phase(before) == "active" && Phase(after) == "active" && Id(turn, "id") == requestId
                            && Text(turn, "user_text") == Text(body, "text"), "응답 턴이 보낸 발언과 다릅니다.");
                    }
                    else if (action == "close") Require(Phase(after) == "pending_evaluation" && Id(current, "closing_request_id") == requestId, "종료 결과가 일치하지 않습니다.");
                    else Require(Phase(after) == "evaluated", "평가 결과가 없습니다.");
                    if (action == "close" || action == "evaluate") Same(before["memory"], after["memory"], "종료·평가가 대화 기억을 변경했습니다.");
                    if (action == "evaluate") foreach (string key in new[] { "closed_at", "closing_request_id", "closing_text", "closing_speaker", "closing_metrics" })
                        Same(old[key], current[key], "평가가 종료 원문을 변경했습니다.");
                }
            }
            var candidates = Array(response, "disclosure_candidates");
            var speech = action == "turn" ? Array(Current(after), "turns").Last as JObject : Current(after)["opening"] as JObject;
            var permitted = speech == null ? new HashSet<string>() : new HashSet<string>(Array(speech, "candidate_fact_ids").Select(v => (string)v));
            Require(candidates.All(v => v.Type == JTokenType.String && permitted.Contains((string)v)), "공개 후보가 원문에 없습니다.");
            Require((action != "close" && action != "evaluate") || candidates.Count == 0, "종료·평가에서 새 정보를 공개했습니다.");
            // Unity's explicit demo policy accepts only the validated proposals for this response.
            var accepted = Copy(after);
            accepted["revealed_fact_ids"] = new JArray(Array(after, "revealed_fact_ids").Select(v => (string)v)
                .Union(candidates.Select(v => (string)v)).OrderBy(v => v, StringComparer.Ordinal));
            ValidateState(accepted);
            return accepted;
        }
    }
}
