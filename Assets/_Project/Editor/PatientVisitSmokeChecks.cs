using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gamsamo.Dialogue;
using Gamsamo.Networking;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Exercises the real scene UI and the six-patient guard; leaves a fresh preview afterward.</summary>
    public static class PatientVisitSmokeChecks
    {
        private const string ReportPath = "artifacts/ui/patient-visit-checks.txt";
        private static PatientVisitController visit;
        private static IEnumerator steps;
        private static readonly List<string> Results = new List<string>();
        private static double nextStepAt;
        private static double deadline;
        private static bool previousRunInBackground;
        private static int failures;
        public static bool IsRunning => steps != null;

        [MenuItem("Gamsamo/Run Patient Visit Checks")]
        public static void Run()
        {
            if (IsRunning) return;
            visit = UnityEngine.Object.FindFirstObjectByType<PatientVisitController>();
            if (!Application.isPlaying || EditorApplication.isPaused || visit == null)
                throw new InvalidOperationException("Patient visit checks require the consultation scene in unpaused Play mode.");
            if (visit.GetComponent<PatientAiAdapter>() != null)
                throw new InvalidOperationException("External AI is attached. Local demo automation is blocked to protect AI requests and saved conversations. Use Gamsamo/AI/Validate Current Scene and the documented manual checks instead.");
            if (visit.AdmittedCount != 0 || visit.State != PatientVisitController.VisitState.Waiting || visit.room.IsOverlayBlocking)
                throw new InvalidOperationException("Start fresh Play mode and close room windows before running patient visit checks.");
            Results.Clear();
            failures = 0;
            previousRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
            steps = CheckVisits();
            nextStepAt = EditorApplication.timeSinceStartup;
            deadline = nextStepAt + 90;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Abort;
            EditorApplication.quitting += Abort;
        }

        private static void Tick()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            if (!Application.isPlaying || visit == null || EditorApplication.isPaused || EditorApplication.timeSinceStartup > deadline)
            {
                Check(false, "Checks remained in unpaused Play mode and completed within 90 seconds");
                Finish();
                return;
            }
            if (EditorApplication.timeSinceStartup < nextStepAt) return;
            try
            {
                if (steps != null && steps.MoveNext())
                {
                    nextStepAt = EditorApplication.timeSinceStartup + (steps.Current is float delay ? delay : 0d);
                    return;
                }
            }
            catch (Exception exception) { Check(false, "Unexpected exception: " + exception); }
            Finish();
        }

        private static IEnumerator CheckVisits()
        {
            var room = visit.room;
            var view = visit.dialogue;
            // Graphic.depth and the raycast registry are assigned during the next canvas render.
            // EditorApplication callbacks can otherwise observe an activated graphic one frame early.
            yield return .2f;
            Check(visit.ValidateConfiguration(out string reason), "All required references and six patients: " + reason);
            Check(visit.patients.Select(p => p.patientId).Distinct().Count() == 6, "Six unique patient IDs");
            Check(visit.patients.Select(p => p.displayName).Distinct().Count() == 6, "Six distinct display names");
            Check(visit.patients.All(p => !p.displayName.Contains("?") && p.demoLines != null && p.demoLines.Length > 0), "Real names and temporary lines are supplied");
            Check(visit.portraitImage.material != null && visit.portraitImage.material.shader.name == "UI/Gamsamo/PatientPortrait", "Portrait material removes the supplied dark backdrop");
            Check(visit.portraitRoot.parent.GetComponent<RectMask2D>() != null, "Portrait is clipped behind the tabletop");
            Check(!view.IsVisible && visit.portraitGroup.alpha == 0, "The room starts without an active patient or dialogue");
            Check(view.transform.parent.Find("VisitStatus") == null, "The old status box beside the door is removed");
            Check(view.endButton.GetComponentInChildren<TMP_Text>().text == "상담 그만하기", "The explicit end button uses the requested wording");
            Check(TextFits(view.statusText), "Dialogue connection status fits its text area");
            Check(visit.doorButton.IsInteractable(), "Door accepts the first patient");
            Check(FrontmostIs(visit.doorButton), "The real UI raycaster reaches the door hotspot: " + DescribeRaycast(visit.doorButton));
            room.OpenPc();
            yield return .35f;
            Check(!visit.TryAdmitNextPatient() && visit.AdmittedCount == 0, "PC overlay blocks door admission");
            room.ClosePc();
            Check(!visit.TryAdmitNextPatient(), "PC fade-out still blocks door admission");
            yield return .35f;

            var sessions = new HashSet<string>();
            for (int patientIndex = 0; patientIndex < 6; patientIndex++)
            {
                string expectedName = visit.patients[patientIndex].displayName;
                string expectedId = visit.patients[patientIndex].patientId;
                string prefix = "Patient " + (patientIndex + 1) + " (" + expectedName + "): ";
                Click(visit.doorButton);
                string session = visit.CurrentSessionId;
                Check(visit.State == PatientVisitController.VisitState.Entering && visit.CurrentPatientId == expectedId, prefix + "door reserves the correct next patient immediately");
                Check(!string.IsNullOrEmpty(session) && sessions.Add(session), prefix + "visit has a fresh session ID");
                for (int click = 0; click < 5; click++)
                    Check(!visit.TryAdmitNextPatient(), prefix + "duplicate entry rejected " + click);
                Check(visit.AdmittedCount == patientIndex + 1, prefix + "spam clicks do not skip anyone");
                yield return visit.entryDuration + .3f;
                Check(visit.State == PatientVisitController.VisitState.Consulting, prefix + "entry finishes in consulting state");
                Check(view.IsVisible && !string.IsNullOrWhiteSpace(view.DisplayedText), prefix + "dialogue begins automatically");
                foreach (var caption in view.nextButton.GetComponentsInChildren<TMP_Text>().Concat(view.endButton.GetComponentsInChildren<TMP_Text>()))
                    Check(TextFits(caption), prefix + "dialogue button caption fits: " + caption.transform.parent.name);
                Check(view.speakerText.text == expectedName && room.record.patientName == expectedName, prefix + "speaker and record use this patient's name");
                Check(visit.portraitImage.sprite == visit.patients[patientIndex].portrait && visit.portraitGroup.alpha == 1f, prefix + "correct portrait is visible");
                Check(!visit.doorButton.IsInteractable() && !visit.TryAdmitNextPatient(), prefix + "unfinished consultation locks the door");
                Check(!visit.CompleteConsultation("wrong-session") && !visit.PresentPatientLine("wrong-session", "잘못 도착한 대사"), prefix + "foreign session updates are rejected");
                view.RevealAll();
                yield return .1f;
                Check(TextFits(view.speakerText) && TextFits(view.bodyText), prefix + "name and temporary dialogue fit their text areas");
                if (patientIndex == 0)
                {
                    Directory.CreateDirectory("artifacts/ui");
                    ScreenCapture.CaptureScreenshot("artifacts/ui/patient-first-visit.png");
                    yield return .1f;
                }
                for (int lineIndex = 1; lineIndex < visit.patients[patientIndex].demoLines.Length; lineIndex++)
                {
                    visit.NextDemoLine();
                    view.RevealAll();
                    yield return .05f;
                    Check(TextFits(view.bodyText), prefix + "temporary line " + (lineIndex + 1) + " fits the panel");
                }
                visit.NextDemoLine();
                Check(visit.State == PatientVisitController.VisitState.Consulting && visit.CompletedCount == patientIndex,
                    prefix + "the final temporary line never auto-completes a visit");
                if (patientIndex == 0)
                {
                    Check(visit.PresentPatientLine(session, "잠깐만요. 제 이야기를 천천히 들어 주실 수 있을까요?"), "Current session can receive a future LLM-style line");
                    yield return .1f;
                    room.OpenRecord();
                    yield return .35f;
                    int revealed = view.bodyText.maxVisibleCharacters;
                    Check(!view.IsVisible && !visit.TryAdmitNextPatient(), "Record overlay suspends dialogue and patient replacement");
                    yield return .15f;
                    Check(view.bodyText.maxVisibleCharacters == revealed, "Typing pauses while a room overlay is open");
                    room.CloseRecord();
                    yield return .35f;
                    Check(view.IsVisible && visit.CurrentSessionId == session, "Closing the record restores the same visit");
                    view.RevealAll();
                }
                Click(view.endButton);
                yield return .1f;
                Check(view.IsConfirming && visit.State == PatientVisitController.VisitState.Consulting, prefix + "end button asks for confirmation");
                foreach (var confirmationText in view.confirmationPanel.GetComponentsInChildren<TMP_Text>())
                    Check(TextFits(confirmationText), prefix + "confirmation text fits: " + confirmationText.name);
                var confirmationHits = HitsAt(visit.doorButton);
                Check(confirmationHits.Count > 0 && confirmationHits[0].gameObject.transform.IsChildOf(view.confirmationPanel.transform)
                    && !visit.TryAdmitNextPatient(), prefix + "confirmation blocks room clicks: " + DescribeRaycast(visit.doorButton));
                Click(view.cancelEndButton);
                Check(!view.IsConfirming && visit.CurrentSessionId == session && visit.CompletedCount == patientIndex, prefix + "cancel keeps the current consultation");
                Click(view.endButton);
                Click(view.confirmEndButton);
                Check(visit.State == PatientVisitController.VisitState.Leaving && !visit.TryAdmitNextPatient(), prefix + "departure remains locked");
                Check(!visit.CompleteConsultation(session), prefix + "duplicate completion cannot count the same patient twice");
                Check(!visit.PresentPatientLine(session, "늦게 도착한 대사"), prefix + "departing patient's late response is rejected");
                yield return visit.exitDuration + .25f;
                Check(visit.CompletedCount == patientIndex + 1 && visit.CurrentPatient == null && !view.IsVisible, prefix + "only explicit completion releases the seat");
                if (patientIndex < 5)
                    Check(visit.State == PatientVisitController.VisitState.Waiting && visit.doorButton.IsInteractable(), prefix + "next patient waits for a new door click");
            }
            Check(visit.State == PatientVisitController.VisitState.Finished && visit.CompletedCount == 6, "All six visits finish exactly once");
            for (int attempt = 0; attempt < 5; attempt++)
                Check(!visit.TryAdmitNextPatient(), "No seventh patient or automatic wraparound " + attempt);
            Check(!visit.doorButton.IsInteractable() && !view.IsVisible && visit.portraitGroup.alpha == 0, "Completed queue leaves an empty room and disabled door");
        }

        private static bool TextFits(TMP_Text text)
        {
            text.ForceMeshUpdate();
            return !text.isTextOverflowing;
        }

        private static bool FrontmostIs(Button button)
        {
            var hits = HitsAt(button);
            return hits.Count > 0 && hits[0].gameObject.GetComponentInParent<Button>() == button;
        }

        private static List<RaycastResult> HitsAt(Button button)
        {
            Canvas.ForceUpdateCanvases();
            var rectangle = (RectTransform)button.transform;
            var canvas = rectangle.GetComponentInParent<Canvas>().rootCanvas;
            Camera eventCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 point = RectTransformUtility.WorldToScreenPoint(eventCamera, rectangle.TransformPoint(rectangle.rect.center));
            var pointer = new PointerEventData(EventSystem.current) { position = point };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, hits);
            return hits;
        }

        private static string DescribeRaycast(Button button)
        {
            var hits = HitsAt(button);
            return "hits=" + (hits.Count == 0 ? "none" : string.Join(",", hits.Select(hit => hit.gameObject.name)))
                + "; graphicDepth=" + (button.targetGraphic != null ? button.targetGraphic.depth.ToString() : "missing")
                + "; canvas=" + button.GetComponentInParent<Canvas>().rootCanvas.pixelRect;
        }

        private static void Click(Button button)
        {
            Check(button != null && button.IsActive() && button.IsInteractable(), "Button can be clicked: " + (button != null ? button.name : "null"));
            if (button == null) return;
            ExecuteEvents.Execute(button.gameObject, new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left
            }, ExecuteEvents.pointerClickHandler);
        }

        private static void Check(bool passed, string name)
        {
            Results.Add((passed ? "PASS " : "FAIL ") + name);
            if (!passed) failures++;
        }

        private static void Abort()
        {
            Check(false, "The Editor changed during patient visit checks");
            Finish();
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Abort;
            EditorApplication.quitting -= Abort;
            steps = null;
            if (Application.isPlaying && visit != null)
            {
                if (visit.room.IsPcOpen) visit.room.ClosePc();
                if (visit.room.IsRecordOpen) visit.room.CloseRecord();
                visit.ResetPreview();
            }
            Application.runInBackground = previousRunInBackground;
            string summary = "Patient visit checks: " + (Results.Count - failures) + " passed, " + failures + " failed.";
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, summary + Environment.NewLine + string.Join(Environment.NewLine, Results), new UTF8Encoding(false));
            if (failures == 0) Debug.Log(summary + " Report: " + ReportPath);
            else Debug.LogError(summary + " Report: " + ReportPath);
            visit = null;
        }
    }
}
