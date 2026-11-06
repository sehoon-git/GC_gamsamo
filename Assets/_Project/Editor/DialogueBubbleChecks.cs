using System;
using System.IO;
using System.Linq;
using System.Text;
using Gamsamo.Dialogue;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Real TMP layout checks in an isolated prefab preview. Never saves a scene/prefab or calls AI.</summary>
    public static class DialogueBubbleChecks
    {
        [MenuItem("Gamsamo/AI/Run Single Bubble Checks")]
        public static void Run()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play before isolated layout checks.");
            var report = new StringBuilder();
            int count = 0;
            void Check(bool value, string label)
            {
                report.AppendLine((value ? "PASS " : "FAIL ") + label);
                if (!value) throw new InvalidOperationException(label);
                count++;
            }
            const string path = "Assets/_Project/Prefabs/ConsultationRoomUI.prefab";
            byte[] original = File.ReadAllBytes(path);
            var instance = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var view = instance.GetComponentInChildren<PatientDialogueView>(true);
                Check(view != null && view.bodyText != null && view.bodyText.font != null, "authored dialogue and Korean TMP font exist");
                instance.SetActive(true); view.gameObject.SetActive(true);
                var samples = new[] { "네.", new string('가', 55) + ".",
                    "좋기도 하고 서운하기도 해요. 제 마음부터 천천히 이야기해 볼게요.",
                    "‘괜찮아요’라고 말했지만, 사실은 아직 걱정돼요.",
                    "WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW." };
                foreach (float scale in new[] { .666667f, 1f, 1.5f })
                {
                    instance.transform.localScale = Vector3.one * scale;
                    foreach (string text in samples)
                    {
                        view.ShowLine("검사", text); view.RevealAll();
                        Canvas.ForceUpdateCanvases(); view.bodyText.ForceMeshUpdate(true);
                        var viewport = view.bodyText.rectTransform.parent as RectTransform;
                        Check(view.DisplayedText == text && !view.HasMorePages && view.PageCount == 1, "full reply preserved at scale " + scale);
                        Check(!view.NeedsLegacyScroll, "bounded reply fits in one bubble at scale " + scale);
                        foreach (var character in view.bodyText.textInfo.characterInfo.Take(view.bodyText.textInfo.characterCount).Where(c => c.isVisible))
                        {
                            var lower = viewport.InverseTransformPoint(view.bodyText.rectTransform.TransformPoint(character.bottomLeft));
                            var upper = viewport.InverseTransformPoint(view.bodyText.rectTransform.TransformPoint(character.topRight));
                            Check(lower.x >= viewport.rect.xMin - 1 && lower.y >= viewport.rect.yMin - 1
                                && upper.x <= viewport.rect.xMax + 1 && upper.y <= viewport.rect.yMax + 1, "glyph bounds remain inside bubble");
                        }
                        Check(view.previousPageButton == null || !view.previousPageButton.gameObject.activeSelf, "previous-page control is hidden");
                        Check(view.pageText == null || !view.pageText.gameObject.activeSelf, "page counter is hidden");
                        Check(!view.AdvancePageOrReveal(), "page input does not advance or finish consultation");
                    }
                }
                string legacy = string.Concat(Enumerable.Repeat("이전 서버의 긴 원문입니다. <b>내용을 삭제하지 않습니다.</b>\n", 40));
                view.ShowLine("긴 기록 검사", legacy); view.RevealAll(); view.bodyText.ForceMeshUpdate(true);
                Check(view.DisplayedText == legacy && !view.bodyText.richText && view.NeedsLegacyScroll, "legacy text stays literal and scrollable without paging");
                var scroll = view.bodyText.rectTransform.parent.GetComponent<ScrollRect>();
                Check(scroll.viewport.GetComponent<RectMask2D>() != null && scroll.content == view.bodyText.rectTransform, "scroll fallback is clipped to original bubble");
                scroll.verticalNormalizedPosition = 0;
                view.ShowLine("검사", "다음 짧은 답변입니다."); view.RevealAll();
                Check(!view.NeedsLegacyScroll && scroll.verticalNormalizedPosition >= .99f, "next short reply resets the legacy scroll");
                Debug.Log("SINGLE_BUBBLE_CHECKS_PASS " + count);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(instance);
                if (!original.SequenceEqual(File.ReadAllBytes(path))) throw new InvalidOperationException("Checks changed the source prefab.");
                Directory.CreateDirectory("artifacts/dialogue-fit");
                File.WriteAllText("artifacts/dialogue-fit/unity-checks.txt", report.ToString());
            }
        }
    }
}
