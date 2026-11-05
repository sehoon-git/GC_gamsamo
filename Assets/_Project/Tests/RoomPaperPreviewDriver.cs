#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Gamsamo.UI.Presentation;
using UnityEngine;
using System.Collections;
using System.IO;
using System.Linq;

/// <summary>Only attached to the generated visual fixture, never a production scene.</summary>
public sealed class RoomPaperPreviewDriver : MonoBehaviour
{
    public GuidebookView guide;
    public RoomStatusView status;
    const string SampleError = "요청·자동 저장을 완료한 후 돌아가세요.";
    private void Start()
    {
        Application.runInBackground = true;
        status.Render(SampleError, true, true);
        guide.BindModalGate(() => { status.controls.interactable = status.controls.blocksRaycasts = false; return true; },
            () => { status.controls.interactable = status.controls.blocksRaycasts = true; });
        status.retryButton.onClick.AddListener(() => status.Render(null, false, true));
        status.homeButton.onClick.AddListener(() => status.Render(SampleError, true, true));
        Debug.Log("ROOM_PAPER_VISUAL_FIXTURE: no server or player save; retry/home only toggle sample feedback.");
        if (System.Environment.GetCommandLineArgs().Contains("-paper-guide-capture")) StartCoroutine(Capture());
    }
    private IEnumerator Capture()
    {
        string folder = Path.Combine(Application.dataPath, "..", "captures"); Directory.CreateDirectory(folder);
        // Render to a texture so unattended verification works with a hidden window.
        var camera = Camera.main;
        var target = new RenderTexture(Screen.width, Screen.height, 24);
        camera.targetTexture = target;
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 5; }
        yield return new WaitForSeconds(1);
        yield return new WaitForEndOfFrame();
        SaveFrame(camera, target, Path.Combine(folder, "room-error.png"));
        yield return new WaitForEndOfFrame(); yield return null;
        status.Render(null, false, true);
        yield return new WaitForEndOfFrame();
        SaveFrame(camera, target, Path.Combine(folder, "room-normal.png"));
        yield return new WaitForEndOfFrame(); yield return null;
        guide.TryOpen();
        for (int page = 0; page < guide.PageCount; page++)
        {
            guide.GoTo(page); yield return new WaitForEndOfFrame();
            guide.bodyText.ForceMeshUpdate();
            if (guide.bodyText.textInfo.pageCount > 1) Debug.LogError("Guide text overflows page " + page);
            SaveFrame(camera, target, Path.Combine(folder, "guide-" + (page + 1) + ".png"));
            yield return new WaitForEndOfFrame(); yield return null;
        }
        guide.Close();
        camera.targetTexture = null; target.Release(); Destroy(target);
        Debug.Log("ROOM_PAPER_CAPTURE_COMPLETE");
        yield return new WaitForSeconds(.5f); Application.Quit();
    }
    private static void SaveFrame(Camera camera, RenderTexture target, string path)
    {
        Canvas.ForceUpdateCanvases(); camera.Render();
        var previous = RenderTexture.active; RenderTexture.active = target;
        var frame = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        frame.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); frame.Apply();
        File.WriteAllBytes(path, frame.EncodeToPNG());
        Destroy(frame); RenderTexture.active = previous;
    }
}
#endif
