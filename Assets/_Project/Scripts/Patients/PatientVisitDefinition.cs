using System;
using UnityEngine;

namespace Gamsamo.Patients
{
    /// <summary>Presentation data for the local visit demo, not an LLM patient or a clinical record.</summary>
    [Serializable]
    public sealed class PatientVisitDefinition
    {
        public string patientId;
        public string displayName;
        public Sprite portrait;
        [TextArea(2, 5)] public string[] demoLines;
    }
}
