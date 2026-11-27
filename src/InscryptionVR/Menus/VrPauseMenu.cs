using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;
using UiImage = UnityEngine.UI.Image;
using UiRawImage = UnityEngine.UI.RawImage;
using UiText = UnityEngine.UI.Text;

namespace InscryptionVR
{
    public sealed class VrSetting
    {
        public string Tab;
        public string Label;
        public Func<string> Value;
        public Action<int> Step;
        public Action Activate;
    }

    public sealed class VrPauseMenu : MonoBehaviour
    {
        private const float MirrorWidthMeters = 0.6f;
        private const float SideWidthMeters = 0.3f;
        private const float UnitMeters = 0.0005f;
        private const float SideAngle = 35f;
        private const float PokeHoverMeters = 0.08f;
        private const float PokeBehindMeters = 0.04f;
        private const float PokePressMeters = 0.008f;
        private const float PokeReleaseMeters = 0.025f;
        private const float LaserMeters = 1.5f;
        private const float HintUnits = 56f;
        private const float MarginUnits = 12f;
        private const float TitleUnits = 64f;
        private const float TabUnits = 60f;
        private const float RowUnits = 64f;
        private const float FooterUnits = 44f;
        private const int MirrorSurface = 1;
        private const int SideSurface = 2;

        private static readonly Color ButtonNormal = new Color(0.17f, 0.15f, 0.13f, 1f);
        private static readonly Color ButtonHover = new Color(0.4f, 0.34f, 0.24f, 1f);
        private static readonly Color ButtonDown = new Color(0.68f, 0.55f, 0.3f, 1f);
        private static readonly Color TabSelected = new Color(0.33f, 0.26f, 0.16f, 1f);
        private static readonly Color Ink = new Color(0.93f, 0.89f, 0.8f, 1f);
        private static readonly Color Dim = new Color(0.8f, 0.76f, 0.68f, 1f);

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private VrInput _input;
        private List<VrSetting> _settings;
        private float _distanceMeters;
        private bool _flipMirror;
        private bool _failed;

        private GameObject _root;
        private RectTransform _mirrorRect;
        private RectTransform _imageRect;
        private RectTransform _sideRect;
        private UiText _hint;
        private Material _uiMaterial;
        private Material _lineMaterial;
        private Font _font;
        private ScreenMirror _mirror;
        private int _screenWidth;
        private int _screenHeight;
        private readonly List<string> _tabs = new List<string>();
        private readonly List<RectTransform> _pages = new List<RectTransform>();
        private readonly List<SettingButton> _buttons = new List<SettingButton>();
        private readonly List<ValueLabel> _values = new List<ValueLabel>();
        private int _tab;

        private PanelHand _left;
        private PanelHand _right;
        private CursorDriver _driver;

        private bool _visible;
        private int _placedRevision = -1;

        private sealed class SettingButton
        {
            internal RectTransform Rect;
            internal UiImage Image;
            internal VrSetting Setting;
            internal int Direction;
            internal int Page = -1;
            internal int OpensTab = -1;
        }

        private sealed class ValueLabel
        {
            internal UiText Text;
            internal VrSetting Setting;
        }

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, VrInput input, List<VrSetting> settings, float distanceMeters, bool flipMirror)
        {
            _logger = logger;
            _renderer = renderer;
            _input = input;
            _left = new PanelHand(input.Left);
            _right = new PanelHand(input.Right);
            _driver = new CursorDriver(logger, "VR pause");
            _settings = settings ?? new List<VrSetting>();
            _distanceMeters = Mathf.Clamp(distanceMeters, 0.3f, 1.5f);
            _flipMirror = flipMirror;
            try
            {
                GameHooks.Require();
                BuildUi();
                StartCoroutine(CaptureFrames());
                _logger.LogInfo("VR pause menu ready: " + PauseButtonName() + " pauses and goes back; point and pull the trigger, or touch the screen, to select.");
            }
            catch (Exception exception) { Fail(exception); }
        }

