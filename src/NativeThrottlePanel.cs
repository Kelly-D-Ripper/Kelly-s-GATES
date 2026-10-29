using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UrsaMinorThrottle
{
    // A private screen-space canvas, using only presentation assets from the stock MFD.
    // It never clones stock controllers, listeners, or mutates the native display.
    internal sealed class NativeThrottlePanel : IDisposable
    {
        private const float Width = 700f, Height = 670f;
        private readonly Action next, close;
        private readonly Action<int, bool> setOption;
        private readonly Action<float> setReversePower;
        private readonly Action bindAirbrake, clearAirbrake;
        private readonly Action<bool, bool> setCalibrationMode;
        private GameObject root, ownedEventSystem, calibrationPage, optionsPage;
        private RectTransform panel;
        private Canvas canvas;
        private TMP_Text titleSource, bodySource;
        private Image boxSource;
        private readonly List<Image> frameSources = new List<Image>();
        private RectTransform stockPanel;
        private Color green = Color.green, blue = new Color(0f, .5f, 1f), muted = Color.gray;
        private Color border = Color.gray, dark = new Color(0f, 0f, 0f, .9f);
        private TMP_Text status, left, right, instruction, progress, aircraft, diagnostics, hints;
        private readonly TMP_Text[] optionLabels = new TMP_Text[5];
        private readonly Button[] optionButtons = new Button[5];
        private Button calibrationTab, optionsTab;
        private Slider reverseSlider;
        private TMP_Text reverseLabel;
        private TMP_Text bindingLabel, bindingButtonLabel;
        private TMP_Text leverModeLabel, gateModeLabel;
        private bool singleMode, gateMode;
        private bool options, updating;
        internal bool IsOptions => options;
        internal bool Alive => root != null;
        internal bool HasStockFont => titleSource != null && titleSource.font != null && bodySource != null && bodySource.font != null;
        internal bool HasStockPanel => frameSources.Count > 0;
        internal bool HasStockButtons => boxSource != null;

        internal NativeThrottlePanel(Action next, Action close, Action<int, bool> setOption, Action<float> setReversePower,
            Action bindAirbrake, Action clearAirbrake, Action<bool, bool> setCalibrationMode)
        { this.next = next; this.close = close; this.setOption = setOption; this.setReversePower = setReversePower;
          this.bindAirbrake = bindAirbrake; this.clearAirbrake = clearAirbrake; this.setCalibrationMode = setCalibrationMode; }

        internal void Open()
        {
            Dispose();
            Canvas.ForceUpdateCanvases();
            FindStockAssets();
            root = new GameObject("Ursa throttle MFD overlay", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            panel = Rect("Throttle panel", root.transform);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0f, 1f);
            panel.sizeDelta = new Vector2(Width, Height);
            BuildPanelBackground();
            Build();
            Layout();
            EnsureEventSystem();
        }

        private void FindStockAssets()
        {
            titleSource = bodySource = null; boxSource = null; stockPanel = null;
            frameSources.Clear();
            var gameplay = SceneSingleton<GameplayUI>.i;
            if (gameplay == null) return;
            var mfd = gameplay.GetComponentInChildren<VirtualMFD>(true);
            if (mfd == null) return;
            var field = AccessTools.Field(typeof(VirtualMFD), "leftScreens");
            var screens = field?.GetValue(mfd) as List<MFDScreen>;
            if (screens == null || screens.Count < 2 || screens[1] == null || screens[1].displayPanel == null) return;
            stockPanel = screens[1].displayPanel.transform as RectTransform;
            if (stockPanel == null || stockPanel.rect.width < 1f || stockPanel.rect.height < 1f) { stockPanel = null; return; }
            var texts = stockPanel.GetComponentsInChildren<TMP_Text>(true);
            foreach (var text in texts)
            {
                if (text == null || text.font == null || string.IsNullOrWhiteSpace(text.text)) continue;
                if (titleSource == null || text.fontSize > titleSource.fontSize) titleSource = text;
                if (text.text.Trim().Equals("MAP OPTIONS", StringComparison.OrdinalIgnoreCase)) titleSource = text;
                if (text.text.Trim().Equals("MARKERS", StringComparison.OrdinalIgnoreCase)) bodySource = text;
            }
            if (bodySource == null) foreach (var text in texts)
                if (text != null && text.font != null && text != titleSource && !string.IsNullOrWhiteSpace(text.text)) { bodySource = text; break; }
            if (bodySource == null) bodySource = titleSource;
            foreach (var source in stockPanel.GetComponentsInChildren<Image>(true))
            {
                if (source == null || !source.enabled) continue;
                var r = source.rectTransform;
                float width = r.rect.width * Mathf.Abs(r.lossyScale.x / Mathf.Max(.0001f, Mathf.Abs(stockPanel.lossyScale.x)));
                float height = r.rect.height * Mathf.Abs(r.lossyScale.y / Mathf.Max(.0001f, Mathf.Abs(stockPanel.lossyScale.y)));
                if (width >= stockPanel.rect.width * .85f && height >= stockPanel.rect.height * .85f) frameSources.Add(source);
            }
            foreach (var selectable in stockPanel.GetComponentsInChildren<Selectable>(true))
                if (selectable.targetGraphic is Image image && image.sprite != null) { boxSource = image; break; }
            if (boxSource == null) foreach (var image in stockPanel.GetComponentsInChildren<Image>(true))
                if (image.sprite != null && image.type == Image.Type.Sliced && !image.fillCenter) { boxSource = image; break; }
            var textColors = new List<Color>(); var imageColors = new List<Color>();
            // Only sample the stock BDF/MAP/HUD screens, not other mods' panels.
            for (int index = 0; index < Math.Min(3, screens.Count); index++)
            {
                var screen = screens[index];
                if (screen == null || screen.displayPanel == null) continue;
                foreach (var graphic in screen.displayPanel.GetComponentsInChildren<Graphic>(true))
                {
                    if (!graphic.enabled || graphic.color.a <= 0f) continue;
                    if (graphic is TMP_Text) textColors.Add(graphic.color);
                    else if (graphic is Image) imageColors.Add(graphic.color);
                }
            }
            green = Pick(textColors, Color.green); blue = Pick(textColors, new Color(0f, .4f, 1f));
            muted = Pick(textColors, Color.gray); border = Pick(imageColors, Color.gray);
            dark = Pick(imageColors, new Color(0f, 0f, 0f, .6f));
        }

        private static Color Pick(List<Color> colors, Color wanted)
        {
            Color best = wanted; float score = float.PositiveInfinity;
            foreach (var c in colors)
            {
                float d = (c.r-wanted.r)*(c.r-wanted.r)+(c.g-wanted.g)*(c.g-wanted.g)+
                    (c.b-wanted.b)*(c.b-wanted.b)+(c.a-wanted.a)*(c.a-wanted.a);
                if (d < score) { score = d; best = c; }
            }
            return best;
        }

        private void BuildPanelBackground()
        {
            var hit = panel.gameObject.AddComponent<Image>();
            hit.color = Color.clear; hit.raycastTarget = true;
            foreach (var source in frameSources)
            {
                if (source == null) continue;
                var image = Rect("Native panel artwork", panel).gameObject.AddComponent<Image>();
                CopyImage(source, image);
                var r = source.rectTransform;
                var relative = stockPanel.InverseTransformPoint(r.TransformPoint(r.rect.center));
                var centre = new Vector2((relative.x - stockPanel.rect.xMin) / stockPanel.rect.width,
                    (relative.y - stockPanel.rect.yMin) / stockPanel.rect.height);
                var half = new Vector2(r.rect.width * Mathf.Abs(r.lossyScale.x / stockPanel.lossyScale.x) / stockPanel.rect.width,
                    r.rect.height * Mathf.Abs(r.lossyScale.y / stockPanel.lossyScale.y) / stockPanel.rect.height) * .5f;
                image.rectTransform.anchorMin = centre - half; image.rectTransform.anchorMax = centre + half;
                image.rectTransform.offsetMin = image.rectTransform.offsetMax = Vector2.zero;
            }
            if (frameSources.Count == 0)
            {
                var fill = Rect("Panel background", panel).gameObject.AddComponent<Image>();
                fill.color = dark; fill.raycastTarget = false; Stretch(fill.rectTransform);
                var frame = Rect("Panel frame", panel).gameObject.AddComponent<Image>();
                Box(frame); frame.color = border; frame.raycastTarget = false; Stretch(frame.rectTransform);
            }
        }

        private void Build()
        {
            Text(panel, "KELLY'S GATES / THROTTLE SETUP", 18, 12, 664, 35, 27, green, TextAlignmentOptions.Center);
            status = Text(panel, "", 18, 48, 664, 24, 14, muted, TextAlignmentOptions.Center);
            calibrationTab = Button(panel, "CALIBRATION", 18, 82, 324, 34, () => ShowOptions(false));
            optionsTab = Button(panel, "OPTIONS", 350, 82, 332, 34, () => ShowOptions(true));
            calibrationPage = Rect("Calibration", panel).gameObject;
            Stretch(calibrationPage.GetComponent<RectTransform>());
            optionsPage = Rect("Options", panel).gameObject;
            Stretch(optionsPage.GetComponent<RectTransform>());
            leverModeLabel = Button(calibrationPage.transform, "", 18, 128, 324, 34, () => setCalibrationMode(!singleMode, gateMode)).GetComponentInChildren<TMP_Text>();
            gateModeLabel = Button(calibrationPage.transform, "", 350, 128, 332, 34, () => setCalibrationMode(singleMode, !gateMode)).GetComponentInChildren<TMP_Text>();
            Text(calibrationPage.transform, "LEVER DETECTION", 18, 169, 664, 22, 16, green);
            Frame(calibrationPage.transform, 18, 195, 324, 68);
            Frame(calibrationPage.transform, 350, 195, 332, 68);
            left = Text(calibrationPage.transform, "", 28, 201, 304, 56, 15, green);
            right = Text(calibrationPage.transform, "", 360, 201, 312, 56, 15, green);
            progress = Text(calibrationPage.transform, "", 18, 272, 664, 28, 14, green, TextAlignmentOptions.Center);
            Frame(calibrationPage.transform, 18, 309, 664, 84);
            instruction = Text(calibrationPage.transform, "", 28, 317, 644, 68, 16, green, TextAlignmentOptions.TopLeft);
            hints = Text(calibrationPage.transform, "", 18, 402, 664, 28, 13, muted);
            aircraft = Text(calibrationPage.transform, "", 18, 438, 664, 30, 14, green);
            diagnostics = Text(calibrationPage.transform, "", 18, 472, 664, 49, 13, green);
            Button(calibrationPage.transform, "NEXT / CAPTURE", 18, 556, 220, 38, next);
            Button(calibrationPage.transform, "CLOSE", 248, 556, 150, 38, close);
            Text(calibrationPage.transform, "F9 CLOSES   •   F10 CAPTURES", 18, 612, 664, 25, 12, muted);

            string[] names = { "THROTTLE COMPATIBILITY", "SPLIT THROTTLES", "AIRBRAKE AT ZERO", "HOLD AIRBRAKE BUTTON", "NATIVE REVERSE" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i; float y = 132 + i * 54;
                Text(optionsPage.transform, names[i], 26, y, 478, 36, 16, green);
                optionButtons[i] = Button(optionsPage.transform, "", 534, y, 148, 36, () => { if (!updating) setOption(index, optionValues[index] == false); });
                optionLabels[i] = optionButtons[i].GetComponentInChildren<TMP_Text>();
            }
            Text(optionsPage.transform, "MAXIMUM REVERSE POWER", 26, 423, 476, 30, 16, green);
            reverseLabel = Text(optionsPage.transform, "", 534, 423, 148, 30, 16, blue, TextAlignmentOptions.Center);
            reverseSlider = Slider(optionsPage.transform, 26, 464, 656, 26);
            reverseSlider.minValue = .05f; reverseSlider.maxValue = .90f;
            reverseSlider.onValueChanged.AddListener(v => { if (!updating) setReversePower(v); });
            Text(optionsPage.transform, "AIRBRAKE BUTTON", 26, 496, 276, 22, 15, green);
            var bind = Button(optionsPage.transform, "BIND", 18, 525, 150, 31, bindAirbrake);
            bindingButtonLabel = bind.GetComponentInChildren<TMP_Text>();
            Button(optionsPage.transform, "USE GAME BINDING", 176, 525, 166, 31, clearAirbrake);
            bindingLabel = Text(optionsPage.transform, "", 350, 496, 332, 60, 13, blue, TextAlignmentOptions.MidlineLeft);
            Button(optionsPage.transform, "CALIBRATION", 18, 556, 220, 38, () => ShowOptions(false));
            Button(optionsPage.transform, "CLOSE", 248, 556, 150, 38, close);
            Text(optionsPage.transform, "SETTINGS ARE SAVED AUTOMATICALLY", 18, 612, 664, 25, 12, muted);
            ShowOptions(false);
        }

        private readonly bool[] optionValues = new bool[5];
        internal void Refresh(bool calibrated, bool enabled, string leftInfo, string rightInfo, string message,
            CalibrationPhase phase, string shortcut, string aircraftInfo, string detail, bool[] settings, float reversePower,
            bool capturingButton, string buttonDescription, bool singleLever, bool hasReverseGate)
        {
            if (root == null) return;
            Layout(); EnsureEventSystem();
            singleMode = singleLever; gateMode = hasReverseGate;
            leverModeLabel.text = singleLever ? "LEVERS: SINGLE" : "LEVERS: DUAL";
            gateModeLabel.text = hasReverseGate ? "BELOW IDLE: ANALOG GATE" : "BELOW IDLE: NONE";
            status.text = !enabled ? "PACK DISABLED" : calibrated ? "CALIBRATION SAVED" : "CALIBRATION REQUIRED";
            left.text = "LEFT LEVER\n" + leftInfo; right.text = "RIGHT LEVER\n" + rightInfo;
            instruction.text = message; progress.text = PhaseText(phase); hints.text = shortcut;
            aircraft.text = aircraftInfo; diagnostics.text = detail;
            bindingButtonLabel.text = capturingButton ? "CANCEL" : "BIND";
            bindingLabel.text = buttonDescription;
            updating = true;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    optionValues[i] = settings[i];
                    optionLabels[i].text = settings[i] ? "ON" : "OFF";
                    optionLabels[i].color = settings[i] ? green : muted;
                }
                reverseSlider.value = reversePower;
                reverseLabel.text = (reversePower * 100f).ToString("F0") + "%";
            }
            finally { updating = false; }
        }

        private static string PhaseText(CalibrationPhase phase)
        {
            string[] names = { "LEFT", "RIGHT", "OFF", "IDLE", "MIL", "MAX" };
            int active = phase == CalibrationPhase.Ready || phase == CalibrationPhase.DetectingLeft || phase == CalibrationPhase.Invalid ? 0 :
                phase == CalibrationPhase.ReadyForRight || phase == CalibrationPhase.DetectingRight ? 1 :
                phase == CalibrationPhase.Off ? 2 : phase == CalibrationPhase.Idle ? 3 : phase == CalibrationPhase.Mil ? 4 : phase == CalibrationPhase.Maximum ? 5 : 6;
            string text = "";
            for (int i = 0; i < names.Length; i++) text += (i == 0 ? "" : "     ") + (i == active ? "> " : "") + names[i];
            return text;
        }

        private void ShowOptions(bool show)
        {
            options = show;
            calibrationPage.SetActive(!show); optionsPage.SetActive(show);
            SetTab(calibrationTab, !show); SetTab(optionsTab, show);
        }
        private void SetTab(Button button, bool active)
        {
            var colors = button.colors; colors.normalColor = active ? green : border; button.colors = colors;
        }

        private void Layout()
        {
            if (panel == null) return;
            float scale = Mathf.Min(1f, Mathf.Min(Mathf.Max(1f, Screen.width - 36f) / Width, Mathf.Max(1f, Screen.height - 36f) / Height));
            panel.localScale = Vector3.one * scale; panel.anchoredPosition = new Vector2(18f, -18f);
        }
        private void EnsureEventSystem()
        {
            var current = EventSystem.current;
            if (current != null && current.isActiveAndEnabled && current.currentInputModule != null && current.currentInputModule.isActiveAndEnabled)
            { if (ownedEventSystem != null && current.gameObject != ownedEventSystem) ownedEventSystem.SetActive(false); return; }
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<EventSystem>())
            {
                if (candidate == null || candidate.gameObject == ownedEventSystem || !candidate.isActiveAndEnabled) continue;
                foreach (var module in candidate.GetComponents<BaseInputModule>())
                    if (module.isActiveAndEnabled) { EventSystem.current = candidate; if (ownedEventSystem != null) ownedEventSystem.SetActive(false); return; }
            }
            if (ownedEventSystem == null) ownedEventSystem = new GameObject("Ursa throttle event system", typeof(EventSystem), typeof(StandaloneInputModule));
            else ownedEventSystem.SetActive(true);
            EventSystem.current = ownedEventSystem.GetComponent<EventSystem>();
        }

        private TMP_Text Text(Transform parent, string value, float x, float y, float w, float h, float size, Color color,
            TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft)
        {
            var rect = Rect("Label", parent); Place(rect, x, y, w, h);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            var source = size >= 25f ? titleSource : bodySource;
            if (source != null && source.font != null)
            { text.font = source.font; text.fontSharedMaterial = source.fontSharedMaterial; text.fontStyle = source.fontStyle; text.fontWeight = source.fontWeight; text.characterSpacing = source.characterSpacing; }
            text.fontSize = size; text.text = value; text.color = color; text.alignment = alignment;
            text.enableWordWrapping = true; text.overflowMode = TextOverflowModes.Ellipsis; text.richText = false; text.raycastTarget = false;
            return text;
        }
        private Button Button(Transform parent, string label, float x, float y, float w, float h, Action action)
        {
            var image = Rect(label + " button", parent).gameObject.AddComponent<Image>();
            Place(image.rectTransform, x, y, w, h); Box(image); image.color = Color.white; image.raycastTarget = true;
            var button = image.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var colors = button.colors; colors.normalColor = border; colors.highlightedColor = green;
            colors.selectedColor = green; colors.pressedColor = green; colors.disabledColor = muted; button.colors = colors;
            Text(image.transform, label, 4, 2, w - 8, h - 4, 14, green, TextAlignmentOptions.Center);
            button.onClick.AddListener(() => action());
            return button;
        }
        private void Frame(Transform parent, float x, float y, float w, float h)
        {
            var image = Rect("Border", parent).gameObject.AddComponent<Image>(); Place(image.rectTransform, x, y, w, h);
            Box(image); image.color = border; image.raycastTarget = false;
        }
        private Slider Slider(Transform parent, float x, float y, float w, float h)
        {
            var rect = Rect("Reverse limit", parent); Place(rect, x, y, w, h);
            var hit = rect.gameObject.AddComponent<Image>(); hit.color = Color.clear; hit.raycastTarget = true;
            var slider = rect.gameObject.AddComponent<Slider>();
            var track = Rect("Track", rect).gameObject.AddComponent<Image>();
            Place(track.rectTransform, 8, h / 2 - 2, w - 16, 4); track.color = border; track.raycastTarget = false;
            var area = Rect("Handle area", rect); area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(8, 0); area.offsetMax = new Vector2(-8, 0);
            var handle = Rect("Handle", area).gameObject.AddComponent<Image>(); handle.color = green;
            handle.rectTransform.sizeDelta = new Vector2(18, h); slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle; slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            return slider;
        }
        private void Box(Image image)
        {
            if (boxSource != null) CopyImage(boxSource, image);
            else
            {
                // Menus without a spawned MFD still need a usable, rounded fallback.
                try { image.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd"); }
                catch (Exception) { image.sprite = null; }
            }
            image.type = Image.Type.Sliced; image.fillCenter = false;
        }
        private static void CopyImage(Image from, Image to)
        {
            to.sprite = from.sprite; to.type = from.type; to.fillCenter = from.fillCenter;
            to.pixelsPerUnitMultiplier = from.pixelsPerUnitMultiplier; to.material = from.material;
            to.color = from.color; to.raycastTarget = false;
        }
        private static RectTransform Rect(string name, Transform parent)
        { var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); r.SetParent(parent, false); return r; }
        private static void Place(RectTransform r, float x, float y, float w, float h)
        { r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1); r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h); }
        private static void Stretch(RectTransform r)
        { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }

        public void Dispose()
        {
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null && root != null &&
                EventSystem.current.currentSelectedGameObject.transform.IsChildOf(root.transform))
                EventSystem.current.SetSelectedGameObject(null);
            if (root != null) { root.SetActive(false); UnityEngine.Object.Destroy(root); root = null; }
            if (ownedEventSystem != null) { ownedEventSystem.SetActive(false); UnityEngine.Object.Destroy(ownedEventSystem); ownedEventSystem = null; }
            frameSources.Clear(); stockPanel = null; titleSource = bodySource = null; boxSource = null;
        }
    }
}
