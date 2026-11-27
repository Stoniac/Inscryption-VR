using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;
using UnityEngine.UI;

namespace InscryptionVR
{
    public sealed class NameKeyboard : MonoBehaviour
    {
        private static readonly FieldInfo KeyboardField =
            typeof(DeathCardCreationSequencer).GetField("keyboardInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private const float KeyPitch = 36f;
        private const float KeyGap = 4f;
        private const float Margin = 12f;
        private const float PreviewHeight = 44f;
        private const float BoardWidth = KeyPitch * 10f + Margin * 2f;
        private const float BoardHeight = Margin * 2f + PreviewHeight + 8f + KeyPitch * 5f;
        private const float HoverFront = -45f;
        private const float HoverBack = 25f;
        private const float RearmDepth = -8f;
        private const float PassLimit = 70f;
        private const float RepressSeconds = 0.06f;
        private const float FlashSeconds = 0.18f;
        private const float AheadMeters = 0.3f;
        private const float BelowMeters = 0.36f;
        private const float CardClearMeters = 0.06f;
        private const float LowestMeters = 0.55f;

        private static readonly Color BoardColor = new Color(0.02f, 0.02f, 0.02f, 0.86f);
        private static readonly Color KeyColor = new Color(0.1f, 0.095f, 0.09f, 0.95f);
        private static readonly Color TouchColor = new Color(0.55f, 0.42f, 0.22f, 1f);
        private static readonly Color AimColor = new Color(0.3f, 0.24f, 0.14f, 1f);
        private static readonly Color FlashColor = new Color(1f, 0.8f, 0.45f, 1f);
        private static readonly Color DoneColor = new Color(0.32f, 0.22f, 0.09f, 0.95f);
        private static readonly Color DimColor = new Color(0.05f, 0.05f, 0.05f, 0.9f);
        private static readonly Color ShiftColor = new Color(0.2f, 0.36f, 0.42f, 1f);

        private enum KeyKind { Char, Shift, Back, Space, Done }

        private sealed class Key
        {
            internal KeyKind Kind;
            internal char Lower;
            internal char Upper;
            internal Rect Area;
            internal Image Image;
            internal Text Label;
            internal float Flash;
        }

        private sealed class Finger
        {
            internal bool Armed = true;
            internal bool HadPrevious;
            internal Vector3 Previous;
            internal float LastPress = -10f;
            internal Key Touching;
            internal Key Aiming;
        }

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private VrInput _input;
        private bool _failed;
        private GameObject _root;
        private Material _material;
        private Font _font;
        private Text _preview;
        private Text _count;
        private readonly List<Key> _keys = new List<Key>();
        private readonly Finger _left = new Finger();
        private readonly Finger _right = new Finger();
        private DeathCardCreationSequencer _sequencer;
        private float _nextSearch;
        private bool _active;
        private bool _placed;
        private int _seatRevision = -1;
        private bool _shift;
        private bool _loggedPlacement;

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, VrInput input)
        {
            _logger = logger;
            _renderer = renderer;
            _input = input;
            if (KeyboardField == null || SanctumHands.DeathCardField == null || SanctumHands.VictoryCardField == null)
                _logger.LogWarning("Name keyboard unavailable: the death card's keyboard fields were not found; type your name on the real keyboard.");
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed || KeyboardField == null) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                Hide();
                _logger.LogError("Name keyboard stopped after an error; type your name on the real keyboard: " + exception);
            }
        }

        private void Tick()
        {
            KeyboardInputHandler handler;
            SelectableCard card;
            Vector3 eye;
            Quaternion view;
            float scale;
            if (!FindNaming(out handler, out card) || !_renderer.TryGetHeadPose(out eye, out view, out scale))
            {
                Hide();
                return;
            }
            if (_root == null) Build();
            if (!_active)
            {
                _active = true;
                _placed = false;
                _shift = string.IsNullOrEmpty(handler.KeyboardInput);
                _left.HadPrevious = _right.HadPrevious = false;
                _logger.LogInfo("Name keyboard up for '" + card.name + "': poke the keys with a fingertip, or point and pull the trigger.");
            }
            if (!_placed || _seatRevision != _renderer.SeatRevision) Place(eye, view, card, scale);
            if (!_root.activeSelf) _root.SetActive(true);
            _input.Refresh();
            float now = Time.unscaledTime;
            Poke(_input.Left, _left, handler, now);
            Poke(_input.Right, _right, handler, now);
            Draw(handler, Time.unscaledDeltaTime);
        }

        private bool FindNaming(out KeyboardInputHandler handler, out SelectableCard card)
        {
            handler = null;
            card = null;
            if (!VrScenes.InSanctum) return false;
            if (_sequencer == null && Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + 0.5f;
                _sequencer = UnityEngine.Object.FindObjectOfType<DeathCardCreationSequencer>();
            }
            if (_sequencer == null) return false;
            handler = KeyboardField.GetValue(_sequencer) as KeyboardInputHandler;
            if (handler == null || handler.EnteredInput) return false;
            card = Naming(SanctumHands.DeathCardField);
            if (card == null) card = Naming(SanctumHands.VictoryCardField);
            return card != null;
        }

        private SelectableCard Naming(FieldInfo field)
        {
            if (field == null) return null;
            SelectableCard card = field.GetValue(_sequencer) as SelectableCard;
            if (card == null || !card.gameObject.activeInHierarchy || card.Info == null || card.Info.Mods == null) return null;
            List<CardModificationInfo> mods = card.Info.Mods;
            for (int i = 0; i < mods.Count; i++)
                if (mods[i] != null && mods[i].nameReplacement != null) return card;
            return null;
        }

        private void Place(Vector3 eye, Quaternion view, SelectableCard card, float scale)
        {
            _placed = true;
            _seatRevision = _renderer.SeatRevision;
            Vector3 toCard = card.transform.position - eye;
            bool cardNear = toCard.magnitude <= 1.2f * scale;
            Vector3 flat = Vector3.ProjectOnPlane(cardNear ? toCard : view * Vector3.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f * scale * scale) flat = Vector3.ProjectOnPlane(view * Vector3.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f * scale * scale) flat = Vector3.forward;
            flat.Normalize();
            Vector3 center = eye + flat * (AheadMeters * scale) + Vector3.down * (BelowMeters * scale);
            Bounds bounds;
            if (cardNear && HandReach.CardBounds(card, out bounds))
            {
                float top = center.y + 0.5f * BoardHeight * 0.001f * scale * 0.7f;
                float clear = bounds.min.y - CardClearMeters * scale;
                if (top > clear) center.y -= top - clear;
                center.y = Mathf.Max(center.y, eye.y - LowestMeters * scale);
            }
            _root.transform.SetPositionAndRotation(center, Quaternion.LookRotation(center - eye, Vector3.up));
            _root.transform.localScale = Vector3.one * (0.001f * scale);
            if (_loggedPlacement) return;
            _loggedPlacement = true;
            Vector3 keyboard = (center - eye) / scale;
            Vector3 cardOffset = toCard / scale;
            _logger.LogInfo("Name keyboard placed " + Horizontal(keyboard).ToString("0.00") + " m ahead and " + (-keyboard.y).ToString("0.00") +
                " m below your eyes; the card is " + Horizontal(cardOffset).ToString("0.00") + " m ahead and " + (-cardOffset.y).ToString("0.00") + " m below.");
        }

        private static float Horizontal(Vector3 v) { return new Vector2(v.x, v.z).magnitude; }

        private void Poke(VrHand hand, Finger finger, KeyboardInputHandler handler, float now)
        {
            finger.Touching = null;
            finger.Aiming = null;
            if (!_input.Active || !hand.Tracked)
            {
                finger.HadPrevious = false;
                finger.Armed = true;
                return;
            }
            Transform board = _root.transform;
            bool near = false;
            if (hand.HasIndexTip)
            {
                Vector3 local = board.InverseTransformPoint(hand.IndexTip);
                float depth = local.z;
                Key under = KeyAt(local.x, local.y);
                near = depth > HoverFront && depth < HoverBack && InsideBoard(local, 30f);
                if (near) finger.Touching = under;
                if (finger.HadPrevious && finger.Armed && finger.Previous.z < 0f && depth >= 0f && depth < PassLimit)
                {
                    float t = finger.Previous.z / (finger.Previous.z - depth);
                    Vector3 hit = Vector3.Lerp(finger.Previous, local, t);
                    Key key = KeyAt(hit.x, hit.y);
                    finger.Armed = false;
                    if (key != null && now - finger.LastPress > RepressSeconds)
                    {
                        finger.LastPress = now;
                        Press(key, hand, handler, "poke");
                    }
                }
                if (depth < RearmDepth) finger.Armed = true;
                finger.Previous = local;
                finger.HadPrevious = true;
            }
            else finger.HadPrevious = false;
            if (near) return;
            Vector3 origin = board.InverseTransformPoint(hand.AimOrigin);
            Vector3 direction = board.InverseTransformVector(hand.AimDirection);
            if (origin.z >= 0f || direction.z <= 1e-6f) return;
            Vector3 point = origin + direction * (-origin.z / direction.z);
            finger.Aiming = KeyAt(point.x, point.y);
            if (finger.Aiming != null && hand.TriggerEdge == 1) Press(finger.Aiming, hand, handler, "aim");
        }

        private static bool InsideBoard(Vector3 local, float slack)
        {
            return Mathf.Abs(local.x) <= BoardWidth * 0.5f + slack && Mathf.Abs(local.y) <= BoardHeight * 0.5f + slack;
        }

        private Key KeyAt(float x, float y)
        {
            for (int i = 0; i < _keys.Count; i++)
                if (_keys[i].Area.Contains(new Vector2(x, y))) return _keys[i];
            return null;
        }

        private void Press(Key key, VrHand hand, KeyboardInputHandler handler, string how)
        {
            string text = handler.KeyboardInput ?? "";
            int limit = Mathf.Max(1, handler.maxInputLength);
            bool accepted = true;
            switch (key.Kind)
            {
                case KeyKind.Char:
                    if (text.Length >= limit) accepted = false;
                    else
                    {
                        handler.KeyboardInput = text + (_shift ? key.Upper : key.Lower);
                        _shift = false;
                    }
                    break;
                case KeyKind.Space:
                    if (text.Length == 0 || text.Length >= limit || text.EndsWith(" ")) accepted = false;
                    else handler.KeyboardInput = text + " ";
                    break;
                case KeyKind.Back:
                    if (text.Length == 0) accepted = false;
                    else
                    {
                        handler.KeyboardInput = text.Substring(0, text.Length - 1);
                        if (handler.KeyboardInput.Length == 0) _shift = true;
                    }
                    break;
                case KeyKind.Shift:
                    _shift = !_shift;
                    break;
                case KeyKind.Done:
                    string name = text.Trim();
                    if (name.Length == 0) accepted = false;
                    else
                    {
                        handler.KeyboardInput = name;
                        handler.EnteredInput = true;
                        if (handler.EnterPressed != null) handler.EnterPressed();
                        hand.Play(Feel.Thud);
                        _logger.LogInfo("Name entered on the VR keyboard (" + how + ", " + name.Length + " letters).");
                    }
                    break;
            }
            key.Flash = accepted ? 1f : 0f;
            if (key.Kind != KeyKind.Done || !accepted) hand.Play(accepted ? Feel.Tap : Feel.Reject);
        }

        private void Draw(KeyboardInputHandler handler, float dt)
        {
            string text = handler.KeyboardInput ?? "";
            int limit = Mathf.Max(1, handler.maxInputLength);
            bool caret = Mathf.Repeat(Time.unscaledTime, 1f) < 0.55f;
            string caretText = caret ? "|" : "<color=#00000000>|</color>";
            _preview.text = text.Length == 0 ? caretText + "<color=#ffffff55>your name</color>" : Escape(text) + caretText;
            _count.text = text.Length + "/" + limit;
            for (int i = 0; i < _keys.Count; i++)
            {
                Key key = _keys[i];
                key.Flash = Mathf.MoveTowards(key.Flash, 0f, dt / FlashSeconds);
                Color color = key.Kind == KeyKind.Done ? (text.Trim().Length > 0 ? DoneColor : DimColor) :
                    key.Kind == KeyKind.Shift && _shift ? ShiftColor : KeyColor;
                if (key == _left.Aiming || key == _right.Aiming) color = AimColor;
                if (key == _left.Touching || key == _right.Touching) color = TouchColor;
                key.Image.color = Color.Lerp(color, FlashColor, key.Flash);
                if (key.Kind == KeyKind.Char) key.Label.text = (_shift ? key.Upper : key.Lower).ToString();
            }
        }

        private static string Escape(string text)
        {
            return text.Replace("<", "‹").Replace(">", "›");
        }

        private void Build()
        {
            _root = new GameObject("InscryptionVR Name Keyboard", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            _root.layer = VrDialogue.SubtitleLayer;
            _root.transform.SetParent(transform, false);
            ((RectTransform)_root.transform).sizeDelta = new Vector2(BoardWidth, BoardHeight);
            Canvas canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 31000;
            _root.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 6f;
            Shader shader = Shader.Find("UI/Default");
            if (shader == null) throw new InvalidOperationException("Unity UI shader unavailable");
            _material = new Material(shader);
            _material.name = "InscryptionVR Name Keyboard Material";
            _material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            Panel("Board", Vector2.zero, new Vector2(BoardWidth, BoardHeight), BoardColor);
            float top = BoardHeight * 0.5f - Margin;
            float previewY = top - PreviewHeight * 0.5f;
            Panel("Preview", new Vector2(0f, previewY), new Vector2(BoardWidth - Margin * 2f, PreviewHeight), new Color(0f, 0f, 0f, 0.9f));
            _preview = Label("Name", new Vector2(-14f, previewY), new Vector2(BoardWidth - Margin * 2f - 72f, PreviewHeight), 30, TextAnchor.MiddleLeft);
            _preview.supportRichText = true;
            _count = Label("Count", new Vector2(BoardWidth * 0.5f - Margin - 30f, previewY), new Vector2(52f, PreviewHeight), 15, TextAnchor.MiddleRight);
            _count.color = new Color(1f, 1f, 1f, 0.45f);

            float rowTop = top - PreviewHeight - 8f;
            float left = -KeyPitch * 5f;
            CharRow("1234567890", "1234567890", left, RowY(rowTop, 0));
            CharRow("qwertyuiop", "QWERTYUIOP", left, RowY(rowTop, 1));
            CharRow("asdfghjkl", "ASDFGHJKL", left + KeyPitch * 0.5f, RowY(rowTop, 2));
            float y3 = RowY(rowTop, 3);
            AddKey(KeyKind.Shift, ' ', ' ', "Shift", left, y3, 1.5f, 15);
            CharRow("zxcvbnm", "ZXCVBNM", left + KeyPitch * 1.5f, y3);
            AddKey(KeyKind.Back, ' ', ' ', "Back", left + KeyPitch * 8.5f, y3, 1.5f, 15);
            float y4 = RowY(rowTop, 4);
            AddKey(KeyKind.Space, ' ', ' ', "Space", left, y4, 6f, 15);
            AddKey(KeyKind.Done, ' ', ' ', "Done", left + KeyPitch * 6f, y4, 4f, 18);
            _root.SetActive(false);
        }

        private static float RowY(float rowTop, int row) { return rowTop - (row + 0.5f) * KeyPitch; }

        private void CharRow(string lower, string upper, float left, float y)
        {
            for (int i = 0; i < lower.Length; i++)
                AddKey(KeyKind.Char, lower[i], upper[i], lower[i].ToString(), left + KeyPitch * i, y, 1f, 20);
        }

        private void AddKey(KeyKind kind, char lower, char upper, string label, float left, float y, float width, int fontSize)
        {
            float slot = KeyPitch * width;
            Vector2 center = new Vector2(left + slot * 0.5f, y);
            Vector2 size = new Vector2(slot - KeyGap, KeyPitch - KeyGap);
            Key key = new Key();
            key.Kind = kind;
            key.Lower = lower;
            key.Upper = upper;
            key.Area = new Rect(center - new Vector2(slot, KeyPitch) * 0.5f, new Vector2(slot, KeyPitch));
            key.Image = Panel("Key " + label, center, size, KeyColor);
            key.Label = Label("Label " + label, center, size, fontSize, TextAnchor.MiddleCenter);
            _keys.Add(key);
        }

        private Image Panel(string name, Vector2 center, Vector2 size, Color color)
        {
            GameObject child = Child(name, center, size);
            Image image = child.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            image.material = _material;
            return image;
        }

        private Text Label(string name, Vector2 center, Vector2 size, int fontSize, TextAnchor anchor)
        {
            GameObject child = Child(name, center, size);
            Text text = child.AddComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.alignment = anchor;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            text.raycastTarget = false;
            text.material = _material;
            return text;
        }

        private GameObject Child(string name, Vector2 center, Vector2 size)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.layer = VrDialogue.SubtitleLayer;
            child.transform.SetParent(_root.transform, false);
            RectTransform rect = (RectTransform)child.transform;
            rect.anchoredPosition = center;
            rect.sizeDelta = size;
            return child;
        }

        private void Hide()
        {
            if (_active && _logger != null) _logger.LogInfo("Name keyboard put away.");
            _active = false;
            _placed = false;
            _left.HadPrevious = _right.HadPrevious = false;
            _left.Armed = _right.Armed = true;
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            if (!VrScenes.InSanctum) _sequencer = null;
        }

        private void OnDestroy()
        {
            if (_root != null) Destroy(_root);
            if (_material != null) Destroy(_material);
        }
    }
}
