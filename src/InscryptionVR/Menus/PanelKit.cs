using System;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;
using UiImage = UnityEngine.UI.Image;
using UiRawImage = UnityEngine.UI.RawImage;
using UiText = UnityEngine.UI.Text;

namespace InscryptionVR
{
    internal sealed class PanelHand
    {
        internal readonly VrHand Input;
        internal bool Tracked;
        internal bool Trigger;
        internal bool Poking;
        internal bool PokeDown;
        internal bool PokeArmed;
        internal float PokeDepth;
        internal int Surface;
        internal Vector3 Point;
        internal Vector2 Uv;
        internal bool Down;
        internal bool Pressed;
        internal bool RequireRelease = true;
        internal object Button;
        internal float RepeatAt;
        internal LineRenderer Line;
        internal RectTransform[] Dots;

        internal PanelHand(VrHand input) { Input = input; }

        internal void Sample(bool input)
        {
            if (!input || !Input.Tracked)
            {
                Lose();
                return;
            }
            Tracked = true;
            Trigger = Input.TriggerDigital || Input.TriggerValue > (Trigger ? 0.35f : 0.55f);
        }

        internal void Lose()
        {
            Tracked = false;
            Trigger = false;
            Poking = false;
            PokeDown = false;
            PokeArmed = false;
            Surface = 0;
            Down = false;
            Pressed = false;
            RequireRelease = true;
            Button = null;
        }

        internal void Latch(bool raw)
        {
            if (RequireRelease)
            {
                if (!raw) RequireRelease = false;
                raw = false;
            }
            Pressed = raw && !Down;
            Down = raw;
        }

        internal void Pulse(int microseconds) { Input.Pulse(microseconds); }
    }

    internal sealed class CursorDriver
    {
        private readonly ManualLogSource _logger;
        private readonly string _name;
        private InteractionCursor _cursor;
        private bool _owns;
        private bool _wasEnabled;
        private MainInputInteractable _down;

        internal CursorDriver(ManualLogSource logger, string name)
        {
            _logger = logger;
            _name = name;
        }

        internal MainInputInteractable Hover { get; private set; }
        internal PanelHand DownHand { get; private set; }
        internal PanelHand LastOwner { get; private set; }

        internal PanelHand ChooseOwner(PanelHand left, PanelHand right, int surface)
        {
            PanelHand owner = DownHand;
            if (owner == null)
            {
                if (right.Pressed && right.Surface == surface) owner = right;
                else if (left.Pressed && left.Surface == surface) owner = left;
                else if (LastOwner != null && LastOwner.Surface == surface) owner = LastOwner;
                else if (right.Surface == surface) owner = right;
                else if (left.Surface == surface) owner = left;
            }
            if (owner != null) LastOwner = owner;
            return owner;
        }

        internal MainInputInteractable Drive(InteractionCursor cursor, PanelHand owner, bool aiming, out bool pressedEmpty)
        {
            pressedEmpty = false;
            if (cursor == null || !cursor.gameObject.activeInHierarchy) { Release(); return null; }
            if (cursor != _cursor) { Release(); _cursor = cursor; }
            if (!aiming && _down == null && DownHand == null) { Release(); return null; }
            if (!_owns)
            {
                _wasEnabled = cursor.enabled;
                cursor.enabled = false;
                _owns = true;
                MainInputInteractable existing = GameHooks.Current(cursor);
                if (existing != null && existing.Enabled) existing.CursorExit();
                GameHooks.SetCurrent(cursor, null);
                GameHooks.SetDown(cursor, null);
                Hover = null;
                _down = null;
            }
            else if (cursor.enabled)
            {
                _wasEnabled = true;
                cursor.enabled = false;
            }

            MainInputInteractable target = null;
            if (aiming && owner.Uv.x >= 0f && owner.Uv.x <= 1f && owner.Uv.y >= 0f && owner.Uv.y <= 1f)
            {
                Camera rayCamera = GameHooks.RayCamera(cursor);
                if (rayCamera != null)
                {
                    Vector3 world = rayCamera.ScreenToWorldPoint(new Vector3(owner.Uv.x * Screen.width, owner.Uv.y * Screen.height, 0f));
                    cursor.transform.position = new Vector3(world.x, world.y, rayCamera.nearClipPlane);
                    target = GameHooks.RaycastAtCursor(cursor);
                }
            }
            if (target != null && (!target.Enabled || GameHooks.Disabled(cursor, target))) target = null;
            if (target != Hover)
            {
                if (Hover != null && Hover.Enabled) Hover.CursorExit();
                Hover = target;
                if (Hover != null)
                {
                    Hover.CursorEnter();
                    if (owner != null) owner.Pulse(250);
                }
            }
            GameHooks.SetCurrent(cursor, Hover);
            if (Hover != null) Hover.CursorStay();

            MainInputInteractable started = null;
            if (owner != null && owner.Pressed && DownHand == null)
            {
                DownHand = owner;
                if (Hover != null)
                {
                    Hover.CursorSelectStart();
                    _down = Hover;
                    GameHooks.SetDown(cursor, _down);
                    owner.Pulse(900);
                    started = Hover;
                    VrLog.Chatter(_logger, _name + " select: " + Hover.GetType().Name + " (" + Hover.name + ").");
                }
                else pressedEmpty = true;
            }
            if (DownHand != null && !DownHand.Down)
            {
                if (Hover != null && Hover.Enabled) Hover.CursorSelectEnd();
                _down = null;
                DownHand = null;
                GameHooks.SetDown(cursor, null);
            }
            if (_down != null && _down != Hover && !GameHooks.Disabled(cursor, _down))
            {
                _down.CursorDragOff();
                _down = null;
                GameHooks.SetDown(cursor, null);
            }
            return started;
        }

