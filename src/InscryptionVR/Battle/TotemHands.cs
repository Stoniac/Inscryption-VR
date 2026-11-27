using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using Pixelplacement;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class TotemHands
    {
        private enum HeadState { Loose, Held, Threaded, Seated }

        private sealed class Lift
        {
            internal Transform Target;
            internal VrHand Hand;
            private Transform _parent;
            private Component _token;
            private Vector3 _restPosition;
            private Quaternion _restRotation;
            private Vector3 _localPosition;
            private Quaternion _localRotation;
            private Vector3 _from;
            private Quaternion _fromRotation;
            private float _progress;
            private bool _returning;

            internal bool Holding { get { return Target != null && Hand != null && !_returning; } }

            internal bool Active { get { return Target != null; } }

            internal void Start(VrHand hand, Transform target, Component token)
            {
                if (Target != target)
                {
                    Target = target;
                    _parent = target.parent;
                    _restPosition = target.localPosition;
                    _restRotation = target.localRotation;
                }
                Tween.Stop(target.GetInstanceID());
                if (Hand != null && Hand != hand && Hand.Lifted == _token) Hand.Lifted = null;
                Hand = hand;
                _token = token;
                _returning = false;
                Quaternion inverse = Quaternion.Inverse(hand.Rotation);
                _localPosition = inverse * (target.position - hand.Position);
                _localRotation = inverse * target.rotation;
                hand.Lifted = token;
            }

            internal void Drop()
            {
                if (Target == null || _returning) return;
                Unclaim();
                _returning = true;
                _progress = 0f;
                _from = Target.position;
                _fromRotation = Target.rotation;
            }

            private void Unclaim()
            {
                if (Hand != null && Hand.Lifted == _token) Hand.Lifted = null;
                Hand = null;
            }

            internal void Animate(float dt, Vector3 shake, Quaternion wobble)
            {
                if (Target == null) return;
                if (Target.parent != _parent)
                {
                    Unclaim();
                    Target = null;
                    return;
                }
                Tween.Stop(Target.GetInstanceID());
                if (_returning)
                {
                    _progress = Mathf.Min(1f, _progress + dt / ReturnSeconds);
                    float k = Mathf.SmoothStep(0f, 1f, _progress);
                    Vector3 rest = _parent != null ? _parent.TransformPoint(_restPosition) : _restPosition;
                    Quaternion restRotation = _parent != null ? _parent.rotation * _restRotation : _restRotation;
                    Target.SetPositionAndRotation(Vector3.Lerp(_from, rest, k), Quaternion.Slerp(_fromRotation, restRotation, k));
                    if (_progress < 1f) return;
                    Target.localPosition = _restPosition;
                    Target.localRotation = _restRotation;
                    VrSounds.Play(PlacedSound, MixerGroup.TableObjectsSFX, Target.position, 0.45f);
                    Target = null;
                    _returning = false;
                    return;
                }
                if (Hand == null)
                {
                    Drop();
                    return;
                }
                Target.SetPositionAndRotation(Hand.Position + Hand.Rotation * _localPosition + shake, wobble * (Hand.Rotation * _localRotation));
            }

            internal void Clear(bool restore)
            {
                Unclaim();
                if (restore && Target != null && Target.parent == _parent)
                {
                    Tween.Stop(Target.GetInstanceID());
                    Target.localPosition = _restPosition;
                    Target.localRotation = _restRotation;
                }
                Target = null;
                _returning = false;
            }
        }

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo CompleteSlotField = typeof(BuildTotemSequencer).GetField("completeTotemSlot", Members);
        private static readonly FieldInfo TopParentField = typeof(Totem).GetField("topPieceParent", Members);

        private const string PickupSound = "stone_object_up";
        private const string PlacedSound = "stone_object_hit";
        private const string ActivateSound = "totem_activate";

        private const float TurnDegrees = 360f;
        private const float ClickDegrees = 30f;
        private const float MaxStepDegrees = 45f;
        private const float ThreadFraction = 0.3f;
        private const float MinThreadMeters = 0.01f;
        private const float CaptureFraction = 0.5f;
        private const float MinCaptureMeters = 0.015f;
        private const float UprightDot = 0.766f;
        private const float SlipMeters = 0.12f;
        private const float PullOffMeters = 0.015f;
        private const float SeparateSeconds = 0.4f;
        private const float ReturnSeconds = 0.25f;
        private const float ConfirmSeconds = 0.7f;
        private const float BuzzSeconds = 0.35f;
        private const float NearMeters = 0.06f;
        private const float LooseYawDegrees = -40f;
        private const float GapMeters = 0.035f;
        private const float PieceReachMeters = 0.07f;
        private const float HeadBiasMeters = 0.04f;
        private const float LookbackSeconds = 0.2f;

        private readonly ManualLogSource _logger;
        private readonly TotemPieces _pieces;
        private readonly RendererHider _leaving = new RendererHider();
        private readonly RendererHider _finishedHidden = new RendererHider();
        private HandProp _finishedProp;
        private SelectableItemSlot _finishedSlot;

        private BuildTotemSequencer _sequencer;
        private float _nextFind;
        private SelectableItemSlot _slot;
        private Totem _totem;
        private Totem _finished;
        private Transform _root;
        private Transform _head;
        private Renderer[] _headRenderers = new Renderer[0];
        private Renderer[] _baseRenderers = new Renderer[0];
        private Vector3 _headLocalPosition;
        private Quaternion _headLocalRotation;
        private Vector3 _seatPoint;
        private Quaternion _seatRotation;
        private Vector3 _axis;
        private Vector3 _headAxis;
        private float _thread;
        private float _capture;
        private Vector3 _loosePosition;
        private Quaternion _looseRotation;
        private HeadState _state;
        private VrHand _headHand;
        private Vector3 _headGrabPosition;
        private Quaternion _headGrabRotation;
        private Vector3 _glideFrom;
        private Quaternion _glideFromRotation;
        private float _glide = 1f;
        private float _glideSeconds = ReturnSeconds;
        private float _twist;
        private float _required = TurnDegrees;
        private Quaternion _previous = Quaternion.identity;
        private int _clicks;
        private float _seatedAt = -1f;
        private bool _confirmed;
        private readonly Lift _base = new Lift();
        private Transform _leftHover;
        private Transform _rightHover;
        private float _leftHeadSeen = -10f;
        private float _rightHeadSeen = -10f;

        private readonly Lift _carried = new Lift();
        private Totem _battleTotem;
        private Animator _battleAnimator;
        private bool _wasTrigger;
        private float _lastNormalized;
        private float _buzzStart = -10f;
        private float _buzzUntil = -10f;
        private Totem _leftBattleHover;
        private Totem _rightBattleHover;

        internal TotemHands(ManualLogSource logger)
        {
            _logger = logger;
            if (CompleteSlotField == null || TopParentField == null)
                _logger.LogWarning("Totem screwing unavailable: BuildTotemSequencer or Totem internals not found; the Woodcarver's totem is confirmed by touch instead.");
            else _pieces = new TotemPieces(logger);
        }

        internal bool Holds(Component target)
        {
            if (target == null) return false;
            if (_pieces != null && _pieces.Holds(target)) return true;
            Transform t = target.transform;
            if (_totem != null && _root != null && !_confirmed && (t.IsChildOf(_root) || (_slot != null && t == _slot.transform))) return true;
            return _carried.Active && t.IsChildOf(_carried.Target);
        }

        internal void Release()
        {
            Abandon();
            ReleaseBattle();
            if (_pieces != null) _pieces.Release();
            DropFinished();
            _leaving.Forget();
        }

        private void DropFinished()
        {
            if (_finishedProp != null) _finishedProp.Destroy();
            _finishedProp = null;
            _finishedSlot = null;
            _finishedHidden.Forget();
        }

        private void UpdateFinished(SelectableItemSlot slot)
        {
            _leaving.Keep();
            if (_finishedProp == null) return;
            if (slot == null || !_finishedProp.Alive)
            {
                DropFinished();
                return;
            }
            if (_finishedHidden.Any)
            {
                _finishedHidden.Keep();
                return;
            }
            if (_finishedSlot != null && _finishedSlot.Item != null && HandReach.Live(_finishedSlot)) return;
            _finishedProp.SetVisible(true);
            if (_finishedSlot != null && _finishedSlot.Item != null) _finishedHidden.Hide(_finishedSlot.Item);
            _logger.LogInfo("The finished totem stays on the stump instead of flying off.");
        }

        internal void ReleaseBattle()
        {
            _carried.Clear(true);
            _battleTotem = null;
            _battleAnimator = null;
            _wasTrigger = false;
            _leftBattleHover = null;
            _rightBattleHover = null;
        }

        internal void TickBuild(CardGrabber grabber, VrHand left, VrHand right, VrHand main, bool shown, bool input, float scale)
        {
            if (CompleteSlotField == null || TopParentField == null) return;
            SelectableItemSlot slot = shown ? FindBuildSlot() : null;
            if (_pieces != null) _pieces.Tick(slot != null ? _sequencer : null, grabber, left, right, input, scale);
            UpdateFinished(slot);
            Totem totem = slot != null && HandReach.Live(slot) ? slot.Item as Totem : null;
            if (totem != null && totem == _finished) totem = null;
            if (totem != _totem)
            {
                if (_totem != null && !_confirmed && slot != null && slot.Item == _totem && !HandReach.Live(slot))
                {
                    _leaving.Hide(_totem);
                    _logger.LogInfo("Totem taken apart; the assembled one disappears instead of flying off.");
                }
                Abandon();
                if (totem != null) Begin(grabber, totem, slot, main, scale);
            }
            if (_totem == null || _root == null || _head == null)
            {
                Abandon();
                return;
            }

            float dt = Time.unscaledDeltaTime;
            if (_state == HeadState.Seated)
            {
                _base.Animate(dt, Vector3.zero, Quaternion.identity);
                Confirm();
                return;
            }
            if (_headHand != null && (!input || !_headHand.Ready || !_headHand.Grip.Held)) ReleaseHead();
            if (_base.Holding && (!input || !_base.Hand.Ready || !_base.Hand.Grip.Held)) _base.Drop();
            Offer(grabber, left, input, scale, ref _leftHover, ref _leftHeadSeen);
            Offer(grabber, right, input, scale, ref _rightHover, ref _rightHeadSeen);
            if (_base.Holding) _base.Hand.SetHoldPose(HandHold.Wrap);
            _base.Animate(dt, Vector3.zero, Quaternion.identity);
            UpdateHead(dt, scale);
            if (_state == HeadState.Loose && _headHand == null && !_base.Holding && _glide >= 1f)
                VrTips.Show("totem.screw", _loosePosition + Vector3.up * (0.05f * scale), "Pick up the head and screw it onto the base: turn it clockwise until it's tight.", 65);
        }

        private SelectableItemSlot FindBuildSlot()
        {
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            if (flow == null || flow.CurrentGameState != GameState.SpecialCardSequence)
            {
                _sequencer = null;
                return null;
            }
            if (_sequencer == null && Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + 0.5f;
                _sequencer = Object.FindObjectOfType<BuildTotemSequencer>();
            }
            if (_sequencer == null || !_sequencer.gameObject.activeInHierarchy) return null;
            return CompleteSlotField.GetValue(_sequencer) as SelectableItemSlot;
        }

        private void Begin(CardGrabber grabber, Totem totem, SelectableItemSlot slot, VrHand main, float scale)
        {
            Transform head = TopParentField.GetValue(totem) as Transform;
            if (head == null || head == totem.transform)
            {
                _finished = totem;
                _logger.LogWarning("Totem screwing skipped: the finished totem has no separate head; touch it to confirm it.");
                return;
            }
            Animator animator = totem.Anim;
            if (animator != null && animator.isActiveAndEnabled)
            {
                animator.Play("assemble", 0, 1f);
                animator.Update(0f);
            }
            _totem = totem;
            _slot = slot;
            _root = totem.transform;
            _head = head;
            _confirmed = false;
            _seatedAt = -1f;
            _headLocalPosition = head.localPosition;
            _headLocalRotation = head.localRotation;
            _seatPoint = _root.InverseTransformPoint(head.position);
            _seatRotation = Quaternion.Inverse(_root.rotation) * head.rotation;
            _axis = _root.InverseTransformDirection(Vector3.up).normalized;
            _headAxis = Quaternion.Inverse(_seatRotation) * _axis;
            CacheRenderers();

            Bounds headBounds, baseBounds;
            bool hasHead = BoundsOf(_headRenderers, out headBounds);
            bool hasBase = BoundsOf(_baseRenderers, out baseBounds);
            float height = hasHead ? headBounds.size.y : 0.05f * scale;
            float width = hasHead ? Mathf.Max(headBounds.size.x, headBounds.size.z) : 0.05f * scale;
            _thread = Mathf.Max(height * ThreadFraction, MinThreadMeters * scale);
            _capture = Mathf.Max(width * CaptureFraction, MinCaptureMeters * scale);
            PlaceLoose(grabber, main, headBounds, hasHead, baseBounds, hasBase, scale);

            HandProp carried = _pieces != null ? _pieces.HeadProp : null;
            VrHand carrier = carried != null && carried.Root != null ? carried.Hand : null;
            string aligned = carrier != null ? AlignHead(carried.Root) : null;
            if (_pieces != null) _pieces.Assembled();

            _state = HeadState.Loose;
            _headHand = null;
            _twist = 0f;
            _required = TurnDegrees;
            _clicks = 0;
            if (carrier != null)
            {
                TakeHead(carrier);
                _logger.LogInfo("Totem head stays in the " + carrier.Name + " hand (lined up by " + aligned + ").");
            }
            else StartGlide(SeparateSeconds);

            float meters = 1f / Mathf.Max(scale, 0.0001f);
            _logger.LogInfo("Totem screwing: the finished totem came apart (head " + Path(head) + ", " + _headRenderers.Length + " head and " + _baseRenderers.Length +
                " base renderers, head " + (height * meters).ToString("0.000") + " m tall and " + (width * meters).ToString("0.000") + " m wide, base " +
                (hasBase ? (baseBounds.size.y * meters).ToString("0.000") + " m tall" : "unmeasured") + ", thread " + (_thread * meters).ToString("0.000") +
                " m, axis " + _root.TransformDirection(_axis).ToString("F2") + ", animator " + (animator != null) + ", loose head " +
                (Vector3.Distance(_loosePosition, head.position) * meters).ToString("0.00") + " m from its seat). Screw it on clockwise.");
        }

        private void CacheRenderers()
        {
            var head = new List<Renderer>();
            var body = new List<Renderer>();
            Renderer[] all = _root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Renderer renderer = all[i];
                if (renderer == null || renderer is ParticleSystemRenderer || GrabGlow.IsShell(renderer)) continue;
                if (renderer.transform.IsChildOf(_head)) head.Add(renderer);
                else body.Add(renderer);
            }
            _headRenderers = head.ToArray();
            _baseRenderers = body.ToArray();
        }

        private static bool BoundsOf(Renderer[] renderers, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else bounds.Encapsulate(renderer.bounds);
            }
            return any;
        }

        private void PlaceLoose(CardGrabber grabber, VrHand main, Bounds headBounds, bool hasHead, Bounds baseBounds, bool hasBase, float scale)
        {
            Vector3 center = hasBase ? baseBounds.center : _root.position;
            float surface = hasBase ? baseBounds.min.y : _root.position.y;
            Vector3 toward = Vector3.back;
            Vector3 eye;
            Quaternion eyeRotation;
            float eyeScale;
            if (grabber.Renderer != null && grabber.Renderer.TryGetHeadPose(out eye, out eyeRotation, out eyeScale)) toward = eye - center;
            toward.y = 0f;
            if (toward.sqrMagnitude < 1e-8f) toward = Vector3.back;
            toward.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, -toward);
            if (main != null && main.IsLeft) side = -side;
            float baseHalf = hasBase ? 0.5f * Mathf.Max(baseBounds.size.x, baseBounds.size.z) : 0.03f * scale;
            float headHalf = hasHead ? 0.5f * Mathf.Max(headBounds.size.x, headBounds.size.z) : 0.03f * scale;
            Vector3 spot = center + side * (baseHalf + headHalf + GapMeters * scale) + toward * (baseHalf * 0.5f);
            spot.y = surface;
            float found;
            if (SurfaceBelow(spot, surface, scale, out found)) spot.y = found;
            float pivotAbove = hasHead ? _head.position.y - headBounds.min.y : 0f;
            _loosePosition = new Vector3(spot.x, spot.y + pivotAbove, spot.z);
            _looseRotation = Quaternion.AngleAxis(LooseYawDegrees, Vector3.up) * _head.rotation;
        }

        private bool SurfaceBelow(Vector3 spot, float surface, float scale, out float y)
        {
            y = surface;
            Vector3 from = new Vector3(spot.x, surface + 0.3f * scale, spot.z);
            RaycastHit[] hits = Physics.RaycastAll(from, Vector3.down, 0.6f * scale, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                Transform t = hits[i].collider.transform;
                if (t.IsChildOf(_root) || (_slot != null && t.IsChildOf(_slot.transform))) continue;
                float height = hits[i].point.y;
                if (height > surface + 0.03f * scale || height < surface - 0.15f * scale) continue;
                if (hits[i].distance >= best) continue;
                best = hits[i].distance;
                y = height;
                found = true;
            }
            return found;
        }

        private void Offer(CardGrabber grabber, VrHand hand, bool input, float scale, ref Transform hover, ref float headSeen)
        {
            Transform target = null;
            if (input && hand.Ready && !hand.Busy)
            {
                float reach = Mathf.Max(grabber.ReachMeters, PieceReachMeters) * scale;
                float reachSqr = reach * reach;
                float head = _headHand != hand ? PieceSqrDistance(_headRenderers, hand) : float.MaxValue;
                float body = !(_base.Holding && _base.Hand == hand) ? PieceSqrDistance(_baseRenderers, hand) : float.MaxValue;
                if (head <= reachSqr && Mathf.Sqrt(head) <= Mathf.Sqrt(body) + HeadBiasMeters * scale) target = _head;
                else if (body <= reachSqr) target = _root;
                if (target == _head) headSeen = Time.unscaledTime;
                else if (_headHand != hand && hand.GripEdge == 1 && Time.unscaledTime - headSeen <= LookbackSeconds) target = _head;
            }
            if (target != null)
            {
                hand.Highlight();
                Transform glow = target == _head ? _head : _totem.Bottom;
                GrabGlow.Mark(glow != null ? glow : target);
                if (target != hover) hand.Play(Feel.Tick, 0.4f);
            }
            hover = target;
            if (target == null || hand.GripEdge != 1) return;
            hand.GripEdge = 0;
            hover = null;
            if (target == _head) GrabHead(hand);
            else GrabBase(hand);
        }

        private static float PieceSqrDistance(Renderer[] renderers, VrHand hand)
        {
            Vector3 tip = CardGrabber.CardPoint(hand);
            Vector3 palm = hand.Position;
            float best = float.MaxValue;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                best = Mathf.Min(best, Mathf.Min(OrientedSqrDistance(renderer, tip), OrientedSqrDistance(renderer, palm)));
            }
            return best;
        }

        internal static float OrientedSqrDistance(Renderer renderer, Vector3 point)
        {
            Transform frame;
            Bounds local;
            if (!LocalBounds(renderer, out frame, out local)) return renderer.bounds.SqrDistance(point);
            Vector3 p = frame.InverseTransformPoint(point) - local.center;
            Vector3 half = local.extents;
            Vector3 clamped = new Vector3(Mathf.Clamp(p.x, -half.x, half.x), Mathf.Clamp(p.y, -half.y, half.y), Mathf.Clamp(p.z, -half.z, half.z));
            return (frame.TransformPoint(local.center + clamped) - point).sqrMagnitude;
        }

        private static bool LocalBounds(Renderer renderer, out Transform frame, out Bounds local)
        {
            frame = renderer.transform;
            local = new Bounds();
            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null)
            {
                if (skinned.rootBone != null) frame = skinned.rootBone;
                local = skinned.localBounds;
                return local.size.sqrMagnitude > 0f;
            }
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return false;
            local = filter.sharedMesh.bounds;
            return local.size.sqrMagnitude > 0f;
        }

        private static float TotemSqrDistance(Totem totem, VrHand hand)
        {
            Renderer[] renderers = totem.GetComponentsInChildren<Renderer>();
            Vector3 tip = CardGrabber.CardPoint(hand);
            float best = float.MaxValue;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer is ParticleSystemRenderer || GrabGlow.IsShell(renderer)) continue;
                best = Mathf.Min(best, Mathf.Min(OrientedSqrDistance(renderer, tip), OrientedSqrDistance(renderer, hand.Position)));
            }
            return best;
        }

        private void GrabHead(VrHand hand)
        {
            VrHand previous = _headHand;
            if (previous != null && previous != hand && previous.Lifted == (Component)_totem) previous.Lifted = null;
            bool handover = previous != null && previous != hand;
            _headHand = hand;
            Quaternion inverse = Quaternion.Inverse(hand.Rotation);
            _headGrabPosition = inverse * (_head.position - hand.Position);
            _headGrabRotation = inverse * _head.rotation;
            hand.Lifted = _totem;
            if (_state == HeadState.Threaded)
            {
                _previous = Relative(hand);
                hand.Play(Feel.Tap, 0.5f);
                VrLog.Chatter(_logger, "Totem head regripped with the " + hand.Name + " hand (" + _twist.ToString("0") + " of " + _required.ToString("0") + " degrees turned).");
                return;
            }
            _state = HeadState.Held;
            _glide = 1f;
            hand.Play(Feel.Tick);
            if (handover)
            {
                previous.Play(Feel.Tick, 0.4f);
                VrLog.Chatter(_logger, "Totem head passed from the " + previous.Name + " hand to the " + hand.Name + " hand.");
                return;
            }
            VrSounds.Play(PickupSound, MixerGroup.TableObjectsSFX, _head.position, 0.6f);
            _logger.LogInfo("Totem head picked up with the " + hand.Name + " hand.");
        }

        private void TakeHead(VrHand hand)
        {
            _headHand = hand;
            Quaternion inverse = Quaternion.Inverse(hand.Rotation);
            _headGrabPosition = inverse * (_head.position - hand.Position);
            _headGrabRotation = inverse * _head.rotation;
            hand.Lifted = _totem;
            hand.SetHoldPose(HandHold.Wrap);
            _state = HeadState.Held;
            _glide = 1f;
        }

        private string AlignHead(Transform prop)
        {
            MeshFilter[] parts = prop.GetComponentsInChildren<MeshFilter>();
            for (int i = 0; i < _headRenderers.Length; i++)
            {
                Renderer renderer = _headRenderers[i];
                if (renderer == null) continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                for (int j = 0; j < parts.Length; j++)
                {
                    if (parts[j] == null || parts[j].sharedMesh != filter.sharedMesh) continue;
                    Transform part = parts[j].transform;
                    Quaternion relative = Quaternion.Inverse(_head.rotation) * renderer.transform.rotation;
                    Vector3 offset = Quaternion.Inverse(_head.rotation) * (renderer.transform.position - _head.position);
                    Quaternion rotation = part.rotation * Quaternion.Inverse(relative);
                    _head.SetPositionAndRotation(part.position - rotation * offset, rotation);
                    return "matching mesh " + filter.sharedMesh.name;
                }
            }
            Bounds headBounds, propBounds;
            if (BoundsOf(_headRenderers, out headBounds) && HandReach.RendererBounds(prop, out propBounds))
            {
                _head.position += propBounds.center - headBounds.center;
                return "bounds centre";
            }
            return "nothing";
        }

        private void GrabBase(VrHand hand)
        {
            VrHand previous = _base.Holding ? _base.Hand : null;
            _base.Start(hand, _root, _totem);
            hand.Play(Feel.Tick);
            if (previous != null && previous != hand)
            {
                previous.Play(Feel.Tick, 0.4f);
                VrLog.Chatter(_logger, "Totem base passed from the " + previous.Name + " hand to the " + hand.Name + " hand.");
                return;
            }
            VrSounds.Play(PickupSound, MixerGroup.TableObjectsSFX, _root.position, 0.6f);
            _logger.LogInfo("Totem base picked up with the " + hand.Name + " hand.");
        }

        private void ReleaseHead()
        {
            VrHand hand = _headHand;
            _headHand = null;
            if (hand != null && hand.Lifted == (Component)_totem) hand.Lifted = null;
            if (_state != HeadState.Held) return;
            _state = HeadState.Loose;
            StartGlide(ReturnSeconds);
            if (hand != null) hand.Play(Feel.Tick, 0.5f);
        }

        private void StartGlide(float seconds)
        {
            _glideFrom = _head.position;
            _glideFromRotation = _head.rotation;
            _glide = 0f;
            _glideSeconds = Mathf.Max(seconds, 0.01f);
        }

        private Quaternion Relative(VrHand hand)
        {
            return Quaternion.Inverse(_root.rotation) * hand.Rotation;
        }

        private Vector3 WorldAxis { get { return _root.TransformDirection(_axis).normalized; } }

        private Vector3 SeatPosition { get { return _root.TransformPoint(_seatPoint); } }

        private Vector3 EntryPosition { get { return SeatPosition + WorldAxis * _thread; } }

        private float EngageTop { get { return _thread + _capture * 0.5f; } }

        private float Progress { get { return Mathf.Clamp01(_twist / Mathf.Max(_required, 1f)); } }

        private void UpdateHead(float dt, float scale)
        {
            Tween.Stop(_head.GetInstanceID());
            if (_state == HeadState.Loose)
            {
                Vector3 position = _loosePosition;
                Quaternion rotation = _looseRotation;
                if (_glide < 1f)
                {
                    _glide = Mathf.Min(1f, _glide + dt / _glideSeconds);
                    float k = Mathf.SmoothStep(0f, 1f, _glide);
                    position = Vector3.Lerp(_glideFrom, _loosePosition, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * _thread);
                    rotation = Quaternion.Slerp(_glideFromRotation, _looseRotation, k);
                    if (_glide >= 1f) VrSounds.Play(PlacedSound, MixerGroup.TableObjectsSFX, _loosePosition, 0.4f);
                }
                _head.SetPositionAndRotation(position, rotation);
                return;
            }
            if (_state == HeadState.Held)
            {
                VrHand hand = _headHand;
                Vector3 position = hand.Position + hand.Rotation * _headGrabPosition;
                Quaternion rotation = hand.Rotation * _headGrabRotation;
                hand.SetHoldPose(HandHold.Wrap);
                if (!CanEngage(position, rotation))
                {
                    _head.SetPositionAndRotation(position, rotation);
                    return;
                }
                Engage(hand, rotation);
            }
            if (_state == HeadState.Threaded && _headHand != null) Turn(_headHand, dt, scale);
            if (_state == HeadState.Threaded) PoseThreaded();
        }

        private bool CanEngage(Vector3 position, Quaternion rotation)
        {
            Vector3 axis = WorldAxis;
            Vector3 offset = position - EntryPosition;
            float along = Vector3.Dot(offset, axis);
            Vector3 across = offset - axis * along;
            if (across.sqrMagnitude > _capture * _capture) return false;
            if (along < -_thread * 0.6f || along > EngageTop) return false;
            return Vector3.Dot(rotation * _headAxis, axis) >= UprightDot;
        }

        private void Engage(VrHand hand, Quaternion rotation)
        {
            Quaternion turned = Quaternion.Inverse(_root.rotation) * rotation * Quaternion.Inverse(_seatRotation);
            float yaw = Twist(turned, _axis);
            _required = TurnDegrees - yaw;
            _twist = 0f;
            _clicks = 0;
            _previous = Relative(hand);
            _state = HeadState.Threaded;
            hand.Play(Feel.Tap, 0.8f);
            VrSounds.Play(PlacedSound, MixerGroup.TableObjectsSFX, EntryPosition, 0.3f);
            _logger.LogInfo("Totem head threaded onto the base with the " + hand.Name + " hand; " + _required.ToString("0") + " degrees clockwise to seat it.");
        }

        private void Turn(VrHand hand, float dt, float scale)
        {
            Quaternion now = Relative(hand);
            float step = Mathf.Clamp(Twist(now * Quaternion.Inverse(_previous), _axis), -MaxStepDegrees, MaxStepDegrees);
            _previous = now;
            Vector3 held = hand.Position + hand.Rotation * _headGrabPosition;
            if (_twist <= 0.01f && step <= 0f && Vector3.Dot(held - EntryPosition, WorldAxis) > EngageTop + PullOffMeters * scale)
            {
                _state = HeadState.Held;
                hand.Play(Feel.Tick, 0.6f);
                VrSounds.Play(PickupSound, MixerGroup.TableObjectsSFX, _head.position, 0.4f);
                VrLog.Chatter(_logger, "Totem head lifted off the thread.");
                return;
            }
            if ((held - ThreadedPosition()).sqrMagnitude > SlipMeters * SlipMeters * scale * scale)
            {
                _headHand = null;
                if (hand.Lifted == (Component)_totem) hand.Lifted = null;
                hand.Play(Feel.Tick, 0.5f);
                VrLog.Chatter(_logger, "The " + hand.Name + " hand slipped off the totem head; it stays on the thread.");
                return;
            }
            _twist = Mathf.Max(0f, _twist + step);
            float progress = Progress;
            int clicks = Mathf.FloorToInt(_twist / ClickDegrees);
            if (clicks != _clicks)
            {
                hand.Play(Feel.Tick, 0.45f + 0.55f * progress);
                if (_base.Holding) _base.Hand.Play(Feel.Tick, 0.3f + 0.4f * progress);
                _clicks = clicks;
            }
            float speed = Mathf.Abs(step) / Mathf.Max(dt, 0.0001f);
            hand.Grit(Mathf.Clamp01(speed / 360f) * (0.12f + 0.3f * progress));
            if (_twist >= _required) Seat();
        }

        private Vector3 ThreadedPosition()
        {
            return SeatPosition + WorldAxis * (_thread * (1f - Progress));
        }

        private void PoseThreaded()
        {
            Vector3 position = ThreadedPosition();
            Quaternion rotation = _root.rotation * Quaternion.AngleAxis(_twist - _required, _axis) * _seatRotation;
            _head.SetPositionAndRotation(position, rotation);
            VrHand hand = _headHand;
            if (hand == null) return;
            hand.Pin(hand.Position + hand.Rotation * _headGrabPosition, hand.Rotation * _headGrabRotation, position, rotation);
            hand.SetHoldPose(HandHold.Wrap);
        }

        internal static float Twist(Quaternion q, Vector3 axis)
        {
            if (q.w < 0f)
            {
                q.x = -q.x;
                q.y = -q.y;
                q.z = -q.z;
                q.w = -q.w;
            }
            float along = q.x * axis.x + q.y * axis.y + q.z * axis.z;
            float angle = 2f * Mathf.Atan2(along, q.w) * Mathf.Rad2Deg;
            if (angle > 180f) angle -= 360f;
            else if (angle < -180f) angle += 360f;
            return angle;
        }

        private void Seat()
        {
            _state = HeadState.Seated;
            _seatedAt = Time.unscaledTime;
            _head.localPosition = _headLocalPosition;
            _head.localRotation = _headLocalRotation;
            VrHand hand = _headHand;
            _headHand = null;
            if (hand != null)
            {
                hand.Play(Feel.Thunk);
                if (hand.Lifted == (Component)_totem) hand.Lifted = null;
            }
            if (_base.Holding)
            {
                _base.Hand.Play(Feel.Thunk, 0.8f);
                _base.Drop();
            }
            VrSounds.Play(PlacedSound, MixerGroup.TableObjectsSFX, _head.position, 1f);
            VrSounds.Play(ActivateSound, MixerGroup.TableObjectsSFX, _root.position, 0.65f);
            CompositeTotemPiece[] pieces = _root.GetComponentsInChildren<CompositeTotemPiece>();
            for (int i = 0; i < pieces.Length; i++) pieces[i].PulseEmission();
            VrTips.Done("totem.screw");
            _logger.LogInfo("Totem screwed tight; handing it to the game.");
        }

        private void Confirm()
        {
            if (_confirmed || _base.Active || Time.unscaledTime - _seatedAt < ConfirmSeconds) return;
            _confirmed = true;
            _finished = _totem;
            if (!HandReach.Live(_slot))
            {
                _logger.LogWarning("Totem screwed tight, but the game no longer accepts it.");
                return;
            }
            DropFinished();
            _finishedProp = HandProp.Copy(_root, "finished totem");
            if (_finishedProp != null) _finishedProp.SetVisible(false);
            _finishedSlot = _slot;
            _slot.CursorSelectStart();
            if (_slot != null) _slot.CursorSelectEnd();
        }

        private void Abandon()
        {
            if (ReferenceEquals(_totem, null) && ReferenceEquals(_root, null)) return;
            VrHand hand = _headHand;
            _headHand = null;
            if (hand != null && hand.Lifted == (Component)_totem) hand.Lifted = null;
            _base.Clear(true);
            if (_head != null && _state != HeadState.Seated)
            {
                Tween.Stop(_head.GetInstanceID());
                _head.localPosition = _headLocalPosition;
                _head.localRotation = _headLocalRotation;
            }
            _totem = null;
            _root = null;
            _head = null;
            _slot = null;
            _headRenderers = new Renderer[0];
            _baseRenderers = new Renderer[0];
            _leftHover = null;
            _rightHover = null;
            _leftHeadSeen = -10f;
            _rightHeadSeen = -10f;
            _state = HeadState.Loose;
            _confirmed = false;
        }

        internal void TickBattle(CardGrabber grabber, VrHand left, VrHand right, bool inBattle, bool input, float scale)
        {
            Totem totem = inBattle ? BattleTotem() : null;
            if (totem != _battleTotem)
            {
                ReleaseBattle();
                _battleTotem = totem;
                _battleAnimator = totem != null ? totem.Anim : null;
            }
            if (totem == null) return;
            if (_carried.Holding && (!input || !_carried.Hand.Ready || !_carried.Hand.Grip.Held)) _carried.Drop();
            WatchTrigger(totem, left, right, scale);
            OfferBattle(grabber, left, totem, input, scale, ref _leftBattleHover);
            OfferBattle(grabber, right, totem, input, scale, ref _rightBattleHover);
            Vector3 shake = Vector3.zero;
            Quaternion wobble = Quaternion.identity;
            if (_carried.Holding)
            {
                VrHand hand = _carried.Hand;
                hand.SetHoldPose(HandHold.Wrap);
                float t = Time.unscaledTime - _buzzStart;
                if (Time.unscaledTime < _buzzUntil)
                {
                    float fade = 1f - t / BuzzSeconds;
                    hand.Rumble(0.55f * fade);
                    shake = hand.Rotation * new Vector3(Mathf.Sin(t * 95f), Mathf.Sin(t * 77f + 1f), Mathf.Sin(t * 83f + 2f)) * (0.003f * scale * fade);
                    wobble = Quaternion.Euler(Mathf.Sin(t * 70f) * 5f * fade, 0f, Mathf.Sin(t * 61f + 1f) * 5f * fade);
                }
            }
            _carried.Animate(Time.unscaledDeltaTime, shake, wobble);
            if (!_carried.Active && input && (left.Ready || right.Ready))
            {
                Bounds bounds;
                if (HandReach.RendererBounds(totem, out bounds))
                    VrTips.Show("totem.lift", new Vector3(bounds.center.x, bounds.max.y, bounds.center.z), "Grip your totem to pick it up. It buzzes when it fires.", 12);
            }
        }

        private static Totem BattleTotem()
        {
            Part1ItemsManager items = Part1ItemsManager.Instance;
            if (items == null) return null;
            List<Totem> totems = items.Totems;
            if (totems.Count == 0) return null;
            Totem totem = totems[0];
            return totem != null && totem.gameObject.activeInHierarchy ? totem : null;
        }

        private void WatchTrigger(Totem totem, VrHand left, VrHand right, float scale)
        {
            Animator animator = _battleAnimator;
            if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) return;
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            bool trigger = info.IsName("trigger");
            bool fresh = trigger && (!_wasTrigger || info.normalizedTime + 0.05f < _lastNormalized);
            _wasTrigger = trigger;
            _lastNormalized = info.normalizedTime;
            if (!fresh) return;
            _buzzStart = Time.unscaledTime;
            if (_carried.Holding)
            {
                _buzzUntil = Time.unscaledTime + BuzzSeconds;
                _carried.Hand.Play(Feel.Thunk);
                _logger.LogInfo("Your totem fired while you held it in the " + _carried.Hand.Name + " hand.");
                return;
            }
            Bounds bounds;
            if (!HandReach.RendererBounds(totem, out bounds)) return;
            float near = NearMeters * scale;
            if (left.Tracked && bounds.SqrDistance(CardGrabber.CardPoint(left)) <= near * near) left.Play(Feel.Tap, 0.8f);
            if (right.Tracked && bounds.SqrDistance(CardGrabber.CardPoint(right)) <= near * near) right.Play(Feel.Tap, 0.8f);
        }

        private void OfferBattle(CardGrabber grabber, VrHand hand, Totem totem, bool input, float scale, ref Totem hover)
        {
            Totem target = null;
            if (input && hand.Ready && !hand.Busy && hand.HoverCard == null && !(_carried.Holding && _carried.Hand == hand))
            {
                float reach = grabber.ReachMeters * scale;
                if (TotemSqrDistance(totem, hand) <= reach * reach) target = totem;
            }
            if (target != null)
            {
                hand.Highlight();
                GrabGlow.Mark(target);
                if (target != hover) hand.Play(Feel.Tick, 0.4f);
            }
            hover = target;
            if (target == null || hand.GripEdge != 1) return;
            hand.GripEdge = 0;
            hover = null;
            VrHand previous = _carried.Holding ? _carried.Hand : null;
            _carried.Start(hand, totem.transform, totem);
            hand.Play(Feel.Tick);
            if (previous != null && previous != hand)
            {
                previous.Play(Feel.Tick, 0.4f);
                VrLog.Chatter(_logger, "Totem passed from the " + previous.Name + " hand to the " + hand.Name + " hand.");
                return;
            }
            VrSounds.Play(PickupSound, MixerGroup.TableObjectsSFX, totem.transform.position, 0.6f);
            VrTips.Done("totem.lift");
            _logger.LogInfo("Picked up your totem with the " + hand.Name + " hand.");
        }

        private static string Path(Transform t)
        {
            string path = t.name;
            Transform parent = t.parent;
            for (int i = 0; parent != null && i < 4; i++)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
