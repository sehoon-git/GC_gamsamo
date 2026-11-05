// Compile-only stubs for machines without Unity. They catch C# errors in the adapter, not Unity API/runtime compatibility.
// Never copied into Assets, used for a player build, or described as a Unity Editor/Play test.
using System;
using System.Collections;
namespace UnityEngine
{
    public class Object
    {
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) => new T[0];
        public static T FindFirstObjectByType<T>() => default(T);
    }
    public enum FindObjectsInactive { Include }
    public enum FindObjectsSortMode { None }
    public class Component : Object
    {
        public GameObject gameObject = new GameObject();
        public T GetComponent<T>() => default(T);
    }
    public class Behaviour : Component { public bool enabled; public bool isActiveAndEnabled = true; }
    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => new Coroutine();
        public void StopCoroutine(Coroutine routine) { }
        public void StopAllCoroutines() { }
    }
    public class Coroutine { }
    public class GameObject : Object { public void SetActive(bool active) { } public T AddComponent<T>() => default(T); }
    public class RectTransform : Component { public Vector2 anchoredPosition; }
    public class Sprite : Object { }
    public class CanvasGroup : Behaviour { public float alpha; public bool interactable; public bool blocksRaycasts; }
    public class CanvasRenderer { public bool cullTransparentMesh; }
    public struct Vector2
    {
        public Vector2(float x, float y) { }
        public static Vector2 zero => new Vector2();
        public static Vector2 operator +(Vector2 a, Vector2 b) => a;
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a;
    }
    public static class Mathf
    {
        public static float SmoothStep(float a, float b, float t) => t;
        public static float Clamp01(float value) => value;
        public static float Max(float a, float b) => a;
        public static int Min(int a, int b) => a;
        public static int FloorToInt(float value) => (int)value;
        public static float Lerp(float a, float b, float t) => a;
    }
    public static class Time { public static float unscaledDeltaTime; public static float unscaledTime; }
    public enum KeyCode { Escape }
    public static class Input { public static bool GetKeyDown(KeyCode key) => false; }
    public static class Application { public static string persistentDataPath; public static string streamingAssetsPath; public static bool isPlaying; }
    public static class Debug { public static void Log(object value) { } public static void LogError(object value, Object context = null) { } }
    public class DisallowMultipleComponent : Attribute { }
    public class MinAttribute : Attribute { public MinAttribute(float value) { } }
    public class TextAreaAttribute : Attribute { public TextAreaAttribute(params int[] values) { } }
    public enum RuntimeInitializeLoadType { BeforeSceneLoad, AfterSceneLoad }
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { } }
}
namespace UnityEngine.UI
{
    public class Graphic : UnityEngine.Behaviour { public UnityEngine.CanvasRenderer canvasRenderer = new UnityEngine.CanvasRenderer(); }
    public class Image : Graphic { public bool raycastTarget; public bool preserveAspect; public UnityEngine.Sprite sprite; }
    public class Button : UnityEngine.Behaviour
    {
        public bool interactable; public Graphic targetGraphic; public ButtonClickedEvent onClick = new ButtonClickedEvent();
        public class ButtonClickedEvent { public void AddListener(Action listener) { } public void RemoveListener(Action listener) { } }
    }
}
namespace TMPro
{
    public enum TextOverflowModes { Page }
    public class TMP_Text : UnityEngine.Behaviour
    {
        public bool richText; public string text; public int maxVisibleCharacters; public int pageToDisplay;
        public TextOverflowModes overflowMode; public TMP_TextInfo textInfo = new TMP_TextInfo();
        public void ForceMeshUpdate() { }
    }
    public class TMP_TextInfo { public int characterCount; public int pageCount; public TMP_PageInfo[] pageInfo; }
    public struct TMP_PageInfo { public int firstCharacterIndex; public int lastCharacterIndex; }
}
namespace UnityEngine.Networking
{
    public class UnityWebRequest : IDisposable
    {
        public enum Result { Success }
        public Result result; public long responseCode; public int timeout; public int redirectLimit;
        public UploadHandlerRaw uploadHandler; public DownloadHandlerBuffer downloadHandler;
        public UnityWebRequest(string url, string method) { }
        public static UnityWebRequest Get(string url) => new UnityWebRequest(url, "GET");
        public void SetRequestHeader(string name, string value) { }
        public object SendWebRequest() => null;
        public void Abort() { }
        public void Dispose() { }
    }
    public class UploadHandlerRaw { public UploadHandlerRaw(byte[] bytes) { } }
    public class DownloadHandlerBuffer { public string text; }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { }
    public enum LoadSceneMode { Single, Additive }
    public static class SceneManager { public static event Action<Scene, LoadSceneMode> sceneLoaded; public static void Raise() => sceneLoaded?.Invoke(new Scene(), LoadSceneMode.Single); }
}
namespace Gamsamo.UI
{
    public class ConsultationRoomController : UnityEngine.MonoBehaviour
    {
        public bool IsOverlayBlocking; public Gamsamo.Patients.MedicalRecordData record;
        public void SetRecord(Gamsamo.Patients.MedicalRecordData data) { record = data; }
    }
}
namespace UnityEditor
{
    public class MenuItem : Attribute { public MenuItem(string path) { } }
    public class CustomEditor : Attribute { public CustomEditor(Type type) { } }
    public class Editor { public UnityEngine.Object target; public virtual void OnInspectorGUI() { } public void Repaint() { } }
    public enum MessageType { Info, Warning }
    public static class EditorApplication { public static bool isPaused; }
    public static class EditorUtility
    {
        public static string OpenFilePanel(string title, string directory, string extension) => "";
        public static bool DisplayDialog(string title, string message, string ok, string cancel = null) => false;
    }
    public static class EditorGUILayout { public static void LabelField(string name, string value) { } public static void HelpBox(string text, MessageType type) { } }
}