        internal void Release()
        {
            if (_cursor != null && _owns)
            {
                try
                {
                    if (Hover != null && Hover.Enabled) Hover.CursorExit();
                }
                catch (Exception exception)
                {
                    if (_logger != null) _logger.LogWarning(_name + " cursor cleanup callback failed: " + exception.Message);
                }
                finally
                {
                    try
                    {
                        GameHooks.SetCurrent(_cursor, null);
                        GameHooks.SetDown(_cursor, null);
                    }
                    finally { _cursor.enabled = _wasEnabled; }
                }
            }
            _owns = false;
            Hover = null;
            _down = null;
            DownHand = null;
        }

        internal void Reset()
        {
            Release();
            LastOwner = null;
        }
    }

    internal sealed class ScreenMirror
    {
        private readonly string _name;
        private readonly UiRawImage _image;
        private RenderTexture _capture;
        private RenderTexture _display;

        internal ScreenMirror(string name, UiRawImage image)
        {
            _name = name;
            _image = image;
        }

        internal void Capture()
        {
            int width = Screen.width;
            int height = Screen.height;
            if (width <= 0 || height <= 0) return;
            if (_capture == null || _capture.width != width || _capture.height != height)
            {
                Dispose();
                _capture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                _capture.name = "InscryptionVR " + _name + " Capture";
                _capture.Create();
                _display = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                _display.name = "InscryptionVR " + _name + " Display";
                _display.useMipMap = true;
                _display.autoGenerateMips = true;
                _display.filterMode = FilterMode.Trilinear;
                _display.anisoLevel = 4;
                _display.Create();
                _image.texture = _display;
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_capture);
            Graphics.Blit(_capture, _display);
        }

        internal void Dispose()
        {
            StereoRenderer.ReleaseTexture(_capture);
            StereoRenderer.ReleaseTexture(_display);
            _capture = null;
            _display = null;
        }
    }

    internal static class PanelUi
    {
        internal static readonly Color LaserHover = new Color(0.3f, 1f, 0.45f, 0.9f);
        internal static readonly Color LaserDown = new Color(1f, 0.85f, 0.4f, 0.95f);
        internal static readonly Color DotColor = new Color(0.35f, 1f, 0.5f, 0.95f);

        internal static Material UiMaterial(string name, bool alwaysOnTop)
        {
            Shader shader = Shader.Find("UI/Default");
            if (shader == null) throw new InvalidOperationException("Unity UI shader unavailable");
            var material = new Material(shader);
            material.name = name;
            if (alwaysOnTop) material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            return material;
        }