        private string PauseButtonName() { return _input.LeftHanded ? "the right B button" : "the left Y (or menu) button"; }

        private void Update()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception) { Fail(exception); }
        }

        private void Tick()
        {
            Vector3 head = Vector3.zero;
            Quaternion headRotation = Quaternion.identity;
            float headScale = 1f;
            _input.Refresh();
            bool tracking = _renderer != null && _input.Active && VrScenes.InVrScene &&
                _renderer.TryGetHeadPose(out head, out headRotation, out headScale);
            bool input = tracking && _input.InputAvailable;
            float scale = _input.Scale;
            _left.Sample(input);
            _right.Sample(input);

            PauseMenu pause = PauseMenu.instance;
            if (input && _input.Off.MenuPressed) TogglePause(pause);

            bool show = tracking && pause != null && pause.Paused;
            if (!show)
            {
                Hide();
                return;
            }
            if (!_visible)
            {
                _visible = true;
                _root.SetActive(true);
                _left.RequireRelease = true;
                _right.RequireRelease = true;
                _placedRevision = -1;
                _hint.text = (_input.LeftHanded ? "RIGHT B" : "LEFT Y / MENU") + ": BACK / RESUME        TRIGGER OR TOUCH: SELECT";
            }
            ResizeMirror();
            if (_placedRevision != _renderer.SeatRevision)
            {
                Place(head, headRotation, scale);
                _placedRevision = _renderer.SeatRevision;
            }

            UpdateHand(_left, scale);
            UpdateHand(_right, scale);
            DriveGame(pause);
            UpdateSettings();
            DrawHand(_left, scale);
            DrawHand(_right, scale);
        }

        private void TogglePause(PauseMenu pause)
        {
            if (pause == null) return;
            VrHand off = _input.Off;
            if (!pause.Paused)
            {
                InteractionCursor main = Singleton<InteractionCursor>.Instance;
                if (PauseMenu.pausingDisabled || (main != null && !main.gameObject.activeSelf))
                {
                    off.Pulse(3500);
                    _logger.LogInfo("VR pause refused: the game has pausing disabled right now.");
                    return;
                }
                GameHooks.SetPaused(pause, true);
                off.Pulse(1200);
                _logger.LogInfo("Game paused from the " + off.Name + " controller.");
                return;
            }
            if (pause.Unpausing) return;
            MenuController menu = GameHooks.PauseMenuControllerOf(pause);
            if (menu != null && menu.TransitioningToScene) return;
            if (menu != null && menu.CardInSlot)
            {
                if (!menu.DoingCardTransition) menu.OnCardSelected(GameHooks.SlottedCard(menu));
                off.Pulse(800);
                return;
            }
            _driver.Release();
            pause.UnPause();
            off.Pulse(1200);
            _logger.LogInfo("Game resumed from the " + off.Name + " controller.");
        }

        private void Hide()
        {
            if (!_visible) return;
            _visible = false;
            _driver.Reset();
            if (_root != null) _root.SetActive(false);
            _left.Button = null;
            _right.Button = null;
        }

        private void ResizeMirror()
        {
            int width = Mathf.Max(1, Screen.width);
            int height = Mathf.Max(1, Screen.height);
            if (width == _screenWidth && height == _screenHeight) return;
            _screenWidth = width;
            _screenHeight = height;
            float canvasWidth = MirrorWidthMeters / UnitMeters;
            float imageWidth = canvasWidth - 2f * MarginUnits;
            float imageHeight = imageWidth * height / width;
            _mirrorRect.sizeDelta = new Vector2(canvasWidth, HintUnits + imageHeight + MarginUnits);
            _placedRevision = -1;
        }

        private void Place(Vector3 head, Quaternion headRotation, float scale)
        {
            Quaternion yaw = Quaternion.Euler(0f, headRotation.eulerAngles.y, 0f);
            Vector3 center = head + yaw * new Vector3(0f, -0.12f, _distanceMeters) * scale;
            Quaternion facing = Quaternion.LookRotation(center - head, Vector3.up);
            _mirrorRect.position = center;
            _mirrorRect.rotation = facing;
            _mirrorRect.localScale = Vector3.one * (UnitMeters * scale);
            Quaternion sideRotation = facing * Quaternion.Euler(0f, SideAngle, 0f);
            Vector3 edge = center + facing * Vector3.right * ((MirrorWidthMeters * 0.5f + 0.015f) * scale);
            _sideRect.position = edge + sideRotation * Vector3.right * (SideWidthMeters * 0.5f * scale);
            _sideRect.rotation = sideRotation;
            _sideRect.localScale = Vector3.one * (UnitMeters * scale);
        }

        private RectTransform SurfaceRect(int surface) { return surface == MirrorSurface ? _mirrorRect : _sideRect; }

        private void UpdateHand(PanelHand hand, float scale)
        {
            bool wasPoking = hand.Poking;
            hand.Poking = false;
            hand.Surface = 0;
            if (hand.Tracked)
            {
                Vector3 forward = hand.Input.AimDirection;
                Vector3 origin = hand.Input.AimOrigin;
                Vector3 tip = hand.Input.HasIndexTip ? hand.Input.IndexTip : origin + forward * (0.02f * scale);
                float best = float.MaxValue;
                for (int surface = MirrorSurface; surface <= SideSurface; surface++)
                {
                    RectTransform rect = SurfaceRect(surface);
                    Vector3 normal = rect.forward;
                    float depth = Vector3.Dot(rect.position - tip, normal);
                    if (depth < -PokeBehindMeters * scale || depth > PokeHoverMeters * scale) continue;
                    Vector3 projected = tip + normal * depth;
                    if (!PanelUi.Inside(rect, projected) || Mathf.Abs(depth) >= best) continue;
                    best = Mathf.Abs(depth);
                    hand.Surface = surface;
                    hand.Point = projected;
                    hand.PokeDepth = depth;
                }
                if (hand.Surface != 0)
                {
                    hand.Poking = true;
                    if (!wasPoking) { hand.PokeDown = false; hand.PokeArmed = false; }
                    if (hand.PokeDown) { if (hand.PokeDepth > PokeReleaseMeters * scale) hand.PokeDown = false; }
                    else if (hand.PokeArmed && hand.PokeDepth <= PokePressMeters * scale) hand.PokeDown = true;
                    hand.PokeArmed = !hand.PokeDown && hand.PokeDepth > PokePressMeters * scale;
                }
                else
                {
                    hand.PokeDown = false;
                    hand.PokeArmed = false;
                    float nearest = LaserMeters * scale;
                    for (int surface = MirrorSurface; surface <= SideSurface; surface++)
                    {
                        Vector3 hit;
                        float distance;
                        if (!PanelUi.RayHit(SurfaceRect(surface), origin, forward, nearest, out hit, out distance)) continue;
                        nearest = distance;
                        hand.Surface = surface;
                        hand.Point = hit;
                    }
                }
                if (hand.Surface == MirrorSurface) hand.Uv = PanelUi.Uv(_imageRect, hand.Point);
            }
            hand.Latch(hand.Tracked && (hand.Poking ? hand.PokeDown : hand.Trigger));
            if (hand.Pressed && hand.Poking) hand.Pulse(1500);
        }

        private void DriveGame(PauseMenu pause)
        {
            PanelHand owner = _driver.ChooseOwner(_left, _right, MirrorSurface);
            bool pressedEmpty;
            MainInputInteractable started = _driver.Drive(GameHooks.PauseCursor(pause), owner, owner != null && owner.Surface == MirrorSurface, out pressedEmpty);
            if (started != null && started == GameHooks.PauseCloseButton(pause)) pause.UnPause();
            if (pressedEmpty)
            {
                MenuController menu = GameHooks.PauseMenuControllerOf(pause);
                if (menu != null && menu.SelectedCard != null && !menu.CardInSlot) menu.OnCardSelected(null);
            }
        }

        private void UpdateSettings()
        {
            SettingButton leftHover = _left.Surface == SideSurface ? ButtonAt(_left.Point) : null;
            SettingButton rightHover = _right.Surface == SideSurface ? ButtonAt(_right.Point) : null;
            PressSetting(_left, leftHover);
            PressSetting(_right, rightHover);
            foreach (SettingButton button in _buttons)
            {
                if (!Shown(button)) continue;
                bool held = (_left.Button == button && _left.Down) || (_right.Button == button && _right.Down);
                bool hovered = button == leftHover || button == rightHover;
                Color color = held ? ButtonDown : hovered ? ButtonHover : button.OpensTab == _tab ? TabSelected : ButtonNormal;
                if (button.Image.color != color) button.Image.color = color;
            }
            foreach (ValueLabel label in _values)
            {
                if (!label.Text.gameObject.activeInHierarchy) continue;
                string value = label.Setting.Value();
                if (label.Text.text != value) label.Text.text = value;
            }
        }

        private bool Shown(SettingButton button) { return button.Page < 0 || button.Page == _tab; }

        private void PressSetting(PanelHand hand, SettingButton hovered)
        {
            if (hand.Pressed && hovered != null)
            {
                hand.Button = hovered;
                hand.RepeatAt = Time.unscaledTime + 0.45f;
                Apply(hovered, hand, 900);
            }
            else if (hand.Down && hand.Button != null && hand.Button == hovered && hovered.Direction != 0 && Time.unscaledTime >= hand.RepeatAt)
            {
                hand.RepeatAt = Time.unscaledTime + 0.12f;
                Apply(hovered, hand, 400);
            }
            if (!hand.Down) hand.Button = null;
        }

        private void Apply(SettingButton button, PanelHand hand, int pulse)
        {
            if (button.OpensTab >= 0) ShowTab(button.OpensTab);
            else if (button.Direction != 0) button.Setting.Step(button.Direction);
            else if (button.Setting.Activate != null) button.Setting.Activate();
            hand.Pulse(pulse);
            try { MenuController.PlayMenuCrunchSound(false); }
            catch (Exception) { }
        }

        private void ShowTab(int tab)
        {
            _tab = Mathf.Clamp(tab, 0, Mathf.Max(0, _pages.Count - 1));
            for (int i = 0; i < _pages.Count; i++) _pages[i].gameObject.SetActive(i == _tab);
        }

        private SettingButton ButtonAt(Vector3 world)
        {
            foreach (SettingButton button in _buttons)
                if (Shown(button) && PanelUi.Inside(button.Rect, world)) return button;
            return null;
        }

        private void DrawHand(PanelHand hand, float scale)
        {
            bool laser = hand.Tracked && !hand.Poking;
            Vector3 origin = hand.Input.AimOrigin;
            Vector3 end = hand.Surface != 0 ? hand.Point : origin + hand.Input.AimDirection * (LaserMeters * 0.5f * scale);
            Color color = hand.Surface == 0 ? new Color(0.3f, 0.75f, 1f, 0.45f) : hand.Down ? PanelUi.LaserDown : PanelUi.LaserHover;
            PanelUi.DrawLaser(hand.Line, laser, origin, end, color, 0.6f, 0.002f * scale, 0.001f * scale);
            PanelUi.PlaceDot(hand.Dots[0], _mirrorRect, hand.Tracked && hand.Surface == MirrorSurface, hand.Point);
            PanelUi.PlaceDot(hand.Dots[1], _sideRect, hand.Tracked && hand.Surface == SideSurface, hand.Point);
        }

        private IEnumerator CaptureFrames()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                yield return endOfFrame;
                if (!_visible || _failed) continue;
                try { _mirror.Capture(); }
                catch (Exception exception) { Fail(exception); }
            }
        }

        private void BuildUi()
        {
            _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _uiMaterial = PanelUi.UiMaterial("InscryptionVR Pause Menu Material", true);
            _lineMaterial = PanelUi.LineMaterial();

            _root = new GameObject("InscryptionVR Pause Menu");
            _root.layer = VrDialogue.SubtitleLayer;
            _root.transform.SetParent(transform, false);

            _mirrorRect = PanelUi.Canvas(_root.transform, "InscryptionVR Pause Mirror", 31000);
            PanelUi.Stretch(PanelUi.Image(_mirrorRect, "Frame", new Color(0.03f, 0.03f, 0.03f, 0.96f), _uiMaterial).rectTransform, 0f);
            _hint = Text(_mirrorRect, "Hint", "", 26, TextAnchor.MiddleCenter);
            _hint.color = Dim;
            _hint.rectTransform.anchorMin = new Vector2(0f, 1f);
            _hint.rectTransform.anchorMax = new Vector2(1f, 1f);
            _hint.rectTransform.pivot = new Vector2(0.5f, 1f);
            _hint.rectTransform.anchoredPosition = Vector2.zero;
            _hint.rectTransform.sizeDelta = new Vector2(-2f * MarginUnits, HintUnits);
            UiRawImage image = PanelUi.RawImage(_mirrorRect, "Screen", _uiMaterial, _flipMirror);
            _imageRect = image.rectTransform;
            PanelUi.Stretch(_imageRect, MarginUnits, HintUnits, MarginUnits, MarginUnits);
            _mirror = new ScreenMirror("Pause Mirror", image);
            ResizeMirror();

            BuildSidePanel();
            BuildHand(_left);
            BuildHand(_right);
            _root.SetActive(false);
        }

        private void BuildSidePanel()
        {
            foreach (VrSetting setting in _settings)
            {
                string tab = string.IsNullOrEmpty(setting.Tab) ? "General" : setting.Tab;
                if (!_tabs.Contains(tab)) _tabs.Add(tab);
            }
            if (_tabs.Count == 0) _tabs.Add("General");
            int rows = 1;
            foreach (string tab in _tabs)
            {
                int count = 0;
                foreach (VrSetting setting in _settings) if (TabOf(setting) == tab) count++;
                rows = Mathf.Max(rows, count);
            }

            float width = SideWidthMeters / UnitMeters;
            float rowsTop = TitleUnits + TabUnits + 8f;
            _sideRect = PanelUi.Canvas(_root.transform, "InscryptionVR VR Settings", 31000);
            _sideRect.sizeDelta = new Vector2(width, rowsTop + rows * RowUnits + FooterUnits);
            PanelUi.Stretch(PanelUi.Image(_sideRect, "Background", new Color(0.03f, 0.03f, 0.03f, 0.96f), _uiMaterial).rectTransform, 0f);
            UiText title = Text(_sideRect, "Title", "VR SETTINGS", 34, TextAnchor.MiddleCenter);
            PanelUi.Place(title.rectTransform, 0f, 6f, width, TitleUnits - 12f);

            float tabWidth = (width - 24f - 6f * (_tabs.Count - 1)) / _tabs.Count;
            for (int t = 0; t < _tabs.Count; t++)
            {
                SettingButton tabButton = AddButton(_sideRect, null, 0, _tabs[t].ToUpperInvariant(), 12f + t * (tabWidth + 6f), TitleUnits, tabWidth, TabUnits - 8f, 24);
                tabButton.OpensTab = t;
                RectTransform page = PanelUi.Rect(_sideRect, "Page " + _tabs[t]);
                PanelUi.Stretch(page, 0f);
                _pages.Add(page);
                int row = 0;
                foreach (VrSetting setting in _settings)
                {
                    if (TabOf(setting) != _tabs[t]) continue;
                    BuildRow(page, t, setting, rowsTop + row * RowUnits, width);
                    row++;
                }
            }

            UiText footer = Text(_sideRect, "Footer", "Press a number to reset it.", 22, TextAnchor.MiddleCenter);
            footer.color = Dim;
            PanelUi.Place(footer.rectTransform, 0f, rowsTop + rows * RowUnits + 4f, width, FooterUnits - 12f);
            ShowTab(_tab);
        }

        private static string TabOf(VrSetting setting) { return string.IsNullOrEmpty(setting.Tab) ? "General" : setting.Tab; }

        private void BuildRow(RectTransform page, int pageIndex, VrSetting setting, float y, float width)
        {
            if (setting.Step != null)
            {
                PanelUi.Place(Text(page, setting.Label, setting.Label, 26, TextAnchor.MiddleLeft).rectTransform, 24f, y, 290f, 56f);
                AddButton(page, setting, -1, "−", 320f, y + 4f, 64f, 48f, 36).Page = pageIndex;
                UiText value;
                if (setting.Activate != null)
                {
                    SettingButton reset = AddButton(page, setting, 0, "", 388f, y + 4f, 120f, 48f, 26);
                    reset.Page = pageIndex;
                    value = reset.Rect.GetComponentInChildren<UiText>();
                }
                else
                {
                    value = Text(page, setting.Label + " Value", "", 26, TextAnchor.MiddleCenter);
                    PanelUi.Place(value.rectTransform, 388f, y, 120f, 56f);
                }
                _values.Add(new ValueLabel { Text = value, Setting = setting });
                AddButton(page, setting, 1, "+", 512f, y + 4f, 64f, 48f, 36).Page = pageIndex;
            }
            else if (setting.Value != null)
            {
                PanelUi.Place(Text(page, setting.Label, setting.Label, 26, TextAnchor.MiddleLeft).rectTransform, 24f, y, 290f, 56f);
                SettingButton button = AddButton(page, setting, 0, "", 320f, y + 4f, 256f, 48f, 26);
                button.Page = pageIndex;
                _values.Add(new ValueLabel { Text = button.Rect.GetComponentInChildren<UiText>(), Setting = setting });
            }
            else AddButton(page, setting, 0, setting.Label, 24f, y + 4f, width - 48f, 48f, 26).Page = pageIndex;
        }

        private SettingButton AddButton(RectTransform parent, VrSetting setting, int direction, string caption, float x, float y, float width, float height, int textSize)
        {
            string name = (setting != null ? setting.Label : caption) + " Button " + direction;
            UiImage image = PanelUi.Image(parent, name, ButtonNormal, _uiMaterial);
            PanelUi.Place(image.rectTransform, x, y, width, height);
            UiText text = Text(image.rectTransform, "Caption", caption, textSize, TextAnchor.MiddleCenter);
            PanelUi.Stretch(text.rectTransform, 0f);
            var button = new SettingButton { Rect = image.rectTransform, Image = image, Setting = setting, Direction = direction };
            _buttons.Add(button);
            return button;
        }

        private UiText Text(Transform parent, string name, string value, int size, TextAnchor alignment)
        {
            return PanelUi.Text(parent, name, value, size, alignment, _font, _uiMaterial, Ink);
        }

        private void BuildHand(PanelHand hand)
        {
            hand.Line = PanelUi.Laser(_root.transform, "InscryptionVR Pause Laser " + hand.Input.Name, _lineMaterial, 31001);
            hand.Dots = new[] { PanelUi.Dot(_mirrorRect, hand.Input.Name, 14f, _uiMaterial), PanelUi.Dot(_sideRect, hand.Input.Name, 14f, _uiMaterial) };
        }

        private void Fail(Exception exception)
        {
            _failed = true;
            try { if (_driver != null) _driver.Reset(); }
            catch (Exception cleanup) { if (_logger != null) _logger.LogWarning("VR pause cursor restore failed: " + cleanup.Message); }
            if (_root != null) _root.SetActive(false);
            _visible = false;
            if (_logger != null) _logger.LogError("VR pause menu disabled; the desktop pause menu still works: " + exception);
        }

        private void OnDisable()
        {
            if (_logger != null && !_failed) Hide();
        }

        private void OnDestroy()
        {
            if (_driver != null) _driver.Reset();
            if (_root != null) Destroy(_root);
            if (_uiMaterial != null) Destroy(_uiMaterial);
            if (_lineMaterial != null) Destroy(_lineMaterial);
            if (_mirror != null) _mirror.Dispose();
        }
    }
}
