using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Gamsamo.Networking
{
    public sealed class AiPatientInfo
    {
        public string Id { get; }
        public string Name { get; }
        public bool Ready { get; }
        public string ContentVersion { get; }
        public string UnavailableReason { get; }
        public IReadOnlyList<string> Aliases { get; }
        internal AiPatientInfo(JObject value)
        {
            Id = AiProtocol.Id(value, "id"); Name = AiProtocol.Text(value, "name");
            Ready = AiProtocol.Flag(value, "ready"); ContentVersion = (string)value["content_version"];
            UnavailableReason = AiProtocol.Text(value, "unavailable_reason");
            Aliases = System.Array.AsReadOnly(AiProtocol.Array(value, "aliases").Select(v =>
                AiProtocol.Id(new JObject { ["id"] = v.DeepClone() }, "id")).ToArray());
            AiProtocol.Require(!Ready || !string.IsNullOrEmpty(ContentVersion), "준비된 환자의 콘텐츠 버전이 없습니다.");
        }
    }

    /// <summary>One roster for API readiness, legacy UI aliases and formal content. No placeholder narrative.</summary>
    public sealed class AiPatientCatalog
    {
        private readonly string gameDataDirectory;
        private readonly Dictionary<string, AiPatientInfo> lookup = new Dictionary<string, AiPatientInfo>(StringComparer.Ordinal);
        public IReadOnlyList<AiPatientInfo> Patients { get; }
        public AiPatientCatalog(string gameDataDirectory)
        {
            this.gameDataDirectory = gameDataDirectory;
            var value = AiProtocol.Parse(File.ReadAllText(Path.Combine(gameDataDirectory, "patient-catalog.json")));
            AiProtocol.Require(AiProtocol.Number(value, "catalog_version") == 1, "환자 목록 버전이 다릅니다.");
            var patients = AiProtocol.Array(value, "patients").Cast<JObject>().Select(v => new AiPatientInfo(v)).ToArray();
            foreach (var patient in patients)
                foreach (string id in new[] { patient.Id }.Concat(patient.Aliases))
                {
                    AiProtocol.Require(!lookup.ContainsKey(id), "환자 ID 또는 이전 ID가 중복되었습니다.");
                    lookup.Add(id, patient);
                }
            Patients = System.Array.AsReadOnly(patients);
        }
        public bool Contains(string id) => id != null && lookup.ContainsKey(id);
        public AiPatientInfo Get(string id)
        {
            AiProtocol.Require(Contains(id), "unsupported_patient: 최종 AI 대상에 없는 환자입니다.");
            return lookup[id];
        }
        public string Register(string id)
        {
            var info = Get(id);
            AiProtocol.Require(info.Ready, "content_not_ready: " + info.UnavailableReason);
            var patient = AiProtocol.Parse(File.ReadAllText(Path.Combine(gameDataDirectory, "Patients", info.Id + ".json")));
            AiProtocol.Require(AiProtocol.Id(patient, "id") == info.Id && AiProtocol.Text(patient, "content_version") == info.ContentVersion,
                "환자 목록과 정식 콘텐츠가 일치하지 않습니다.");
            AiProtocol.RegisterPatient(patient);
            return info.Id;
        }
    }
}
