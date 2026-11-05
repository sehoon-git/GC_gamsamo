using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gamsamo.Patients;
using Gamsamo.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Runs against the real scene, UI raycaster, font, and blur material in Play mode.</summary>
    public static class ConsultationRoomSmokeChecks
    {
        private const string MenuPath = "Gamsamo/Run UI Smoke Checks";
        private static bool running;

        [MenuItem(MenuPath)]
        public static void Run()
        {
            var controller = UnityEngine.Object.FindFirstObjectByType<ConsultationRoomController>();
            if (!Application.isPlaying || controller == null || running)
                return;

            var run = new CheckRun(controller);
            running = true;
            controller.StartCoroutine(GuardedRun(run));
        }

        [MenuItem(MenuPath, true)]
        private static bool CanRun()
        {
            return Application.isPlaying && !running
                && UnityEngine.Object.FindFirstObjectByType<ConsultationRoomController>() != null;
        }

        private static IEnumerator GuardedRun(CheckRun run)
        {
            IEnumerator checks = CheckScene(run);
            while (true)
            {
                object next;
                try
                {
                    if (!checks.MoveNext())
                        break;
                    next = checks.Current;
                }
                catch (Exception exception)
                {
                    run.Check(false, "Unexpected exception", exception.ToString());
                    break;
                }
                yield return next;
            }

            // A failed assertion must not leave the sample cleared or the modal stranded open.
            try
            {
                if (run.Controller != null)
                {
                    run.Controller.SetRecord(run.OriginalRecord);
                    run.Controller.CloseRecord();
                }
            }
            catch (Exception exception)
            {
                run.Check(false, "Cleanup", exception.Message);
            }
            yield return new WaitForSecondsRealtime(0.35f);
            try
            {
                run.WriteReport();
            }
            finally
            {
                running = false;
            }
        }

        private static IEnumerator CheckScene(CheckRun run)
        {
            ConsultationRoomController room = run.Controller;
            bool references = room.modal != null && room.openRecordButton != null
                && room.closeRecordButton != null && room.backdropButton != null
                && room.recordView != null && room.background != null
                && room.backgroundTexture != null && room.backgroundBlur != null
                && room.blurShader != null && EventSystem.current != null;
            run.Check(references, "Scene references and EventSystem are assigned");
            if (!references)
                yield break;

            run.Check(!room.IsRecordOpen && !room.modal.gameObject.activeSelf
                && Mathf.Approximately(room.modal.alpha, 0f) && !room.modal.blocksRaycasts,
                "Record starts fully closed");
            room.CloseRecord();
            yield return new WaitForSecondsRealtime(0.35f);
            Canvas.ForceUpdateCanvases();

            var hotspot = (RectTransform)room.openRecordButton.transform;
            Vector2 inside = ScreenPoint(hotspot, new Vector2(0.6f, 0.4f));
            Vector2 outside = ScreenPoint(hotspot, new Vector2(0.05f, 0.95f));
            List<RaycastResult> insideHits = Raycast(inside);
            List<RaycastResult> outsideHits = Raycast(outside);
            run.Check(FirstButton(insideHits) == room.openRecordButton,
                "Clipboard interior receives pointer input", DescribeHits(insideHits)
                    + " | " + DescribeHotspot(room.openRecordButton, inside));
            run.Check(!outsideHits.Any(hit => hit.gameObject.GetComponentInParent<Button>() == room.openRecordButton),
                "Outside the clipboard polygon does not receive input", DescribeHits(outsideHits));

            EventSystem.current.SetSelectedGameObject(room.openRecordButton.gameObject);
            room.openRecordButton.onClick.Invoke();
            yield return new WaitForSecondsRealtime(0.35f);
            Canvas.ForceUpdateCanvases();
            run.Check(room.IsRecordOpen && room.modal.gameObject.activeSelf
                && Mathf.Approximately(room.modal.alpha, 1f) && room.modal.blocksRaycasts
                && room.modal.interactable && !room.openRecordButton.interactable,
                "Click opens a visible modal and blocks the underlying button");
            Material material = room.background.material;
            run.Check(material != null && material.shader == room.blurShader
                && material.HasProperty("_Amount") && Mathf.Approximately(material.GetFloat("_Amount"), 1f),
                "Room background uses the fully enabled blur material");
            List<RaycastResult> modalHits = Raycast(inside);
            run.Check(FirstButton(modalHits) == room.backdropButton,
                "An open modal intercepts clicks aimed at the desk", DescribeHits(modalHits));

            TMP_Text[] fields = RecordFields(room.recordView);
            run.Check(fields.All(field => field != null), "All twelve record text fields are assigned");
            foreach (TMP_Text field in fields.Where(field => field != null))
            {
                field.ForceMeshUpdate(true);
                run.Check(!field.isTextOverflowing, "Text fits: " + field.name,
                    field.isTextOverflowing ? field.text : null);
                run.Check(!field.richText, "Patient text is literal: " + field.name);
                string missing = field.font == null ? "[font not assigned]"
                    : new string(field.text.Where(character => !char.IsWhiteSpace(character)
                        && !char.IsControl(character) && !field.font.HasCharacter(character, true, false))
                        .Distinct().ToArray());
                run.Check(string.IsNullOrEmpty(missing), "Glyph coverage: " + field.name, missing);
            }
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(run.OutputDirectory, "record-open.png"));
            yield return null;

            room.SetRecord(null);
            run.Check(fields.Where(field => field != null).All(field => field.text == "—"),
                "Clearing record data removes all stale patient text");
            room.SetRecord(run.OriginalRecord);
            run.Check(ReferenceEquals(room.record, run.OriginalRecord), "Original record data is restored");

            room.CloseRecord();
            room.OpenRecord();
            yield return new WaitForSecondsRealtime(0.35f);
            run.Check(room.IsRecordOpen && room.modal.gameObject.activeSelf
                && Mathf.Approximately(room.modal.alpha, 1f) && room.modal.blocksRaycasts,
                "Reopening during the close animation ends fully open");

            room.closeRecordButton.onClick.Invoke();
            yield return new WaitForSecondsRealtime(0.35f);
            CheckClosed(run, "Close button");
            run.Check(EventSystem.current.currentSelectedGameObject == room.openRecordButton.gameObject,
                "Closing restores keyboard focus to the clipboard");

            room.openRecordButton.onClick.Invoke();
            yield return new WaitForSecondsRealtime(0.35f);
            room.backdropButton.onClick.Invoke();
            yield return new WaitForSecondsRealtime(0.35f);
            CheckClosed(run, "Backdrop after reopening");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(run.OutputDirectory, "record-closed.png"));
            yield return null;
        }

        private static void CheckClosed(CheckRun run, string action)
        {
            var room = run.Controller;
            run.Check(!room.IsRecordOpen && !room.modal.gameObject.activeSelf
                && Mathf.Approximately(room.modal.alpha, 0f) && !room.modal.blocksRaycasts
                && room.openRecordButton.interactable, action + " returns to the closed room");
            run.Check(room.background.texture == room.backgroundTexture
                && room.background.material.shader != room.blurShader,
                action + " restores the original unblurred artwork");
        }

        private static TMP_Text[] RecordFields(MedicalRecordView view)
        {
            return new[] { view.registrationNumber, view.patientName, view.gender, view.age,
                view.occupation, view.recordedDate, view.visitReason, view.medicalHistory,
                view.medication, view.consultationNotes, view.specialNotes, view.counselor };
        }

        private static Vector2 ScreenPoint(RectTransform rectTransform, Vector2 normalized)
        {
            Rect rect = rectTransform.rect;
            Vector3 world = rectTransform.TransformPoint(new Vector3(
                rect.xMin + rect.width * normalized.x, rect.yMin + rect.height * normalized.y, 0f));
            Canvas canvas = rectTransform.GetComponentInParent<Canvas>().rootCanvas;
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return RectTransformUtility.WorldToScreenPoint(camera, world);
        }

        private static List<RaycastResult> Raycast(Vector2 position)
        {
            var results = new List<RaycastResult>();
            var pointer = new PointerEventData(EventSystem.current) { position = position };
            EventSystem.current.RaycastAll(pointer, results);
            return results;
        }

        private static Button FirstButton(List<RaycastResult> hits)
        {
            return hits.Count == 0 ? null : hits[0].gameObject.GetComponentInParent<Button>();
        }

        private static string DescribeHits(List<RaycastResult> hits)
        {
            return hits.Count == 0 ? "No hits" : string.Join(", ", hits.Select(hit => hit.gameObject.name));
        }

        private static string DescribeHotspot(Button button, Vector2 point)
        {
            Graphic graphic = button.targetGraphic;
            if (graphic == null)
                return "No target graphic";
            var filter = button.GetComponent<PolygonRaycastFilter>();
            Canvas canvas = button.GetComponentInParent<Canvas>().rootCanvas;
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return "active=" + graphic.IsActive() + ", interactable=" + button.IsInteractable()
                + ", raycastTarget=" + graphic.raycastTarget
                + ", culled=" + graphic.canvasRenderer.cull
                + ", cullTransparentMesh=" + graphic.canvasRenderer.cullTransparentMesh
                + ", polygonAccepts=" + (filter == null || filter.IsRaycastLocationValid(point, camera))
                + ", screenPoint=" + point;
        }

        private sealed class CheckRun
        {
            public readonly ConsultationRoomController Controller;
            public readonly MedicalRecordData OriginalRecord;
            public readonly string OutputDirectory;
            private readonly StringBuilder report = new StringBuilder();
            private int passed;
            private int failed;

            public CheckRun(ConsultationRoomController controller)
            {
                Controller = controller;
                OriginalRecord = controller.record;
                OutputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../artifacts/ui"));
                Directory.CreateDirectory(OutputDirectory);
                report.AppendLine("Consultation room UI smoke checks");
                report.AppendLine("Unity " + Application.unityVersion + " | " + Screen.width + " x " + Screen.height);
                report.AppendLine();
            }

            public void Check(bool success, string name, string detail = null)
            {
                if (success) passed++; else failed++;
                report.AppendLine((success ? "PASS " : "FAIL ") + name);
                if (!string.IsNullOrEmpty(detail))
                    report.AppendLine("  " + detail);
            }

            public void WriteReport()
            {
                string summary = (failed == 0 ? "PASS" : "FAIL") + ": " + passed + " passed, " + failed + " failed";
                report.AppendLine();
                report.AppendLine(summary);
                string path = Path.Combine(OutputDirectory, "smoke-checks.txt");
                File.WriteAllText(path, report.ToString());
                if (failed == 0) Debug.Log("UI smoke checks " + summary + " — " + path);
                else Debug.LogError("UI smoke checks " + summary + " — " + path);
            }
        }
    }
}
