using System;
using UnityEngine;

namespace Gamsamo.Patients
{
    /// <summary>Display-only record data. The room UI does not own patient progress or persistence.</summary>
    [Serializable]
    public sealed class MedicalRecordData
    {
        public string registrationNumber;
        public string patientName;
        public string gender;
        public string age;
        public string occupation;
        public string recordedDate;
        [TextArea] public string visitReason;
        [TextArea] public string medicalHistory;
        [TextArea] public string medication;
        [TextArea] public string consultationNotes;
        [TextArea] public string consultationTranscript;
        [TextArea] public string consultationResult;
        [TextArea] public string specialNotes;
        public string counselor;

        /// <summary>Fictional preview content, replaced by real game data through SetRecord.</summary>
        public static MedicalRecordData CreateSample()
        {
            return new MedicalRecordData
            {
                registrationNumber = "SAMPLE-001",
                patientName = "김하루",
                gender = "남성",
                age = "28세",
                occupation = "회사원",
                recordedDate = "2026.11.05",
                visitReason = "회의에서 의견을 말할 때 긴장해요.",
                medicalHistory = "아직 확인하지 않음",
                medication = "아직 확인하지 않음",
                consultationNotes = "첫 방문 · 예시 기록\n\n새 직장에 적응하며 느끼는 어려움을\n천천히 이야기하기로 했습니다.\n\n상담에서 나눈 내용이 이곳에\n기록됩니다.",
                specialNotes = "화면 확인을 위한 가상 환자입니다.",
                counselor = "초급 상담사"
            };
        }
    }
}
