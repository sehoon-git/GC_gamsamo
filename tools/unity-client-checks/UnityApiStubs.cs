// Compile-only stubs for machines without Unity. They catch C# errors in the adapter, not Unity API/runtime compatibility.
// Never copied into Assets, used for a player build, or described as a Unity Editor/Play test.
using System;
using System.Collections;
namespace UnityEngine
{
    public class Object
    {
        public string name;
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) => new T[0];
        public static T FindFirstObjectByType<T>() => default(T);
        public static T Instantiate<T>(T original, Transform parent, bool worldPositionStays = false) => original;
        public static T Instantiate<T>(T original) => original;
        public static void Destroy(Object value) { }
    }
    public enum FindObjectsInactive { Include }
    public enum FindObjectsSortMode { None }
    public class Component : Object
    {
        public GameObject gameObject = new GameObject();
        public Transform transform;
        public T GetComponent<T>() => default(T);
        public T GetComponentInChildren<T>(bool includeInactive = false) => default(T);
    }
    public class Behaviour : Component { public bool enabled; public bool isActiveAndEnabled = true; }
    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => new Coroutine();
        public void StopCoroutine(Coroutine routine) { }
        public void StopAllCoroutines() { }
    }
    public class Coroutine { }
    public class GameObject : Object { public Transform transform; public GameObject() { } public GameObject(string name, params Type[] components) { } public void SetActive(bool active) { } public T AddComponent<T>() => default(T); public T GetComponent<T>() => default(T); }
    public class RectTransform : Transform { public Vector2 anchoredPosition, anchorMin, anchorMax, pivot, sizeDelta; public Rect rect; }
    public struct Rect { public Vector2 size; public Rect(float x, float y, float width, float height) { size = new Vector2(width, height); } }
    public struct Vector3 { }
    public struct Quaternion { }
    public struct Color { public static Color clear => new Color(); }
    public class Transform : Component { public Transform parent; public Vector3 localScale; public Quaternion localRotation; public void SetParent(Transform parent, bool worldPositionStays) { } public void SetSiblingIndex(int value) { } public int GetSiblingIndex() => 0; }
    public class Sprite : Object { }
    public class CanvasGroup : Behaviour { public float alpha; public bool interactable; public bool blocksRaycasts; }
    public class CanvasRenderer { public bool cullTransparentMesh; }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x=x; this.y=y; }
        public static Vector2 zero => new Vector2();
        public static Vector2 one => new Vector2(1, 1);
        public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
        public static bool operator ==(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
        public override bool Equals(object other) => other is Vector2 v && this == v;
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode();
        public static Vector2 operator +(Vector2 a, Vector2 b) => a;
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a;
    }
    public static class Mathf
    {
        public static float SmoothStep(float a, float b, float t) => t;
        public static float Clamp01(float value) => value;
        public static float Max(float a, float b) => a;
        public static int Max(int a, int b) => a;
        public static int Clamp(int value, int min, int max) => value;
        public static int Min(int a, int b) => a;
        public static int FloorToInt(float value) => (int)value;
        public static float Lerp(float a, float b, float t) => a;
    }
    public static class Time { public static float unscaledDeltaTime; public static float unscaledTime; }
    public enum KeyCode { Escape, LeftControl, RightControl, Return, KeypadEnter }
    public static class Input
    {
        public static string compositionString => "";
        public static bool GetKeyDown(KeyCode key) => false;
        public static bool GetKey(KeyCode key) => false;
    }
    public static class Application { public static string persistentDataPath; public static string streamingAssetsPath; public static string dataPath; public static bool isPlaying; }
    public static class Resources { public static T Load<T>(string path) => default(T); }
    public static class GUI { public static bool enabled; public static void Box(Rect r, string s) { } public static bool Button(Rect r, string s) => false; public static void Label(Rect r, string s) { } }
    public static class Debug { public static void Log(object value) { } public static void LogWarning(object value) { } public static void LogError(object value, Object context = null) { } }
    public class DisallowMultipleComponent : Attribute { }
    public class MinAttribute : Attribute { public MinAttribute(float value) { } }
    public class TextAreaAttribute : Attribute { public TextAreaAttribute(params int[] values) { } }
    public enum RuntimeInitializeLoadType { BeforeSceneLoad, AfterSceneLoad, SubsystemRegistration }
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { } }
}
namespace UnityEngine.UI
{
    public class Graphic : UnityEngine.Behaviour { public UnityEngine.CanvasRenderer canvasRenderer = new UnityEngine.CanvasRenderer(); }
    public class Image : Graphic { public bool raycastTarget; public bool preserveAspect; public UnityEngine.Sprite sprite; public UnityEngine.Color color; }
    public class RectMask2D : UnityEngine.Behaviour { }
    public class ScrollRect : UnityEngine.Behaviour
    {
        public UnityEngine.RectTransform viewport, content;
        public bool horizontal, vertical;
        public enum MovementType { Clamped }
        public MovementType movementType;
        public float scrollSensitivity, verticalNormalizedPosition;
        public void StopMovement() { }
    }
    public class Button : UnityEngine.Behaviour
    {
        public bool interactable; public Graphic targetGraphic; public ButtonClickedEvent onClick = new ButtonClickedEvent();
        public class ButtonClickedEvent { public void AddListener(Action listener) { } public void RemoveListener(Action listener) { } }
    }
}
namespace TMPro
{
    public class TMP_InputField : UnityEngine.Behaviour
    {
        public string text; public bool isFocused; public bool interactable;
        public ChangeEvent onValueChanged = new ChangeEvent();
        public void SetTextWithoutNotify(string value) { text = value; }
        public class ChangeEvent { public void AddListener(Action<string> action) { } public void RemoveListener(Action<string> action) { } }
    }
    public enum TextOverflowModes { Page, Overflow }
    public enum VerticalAlignmentOptions { Top }
    public class TMP_Text : UnityEngine.Behaviour
    {
        public bool richText; public string text; public int maxVisibleCharacters; public int pageToDisplay;
        public UnityEngine.RectTransform rectTransform; public float fontSize; public bool enableAutoSizing, raycastTarget;
        public VerticalAlignmentOptions verticalAlignment;
        public UnityEngine.Vector2 GetPreferredValues(string text, float width, float height) => new UnityEngine.Vector2();
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
    public static class SceneManager { public static event Action<Scene, LoadSceneMode> sceneLoaded; public static void Raise() => sceneLoaded?.Invoke(new Scene(), LoadSceneMode.Single); public static void LoadScene(string name) { } }
}
namespace Gamsamo.UI
{
    public class ConsultationRoomController : UnityEngine.MonoBehaviour
    {
        public bool IsOverlayBlocking; public Gamsamo.Patients.MedicalRecordData record;
        public PcStub pcDesktopView;
        public Func<Gamsamo.Patients.MedicalRecordData, string> CommittedSettlementForRecord;
        public void ClearArchivedRecords() { }
        public void SetArchivedRecord(string key, Gamsamo.Patients.MedicalRecordData data) { }
        public void SetRecord(Gamsamo.Patients.MedicalRecordData data) { record = data; }
        public void OpenRecord() { }
    }
}
namespace UnityEditor
{
    public class MenuItem : Attribute { public MenuItem(string path, bool validate = false) { } }
    public class CustomEditor : Attribute { public CustomEditor(Type type) { } }
    public class Editor { public UnityEngine.Object target; public virtual void OnInspectorGUI() { } public void Repaint() { } }
    public enum MessageType { Info, Warning }
    public static class EditorApplication { public static bool isPaused; public static bool isPlayingOrWillChangePlaymode; }
    public static class Menu { public static void SetChecked(string key, bool value) { } }
    public static class EditorPrefs
    {
        private static readonly System.Collections.Generic.Dictionary<string, bool> values = new System.Collections.Generic.Dictionary<string, bool>();
        public static bool GetBool(string key, bool fallback) => values.TryGetValue(key, out var value) ? value : fallback;
        public static void SetBool(string key, bool value) { values[key] = value; }
    }
    public static class EditorUtility
    {
        public static string OpenFilePanel(string title, string directory, string extension) => "";
        public static bool DisplayDialog(string title, string message, string ok, string cancel = null) => false;
    }
    public static class EditorGUILayout { public static void LabelField(string name, string value) { } public static void HelpBox(string text, MessageType type) { } }
}
