using Gamsamo.Patients;
using TMPro;
using UnityEngine;

namespace Gamsamo.UI
{
    [DisallowMultipleComponent]
    public sealed class MedicalRecordView : MonoBehaviour
    {
        public TMP_Text registrationNumber;
        public TMP_Text patientName;
        public TMP_Text gender;
        public TMP_Text age;
        public TMP_Text occupation;
        public TMP_Text recordedDate;
        public TMP_Text visitReason;
        public TMP_Text medicalHistory;
        public TMP_Text medication;
        public TMP_Text consultationNotes;
        public TMP_Text specialNotes;
        public TMP_Text counselor;

        public void ShowRecord(MedicalRecordData data)
        {
            SetText(registrationNumber, data?.registrationNumber);
            SetText(patientName, data?.patientName);
            SetText(gender, data?.gender);
            SetText(age, data?.age);
            SetText(occupation, data?.occupation);
            SetText(recordedDate, data?.recordedDate);
            SetText(visitReason, data?.visitReason);
            SetText(medicalHistory, data?.medicalHistory);
            SetText(medication, data?.medication);
            SetText(consultationNotes, data?.consultationNotes);
            SetText(specialNotes, data?.specialNotes);
            SetText(counselor, data?.counselor);
        }

        private static void SetText(TMP_Text target, string value)
        {
            if (target == null)
                return;

            // Narrative and player-controlled content must never become TMP markup.
            target.richText = false;
            target.text = string.IsNullOrWhiteSpace(value) ? "—" : value;
        }
    }
}
