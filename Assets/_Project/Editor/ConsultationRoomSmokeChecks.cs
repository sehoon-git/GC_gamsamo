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
                && room.recordPanel != null && room.backdropButton != null && room.recordDrag != null
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
            run.Check(room.closeRecordButton == null || !room.closeRecordButton.gameObject.activeInHierarchy,
                "Expanded record has no visible close button");
            var artwork = (RectTransform)room.recordView.transform;
            Vector2 centeredPosition = ScreenPoint(artwork, new Vector2(0.5f, 0.5f));
            ClickAt(centeredPosition);
            yield return null;
            run.Check(room.IsRecordOpen, "Clicking inside the record keeps it open");
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

            PointerEventData dragPointer;
            GameObject dragTarget = BeginDrag(centeredPosition,
                centeredPosition + new Vector2(Screen.width * 0.15f, Screen.height * 0.03f), out dragPointer);
            run.Check(dragTarget != null, "Record artwork routes to a drag handler");
            if (dragTarget != null)
            {
                yield return null; // Detect layout components undoing the drag on the following frame.
                Vector2 movedPosition = ScreenPoint(artwork, new Vector2(0.5f, 0.5f));
                run.Check(Vector2.Distance(centeredPosition, movedPosition) > 1f && room.IsRecordOpen,
                    "Dragging moves the record without closing it", centeredPosition + " -> " + movedPosition);
                room.backdropButton.onClick.Invoke();
                run.Check(room.IsRecordOpen && room.recordDrag != null && room.recordDrag.IsDragging,
                    "Backdrop callback cannot close the record during a drag");
                DragTo(dragTarget, dragPointer, new Vector2(Screen.width * 3, Screen.height * 3));
                yield return null;
                run.Check(IsContained(artwork, (RectTransform)room.modal.transform),
                    "Record remains within the viewport when dragged past top-right");
                DragTo(dragTarget, dragPointer, new Vector2(-Screen.width * 2, -Screen.height * 2));
                yield return null;
                run.Check(IsContained(artwork, (RectTransform)room.modal.transform),
                    "Record remains within the viewport when dragged past bottom-left");
                ExecuteEvents.Execute(dragTarget, dragPointer, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(dragTarget, dragPointer, ExecuteEvents.endDragHandler);
                dragPointer.dragging = false;
                // Even a stray backdrop callback in the release frame must not close the record.
                room.backdropButton.onClick.Invoke();
                yield return null;
                run.Check(room.IsRecordOpen && Mathf.Approximately(room.modal.alpha, 1f),
                    "Releasing a drag outside the record does not act as an outside click");
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(run.OutputDirectory, "record-dragged.png"));
                yield return null;
            }

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

            run.Check(Vector2.Distance(centeredPosition, ScreenPoint(artwork, new Vector2(0.5f, 0.5f))) < 1f,
                "Reopening the record resets its drag position to center");

            // Exercise all sides through the actual backdrop raycast, rather than invoking its callback.
            var outsidePoints = new[] { new Vector2(0.01f, 0.5f), new Vector2(0.99f, 0.5f),
                new Vector2(0.5f, 0.01f), new Vector2(0.5f, 0.99f) };
            for (int index = 0; index < outsidePoints.Length; index++)
            {
                Vector2 point = ScreenPoint((RectTransform)room.modal.transform, outsidePoints[index]);
                List<RaycastResult> hits = Raycast(point);
                run.Check(FirstButton(hits) == room.backdropButton,
                    "Outside record side reaches backdrop: " + index, DescribeHits(hits));
                ClickAt(point);
                yield return new WaitForSecondsRealtime(0.35f);
                CheckClosed(run, "Outside click " + index);
                if (index == 0)
                    run.Check(EventSystem.current.currentSelectedGameObject == room.openRecordButton.gameObject,
                        "Closing restores keyboard focus to the clipboard");
                if (index == outsidePoints.Length - 1) continue;
                room.openRecordButton.onClick.Invoke();
                yield return new WaitForSecondsRealtime(0.35f);
                run.Check(Vector2.Distance(centeredPosition, ScreenPoint(artwork, new Vector2(0.5f, 0.5f))) < 1f,
                    "Record reopens centered after outside close: " + index);
            }
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(run.OutputDirectory, "record-closed.png"));
            yield return null;
        }

        private static void ClickAt(Vector2 position)
        {
            List<RaycastResult> hits = Raycast(position);
            if (hits.Count == 0) return;
            var pointer = new PointerEventData(EventSystem.current)
            {
                position = position, pressPosition = position, button = PointerEventData.InputButton.Left,
                pointerPressRaycast = hits[0], pointerCurrentRaycast = hits[0], eligibleForClick = true
            };
            GameObject target = hits[0].gameObject;
            ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerClickHandler);
        }

        private static GameObject BeginDrag(Vector2 start, Vector2 end, out PointerEventData pointer)
        {
            pointer = null;
            List<RaycastResult> hits = Raycast(start);
            if (hits.Count == 0) return null;
            GameObject target = ExecuteEvents.GetEventHandler<IDragHandler>(hits[0].gameObject);
            if (target == null) return null;
            pointer = new PointerEventData(EventSystem.current)
            {
                position = start, pressPosition = start, button = PointerEventData.InputButton.Left,
                pointerPressRaycast = hits[0], pointerCurrentRaycast = hits[0],
                pointerDrag = target, eligibleForClick = false, useDragThreshold = false
            };
            ExecuteEvents.ExecuteHierarchy(hits[0].gameObject, pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(target, pointer, ExecuteEvents.initializePotentialDrag);
            ExecuteEvents.Execute(target, pointer, ExecuteEvents.beginDragHandler);
            pointer.dragging = true;
            DragTo(target, pointer, end);
            return target;
        }

        private static void DragTo(GameObject target, PointerEventData pointer, Vector2 position)
        {
            pointer.delta = position - pointer.position;
            pointer.position = position;
            ExecuteEvents.Execute(target, pointer, ExecuteEvents.dragHandler);
        }

        private static bool IsContained(RectTransform inner, RectTransform outer)
        {
            Canvas.ForceUpdateCanvases();
            var corners = new Vector3[4];
            inner.GetWorldCorners(corners);
            const float tolerance = 1f;
            Rect bounds = outer.rect;
            return corners.Select(outer.InverseTransformPoint).All(point =>
                point.x >= bounds.xMin - tolerance && point.x <= bounds.xMax + tolerance
                && point.y >= bounds.yMin - tolerance && point.y <= bounds.yMax + tolerance);
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
