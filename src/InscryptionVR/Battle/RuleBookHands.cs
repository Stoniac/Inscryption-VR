using System.Reflection;
using System.Text;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class RuleBookHands
    {
        private enum Mode { None, Gripped, Carried, Floating, Closing }

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo RigField = typeof(RuleBookController).GetField("rigParent", Members);
        private static readonly FieldInfo FlipperField = typeof(RuleBookController).GetField("flipper", Members);
        private static readonly FieldInfo AnimField = typeof(RuleBookController).GetField("bookAnim", Members);
        private static readonly MethodInfo LearnFlipMethod = typeof(RuleBookController).GetMethod("IncrementFlipsAndTryLearnMechanic", Members);
        private static readonly FieldInfo OpenerField = typeof(TableRuleBook).GetField("openRulebookInteractable", Members);

        private const float OpenReachMeters = 0.015f;
        private const float HoldInsetMeters = 0.03f;
        private const float HoldLiftMeters = 0.02f;
        private const float FloatMeters = 0.45f;
        private const float FloatDropMeters = 0.15f;
        private const float PopSeconds = 0.18f;
        private const float CloseSeconds = 0.2f;
        private const float RestoreAfterSeconds = 1f;
        private const float StickAxis = 0.7f;
        private const float SwipeSpeed = 0.5f;
        private const float SwipeTravelMeters = 0.06f;
        private const float SwipeCooldown = 0.3f;
        private const float ZoneMarginMeters = 0.05f;
        private const float ZoneFrontMeters = 0.14f;
        private const float ZoneBackMeters = 0.04f;

        private sealed class Swipe
        {
            internal bool Armed;
            internal float StartX;
            internal int Direction;
        }

        private readonly ManualLogSource _logger;
        private readonly StereoRenderer _renderer;
        private readonly Swipe _leftSwipe = new Swipe();
        private readonly Swipe _rightSwipe = new Swipe();
        private readonly RuleBookPages _pages = new RuleBookPages();
        private float _widthMeters;
        private bool _loggedZoom;

        private RuleBookController _book;
        private Transform _rig;
        private PageFlipper _flipper;
        private Renderer _cover;
        private Transform _coverSpace;
        private Vector3 _coverLocalCentre;
        private bool _captured;
        private Vector3 _restLocalPosition;
        private Quaternion _restLocalRotation = Quaternion.identity;
        private Vector3 _restLocalScale = Vector3.one;
        private Quaternion _cameraToRig = Quaternion.identity;
        private float _restWidth;
        private float _restHeight;
        private int _layers;

        private Mode _mode;
        private VrHand _holder;
        private Quaternion _turn = Quaternion.identity;
        private Vector3 _targetCentre;
        private Quaternion _targetRotation = Quaternion.identity;
        private bool _posed;
        private float _unitsPerMeter = 1f;
        private float _modeStart;
        private float _nextFlip;
        private float _closeScale = 1f;
        private MainInputInteractable _hovered;
        private VrHand _hoverHand;
        private bool _loggedUnavailable;

        internal static bool Available { get { return RigField != null && FlipperField != null && AnimField != null; } }

        internal static bool Open { get; private set; }

        internal RuleBookHands(ManualLogSource logger, StereoRenderer renderer, float widthMeters)
        {
            _logger = logger;
            _renderer = renderer;
            SetWidth(widthMeters);
            if (_renderer != null) _renderer.BeforeEyes += Apply;
        }

        internal void SetWidth(float meters) { _widthMeters = Mathf.Clamp(meters, 0.2f, 0.9f); }

        internal void Tick(VrHand left, VrHand right, VrHand main, VrHand off, bool tracking, bool input, float reach, float scale)
        {
            if (!Bind())
            {
                Open = false;
                return;
            }
            _unitsPerMeter = scale;
            if (!tracking)
            {
                Release();
                return;
            }
            bool shown = _book.Shown;
            if (shown && (_mode == Mode.None || _mode == Mode.Closing)) Attach(main, off, input);
            else if (!shown && _mode != Mode.None && _mode != Mode.Closing) BeginClosing("the game closed it");

            if (_mode == Mode.Gripped || _mode == Mode.Carried || _mode == Mode.Floating)
            {
                if (input) Interact(left, right, main);
                if (_mode != Mode.Closing) UpdateTarget();
            }
            if (_mode == Mode.Closing) UpdateClosing();
            if (_mode == Mode.None)
            {
                if (input && !shown) TryOpen(left, right, reach);
                else ClearHover();
            }
            else ClearHover();
            Open = _mode == Mode.Gripped || _mode == Mode.Carried || _mode == Mode.Floating;
            Apply();
        }

        private bool Bind()
        {
            RuleBookController book = Singleton<RuleBookController>.Instance;
            if (book == _book && _rig != null) return true;
            if (_book != null && book != _book) Forget();
            if (book == null || !Available)
            {
                if (book != null && !_loggedUnavailable)
                {
                    _loggedUnavailable = true;
                    _logger.LogWarning("Rule book in hand unavailable: RuleBookController internals not found.");
                }
                return false;
            }
            GameObject rig = RigField.GetValue(book) as GameObject;
            PageFlipper flipper = FlipperField.GetValue(book) as PageFlipper;
            if (rig == null || flipper == null) return false;
            _book = book;
            _rig = rig.transform;
            _flipper = flipper;
            _captured = false;
            _cover = null;
            return true;
        }

        private void Forget()
        {
            ReleaseHolder();
            _pages.Clear();
            _book = null;
            _rig = null;
            _flipper = null;
            _cover = null;
            _captured = false;
            _posed = false;
            _mode = Mode.None;
            _hovered = null;
            _hoverHand = null;
            Open = false;
            if (_renderer != null) _renderer.ExtraEyeLayers = 0;
        }

        private void TryOpen(VrHand left, VrHand right, float reach)
        {
            MainInputInteractable opener = TableOpener();
            if (opener == null)
            {
                ClearHover();
                return;
            }
            VrHand best = null;
            float bookReach = Mathf.Min(reach, OpenReachMeters * _unitsPerMeter);
            float bestSqr = bookReach * bookReach;
            Consider(left, opener, ref best, ref bestSqr);
            Consider(right, opener, ref best, ref bestSqr);
            if (best == null)
            {
                ClearHover();
                return;
            }
            best.Highlight();
            GrabGlow.Mark(Body(opener));
            if (_hovered != opener || _hoverHand != best)
            {
                SetHover(opener, best);
                best.Play(Feel.Tick, 0.5f);
                VrTips.Show("rulebook.open", opener.transform.position + Vector3.up * (0.12f * _unitsPerMeter), "Grip the rule book to read it.", 6);
            }
            if (best.GripEdge != 1) return;
            ClearHover();
            VrTips.Done("rulebook.open");
            _logger.LogInfo("Opened the rule book with the " + best.Name + " hand.");
            _book.OpenToAbilityPage(Ability.Reach.ToString(), null);
            if (!_book.Shown) return;
            Grip(best);
        }

        private static void Consider(VrHand hand, MainInputInteractable opener, ref VrHand best, ref float bestSqr)
        {
            if (!hand.Ready || hand.Busy || hand.HoverCard != null) return;
            float distance = BookSqrDistance(Body(opener), CardGrabber.CardPoint(hand));
            if (distance > bestSqr) return;
            best = hand;
            bestSqr = distance;
        }

        private static float BookSqrDistance(Component body, Vector3 point)
        {
            float best = float.MaxValue;
            Renderer[] renderers = body.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || renderer is ParticleSystemRenderer || GrabGlow.IsShell(renderer)) continue;
                best = Mathf.Min(best, TotemHands.OrientedSqrDistance(renderer, point));
            }
            return best < float.MaxValue ? best : HandReach.RendererSqrDistance(body, point);
        }

        private static Component Body(MainInputInteractable opener)
        {
            TableRuleBook table = opener.GetComponentInParent<TableRuleBook>();
            return table != null ? (Component)table : opener;
        }

        private static MainInputInteractable TableOpener()
        {
            TableRuleBook table = Singleton<TableRuleBook>.Instance;
            if (table == null || !table.gameObject.activeInHierarchy || OpenerField == null) return null;
            MainInputInteractable opener = OpenerField.GetValue(table) as MainInputInteractable;
            if (opener == null || !opener.gameObject.activeInHierarchy || !opener.Enabled) return null;
            InteractionCursor cursor = Singleton<InteractionCursor>.Instance;
            if (cursor != null && cursor.InteractionDisabled) return null;
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            if (flow != null && flow.Transitioning) return null;
            return opener;
        }

        private void SetHover(MainInputInteractable target, VrHand hand)
        {
            if (_hovered == target && _hoverHand == hand) return;
            if (_hovered != null && HandReach.Active(_hovered)) _hovered.CursorExit();
            _hovered = target;
            _hoverHand = hand;
            if (_hovered != null) _hovered.CursorEnter();
        }

        private void ClearHover() { SetHover(null, null); }

        private void Attach(VrHand main, VrHand off, bool input)
        {
            Capture();
            if (input && off.Ready && !off.Busy) Carry(off);
            else if (input && main.Ready && !main.Busy) Carry(main);
            else Float();
        }

        private void Grip(VrHand hand)
        {
            Capture();
            BeginHold(hand, Mode.Gripped);
            hand.Lifted = _book;
            hand.Play(Feel.Tick);
            ShowFlipTip();
        }

        private void Carry(VrHand hand)
        {
            BeginHold(hand, Mode.Carried);
            hand.Play(Feel.Tick, 0.6f);
            _logger.LogInfo("The game opened the rule book; it's in your " + hand.Name + " hand. Press B to close it.");
            ShowFlipTip();
        }

        private void BeginHold(VrHand hand, Mode mode)
        {
            ReleaseHolder();
            bool reopening = _mode == Mode.Closing;
            _holder = hand;
            _mode = mode;
            _modeStart = reopening ? Time.unscaledTime - PopSeconds : Time.unscaledTime;
            _closeScale = 1f;
            Vector3 head;
            Quaternion view;
            float unitsPerMeter;
            Vector3 towardBook = _renderer != null && _renderer.TryGetHeadPose(out head, out view, out unitsPerMeter) ? hand.Position - head : hand.Rotation * Vector3.forward;
            if (towardBook.sqrMagnitude < 1e-6f) towardBook = Vector3.forward;
            Quaternion facing = Quaternion.LookRotation(towardBook, Vector3.up);
            _turn = Quaternion.Inverse(hand.Rotation) * facing;
            ResetSwipes();
            UpdateTarget();
        }

        private void Float()
        {
            ReleaseHolder();
            bool reopening = _mode == Mode.Closing;
            _mode = Mode.Floating;
            _modeStart = reopening ? Time.unscaledTime - PopSeconds : Time.unscaledTime;
            _closeScale = 1f;
            Vector3 head;
            Quaternion view;
            float unitsPerMeter;
            if (_renderer == null || !_renderer.TryGetHeadPose(out head, out view, out unitsPerMeter))
            {
                head = _rig.position;
                view = _rig.rotation;
                unitsPerMeter = _unitsPerMeter;
            }
            Vector3 forward = view * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
            _targetCentre = head + forward * (FloatMeters * unitsPerMeter) - Vector3.up * (FloatDropMeters * unitsPerMeter);
            _targetRotation = Quaternion.LookRotation(_targetCentre - head, Vector3.up);
            _posed = true;
            ResetSwipes();
            _logger.LogInfo("The game opened the rule book with both hands busy; it floats in front of you. Press B to close it.");
            ShowFlipTip();
        }

        private void ShowFlipTip()
        {
            VrTips.Show("rulebook.flip", _targetCentre + Vector3.up * (0.25f * _unitsPerMeter),
                "Hold trigger or grip and swipe across the pages, or flick a stick left or right, to turn them. " + (_mode == Mode.Gripped ? "Let go" : "Press B") + " to close the book.", 12);
        }

        private void UpdateTarget()
        {
            if (_mode == Mode.Floating || _holder == null) return;
            if (!_holder.Tracked) return;
            if (_mode == Mode.Gripped) _holder.SetHoldPose(HandHold.Pinch);
            _targetRotation = _holder.Rotation * _turn;
            float side = _holder.IsLeft ? 1f : -1f;
            float half = _widthMeters * 0.5f;
            _targetCentre = _holder.Position + _targetRotation * new Vector3(side * (half - HoldInsetMeters), HoldLiftMeters, 0f) * _unitsPerMeter;
            _posed = true;
        }

        private void Interact(VrHand left, VrHand right, VrHand main)
        {
            if (main.Tracked && main.MenuPressed)
            {
                main.Play(Feel.Tap, 0.5f);
                Close("B pressed");
                return;
            }
            if (_mode == Mode.Gripped && _holder != null)
            {
                if (_holder.Lifted == null) _holder.Lifted = _book;
                if (_holder.Ready && !_holder.Grip.Held)
                {
                    _holder.Play(Feel.Tap, 0.5f);
                    Close("let go");
                    return;
                }
            }
            TryStick(left);
            TryStick(right);
            TrySwipe(left, _leftSwipe);
            TrySwipe(right, _rightSwipe);
        }

        private void TryStick(VrHand hand)
        {
            if (!hand.Tracked || Mathf.Abs(hand.StickFlick.x) < StickAxis) return;
            bool forwards = hand.StickFlick.x > 0f;
            hand.StickFlick = Vector2.zero;
            Flip(forwards, hand);
        }

        private void TrySwipe(VrHand hand, Swipe swipe)
        {
            if (hand == _holder || !hand.Ready || hand.Busy || _rig == null || !(hand.Grip.Held || hand.Trigger.Held))
            {
                swipe.Armed = false;
                return;
            }
            float u = Mathf.Max(_unitsPerMeter, 1e-4f);
            Quaternion inverse = Quaternion.Inverse(_targetRotation);
            Vector3 local = inverse * (CardGrabber.CardPoint(hand) - _targetCentre) / u;
            float halfWidth = _widthMeters * 0.5f + ZoneMarginMeters;
            float halfHeight = HeightMeters() * 0.5f + ZoneMarginMeters;
            bool inside = Mathf.Abs(local.x) <= halfWidth && Mathf.Abs(local.y) <= halfHeight && local.z >= -ZoneFrontMeters && local.z <= ZoneBackMeters;
            if (!inside)
            {
                swipe.Armed = false;
                return;
            }
            hand.Claimed = true;
            Vector3 velocity = hand.Velocity;
            if (_holder != null && _holder.Tracked) velocity -= _holder.Velocity;
            float vx = (inverse * velocity).x / u;
            int direction = vx > 0.15f ? 1 : vx < -0.15f ? -1 : 0;
            if (!swipe.Armed || direction != swipe.Direction)
            {
                swipe.Armed = true;
                swipe.Direction = direction;
                swipe.StartX = local.x;
                return;
            }
            if (direction == 0 || Mathf.Abs(vx) < SwipeSpeed || Mathf.Abs(local.x - swipe.StartX) < SwipeTravelMeters) return;
            if (!Flip(direction > 0, hand)) return;
            swipe.StartX = local.x;
        }

        private bool Flip(bool forwards, VrHand hand)
        {
            if (_flipper == null || _flipper.FlippingToPage || Time.unscaledTime < _nextFlip) return false;
            _nextFlip = Time.unscaledTime + SwipeCooldown;
            _flipper.Flip(forwards, 0.15f);
            if (LearnFlipMethod != null)
            {
                try { LearnFlipMethod.Invoke(_book, null); }
                catch (System.Exception) { }
            }
            hand.Play(Feel.Tick, 0.9f);
            if (_holder != null && _holder != hand) _holder.Play(Feel.Tick, 0.4f);
            VrTips.Done("rulebook.flip");
            VrLog.Chatter(_logger, "Rule book: page " + (forwards ? "forward" : "back") + " (" + hand.Name + " hand).");
            return true;
        }

        private void ResetSwipes()
        {
            _leftSwipe.Armed = false;
            _rightSwipe.Armed = false;
        }

        private float HeightMeters()
        {
            if (_restWidth <= 1e-5f || _restHeight <= 1e-5f) return _widthMeters * 0.7f;
            return _widthMeters * _restHeight / _restWidth;
        }

        private void Close(string reason)
        {
            if (_book != null && _book.Shown) _book.SetShown(false);
            BeginClosing(reason);
        }

        private void BeginClosing(string reason)
        {
            ReleaseHolder();
            _mode = Mode.Closing;
            _modeStart = Time.unscaledTime;
            VrTips.Done("rulebook.flip");
            VrLog.Chatter(_logger, "Rule book closed (" + reason + ").");
        }

        private void UpdateClosing()
        {
            float elapsed = Time.unscaledTime - _modeStart;
            _closeScale = Mathf.Lerp(1f, 0.3f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / CloseSeconds)));
            if ((_rig != null && !_rig.gameObject.activeInHierarchy) || elapsed >= RestoreAfterSeconds)
            {
                Restore();
                _mode = Mode.None;
            }
        }

        private void ReleaseHolder()
        {
            if (_holder != null && _holder.Lifted == (Component)_book) _holder.Lifted = null;
            _holder = null;
        }

        internal void Release()
        {
            ReleaseHolder();
            Restore();
            _mode = Mode.None;
            Open = false;
            ClearHover();
        }

        private void Restore()
        {
            if (_captured && _rig != null && _posed)
            {
                _rig.localPosition = _restLocalPosition;
                _rig.localRotation = _restLocalRotation;
                _rig.localScale = _restLocalScale;
            }
            _pages.Reset();
            _posed = false;
            if (_renderer != null) _renderer.ExtraEyeLayers = 0;
        }

        private void Apply()
        {
            if (!_posed || !_captured || _rig == null || _mode == Mode.None) return;
            if (_restWidth <= 1e-5f) return;
            float pop = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.unscaledTime - _modeStart) / PopSeconds));
            float grow = _mode == Mode.Closing ? _closeScale : Mathf.Lerp(0.5f, 1f, pop);
            float k = _widthMeters * _unitsPerMeter / _restWidth * grow;
            _rig.localScale = _restLocalScale * k;
            _rig.rotation = _targetRotation * _cameraToRig;
            Vector3 centre = _coverSpace != null ? _coverSpace.TransformPoint(_coverLocalCentre) : _rig.position;
            _rig.position += _targetCentre - centre;
            _pages.Apply(Time.unscaledTime);
            if (!_loggedZoom && _pages.Count > 0)
            {
                _loggedZoom = true;
                _logger.LogInfo("Rule book pages: " + _pages.Count + " page camera(s), text zoomed " + _pages.Zoom.ToString("F2") + "x to fit the page.");
            }
            if (_renderer != null) _renderer.ExtraEyeLayers = _layers;
        }

        private void Capture()
        {
            if (_captured || _rig == null) return;
            Camera camera = _renderer != null ? _renderer.SourceCamera : null;
            Quaternion cameraRotation = camera != null ? camera.transform.rotation : Quaternion.identity;
            _restLocalPosition = _rig.localPosition;
            _restLocalRotation = _rig.localRotation;
            _restLocalScale = _rig.localScale;
            _cameraToRig = Quaternion.Inverse(cameraRotation) * _rig.rotation;
            FindCover();
            _pages.Bind(_rig);
            _captured = true;
            Probe(camera);
        }

        private void FindCover()
        {
            _cover = null;
            _coverSpace = null;
            _restWidth = 0f;
            _restHeight = 0f;
            _layers = 0;
            float bestVolume = 0f;
            Renderer[] renderers = _rig.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer || GrabGlow.IsShell(renderer) || RuleBookPages.UnderPageCamera(renderer.transform)) continue;
                _layers |= 1 << renderer.gameObject.layer;
                Transform space;
                Bounds local;
                if (!LocalBounds(renderer, out space, out local)) continue;
                Vector3 size = Vector3.Scale(local.size, Abs(space.lossyScale));
                float volume = Mathf.Max(size.x, 1e-4f) * Mathf.Max(size.y, 1e-4f) * Mathf.Max(size.z, 1e-4f);
                if (volume <= bestVolume) continue;
                bestVolume = volume;
                _cover = renderer;
                _coverSpace = space;
                _coverLocalCentre = local.center;
                float a = size.x, b = size.y, c = size.z;
                float largest = Mathf.Max(a, Mathf.Max(b, c));
                float smallest = Mathf.Min(a, Mathf.Min(b, c));
                _restWidth = largest;
                _restHeight = a + b + c - largest - smallest;
            }
        }

        private static bool LocalBounds(Renderer renderer, out Transform space, out Bounds bounds)
        {
            space = renderer.transform;
            bounds = new Bounds();
            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null)
            {
                if (skinned.rootBone != null) space = skinned.rootBone;
                bounds = skinned.localBounds;
                return bounds.size.sqrMagnitude > 1e-10f;
            }
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return false;
            bounds = filter.sharedMesh.bounds;
            return bounds.size.sqrMagnitude > 1e-10f;
        }

        private static Vector3 Abs(Vector3 v) { return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z)); }

        private void Probe(Camera camera)
        {
            var text = new StringBuilder(512);
            text.Append("Rule book rig probe: '").Append(Path(_rig)).Append("'");
            text.Append(", game camera '").Append(camera != null ? Path(camera.transform) : "none").Append("'");
            text.Append(camera != null && _rig.IsChildOf(camera.transform) ? " (rig is under the camera)" : " (rig is not under the camera)");
            text.Append(", rest local pos ").Append(_restLocalPosition.ToString("F3")).Append(" scale ").Append(_restLocalScale.ToString("F3"));
            text.Append(", cover '").Append(_cover != null ? _cover.name : "none").Append("' ").Append(_restWidth.ToString("F3")).Append(" x ").Append(_restHeight.ToString("F3")).Append(" units");
            int missing = camera != null ? _layers & ~camera.cullingMask : 0;
            text.Append(", layers 0x").Append(_layers.ToString("X8"));
            if (missing != 0) text.Append(" (0x").Append(missing.ToString("X8")).Append(" not drawn by the game camera; adding them to the eyes)");
            text.Append(". Renderers:");
            Renderer[] renderers = _rig.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length && i < 24; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                text.Append(' ').Append(renderer.name).Append('[').Append(renderer.GetType().Name).Append(" L").Append(renderer.gameObject.layer).Append(']');
            }
            _logger.LogInfo(text.ToString());
        }

        private static string Path(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