        internal static Material LineMaterial()
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader == null) throw new InvalidOperationException("No shader available for a laser");
            return new Material(shader);
        }

        internal static RectTransform Canvas(Transform parent, string name, int sorting)
        {
            var canvasObject = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            canvasObject.layer = VrDialogue.SubtitleLayer;
            canvasObject.transform.SetParent(parent, false);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = true;
            canvas.sortingOrder = sorting;
            return (RectTransform)canvasObject.transform;
        }

        internal static RectTransform Rect(Transform parent, string name)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.layer = VrDialogue.SubtitleLayer;
            child.transform.SetParent(parent, false);
            return (RectTransform)child.transform;
        }

        internal static UiImage Image(Transform parent, string name, Color color, Material material)
        {
            UiImage image = Rect(parent, name).gameObject.AddComponent<UiImage>();
            image.color = color;
            image.raycastTarget = false;
            image.material = material;
            return image;
        }

        internal static UiRawImage RawImage(Transform parent, string name, Material material, bool flip)
        {
            UiRawImage image = Rect(parent, name).gameObject.AddComponent<UiRawImage>();
            image.material = material;
            image.raycastTarget = false;
            image.color = Color.white;
            image.uvRect = flip ? new UnityEngine.Rect(0f, 1f, 1f, -1f) : new UnityEngine.Rect(0f, 0f, 1f, 1f);
            return image;
        }

        internal static UiText Text(Transform parent, string name, string value, int size, TextAnchor alignment, Font font, Material material, Color ink)
        {
            UiText text = Rect(parent, name).gameObject.AddComponent<UiText>();
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = ink;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 14;
            text.resizeTextMaxSize = size;
            text.raycastTarget = false;
            text.material = material;
            return text;
        }

        internal static void Stretch(RectTransform rect, float inset) { Stretch(rect, inset, inset, inset, inset); }

        internal static void Stretch(RectTransform rect, float left, float top, float right, float bottom)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        internal static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        internal static LineRenderer Laser(Transform parent, string name, Material material, int sorting)
        {
            var lineObject = new GameObject(name);
            lineObject.layer = VrDialogue.SubtitleLayer;
            lineObject.transform.SetParent(parent, false);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.sortingOrder = sorting;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            lineObject.SetActive(false);
            return line;
        }

        internal static void DrawLaser(LineRenderer line, bool visible, Vector3 origin, Vector3 end, Color color, float endFade, float startWidth, float endWidth)
        {
            if (line.gameObject.activeSelf != visible) line.gameObject.SetActive(visible);
            if (!visible) return;
            line.startColor = color;
            line.endColor = new Color(color.r, color.g, color.b, color.a * endFade);
            line.startWidth = startWidth;
            line.endWidth = endWidth;
            line.SetPosition(0, origin);
            line.SetPosition(1, end);
        }

        internal static RectTransform Dot(Transform canvas, string name, float size, Material material)
        {
            UiImage dot = Image(canvas, "Pointer " + name, DotColor, material);
            dot.rectTransform.anchorMin = dot.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            dot.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            dot.rectTransform.sizeDelta = new Vector2(size, size);
            dot.gameObject.SetActive(false);
            return dot.rectTransform;
        }

        internal static void PlaceDot(RectTransform dot, RectTransform canvas, bool visible, Vector3 world)
        {
            if (dot.gameObject.activeSelf != visible) dot.gameObject.SetActive(visible);
            if (!visible) return;
            Vector3 local = canvas.InverseTransformPoint(world);
            dot.localPosition = new Vector3(local.x, local.y, 0f);
        }

        internal static bool Inside(RectTransform rect, Vector3 world)
        {
            Vector3 local = rect.InverseTransformPoint(world);
            return rect.rect.Contains(new Vector2(local.x, local.y));
        }

        internal static Vector2 Uv(RectTransform rect, Vector3 world)
        {
            Vector3 local = rect.InverseTransformPoint(world);
            UnityEngine.Rect area = rect.rect;
            return new Vector2((local.x - area.xMin) / area.width, (local.y - area.yMin) / area.height);
        }

        internal static bool RayHit(RectTransform rect, Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hit, out float distance)
        {
            hit = Vector3.zero;
            distance = 0f;
            Vector3 normal = rect.forward;
            float facing = Vector3.Dot(direction, normal);
            if (facing < 0.05f) return false;
            distance = Vector3.Dot(rect.position - origin, normal) / facing;
            if (distance <= 0f || distance >= maxDistance) return false;
            hit = origin + direction * distance;
            return Inside(rect, hit);
        }
    }
}
