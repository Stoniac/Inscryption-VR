using System;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;
using UiImage = UnityEngine.UI.Image;
using UiRawImage = UnityEngine.UI.RawImage;

namespace InscryptionVR
{
    public sealed class MenuScreen : MonoBehaviour
    {
        private const float UnitMeters = 0.001f;
        private const float BezelUnits = 24f;
        private const float DropMeters = 0.05f;
        private const int ScreenSurface = 1;
        private static readonly Vector3 TheaterPosition = new Vector3(0f, -2000f, 0f);

        private ManualLogSource _logger;
        private VrInput _input;
        private bool _flip;
        private float _distanceMeters;
        private float _widthMeters;
        private bool _failed;
        private bool _active;

        private GameObject _root;
        private Camera _camera;
        private RectTransform _canvas;
        private RectTransform _imageRect;
        private Material _uiMaterial;
        private Material _lineMaterial;
        private ScreenMirror _mirror;
        private int _screenWidth;
        private int _screenHeight;
        private float _layoutScale;

        private PanelHand _left;
        private PanelHand _right;
        private CursorDriver _driver;

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, VrInput input, bool flip, float distanceMeters, float widthMeters)
        {
            _logger = logger;
            _input = input;
            _flip = flip;
            _distanceMeters = Mathf.Clamp(distanceMeters, 1f, 5f);
            _widthMeters = Mathf.Clamp(widthMeters, 0.8f, 6f);
            _left = new PanelHand(input.Left);
            _right = new PanelHand(input.Right);
            _driver = new CursorDriver(logger, "Menu screen");
            try
            {
                GameHooks.Require();
                Build();
                _logger.LogInfo("Menu screen ready: menus show on a floating screen in VR; point a controller at it and pull the trigger to click.");
            }
            catch (Exception exception) { Fail(exception); }
        }

        internal bool Available { get { return !_failed && _root != null; } }

        internal Vector3 Seat { get { return TheaterPosition; } }

        internal void SetDistance(float meters)
        {
            _distanceMeters = Mathf.Clamp(meters, 1f, 5f);
            _layoutScale = 0f;
        }

        internal void SetWidth(float meters)
        {
            _widthMeters = Mathf.Clamp(meters, 0.8f, 6f);
            _layoutScale = 0f;
        }

        internal Camera Activate()
        {
            if (!Available) return null;
            if (!_active)
            {
                _active = true;
                _root.SetActive(true);
                _left.RequireRelease = true;
                _right.RequireRelease = true;
                _layoutScale = 0f;
                _logger.LogInfo("'" + VrScenes.Current + "' is on the floating screen in VR. Point a controller at it and pull the trigger to click.");
            }
            return _camera;
        }

        internal void Deactivate()
        {
            if (!_active) return;
            _active = false;
            _driver.Reset();
            if (_root != null) _root.SetActive(false);
        }

        internal void BeforeRender(float unitsPerMeter)
        {
            if (!_active || _failed) return;
            try
            {
                Layout(unitsPerMeter);
                _mirror.Capture();
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void Update()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception) { Fail(exception); }
        }

        private void Tick()
        {
            if (!_active) return;
            if (!VrScenes.InScreen)
            {
                Deactivate();
                return;
            }
            _input.Refresh();
            bool input = _input.Active && _input.InputAvailable && _layoutScale > 0f;
            float scale = _input.Scale;
            _left.Sample(input);
            _right.Sample(input);
            if (input && (_input.Left.MenuPressed || _input.Right.MenuPressed)) StartScreenInput.TryBack();
            Aim(_left, scale);
            Aim(_right, scale);
            if ((_left.Pressed || _right.Pressed) && StartScreenInput.TryPressFirstPlayStart())
            {
                _left.RequireRelease = true;
                _right.RequireRelease = true;
                _left.Latch(false);
                _right.Latch(false);
            }
            PanelHand owner = _driver.ChooseOwner(_left, _right, ScreenSurface);
            bool pressedEmpty;
            _driver.Drive(ActiveCursor(), owner, owner != null, out pressedEmpty);
            Draw(_left, scale);
            Draw(_right, scale);
        }

        private void Aim(PanelHand hand, float scale)
        {
            hand.Surface = 0;
            Vector3 hit;
            float distance;
            if (hand.Tracked && PanelUi.RayHit(_imageRect, hand.Input.AimOrigin, hand.Input.AimDirection, _distanceMeters * 3f * scale, out hit, out distance))
            {
                hand.Surface = ScreenSurface;
                hand.Point = hit;
                hand.Uv = PanelUi.Uv(_imageRect, hit);
            }
            hand.Latch(hand.Tracked && hand.Trigger);
        }

