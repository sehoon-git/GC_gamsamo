using System;
using System.IO;
using Gamsamo.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamsamo.Editor.UiNext
{
    // Editor-only scene copy: never starts AI, progression, economy or player-save services.
    [InitializeOnLoad]
    public static class UiNextPreview
    {
        const string Key = "Gamsamo.UiNextPreview";
        static double next;
        static int step;
        static string mode;
        static System.Collections.IEnumerator checks;
        static UiNextPreview() { EditorApplication.playModeStateChanged += Changed; }
        public static void CaptureBaseline() { Open("before"); }
        public static void CaptureAfter() { Open("after"); }
        public static void RunChecks() { Open("checks"); }
        [MenuItem("Gamsamo/UiNext/Open Isolated UI Preview")]
        public static void OpenPreview() { Open("manual"); }
        public static void InstallAndOpenPreview() { UiNextInstaller.Install(); UiNextChecks.RunExisting(); Open("manual"); }
        static void Open(string requested)
        {
            if (Application.isPlaying || PrefabStageUtility.GetCurrentPrefabStage() != null) throw new InvalidOperationException("Leave Play/Prefab mode first.");
            for (int i=0;i<SceneManager.sceneCount;i++) if(SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save open scenes first.");
            EditorSceneManager.OpenScene("Assets/_Project/Scenes/ConsultationRoom.unity");
            foreach(var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string ns=component.GetType().Namespace ?? "";
                if(ns.StartsWith("Gamsamo.Networking") || ns.StartsWith("Gamsamo.Progression") || ns.StartsWith("Gamsamo.Economy")) UnityEngine.Object.DestroyImmediate(component);
            }
            // This modified copy is deliberately never saved to the production scene.
            SessionState.SetString(Key, requested); EditorApplication.isPlaying=true;
        }
        static void Changed(PlayModeStateChange state)
        {
            if(state==PlayModeStateChange.EnteredPlayMode)
            {
                mode=SessionState.GetString(Key, ""); if(mode=="")return;
                step=0; next=EditorApplication.timeSinceStartup+1; EditorApplication.update+=Tick;
            }
            if(state==PlayModeStateChange.ExitingPlayMode) EditorApplication.update-=Tick;
            if(state==PlayModeStateChange.EnteredEditMode && SessionState.GetString(Key, "")!="")
            { SessionState.EraseString(Key); if(Application.isBatchMode) EditorApplication.Exit(0); }
        }
        static void Tick()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            if(EditorApplication.timeSinceStartup<next)return;
            var room=UnityEngine.Object.FindFirstObjectByType<ConsultationRoomController>();
            try
            {
                if(mode=="checks")
                {
                    if(step==0) { room.OpenPc(); step++; next=EditorApplication.timeSinceStartup+.6; return; }
                    if(step==1) { checks=UiNextChecks.Run(room); step++; }
                    if(step==2) { if(checks.MoveNext()) {next=EditorApplication.timeSinceStartup+.3;return;} next=EditorApplication.timeSinceStartup+.4; step++; return; }
                    EditorApplication.update-=Tick; EditorApplication.isPlaying=false; return;
                }
                if(step==0) { room.OpenPc(); }
                if(step==1) { room.pcDesktopView.OpenProfile(); }
                if(step==2 && mode!="manual") Capture("profile-"+mode,1280,720);
                if(step==3 && mode!="manual") { room.pcDesktopView.CloseActiveWindow(); room.ClosePc(); }
                if(step==4 && mode!="manual") room.OpenRecord();
                if(step==5 && mode!="manual") Capture("record-"+mode,1280,720);
                if(step==6) { EditorApplication.update-=Tick; if(mode!="manual")EditorApplication.isPlaying=false; }
                step++; next=EditorApplication.timeSinceStartup+.6;
            }
            catch(Exception error) { Debug.LogException(error); EditorApplication.update-=Tick; if(Application.isBatchMode)EditorApplication.Exit(1); }
        }
        public static void Capture(string name,int width,int height)
        {
            Directory.CreateDirectory("artifacts/ui-next");
            var go=new GameObject("UI capture camera"); var camera=go.AddComponent<Camera>();
            camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.12f,.09f,.07f); camera.orthographic=true;
            var target=new RenderTexture(width,height,24); camera.targetTexture=target;
            var canvases=UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            var modes=new RenderMode[canvases.Length]; var cameras=new Camera[canvases.Length]; var distances=new float[canvases.Length]; var sorting=new int[canvases.Length]; var overrides=new bool[canvases.Length];
            for(int i=0;i<canvases.Length;i++){sorting[i]=canvases[i].sortingOrder;overrides[i]=canvases[i].overrideSorting;distances[i]=canvases[i].planeDistance;modes[i]=canvases[i].renderMode;cameras[i]=canvases[i].worldCamera;canvases[i].renderMode=RenderMode.ScreenSpaceCamera;canvases[i].worldCamera=camera;canvases[i].planeDistance=1;}
            Canvas.ForceUpdateCanvases(); camera.Render();
            var old=RenderTexture.active; RenderTexture.active=target;
            var image=new Texture2D(width,height,TextureFormat.RGB24,false); image.ReadPixels(new Rect(0,0,width,height),0,0); image.Apply();
            File.WriteAllBytes("artifacts/ui-next/"+name+".png",image.EncodeToPNG()); RenderTexture.active=old;
            for(int i=0;i<canvases.Length;i++){canvases[i].renderMode=modes[i];canvases[i].worldCamera=cameras[i];canvases[i].planeDistance=distances[i];canvases[i].overrideSorting=overrides[i];canvases[i].sortingOrder=sorting[i];}
            UnityEngine.Object.DestroyImmediate(image); camera.targetTexture=null; UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
