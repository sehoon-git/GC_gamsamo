using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>Edits an isolated draft. Only ApplySettings commits preferences or runtime values.</summary>
    [DisallowMultipleComponent]
    public sealed class PcSettingsView : MonoBehaviour
    {
        public TMP_Dropdown resolutionDropdown;
        public TMP_Dropdown modeDropdown;
        public Slider masterSlider;
        public Slider musicSlider;
        public Slider effectsSlider;
        public TMP_Text masterPercent;
        public TMP_Text musicPercent;
        public TMP_Text effectsPercent;
        public Button applyButton;
        public Button closeButton;
        public Button secondaryCloseButton;
        public AudioSource[] musicSources;
        public AudioSource[] effectsSources;

        public bool IsEditing { get; private set; }
        public GameSettings.Values Draft => draft?.Copy();
        public bool HasUnappliedChanges
        {
            get
            {
                if (!IsEditing || draft == null) return false;
                GameSettings.Values saved = GameSettings.Current;
                return draft.width != saved.width || draft.height != saved.height || draft.screenMode != saved.screenMode
                    || !Mathf.Approximately(draft.masterVolume, saved.masterVolume)
                    || !Mathf.Approximately(draft.musicVolume, saved.musicVolume)
                    || !Mathf.Approximately(draft.effectsVolume, saved.effectsVolume);
            }
        }

        private GameSettings.Values draft;
        private List<Vector2Int> resolutions;
        private bool bound;
        private int dropdownVisibleFrame = -100;
        private int dropdownClosedFrame = -100;

        private void Awake()
        {
            BindControls();
            if (musicSources != null)
                foreach (AudioSource source in musicSources)
                    if (source != null) GameSettings.RegisterMusicSource(source, source.volume);
            if (effectsSources != null)
                foreach (AudioSource source in effectsSources)
                    if (source != null) GameSettings.RegisterEffectsSource(source, source.volume);
        }

        private void OnEnable() => BeginEdit();

        private void LateUpdate()
        {
            if (DropdownExpanded()) dropdownVisibleFrame = Time.frameCount;
        }

        public void BeginEdit()
        {
            BindControls();
            HideDropdowns();
            draft = GameSettings.Current;
            resolutions = GameSettings.GetSupportedResolutions();
            var committedSize = new Vector2Int(draft.width, draft.height);
            // A window can be resized after loading; keep the committed choice representable until Apply.
            if (!resolutions.Contains(committedSize)) resolutions.Add(committedSize);
            if (resolutionDropdown != null)
            {
                resolutionDropdown.ClearOptions();
                var captions = new List<string>();
                foreach (Vector2Int size in resolutions) captions.Add(size.x + " × " + size.y);
                resolutionDropdown.AddOptions(captions);
            }
            if (modeDropdown != null)
            {
                modeDropdown.ClearOptions();
                modeDropdown.AddOptions(new List<string> { "창 모드", "전체 화면", "테두리 없는 창" });
            }
            IsEditing = true;
            RenderDraft();
        }

        public void CancelEdit()
        {
            HideDropdowns();
            if (draft != null) draft = GameSettings.Current;
            IsEditing = false;
            RenderDraft();
        }

        public void ApplySettings()
        {
            if (!IsEditing || draft == null) return;
            HideDropdowns();
            GameSettings.Apply(draft);
            draft = GameSettings.Current;
            RenderDraft();
        }

        /// <summary>Call before handling Escape/backdrop at PC level; true consumes this close request.</summary>
        public bool TryCloseDropdown()
        {
            if (DropdownExpanded())
            {
                HideDropdowns();
                return true;
            }
            // TMP's Cancel may run before the room controller in the same frame. Its fade is
            // nonzero, and these frame guards also cover immediate teardown/event ordering.
            return dropdownClosedFrame == Time.frameCount
                || (Input.GetKeyDown(KeyCode.Escape) && dropdownVisibleFrame >= Time.frameCount - 1);
        }

        private bool DropdownExpanded()
        {
            return (resolutionDropdown != null && resolutionDropdown.IsExpanded)
                || (modeDropdown != null && modeDropdown.IsExpanded);
        }

        private void HideDropdowns()
        {
            if (!DropdownExpanded()) return;
            dropdownClosedFrame = Time.frameCount;
            if (resolutionDropdown != null && resolutionDropdown.IsExpanded) resolutionDropdown.Hide();
            if (modeDropdown != null && modeDropdown.IsExpanded) modeDropdown.Hide();
        }

        private void RenderDraft()
        {
            if (draft == null) return;
            if (resolutionDropdown != null && resolutions != null)
                resolutionDropdown.SetValueWithoutNotify(Mathf.Max(0, resolutions.IndexOf(new Vector2Int(draft.width, draft.height))));
            if (modeDropdown != null) modeDropdown.SetValueWithoutNotify((int)draft.screenMode);
            SetSlider(masterSlider, masterPercent, draft.masterVolume);
            SetSlider(musicSlider, musicPercent, draft.musicVolume);
            SetSlider(effectsSlider, effectsPercent, draft.effectsVolume);
        }

        private static void SetSlider(Slider slider, TMP_Text label, float volume)
        {
            int percent = Mathf.RoundToInt(Mathf.Clamp01(volume) * 100f);
            if (slider != null) slider.SetValueWithoutNotify(percent);
            if (label != null) label.text = percent + "%";
        }

        private void SelectResolution(int index)
        {
            if (!IsEditing || draft == null || resolutions == null || index < 0 || index >= resolutions.Count) return;
            draft.width = resolutions[index].x;
            draft.height = resolutions[index].y;
            dropdownClosedFrame = Time.frameCount;
        }

        private void SelectMode(int index)
        {
            if (!IsEditing || draft == null || index < 0 || index > 2) return;
            draft.screenMode = (GameSettings.ScreenMode)index;
            dropdownClosedFrame = Time.frameCount;
        }

        private void SetMaster(float percent)
        {
            if (!IsEditing || draft == null) return;
            draft.masterVolume = Mathf.Clamp01(percent / 100f);
            SetSlider(masterSlider, masterPercent, draft.masterVolume);
        }

        private void SetMusic(float percent)
        {
            if (!IsEditing || draft == null) return;
            draft.musicVolume = Mathf.Clamp01(percent / 100f);
            SetSlider(musicSlider, musicPercent, draft.musicVolume);
        }

        private void SetEffects(float percent)
        {
            if (!IsEditing || draft == null) return;
            draft.effectsVolume = Mathf.Clamp01(percent / 100f);
            SetSlider(effectsSlider, effectsPercent, draft.effectsVolume);
        }

        private void BindControls()
        {
            if (bound) return;
            if (resolutionDropdown != null) resolutionDropdown.onValueChanged.AddListener(SelectResolution);
            if (modeDropdown != null) modeDropdown.onValueChanged.AddListener(SelectMode);
            if (masterSlider != null) masterSlider.onValueChanged.AddListener(SetMaster);
            if (musicSlider != null) musicSlider.onValueChanged.AddListener(SetMusic);
            if (effectsSlider != null) effectsSlider.onValueChanged.AddListener(SetEffects);
            if (applyButton != null) applyButton.onClick.AddListener(ApplySettings);
            bound = true;
        }

        private void OnDisable() => CancelEdit();

        private void OnDestroy()
        {
            if (resolutionDropdown != null) resolutionDropdown.onValueChanged.RemoveListener(SelectResolution);
            if (modeDropdown != null) modeDropdown.onValueChanged.RemoveListener(SelectMode);
            if (masterSlider != null) masterSlider.onValueChanged.RemoveListener(SetMaster);
            if (musicSlider != null) musicSlider.onValueChanged.RemoveListener(SetMusic);
            if (effectsSlider != null) effectsSlider.onValueChanged.RemoveListener(SetEffects);
            if (applyButton != null) applyButton.onClick.RemoveListener(ApplySettings);
            if (musicSources != null) foreach (AudioSource source in musicSources) GameSettings.UnregisterSource(source);
            if (effectsSources != null) foreach (AudioSource source in effectsSources) GameSettings.UnregisterSource(source);
        }
    }
}