        private static InteractionCursor ActiveCursor()
        {
            PauseMenu pause = PauseMenu.instance;
            if (pause != null && pause.Paused) return GameHooks.PauseCursor(pause);
            InteractionCursor main = Singleton<InteractionCursor>.Instance;
            return main != null && main.gameObject.activeInHierarchy ? main : FindObjectOfType<InteractionCursor>();
        }

        private void Draw(PanelHand hand, float scale)
        {
            bool onScreen = hand.Surface == ScreenSurface;
            Vector3 origin = hand.Input.AimOrigin;
            Vector3 end = onScreen ? hand.Point : origin + hand.Input.AimDirection * (0.6f * scale);
            bool hovering = onScreen && hand == _driver.LastOwner && _driver.Hover != null;
            Color color = !onScreen ? new Color(0.3f, 0.75f, 1f, 0.35f) : hand.Down ? PanelUi.LaserDown : hovering ? PanelUi.LaserHover : new Color(0.3f, 0.75f, 1f, 0.7f);
            PanelUi.DrawLaser(hand.Line, hand.Tracked, origin, end, color, 0.4f, 0.003f * scale, 0.0015f * scale);
            PanelUi.PlaceDot(hand.Dots[0], _canvas, hand.Tracked && onScreen, hand.Point);
        }

        private void Layout(float unitsPerMeter)
        {
            int width = Mathf.Max(1, Screen.width);
            int height = Mathf.Max(1, Screen.height);
            if (width == _screenWidth && height == _screenHeight && Mathf.Approximately(unitsPerMeter, _layoutScale)) return;
            _screenWidth = width;
            _screenHeight = height;
            _layoutScale = unitsPerMeter;
            float imageWidth = _widthMeters / UnitMeters;
            float imageHeight = imageWidth * height / width;
            _canvas.sizeDelta = new Vector2(imageWidth + 2f * BezelUnits, imageHeight + 2f * BezelUnits);
            _canvas.position = TheaterPosition + new Vector3(0f, -DropMeters, _distanceMeters) * unitsPerMeter;
            _canvas.rotation = Quaternion.identity;
            _canvas.localScale = Vector3.one * (UnitMeters * unitsPerMeter);
            _camera.nearClipPlane = 0.03f * unitsPerMeter;
            _camera.farClipPlane = (_distanceMeters * 4f + 10f) * unitsPerMeter;
        }

        private void Build()
        {
            _uiMaterial = PanelUi.UiMaterial("InscryptionVR Menu Screen Material", false);
            _lineMaterial = PanelUi.LineMaterial();

            _root = new GameObject("InscryptionVR Menu Theater");
            _root.layer = VrDialogue.SubtitleLayer;
            _root.transform.SetParent(transform, false);
            _root.transform.position = TheaterPosition;
            _root.transform.rotation = Quaternion.identity;

            var cameraObject = new GameObject("InscryptionVR Menu Theater Camera");
            cameraObject.transform.SetParent(_root.transform, false);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.orthographic = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.018f, 0.015f, 0.012f, 1f);
            _camera.cullingMask = 1 << VrDialogue.SubtitleLayer;
            _camera.useOcclusionCulling = false;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.nearClipPlane = 0.03f * VrConfig.DefaultWorldUnitsPerMeter;
            _camera.farClipPlane = 60f * VrConfig.DefaultWorldUnitsPerMeter;

            _canvas = PanelUi.Canvas(_root.transform, "InscryptionVR Menu Screen", 100);
            UiImage bezel = PanelUi.Image(_canvas, "Bezel", new Color(0.06f, 0.05f, 0.045f, 1f), _uiMaterial);
            PanelUi.Stretch(bezel.rectTransform, 0f);
            UiRawImage image = PanelUi.RawImage(_canvas, "Image", _uiMaterial, _flip);
            _imageRect = image.rectTransform;
            PanelUi.Stretch(_imageRect, BezelUnits);
            _mirror = new ScreenMirror("Menu Screen", image);

            BuildHand(_left);
            BuildHand(_right);
            _root.SetActive(false);
        }

        private void BuildHand(PanelHand hand)
        {
            hand.Line = PanelUi.Laser(_root.transform, "InscryptionVR Menu Laser " + hand.Input.Name, _lineMaterial, 200);
            hand.Dots = new[] { PanelUi.Dot(_canvas, hand.Input.Name, 12f, _uiMaterial) };
        }

        private void Fail(Exception exception)
        {
            _failed = true;
            try { if (_driver != null) _driver.Reset(); }
            catch (Exception cleanup) { if (_logger != null) _logger.LogWarning("Menu screen cursor restore failed: " + cleanup.Message); }
            if (_root != null) _root.SetActive(false);
            _active = false;
            if (_logger != null) _logger.LogError("Menu screen disabled; use the monitor for menus: " + exception);
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
