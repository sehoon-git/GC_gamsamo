using System;
using System.IO;
using Gamsamo.Dialogue;
using Gamsamo.Networking;
using UnityEditor;
using UnityEngine;

namespace Gamsamo.Editor
{
    /// <summary>Editor-only verification commands. No runtime input field or send-button implementation.</summary>
    public static class PatientAiEditorTools
    {
        [MenuItem("Gamsamo/AI/Import Connection JSON")]
        public static void ImportConnection()
        {
            string source = EditorUtility.OpenFilePanel("AI 연결 JSON 가져오기", "", "json");
            if (string.IsNullOrEmpty(source)) return;
            try
            {
                var settings = AiProtocol.Parse(File.ReadAllText(source));
                AiConnection.FromSettings(AiProtocol.Text(settings, "api_url"), AiProtocol.Text(settings, "api_key"));
                string destination = PatientAiAdapter.ConnectionFile;
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, AiProtocol.Encode(settings));
                    if (File.Exists(destination)) File.Replace(temporary, destination, null);
                    else File.Move(temporary, destination);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                EditorUtility.DisplayDialog("AI 연결", "로컬 연결 설정을 저장했습니다. 진행 중인 오류는 Retry Pending Operation으로 재시도하세요. 환경변수 GAMSAMO_AI_CONNECTION이 있으면 해당 파일이 우선합니다.", "확인");
            }
            catch (Exception) { EditorUtility.DisplayDialog("AI 연결", "설정 형식·HTTPS 주소·접근 키·파일 쓰기 권한을 확인해 주세요. 비밀값은 Console에 출력하지 않습니다.", "확인"); }
        }

        [MenuItem("Gamsamo/AI/Validate Current Scene")]
        public static void ValidateScene()
        {
            var visits = UnityEngine.Object.FindObjectsByType<PatientVisitController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (visits.Length != 1) throw new InvalidOperationException("Open one consultation room scene before validating.");
            if (!visits[0].ValidateConfiguration(out string reason)) throw new InvalidOperationException(reason);
            if (visits[0].dialogue.bodyText == null || visits[0].dialogue.speakerText == null)
                throw new InvalidOperationException("Dialogue body/speaker references are missing.");
            var patient = AiProtocol.Parse(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "GameData", "Patients", "seoyun.json")));
            foreach (string file in Directory.GetFiles(Path.Combine(Application.streamingAssetsPath, "GameData", "Patients"), "*.json"))
                AiProtocol.RegisterPatient(AiProtocol.Parse(File.ReadAllText(file)));
            if (Application.isPlaying && visits[0].GetComponent<PatientAiAdapter>() == null)
                throw new InvalidOperationException("Runtime AI adapter is not attached.");
            Debug.Log("AI scene references/content version passed. Network, input UI, IME, layout and save recovery still require Play checks. No request was sent.");
        }

        private static PatientAiAdapter Current()
        {
            if (!Application.isPlaying || EditorApplication.isPaused) throw new InvalidOperationException("Use unpaused Play mode.");
            var adapter = UnityEngine.Object.FindFirstObjectByType<PatientAiAdapter>();
            if (adapter == null) throw new InvalidOperationException("Open the consultation scene and admit a supported AI patient first.");
            return adapter;
        }
        private static void Result(bool accepted)
        {
            Debug.Log(accepted ? "AI command accepted. Completion is reported after validation and automatic save."
                : "AI command was not accepted in the current state. Read the adapter state in the Inspector; no duplicate request was created.");
        }
        [MenuItem("Gamsamo/AI/Submit Sample Utterance (Play)")]
        public static void SubmitSample() => Result(Current().SubmitText("오늘 이야기하고 싶은 마음은 어떤 건가요? 천천히 말씀해 주셔도 괜찮아요."));
        [MenuItem("Gamsamo/AI/Retry Pending Operation (Play)")]
        public static void Retry() => Result(Current().Retry());
        [MenuItem("Gamsamo/AI/Request End (Play)")]
        public static void RequestEnd() => Result(Current().RequestEnd());
        [MenuItem("Gamsamo/AI/Confirm End (Play)")]
        public static void ConfirmEnd() => Result(Current().ConfirmEnd());
        [MenuItem("Gamsamo/AI/Cancel End (Play)")]
        public static void CancelEnd() => Result(Current().CancelEnd());
        [MenuItem("Gamsamo/AI/Revisit (Play)")]
        public static void Revisit() => Result(Current().Revisit());
        [MenuItem("Gamsamo/AI/Finish Visit (Play)")]
        public static void FinishVisit() => Result(Current().FinishVisit());
        [MenuItem("Gamsamo/AI/New Experiment (Play)")]
        public static void NewExperiment()
        {
            if (EditorUtility.DisplayDialog("새 실험", "현재 결과와 이전 원문은 보존하고 이 환자의 새 실험을 시작할까요?", "새 실험", "취소"))
                Result(Current().NewExperiment());
        }
    }

    [CustomEditor(typeof(PatientAiAdapter))]
    public sealed class PatientAiAdapterInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var adapter = (PatientAiAdapter)target;
            EditorGUILayout.LabelField("AI phase", adapter.CurrentPhase);
            EditorGUILayout.LabelField("Busy / Submit / Edit", adapter.IsBusy + " / " + adapter.CanSubmit + " / " + adapter.CanEditDraft);
            EditorGUILayout.LabelField("End requested / Retry", adapter.EndRequested + " / " + adapter.CanRetry);
            if (!string.IsNullOrEmpty(adapter.LastError)) EditorGUILayout.HelpBox(adapter.LastError, MessageType.Warning);
            EditorGUILayout.HelpBox("Text input UI belongs to the team. Bind SubmitText/UpdateDraft and committed events. Gamsamo > AI contains Editor-only test actions. Connection secrets and dialogue text are not shown here.", MessageType.Info);
            if (Application.isPlaying) Repaint();
        }
    }
}
