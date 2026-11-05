using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Gamsamo.Networking
{
    /// <summary>Formats only committed server records for the desk document; never generates dialogue or scores.</summary>
    public static class AiRecordFormatter
    {
        public static string Transcript(JObject state)
        {
            if (state == null) return "";
            var text = new StringBuilder();
            int index = 0;
            string currentSession = "";
            foreach (JObject record in AiProtocol.Records(state))
            {
                string sessionId = AiProtocol.Text(record, "session_id");
                if (sessionId != currentSession)
                {
                    if (text.Length > 0) text.AppendLine();
                    currentSession = sessionId;
                    text.Append("상담 ").Append(++index).AppendLine();
                }
                bool closing = AiProtocol.Text(record, "kind") == "closing";
                text.Append(closing ? "상담 안내" : AiProtocol.Text(record, "speaker") == "patient" ? "서윤" : "상담자")
                    .Append(": ").AppendLine(AiProtocol.Text(record, "text")).AppendLine();
            }
            return text.ToString().TrimEnd();
        }

        public static string Results(JObject state, IReadOnlyDictionary<string, string> labels)
        {
            if (state == null) return "";
            var text = new StringBuilder();
            int index = 0;
            foreach (JObject session in AiProtocol.Array(state, "sessions"))
            {
                index++;
                if (!(session["evaluation"] is JObject evaluation)) continue;
                if (text.Length > 0) text.AppendLine().AppendLine();
                text.Append("상담 ").Append(index).AppendLine(" · 평가 결과");
                text.Append("환자 변화 ").Append(AiProtocol.Number(evaluation, "patient_score")).AppendLine(" / 100")
                    .Append("상담자 대응 ").Append(AiProtocol.Number(evaluation, "counselor_score")).AppendLine(" / 100")
                    .AppendLine(AiProtocol.Flag(evaluation, "cleared") ? "두 기준을 충족했습니다." : "이번 상담 기록과 평가가 저장되었습니다.");
                foreach (JObject score in AiProtocol.Array(evaluation, "scores"))
                {
                    string id = AiProtocol.Text(score, "criterion_id");
                    text.AppendLine().Append(labels != null && labels.TryGetValue(id, out var label) ? label : id)
                        .Append(" · ").Append(AiProtocol.Number(score, "score")).AppendLine(" / 25")
                        .AppendLine(AiProtocol.Text(score, "explanation"));
                    foreach (JObject evidence in AiProtocol.Array(score, "evidence"))
                        text.Append(AiProtocol.Flag(evidence, "counter") ? "반대 근거: “" : "근거: “")
                            .Append(AiProtocol.Text(evidence, "quote")).AppendLine("”");
                }
            }
            return text.ToString().TrimEnd();
        }
    }
}
