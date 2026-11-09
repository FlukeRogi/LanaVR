using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PoL.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LanaVR
{
    /// <summary>
    /// The VR settings page. It is built at runtime by cloning the game's own Settings Audio view and one
    /// of its Settings list buttons, so fonts, sprites, sliders, arrows, highlights and navigation are all
    /// the game's originals. The page is reached through Settings, from the main menu or the pause menu.
    /// </summary>
    public class VrMenu : MonoBehaviour
    {
        public VrMenu(IntPtr p) : base(p) { }

        public static VrMenu I;

        class Row
        {
            public ToggleAudioVolumeButton Choice;
            public BepInEx.Configuration.ConfigEntry<float> Entry;
            public float Min, Max, Step, Default;
            public string Format;
            public string[] Labels;
            public int LastStep = -1, Notches;
        }

        class ToggleRow
        {
            public ToggleRumbleEnabledButton Choice;
            public BepInEx.Configuration.ConfigEntry<bool> Entry;
            public bool Default, Shown;
        }

        // The positions of the On and Off text in the Rumble row's list of options.
        const int OffIndex = 0, OnIndex = 1;
        static readonly Dictionary<IntPtr, ToggleRow> OurToggles = new Dictionary<IntPtr, ToggleRow>();
        readonly List<ToggleRow> _toggles = new List<ToggleRow>();
        readonly List<Row> _rows = new List<Row>();
        GameObject _view, _entryButton, _firstRow;
        SettingsView _settings;
        CanvasGroup _viewGroup, _settingsGroup;
        Canvas _viewCanvas;
        bool _built, _open;
        float _nextTry;

        public bool IsOpen => _open;
        public string Status => $"built={_built} open={_open} selected={(EventSystem.current?.currentSelectedGameObject != null ? EventSystem.current.currentSelectedGameObject.name : "none")}" +
            (_built ? $" pageCanvas={_viewCanvas.enabled} pageAlpha={_viewGroup.alpha} settingsCanvas={_settings.canvas.enabled} settingsGroup={_settingsGroup.interactable}/{_settingsGroup.alpha} entryInteractable={_entryButton.GetComponent<Button>().IsInteractable()} entryNav={_entryButton.GetComponent<Button>().navigation.mode}" : "") + SelectedSlider;

        string SelectedSlider { get { var s = EventSystem.current?.currentSelectedGameObject; foreach (var r in _rows) if (r.Choice != null && s != null && r.Choice.gameObject == s) return $" slider={r.Choice.slider.Value:F3} step={r.Choice.slider.step:F3} lastStep={r.LastStep}"; return ""; } }

        void Start() { I = this; }

        void Update()
        {
            if (!_built && Time.unscaledTime >= _nextTry)
            {
                _nextTry = Time.unscaledTime + 1f;
                try { TryBuild(); }
                catch (Exception e) { Plugin.L.LogError("Could not build the VR menu: " + e); _nextTry = float.MaxValue; }
            }
            if (_built)
            {
                // If the game closes Settings by any route, such as Start resuming play, this page goes too.
                if (_open && (_settings == null || _settings.canvas == null || !_settings.canvas.enabled)) Close(false);
                SyncInteractable();
                PollRows();
            }
            ApplyCutsceneBorders();
            PollShortcuts();
        }

        CinematicView _cinematic;
        float _nextCinematicTry;

        /// <summary>The game still runs its borders as normal, so it sees no difference. Only the two bars
        /// are hidden, which also means the setting applies at once, even during a cutscene.</summary>
        void ApplyCutsceneBorders()
        {
            if (_cinematic == null)
            {
                if (Time.unscaledTime < _nextCinematicTry) return;
                _nextCinematicTry = Time.unscaledTime + 1f;
                foreach (var v in Resources.FindObjectsOfTypeAll<CinematicView>()) if (v.gameObject.scene.IsValid()) _cinematic = v;
                if (_cinematic == null) return;
            }
            bool show = Cfg.CutsceneBorders.Value;
            var top = _cinematic.top; var bottom = _cinematic.bottom;
            if (top != null && top.gameObject.activeSelf != show) top.gameObject.SetActive(show);
            if (bottom != null && bottom.gameObject.activeSelf != show) bottom.gameObject.SetActive(show);
        }

        /// <summary>The game's menus and movement input send navigation to the nearest usable button, even
        /// during play. These buttons must be usable only while they are really on screen, or walking can
        /// select one and jump can press it.</summary>
        void SyncInteractable()
        {
            bool entryAllowed = !_open && SettingsOnScreen;
            if (_entryUsable != entryAllowed || _rowsUsable != _open)
            {
                _entryUsable = entryAllowed;
                _rowsUsable = _open;
                _entrySelectable.interactable = entryAllowed;
                foreach (var row in _pageSelectables) if (row != null) row.interactable = _open;
            }
        }

        bool SettingsOnScreen => _settings != null && _settings.canvas != null && _settings.canvas.enabled && _settingsGroup.alpha > 0.5f && _settingsGroup.interactable;

        void PollShortcuts()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f8Key.wasPressedThisFrame) XrRig.I?.RequestRecenter();
            Scenery.PollKeys(kb);
            var pad = Gamepad.current;
            if (pad != null && pad.leftStickButton.isPressed && pad.rightStickButton.isPressed
                && (pad.leftStickButton.wasPressedThisFrame || pad.rightStickButton.wasPressedThisFrame))
                XrRig.I?.RequestRecenter();
        }

        Selectable _entrySelectable;
        readonly List<Selectable> _pageSelectables = new List<Selectable>();
        bool _entryUsable = true, _rowsUsable = true;

        void TryBuild()
        {
            SettingsAudioView audio = null;
            foreach (var v in Resources.FindObjectsOfTypeAll<SettingsAudioView>()) if (v.gameObject.scene.IsValid()) audio = v;
            foreach (var v in Resources.FindObjectsOfTypeAll<SettingsView>()) if (v.gameObject.scene.IsValid()) _settings = v;
            if (audio == null || _settings == null) return;

            BuildEntryButton();
            BuildView(audio);
            _entrySelectable = _entryButton.GetComponent<Selectable>();
            foreach (var row in _view.GetComponentsInChildren<Selectable>(true)) _pageSelectables.Add(row);
            _built = true;
            SyncInteractable();
            Plugin.L.LogInfo("VR menu built from the game's Settings views");
        }

        /// <summary>Clones with the source inactive, so the clone's Awake does not run until the game's
        /// scripts have been stripped from it.</summary>
        static GameObject CloneInactive(GameObject source, Transform parent)
        {
            bool was = source.activeSelf;
            source.SetActive(false);
            var clone = UnityEngine.Object.Instantiate(source, parent);
            source.SetActive(was);
            return clone;
        }

        static void Strip<T>(GameObject go) where T : Component
        {
            foreach (var c in go.GetComponentsInChildren<T>(true)) UnityEngine.Object.DestroyImmediate(c);
        }

        static void SetTitle(GameObject row, string text)
        {
            Strip<LocalizedUIText>(row);
            var t = row.transform.Find("Plate/Title");
            if (t != null) t.GetComponent<TextMeshProUGUI>().text = text;
        }

        void BuildEntryButton()
        {
            var group = _settings.transform.Find("Group");
            var source = group.Find("SettingsAudioButton").gameObject;
            _entryButton = CloneInactive(source, group);
            _entryButton.name = "SettingsVRButton";
            UnityEngine.Object.DestroyImmediate(_entryButton.GetComponent<ViewSectionButton>());
            SetTitle(_entryButton, "VR");
            _entryButton.transform.SetSiblingIndex(group.Find("SettingsAccessibilityButton").GetSiblingIndex() + 1);
            AddAction(_entryButton, Open);
            _entryButton.SetActive(true);
        }

        static void AddAction(GameObject row, Action action)
        {
            var button = row.GetComponent<Button>();
            if (button == null) throw new InvalidOperationException("row has no Button: " + row.name);
            var listener = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(action);
            if (listener == null) throw new InvalidOperationException("delegate conversion failed");
            if (button.onClick == null) button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(listener);
        }

        void BuildView(SettingsAudioView audio)
        {
            _view = CloneInactive(audio.gameObject, audio.transform.parent);
            _view.name = "Settings VR [View]";
            UnityEngine.Object.DontDestroyOnLoad(_view);
            UnityEngine.Object.DestroyImmediate(_view.GetComponent<SettingsAudioView>());

            _viewCanvas = _view.GetComponent<Canvas>();
            _viewGroup = _view.GetComponent<CanvasGroup>();
            _settingsGroup = _settings.GetComponent<CanvasGroup>();
            var group = _view.transform.Find("Group");

            var title = group.Find("Title Field [Text]").gameObject;
            Strip<LocalizedUIText>(title);
            title.GetComponent<TextMeshProUGUI>().text = "VR";

            // The world stays visible behind this page, so changes to the screen can be judged live.
            var color = _view.transform.Find("Settings Background/Color [Image]")?.GetComponent<Image>();
            if (color != null) { var c = color.color; c.a = Mathf.Min(c.a, 0.45f); color.color = c; }

            var sliders = new List<GameObject>();
            GameObject reset = null;
            for (int i = 0; i < group.childCount; i++)
            {
                var child = group.GetChild(i).gameObject;
                if (child.GetComponent<ToggleAudioVolumeButton>() != null) sliders.Add(child);
                else if (child.GetComponent<ResetToDefaultButton>() != null) reset = child;
            }
            if (sliders.Count < 4 || reset == null) throw new InvalidOperationException($"unexpected Settings Audio layout: {sliders.Count} sliders, reset={reset != null}");
            for (int i = 4; i < sliders.Count; i++) sliders[i].SetActive(false);

            var skyRow = CloneInactive(sliders[3], group);
            skyRow.transform.SetSiblingIndex(sliders[3].transform.GetSiblingIndex() + 1);
            var waterRow = CloneInactive(sliders[3], group);
            waterRow.transform.SetSiblingIndex(skyRow.transform.GetSiblingIndex() + 1);
            var rippleRow = CloneInactive(sliders[3], group);
            rippleRow.transform.SetSiblingIndex(waterRow.transform.GetSiblingIndex() + 1);

            var recenter = CloneInactive(reset, group);
            recenter.name = "RecenterButton";
            UnityEngine.Object.DestroyImmediate(recenter.GetComponent<ResetToDefaultButton>());
            SetTitle(recenter, "RECENTER VIEW");
            recenter.transform.SetSiblingIndex(title.transform.GetSiblingIndex() + 1);
            AddAction(recenter, () => XrRig.I?.RequestRecenter());
            recenter.SetActive(true);
            _firstRow = recenter;

            UnityEngine.Object.DestroyImmediate(reset.GetComponent<ResetToDefaultButton>());
            SetTitle(reset, "RESET TO DEFAULTS");
            AddAction(reset, ResetDefaults);

            AddSlider(sliders[0], "SCREEN SIZE", Cfg.ScreenWidth, 1f, 10f, 0.25f, Cfg.DefaultWidth, "{0:0.00} m");
            AddSlider(sliders[1], "SCREEN DEPTH", Cfg.ScreenDistance, 1f, 10f, 0.25f, Cfg.DefaultDistance, "{0:0.00} m");
            AddSlider(sliders[2], "STEREO STRENGTH", Cfg.Stereo, 0f, 3f, 0.1f, Cfg.DefaultStereo, "{0:0.0}");
            AddSlider(sliders[3], "HEAD TRACKING", Cfg.HeadTracking, 0f, 2f, 0.1f, Cfg.DefaultHeadTracking, "{0:0.0}");
            AddSlider(skyRow, "SKY QUALITY", Cfg.SkyQuality, 0f, 2f, 1f, Cfg.DefaultSkyQuality, "{0:0}", new[] { "LOW", "MEDIUM", "HIGH" });
            skyRow.SetActive(true);
            AddSlider(waterRow, "WATER HEIGHT", Cfg.WaterHeight, -1f, 1f, 0.05f, Cfg.DefaultWaterHeight, "{0:+0.00;-0.00;0.00} m");
            waterRow.SetActive(true);
            AddSlider(rippleRow, "RIPPLE STRENGTH", Cfg.RippleStrength, 0f, 2f, 0.1f, Cfg.DefaultRipple, "{0:0.0}");
            rippleRow.SetActive(true);
            var reflectionRow = CloneInactive(sliders[3], group);
            reflectionRow.transform.SetSiblingIndex(rippleRow.transform.GetSiblingIndex() + 1);
            AddSlider(reflectionRow, "REFLECTION QUALITY", Cfg.ReflectionQuality, 0f, 2f, 1f, Cfg.DefaultReflectionQuality, "{0:0}", new[] { "LOW", "MEDIUM", "HIGH" });
            reflectionRow.SetActive(true);
            var sizeRow = CloneInactive(sliders[3], group);
            sizeRow.transform.SetSiblingIndex(reflectionRow.transform.GetSiblingIndex() + 1);
            AddSlider(sizeRow, "SCENERY SIZE", Cfg.ScenerySize, 0.5f, 1.5f, 0.05f, Cfg.DefaultScenerySize, "{0:0%}");
            sizeRow.SetActive(true);
            AddToggle(group, sizeRow.transform.GetSiblingIndex() + 1, "CUTSCENE BORDERS", Cfg.CutsceneBorders, Cfg.DefaultCutsceneBorders);
            AddToggle(group, sizeRow.transform.GetSiblingIndex(), "3D SCENERY", Cfg.Scenery, Cfg.DefaultScenery);
            AddToggle(group, reflectionRow.transform.GetSiblingIndex(), "SCREEN REFLECTION", Cfg.ScreenReflection, Cfg.DefaultScreenReflection);

            _view.SetActive(true);
            _viewCanvas.enabled = false;
            _viewGroup.alpha = 0;
            _viewGroup.interactable = false;
            _viewGroup.blocksRaycasts = false;
        }

        void AddSlider(GameObject row, string title, BepInEx.Configuration.ConfigEntry<float> entry, float min, float max, float step, float def, string format, string[] labels = null)
        {
            // The game's slider base class is abstract, so the clone keeps the audio row's script. The
            // patches below stop this instance from reading or changing the game's audio settings.
            var choice = row.GetComponent<ToggleAudioVolumeButton>();
            OurRows.Add(choice.Pointer);
            choice.parameter = "";
            var slider = choice.slider;
            row.name = "VR " + title;
            SetTitle(row, title);

            // The game's slider is normalized, and its arrows move it by one notch of this size.
            int notches = Mathf.RoundToInt((max - min) / step);
            slider.step = 1f / notches;
            _rows.Add(new Row { Choice = choice, Entry = entry, Min = min, Max = max, Step = step, Default = def, Format = format, Labels = labels, Notches = notches });
        }

        /// <summary>On/off rows are clones of the game's Rumble row, so they keep its arrows and its
        /// translated On and Off text. The patches below keep them away from the real rumble setting.</summary>
        void AddToggle(Transform group, int index, string title, BepInEx.Configuration.ConfigEntry<bool> entry, bool def)
        {
            ToggleRumbleEnabledButton source = null;
            foreach (var v in Resources.FindObjectsOfTypeAll<ToggleRumbleEnabledButton>()) if (v.gameObject.scene.IsValid()) source = v;
            if (source == null) { Plugin.L.LogWarning("The game's on/off row was not found, so " + title + " has no menu row"); return; }

            var row = CloneInactive(source.gameObject, group);
            row.name = "VR " + title;
            row.transform.SetSiblingIndex(index);
            var label = row.transform.Find("Plate/Title");
            Strip<LocalizedUIText>(label.gameObject);
            label.GetComponent<TextMeshProUGUI>().text = title;
            var t = new ToggleRow { Choice = row.GetComponent<ToggleRumbleEnabledButton>(), Entry = entry, Default = def };
            _toggles.Add(t);
            OurToggles[t.Choice.Pointer] = t;
            row.SetActive(true);
            ShowToggle(t);
        }

        static void ShowToggle(ToggleRow t)
        {
            t.Shown = t.Entry.Value;
            t.Choice.SetValueByIndex(t.Entry.Value ? OnIndex : OffIndex);
        }

        void PollRows()
        {
            foreach (var t in _toggles) if (t.Shown != t.Entry.Value) ShowToggle(t);
            foreach (var r in _rows)
            {
                if (r.Choice == null || r.Choice.slider == null) continue;
                int current = Mathf.RoundToInt(r.Choice.slider.Value * r.Notches);
                if (r.LastStep < 0 || !_open)
                {
                    // The config is the source of truth while the page is closed.
                    Show(r, Mathf.RoundToInt((Mathf.Clamp(r.Entry.Value, r.Min, r.Max) - r.Min) / r.Step));
                    continue;
                }
                if (current == r.LastStep) continue;
                Show(r, current);
                r.Entry.Value = r.Min + current * r.Step;
            }
        }

        void Show(Row r, int steps)
        {
            r.LastStep = steps;
            r.Choice.slider.SetValue((float)steps / r.Notches);
            r.Choice.SetTextValue(r.Labels != null ? r.Labels[steps] : string.Format(System.Globalization.CultureInfo.InvariantCulture, r.Format, r.Min + steps * r.Step));
        }

        void ResetDefaults()
        {
            foreach (var r in _rows)
            {
                r.Entry.Value = r.Default;
                Show(r, Mathf.RoundToInt((r.Default - r.Min) / r.Step));
            }
            foreach (var t in _toggles) { t.Entry.Value = t.Default; ShowToggle(t); }
            Plugin.L.LogInfo("VR settings reset to defaults");
        }

        readonly List<GameObject> _hidden = new List<GameObject>();

        void HideContents(Transform view)
        {
            for (int i = 0; i < view.childCount; i++)
            {
                var child = view.GetChild(i).gameObject;
                if (!child.activeSelf) continue;
                child.SetActive(false);
                _hidden.Add(child);
            }
        }

        public void Open()
        {
            if (!_built || _open || !SettingsOnScreen) return;
            _open = true;
            // Settings and the menus behind it would show through this page's translucent background. Only
            // their contents are hidden: the game reads each view's canvas and alpha to know its menu
            // state, so those stay untouched and still say whether the game considers a menu open.
            _hidden.Clear();
            HideContents(_settings.transform);
            foreach (var v in Resources.FindObjectsOfTypeAll<MainMenuView>()) if (v.canvas != null && v.canvas.enabled && v.gameObject.scene.IsValid()) HideContents(v.transform);
            foreach (var v in Resources.FindObjectsOfTypeAll<PauseView>()) if (v.canvas != null && v.canvas.enabled && v.gameObject.scene.IsValid()) HideContents(v.transform);
            _viewCanvas.enabled = true;
            _viewGroup.alpha = 1;
            _viewGroup.interactable = true;
            _viewGroup.blocksRaycasts = true;
            EventSystem.current?.SetSelectedGameObject(_firstRow);
            Plugin.L.LogInfo("VR menu opened");
        }

        public void Close(bool restoreSettings = true)
        {
            if (!_open) return;
            _open = false;
            _viewCanvas.enabled = false;
            _viewGroup.alpha = 0;
            _viewGroup.interactable = false;
            _viewGroup.blocksRaycasts = false;
            foreach (var child in _hidden) if (child != null) child.SetActive(true);
            _hidden.Clear();
            if (restoreSettings && _settings != null && _settings.canvas.enabled)
            {
                SyncInteractable();
                EventSystem.current?.SetSelectedGameObject(_entryButton);
            }
            Plugin.L.LogInfo("VR menu closed");
        }

        static readonly HashSet<IntPtr> OurRows = new HashSet<IntPtr>();

        [HarmonyPatch(typeof(ToggleAudioVolumeButton), nameof(ToggleAudioVolumeButton.OnSetValue))]
        public static class SetValuePatch
        {
            public static bool Prefix(ToggleAudioVolumeButton __instance) => !OurRows.Contains(__instance.Pointer);
        }

        [HarmonyPatch(typeof(ToggleAudioVolumeButton), nameof(ToggleAudioVolumeButton.Refresh))]
        public static class RefreshPatch
        {
            public static bool Prefix(ToggleAudioVolumeButton __instance) => !OurRows.Contains(__instance.Pointer);
        }

        static bool FlipOurs(ToggleRumbleEnabledButton row)
        {
            if (!OurToggles.TryGetValue(row.Pointer, out var t)) return true;
            t.Entry.Value = !t.Entry.Value;
            ShowToggle(t);
            return false;
        }

        [HarmonyPatch(typeof(ToggleRumbleEnabledButton), nameof(ToggleRumbleEnabledButton.Next))]
        public static class ToggleNextPatch { public static bool Prefix(ToggleRumbleEnabledButton __instance) => FlipOurs(__instance); }

        [HarmonyPatch(typeof(ToggleRumbleEnabledButton), nameof(ToggleRumbleEnabledButton.Prev))]
        public static class TogglePrevPatch { public static bool Prefix(ToggleRumbleEnabledButton __instance) => FlipOurs(__instance); }

        [HarmonyPatch(typeof(ToggleRumbleEnabledButton), nameof(ToggleRumbleEnabledButton.Toggle))]
        public static class ToggleFlipPatch { public static bool Prefix(ToggleRumbleEnabledButton __instance) => FlipOurs(__instance); }

        [HarmonyPatch(typeof(ToggleRumbleEnabledButton), nameof(ToggleRumbleEnabledButton.Refresh))]
        public static class ToggleRefreshPatch
        {
            public static bool Prefix(ToggleRumbleEnabledButton __instance)
            {
                if (!OurToggles.TryGetValue(__instance.Pointer, out var t)) return true;
                ShowToggle(t);
                return false;
            }
        }

        /// <summary>While the page is open the game still thinks Settings has focus. Its Back input arrives
        /// here, and closes the VR page rather than Settings.</summary>
        [HarmonyPatch(typeof(CustomView), nameof(CustomView.Back))]
        public static class BackPatch
        {
            public static bool Prefix(CustomView __instance, ref bool __result)
            {
                if (I == null || !I._open || __instance.TryCast<SettingsView>() == null) return true;
                I.Close();
                __result = false;  // False keeps Settings open. True would tell the game to leave it.
                return false;
            }
        }
    }
}
