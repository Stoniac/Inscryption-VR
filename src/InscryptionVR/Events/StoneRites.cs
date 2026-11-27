using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class StoneRites
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo MergeStoneField = typeof(CardMergeSequencer).GetField("confirmStone", Members);
        private static readonly FieldInfo MergeSacrificeField = typeof(CardMergeSequencer).GetField("sacrificeSlot", Members);
        private static readonly FieldInfo BoostStoneField = typeof(CardStatBoostSequencer).GetField("confirmStone", Members);
        private static readonly FieldInfo BoostLightField = typeof(CardStatBoostSequencer).GetField("campfireLight", Members);
        private static readonly FieldInfo BoostRetrieveField = typeof(CardStatBoostSequencer).GetField("retrieveCardInteractable", Members);
        private static readonly FieldInfo StoneAnimField = typeof(ConfirmStoneButton).GetField("anim", Members);
        private static readonly FieldInfo StoneCircleField = typeof(CardMergeSequencer).GetField("stoneCircleAnim", Members);

        private const float KnifeLengthMeters = 0.42f;
        private const float HandleFraction = 0.46f;
        private const float MinGripFraction = 0.05f;
        private const float MaxGripFraction = 0.42f;
        private const float GrabReachMeters = 0.09f;
        private const float RestGapMeters = 0.025f;
        private const float StoneEdgeMarginMeters = 0.02f;
        private const float StoneSampleMeters = 0.015f;
        private const float RestLiftMeters = 0.012f;
        private const float AwayShiftMeters = 0.0508f;
        private const float BladeHalfWidthMeters = 0.015f;
        private const float AltarSettleSeconds = 0.3f;
        private const float AltarStillMeters = 0.001f;
        private const float PickupSeconds = 0.12f;
        private const float ReturnSeconds = 0.3f;
        private const float PlungeMetersPerSecond = 0.35f;
        private const float PlungeSteepness = 0.6f;
        private const float CardMarginMeters = 0.015f;
        private const float EmbedMeters = 0.02f;
        private const float StuckSeconds = 6f;
        private const float StuckAfterReleaseSeconds = 1.5f;
        private const float CardMovedMeters = 0.02f;

        private const float FireMeters = 0.2f;
        private const float FireHoldSeconds = 1f;
        private const float LitIntensity = 0.05f;

        private static readonly HashSet<ConfirmStoneButton> HiddenStones = new HashSet<ConfirmStoneButton>();

        private enum KnifeState { None, Resting, Held, Returning, Stuck }

        private sealed class Grip
        {
            internal VrHand Hand;
            internal float Fraction;
        }

        private sealed class StoneHide
        {
            internal ConfirmStoneButton Stone;
            internal readonly List<Renderer> Disabled = new List<Renderer>();
            internal Renderer[] Renderers = new Renderer[0];
            internal float NextScan;
        }

        private readonly ManualLogSource _logger;
        private readonly StoneHide _mergeHide = new StoneHide();
        private readonly StoneHide _boostHide = new StoneHide();
        private CardMergeSequencer _merger;
        private CardStatBoostSequencer _boost;
        private float _nextFind;

        private GameObject _knife;
        private Renderer[] _knifeRenderers = new Renderer[0];
        private bool _knifeFailed;
        private KnifeState _state;
        private readonly List<Grip> _grips = new List<Grip>();
        private Vector3 _position;
        private Quaternion _rotation = Quaternion.identity;
        private Vector3 _restPosition;
        private Quaternion _restRotation = Quaternion.identity;
        private float _surfaceY;
        private float _length = 0.42f;
        private float _progress;
        private Vector3 _fromPosition;
        private Quaternion _fromRotation = Quaternion.identity;
        private bool _haveTip;
        private float _lastTipY;
        private float _stuckAt;
        private float _releasedAt;
        private SelectableCard _stuckCard;
        private Vector3 _stuckCardPosition;
        private float _nextRefuse;
        private bool _restLogged;
        private MeshRenderer _altar;
        private bool _altarInCircle;
        private float _nextAltarFind;
        private Vector3 _altarLastPosition;
        private float _altarStill;
        private bool _haveRest;
        private bool _restFinal;
        private Vector3 _restLocalPosition;
        private Quaternion _restLocalRotation = Quaternion.identity;

        private float _fireHeld;
        private bool _fireNeedsAway;
        private bool _fireArmed;

        internal StoneRites(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal static bool Available
        {
            get
            {
                return MergeStoneField != null && MergeSacrificeField != null && BoostStoneField != null && BoostLightField != null;
            }
        }

        internal static bool Hides(Component target)
        {
            ConfirmStoneButton stone = target as ConfirmStoneButton;
            return stone != null && HiddenStones.Contains(stone);
        }

        internal void Tick(CardGrabber grabber, VrHand left, VrHand right, VrHand main, bool input, float scale)
        {
            bool active = !EventStyle.Simplified && InEvent();
            if (!active)
            {
                Release();
                return;
            }
            Find();
            ConfirmStoneButton mergeStone = _merger != null && HandReach.Active(_merger) ? MergeStoneField.GetValue(_merger) as ConfirmStoneButton : null;
            ConfirmStoneButton boostStone = _boost != null && HandReach.Active(_boost) ? BoostStoneField.GetValue(_boost) as ConfirmStoneButton : null;
            Hide(_mergeHide, mergeStone);
            Hide(_boostHide, boostStone);
            TickKnife(grabber, left, right, main, mergeStone, input, scale);
            TickFire(left, right, boostStone, input, scale);
        }

        internal void Release()
        {
            Restore(_mergeHide);
            Restore(_boostHide);
            DropKnife();
            ForgetAltar();
            _fireHeld = 0f;
            _fireNeedsAway = false;
            _fireArmed = false;
        }

        internal void Dispose()
        {
            Release();
            if (_knife != null) Object.Destroy(_knife);
            _knife = null;
        }

        private static bool InEvent()
        {
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            return flow != null && flow.CurrentGameState == GameState.SpecialCardSequence;
        }

        private void Find()
        {
            if (Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 0.5f;
            if (_merger == null) _merger = Object.FindObjectOfType<CardMergeSequencer>();
            if (_boost == null) _boost = Object.FindObjectOfType<CardStatBoostSequencer>();
        }

        private void Hide(StoneHide hide, ConfirmStoneButton stone)
        {
            if (stone != hide.Stone)
            {
                Restore(hide);
                hide.Stone = stone;
            }
            if (stone == null) return;
            HiddenStones.Add(stone);
            if (Time.unscaledTime >= hide.NextScan)
            {
                hide.NextScan = Time.unscaledTime + 0.5f;
                List<Renderer> found = new List<Renderer>(stone.GetComponentsInChildren<Renderer>(true));
                Animator anim = StoneAnimField != null ? StoneAnimField.GetValue(stone) as Animator : null;
                if (anim != null && !anim.transform.IsChildOf(stone.transform)) found.AddRange(anim.GetComponentsInChildren<Renderer>(true));
                hide.Renderers = found.ToArray();
            }
            for (int i = 0; i < hide.Renderers.Length; i++)
            {
                Renderer renderer = hide.Renderers[i];
                if (renderer == null || !renderer.enabled) continue;
                renderer.enabled = false;
                if (!hide.Disabled.Contains(renderer)) hide.Disabled.Add(renderer);
            }
        }

        private static void Restore(StoneHide hide)
        {
            for (int i = 0; i < hide.Disabled.Count; i++)
                if (hide.Disabled[i] != null) hide.Disabled[i].enabled = true;
            hide.Disabled.Clear();
            hide.Renderers = new Renderer[0];
            hide.NextScan = 0f;
            if (hide.Stone != null) HiddenStones.Remove(hide.Stone);
            hide.Stone = null;
        }

        private static void Confirm(ConfirmStoneButton stone)
        {
            stone.CursorSelectStart();
            if (stone != null) stone.CursorSelectEnd();
        }

        private void TickKnife(CardGrabber grabber, VrHand left, VrHand right, VrHand main, ConfirmStoneButton stone, bool input, float scale)
        {
            SelectCardFromDeckSlot slot = _merger != null && HandReach.Active(_merger) ? MergeSacrificeField.GetValue(_merger) as SelectCardFromDeckSlot : null;
            if (!AltarUp(slot) || !EnsureKnife(grabber))
            {
                DropKnife();
                ForgetAltar();
                return;
            }
            SelectableCard sacrifice = slot.Card;
            Bounds card = new Bounds();
            bool haveCard = sacrifice != null && HandReach.Active(sacrifice) && HandReach.CardBounds(sacrifice, out card);
            bool armed = stone != null && HandReach.Live(stone) && haveCard;
            Bounds spot = haveCard ? card : SlotBounds(slot, scale);

            _length = KnifeLengthMeters * scale;
            _knife.transform.localScale = Vector3.one * _length;
            UpdateRest(grabber, main, slot, spot, haveCard, card, scale);
            if (!_knife.activeSelf) _knife.SetActive(true);
            if (_state == KnifeState.None)
            {
                _position = _restPosition;
                _rotation = _restRotation;
                _state = KnifeState.Resting;
            }

            if (_state == KnifeState.Stuck)
            {
                TickStuck(left, right, scale);
                if (_state == KnifeState.Stuck) return;
            }

            UpdateGrips(left, right, input);
            if (_grips.Count == 0 && _state == KnifeState.Held)
            {
                _state = KnifeState.Returning;
                _progress = 0f;
                _fromPosition = _position;
                _fromRotation = _rotation;
                _haveTip = false;
            }

            if (_state == KnifeState.Resting || _state == KnifeState.Returning)
            {
                if (_state == KnifeState.Returning)
                {
                    _progress = Mathf.Min(1f, _progress + Time.unscaledDeltaTime / ReturnSeconds);
                    float k = Mathf.SmoothStep(0f, 1f, _progress);
                    _position = Vector3.Lerp(_fromPosition, _restPosition, k);
                    _rotation = Quaternion.Slerp(_fromRotation, _restRotation, k);
                    if (_progress >= 1f) _state = KnifeState.Resting;
                }
                else
                {
                    _position = _restPosition;
                    _rotation = _restRotation;
                }
                if (input && armed)
                    VrTips.Show("rite.knife", _restPosition + Vector3.up * (0.05f * scale),
                        "Grab the dagger with both hands and plunge it into the sacrifice.", 80);
            }

            OfferGrab(left, right, input, scale);

            if (_state == KnifeState.Held) Held(sacrifice, card, armed, stone, scale);
            _knife.transform.SetPositionAndRotation(_position, _rotation);
        }

        private bool AltarUp(SelectCardFromDeckSlot slot)
        {
            if (slot == null) return false;
            if (StoneCircleField == null) return slot.Card != null;
            Animator circle = StoneCircleField.GetValue(_merger) as Animator;
            return circle != null && circle.gameObject.activeInHierarchy;
        }

        private static Bounds SlotBounds(SelectCardFromDeckSlot slot, float scale)
        {
            Vector3 seat = slot.transform.position + slot.transform.up * 0.025f;
            Vector3 thin = new Vector3(0f, 0.004f * scale, 0f);
            Bounds box;
            if (HandReach.CardBounds(slot, out box) && box.size.x > 0.02f * scale && box.size.z > 0.02f * scale && box.size.x < 0.5f * scale && box.size.z < 0.5f * scale)
                return new Bounds(new Vector3(box.center.x, seat.y, box.center.z), new Vector3(box.size.x, thin.y, box.size.z));
            return new Bounds(seat, new Vector3(0.13f * scale, thin.y, 0.19f * scale));
        }

        private void ForgetAltar()
        {
            _altar = null;
            _haveRest = false;
            _restFinal = false;
            _restLogged = false;
            _altarStill = 0f;
            _nextAltarFind = 0f;
        }

        private void UpdateRest(CardGrabber grabber, VrHand main, SelectCardFromDeckSlot slot, Bounds spot, bool haveCard, Bounds card, float scale)
        {
            if (_altar != null && (!_altar.enabled || !_altar.gameObject.activeInHierarchy))
            {
                _altar = null;
                _haveRest = false;
                _restFinal = false;
            }
            if (_altar == null && Time.unscaledTime >= _nextAltarFind)
            {
                _nextAltarFind = Time.unscaledTime + 0.25f;
                _altar = FindAltarStone(spot, scale, out _altarInCircle);
                if (_altar != null)
                {
                    _haveRest = false;
                    _restFinal = false;
                    _altarStill = 0f;
                    _altarLastPosition = _altar.transform.position;
                }
            }
            if (_altar != null)
            {
                Vector3 now = _altar.transform.position;
                float still = AltarStillMeters * scale;
                if ((now - _altarLastPosition).sqrMagnitude > still * still) _altarStill = 0f;
                else _altarStill += Time.unscaledDeltaTime;
                _altarLastPosition = now;
            }
            bool settled = _altar == null || _altarStill >= AltarSettleSeconds;
            if (!_haveRest || (!_restFinal && settled))
            {
                RestPose(grabber, main, slot, spot, scale, settled);
                _haveRest = true;
                _restFinal = settled;
                Transform frame = _altar != null ? _altar.transform : null;
                _restLocalPosition = frame != null ? frame.InverseTransformPoint(_restPosition) : _restPosition;
                _restLocalRotation = frame != null ? Quaternion.Inverse(frame.rotation) * _restRotation : _restRotation;
            }
            else if (_altar != null)
            {
                Transform t = _altar.transform;
                _restPosition = t.TransformPoint(_restLocalPosition);
                _restRotation = t.rotation * _restLocalRotation;
            }
            else
            {
                _restPosition = _restLocalPosition;
                _restRotation = _restLocalRotation;
            }
            _surfaceY = _restPosition.y - RestLiftMeters * scale;
            if (haveCard) LiftOverCard(card, scale);
        }

        private void LiftOverCard(Bounds card, float scale)
        {
            Vector3 tip = _restPosition + _restRotation * Vector3.forward * _length;
            float pad = BladeHalfWidthMeters * scale;
            float minX = Mathf.Min(_restPosition.x, tip.x) - pad, maxX = Mathf.Max(_restPosition.x, tip.x) + pad;
            float minZ = Mathf.Min(_restPosition.z, tip.z) - pad, maxZ = Mathf.Max(_restPosition.z, tip.z) + pad;
            if (maxX < card.min.x || minX > card.max.x || maxZ < card.min.z || minZ > card.max.z) return;
            float lifted = card.max.y + RestLiftMeters * scale;
            if (lifted <= _restPosition.y) return;
            _restPosition.y = lifted;
            _surfaceY = card.max.y;
        }

        private void RestPose(CardGrabber grabber, VrHand main, SelectCardFromDeckSlot slot, Bounds card, float scale, bool log)
        {
            Vector3 eye;
            Quaternion view;
            float unitsPerMeter;
            Vector3 toward = Vector3.zero;
            if (grabber.Renderer != null && grabber.Renderer.TryGetHeadPose(out eye, out view, out unitsPerMeter)) toward = eye - card.center;
            toward.y = 0f;
            if (toward.sqrMagnitude < 1e-6f) toward = grabber.TowardPlayer;
            toward.y = 0f;
            if (toward.sqrMagnitude < 1e-6f) toward = -slot.transform.forward;
            toward.y = 0f;
            toward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, -toward).normalized;
            Vector3 handSide = main != null && main.IsLeft ? -right : right;
            Vector3 forward = -handSide;
            float depth = Mathf.Abs(toward.x) * card.extents.x + Mathf.Abs(toward.z) * card.extents.z;
            float distance = depth + RestGapMeters * scale;
            MeshRenderer stone = _altar;
            float stoneReach = 0f;
            if (stone != null)
            {
                Bounds b = stone.bounds;
                stoneReach = Vector3.Dot(b.center - card.center, toward) + Mathf.Abs(toward.x) * b.extents.x + Mathf.Abs(toward.z) * b.extents.z;
                distance = Mathf.Max(distance, stoneReach - StoneEdgeMarginMeters * scale);
            }
            Vector3 away = -grabber.TowardPlayer;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-6f) away = -toward;
            away.Normalize();
            Vector3 center = card.center + toward * distance + away * (AwayShiftMeters * scale);
            string source;
            float surface = RestSurface(stone, center, forward, card, scale, out source);
            _surfaceY = surface;
            center.y = surface + RestLiftMeters * scale;
            _restRotation = Quaternion.LookRotation(forward, toward);
            _restPosition = center - forward * (0.5f * _length);
            if (_restLogged || !log) return;
            _restLogged = true;
            float s = Mathf.Max(scale, 1e-4f);
            Vector3 offset = center - card.center;
            offset.y = 0f;
            _logger.LogInfo("Stone rite dagger laid " + (Vector3.Dot(offset, toward) / s).ToString("0.00") + " m toward you from the sacrifice " +
                (slot.Card != null ? "card" : "slot (no card yet)") + " after a " + AwayShiftMeters.ToString("0.000") + " m shift away along the table, " +
                (stone != null ? "on the altar stone " + stone.name + (_altarInCircle ? " (in the stone circle, " : " (outside the stone circle, ") + (stone.bounds.size / s).ToString("F2") + " m, player-side edge " + (stoneReach / s).ToString("0.00") +
                " m from the card centre, readable " + (stone.GetComponent<MeshFilter>() != null && stone.GetComponent<MeshFilter>().sharedMesh != null && stone.GetComponent<MeshFilter>().sharedMesh.isReadable) + ")"
                : "no altar stone found under the card") +
                ", surface " + ((card.min.y - surface) / s).ToString("0.000") + " m below the card bottom from " + source + ".");
        }

        private MeshRenderer FindAltarStone(Bounds card, float scale, out bool inCircle)
        {
            inCircle = false;
            Animator circle = StoneCircleField != null && _merger != null ? StoneCircleField.GetValue(_merger) as Animator : null;
            if (circle != null)
            {
                MeshRenderer found = BestStone(circle.GetComponentsInChildren<MeshRenderer>(), card, scale, false);
                if (found != null)
                {
                    inCircle = true;
                    return found;
                }
            }
            return BestStone(Object.FindObjectsOfType<MeshRenderer>(), card, scale, true);
        }

        private static MeshRenderer BestStone(MeshRenderer[] renderers, Bounds card, float scale, bool needSeated)
        {
            MeshRenderer best = null;
            float margin = 0.01f * scale;
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.name.StartsWith("InscryptionVR") || renderer.transform.root.name.StartsWith("InscryptionVR")) continue;
                if (renderer.GetComponentInParent<Card>() != null || renderer.GetComponentInParent<ConfirmStoneButton>() != null) continue;
                Bounds b = renderer.bounds;
                if (b.size.y < 0.008f * scale || b.size.x > 1.5f * scale || b.size.z > 1.5f * scale) continue;
                if (card.center.x < b.min.x - margin || card.center.x > b.max.x + margin || card.center.z < b.min.z - margin || card.center.z > b.max.z + margin) continue;
                if (b.max.y > card.max.y + margin) continue;
                if (b.max.y < card.min.y - (needSeated ? 0.12f : 0.6f) * scale) continue;
                if (best == null || b.max.y > best.bounds.max.y) best = renderer;
            }
            return best;
        }

        private float RestSurface(MeshRenderer stone, Vector3 center, Vector3 forward, Bounds card, float scale, out string source)
        {
            if (stone != null)
            {
                float top;
                if (StoneTop(stone, center, forward, scale, out top))
                {
                    source = "the stone's mesh";
                    return top;
                }
            }
            RaycastHit hit;
            Vector3 from = center + Vector3.up * (0.3f * scale);
            float floor = stone != null ? stone.bounds.min.y : float.NegativeInfinity;
            if (Physics.Raycast(from, Vector3.down, out hit, 0.6f * scale, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                hit.point.y < card.max.y + 0.05f * scale && hit.point.y >= floor)
            {
                source = "a raycast hit on " + hit.collider.name;
                return hit.point.y;
            }
            if (stone != null)
            {
                source = "the stone's bounds";
                return stone.bounds.max.y;
            }
            source = "the card bottom";
            return card.min.y;
        }

        private bool StoneTop(MeshRenderer stone, Vector3 center, Vector3 forward, float scale, out float top)
        {
            top = float.NegativeInfinity;
            MeshFilter filter = stone.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) return false;
            Vector3[] vertices = mesh.vertices;
            Transform t = stone.transform;
            float radius = StoneSampleMeters * scale;
            float radiusSq = radius * radius;
            Vector3 half = forward * (0.5f * _length);
            Vector3 a = center - half;
            Vector3 ab = half * 2f;
            ab.y = 0f;
            float abSq = Mathf.Max(ab.sqrMagnitude, 1e-8f);
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = t.TransformPoint(vertices[i]);
                Vector3 ap = p - a;
                ap.y = 0f;
                float k = Mathf.Clamp01(Vector3.Dot(ap, ab) / abSq);
                Vector3 offset = ap - ab * k;
                if (offset.sqrMagnitude > radiusSq) continue;
                if (p.y > top) top = p.y;
            }
            return !float.IsNegativeInfinity(top);
        }

        private void UpdateGrips(VrHand left, VrHand right, bool input)
        {
            for (int i = _grips.Count - 1; i >= 0; i--)
            {
                VrHand hand = _grips[i].Hand;
                if (!input || !hand.Tracked || (hand.Ready && !hand.Grip.Held))
                {
                    _grips.RemoveAt(i);
                    hand.Play(Feel.Tick, 0.5f);
                    if (_grips.Count > 0) Glide();
                    continue;
                }
                hand.Claimed = true;
                hand.SetHoldPose(HandHold.Fist);
            }
        }

        private void Glide()
        {
            _progress = 0f;
            _fromPosition = _position;
            _fromRotation = _rotation;
            _haveTip = false;
        }

        private bool Holding(VrHand hand)
        {
            for (int i = 0; i < _grips.Count; i++)
                if (_grips[i].Hand == hand) return true;
            return false;
        }

        private void OfferGrab(VrHand left, VrHand right, bool input, float scale)
        {
            if (!input || _grips.Count >= 2) return;
            float reach = GrabReachMeters * scale;
            Offer(left, reach, scale);
            Offer(right, reach, scale);
        }

        private void Offer(VrHand hand, float reach, float scale)
        {
            if (Holding(hand) || !hand.Ready || hand.Busy) return;
            float gap = HandGap(hand);
            if (gap <= reach * 4f) hand.BlockAimGrab();
            if (gap > reach) return;
            hand.Highlight();
            GrabGlow.Mark(_knife.transform);
            if (hand.GripEdge != 1) return;
            hand.GripEdge = 0;
            Grip grip = new Grip();
            grip.Hand = hand;
            Vector3 point = hand.HasFist ? hand.FistPosition : hand.GripPosition;
            grip.Fraction = Mathf.Clamp(Vector3.Dot(point - _position, _rotation * Vector3.forward) / Mathf.Max(_length, 1e-4f), MinGripFraction, MaxGripFraction);
            _grips.Add(grip);
            hand.Claimed = true;
            hand.SetHoldPose(HandHold.Fist);
            hand.Play(Feel.Tap);
            if (_grips.Count == 1)
            {
                _state = KnifeState.Held;
                _progress = 0f;
                _fromPosition = _position;
                _fromRotation = _rotation;
                _haveTip = false;
                VrSounds.Play("sharp_object_up#1", MixerGroup.TableObjectsSFX, _position, 0.5f);
                VrTips.Done("rite.knife");
                VrTips.Show("rite.knife.both", _position + Vector3.up * (0.08f * scale), "Both hands on the handle, then plunge it down into the sacrifice.", 70);
            }
            else
            {
                Glide();
                VrTips.Done("rite.knife.both");
            }
            _logger.LogInfo("Stone rite dagger gripped with the " + hand.Name + " hand (" + _grips.Count + " of 2 hands, " + Mathf.RoundToInt(grip.Fraction * 100f) + "% down the handle).");
        }

        private float HandGap(VrHand hand)
        {
            float gap = SegmentDistance(hand.Position);
            gap = Mathf.Min(gap, SegmentDistance(hand.GripPosition));
            if (hand.HasFist) gap = Mathf.Min(gap, SegmentDistance(hand.FistPosition));
            if (hand.HasIndexTip) gap = Mathf.Min(gap, SegmentDistance(hand.IndexTip));
            return gap;
        }

        private float SegmentDistance(Vector3 point)
        {
            Vector3 ab = _rotation * Vector3.forward * (HandleFraction * _length);
            float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(point - _position, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(point, _position + ab * t);
        }

        private static void HandFrame(VrHand hand, out Vector3 fist, out Quaternion rotation)
        {
            if (hand.HasFist)
            {
                fist = hand.FistPosition;
                rotation = hand.FistRotation;
                return;
            }
            Vector3 direction = hand.GripRotation * Vector3.back;
            rotation = Quaternion.LookRotation(direction, hand.GripRotation * Vector3.down);
            fist = hand.GripPosition;
        }

        private void HandPose(out Vector3 position, out Quaternion rotation)
        {
            Vector3 fistA;
            Quaternion rotA;
            HandFrame(_grips[0].Hand, out fistA, out rotA);
            if (_grips.Count == 1)
            {
                rotation = rotA;
                position = fistA - rotA * Vector3.forward * (_grips[0].Fraction * _length);
                return;
            }
            Vector3 fistB;
            Quaternion rotB;
            HandFrame(_grips[1].Hand, out fistB, out rotB);
            Vector3 forwardA = rotA * Vector3.forward;
            Vector3 forwardB = rotB * Vector3.forward;
            Vector3 forward = Vector3.Dot(forwardA, forwardB) > 0.2f ? (forwardA + forwardB).normalized : forwardA;
            Vector3 up = rotA * Vector3.up + rotB * Vector3.up;
            if (Vector3.Cross(forward, up).sqrMagnitude < 1e-6f) up = rotA * Vector3.up;
            rotation = Quaternion.LookRotation(forward, up);
            position = 0.5f * (fistA - forward * (_grips[0].Fraction * _length) + fistB - forward * (_grips[1].Fraction * _length));
        }

        private void PinHands()
        {
            for (int i = 0; i < _grips.Count; i++)
            {
                VrHand hand = _grips[i].Hand;
                Vector3 fist;
                Quaternion rotation;
                HandFrame(hand, out fist, out rotation);
                Vector3 implied = fist - rotation * Vector3.forward * (_grips[i].Fraction * _length);
                Quaternion actual = _grips.Count > 1 ? Quaternion.LookRotation(_rotation * Vector3.forward, rotation * Vector3.up) : _rotation;
                hand.Pin(implied, rotation, _position, actual);
            }
        }

        private void Held(SelectableCard sacrifice, Bounds card, bool armed, ConfirmStoneButton stone, float scale)
        {
            Vector3 handPosition;
            Quaternion handRotation;
            HandPose(out handPosition, out handRotation);
            if (_progress < 1f)
            {
                _progress = Mathf.Min(1f, _progress + Time.unscaledDeltaTime / PickupSeconds);
                float k = Mathf.SmoothStep(0f, 1f, _progress);
                _position = Vector3.Lerp(_fromPosition, handPosition, k);
                _rotation = Quaternion.Slerp(_fromRotation, handRotation, k);
                return;
            }
            _position = handPosition;
            _rotation = handRotation;
            Vector3 forward = _rotation * Vector3.forward;
            Vector3 tip = _position + forward * _length;
            float dt = Time.unscaledDeltaTime;
            float downSpeed = _haveTip && dt > 0f ? (_lastTipY - tip.y) / dt : 0f;
            _lastTipY = tip.y;
            _haveTip = true;

            float margin = CardMarginMeters * scale;
            bool overCard = card.size.sqrMagnitude > 0f && tip.x >= card.min.x - margin && tip.x <= card.max.x + margin && tip.z >= card.min.z - margin && tip.z <= card.max.z + margin;
            float surface = overCard ? Mathf.Max(card.max.y, _surfaceY) : _surfaceY;
            if (tip.y <= surface)
            {
                bool plunge = armed && overCard && downSpeed >= PlungeMetersPerSecond * scale && forward.y <= -PlungeSteepness;
                if (plunge && _grips.Count >= 2)
                {
                    Plunge(sacrifice, stone, tip, surface, scale);
                    return;
                }
                if (plunge && Time.unscaledTime >= _nextRefuse)
                {
                    _nextRefuse = Time.unscaledTime + 0.6f;
                    _grips[0].Hand.Play(Feel.Reject, 0.6f);
                    VrTips.Show("rite.knife.both", tip + Vector3.up * (0.1f * scale), "Use both hands on the handle to plunge it in.", 70);
                    VrLog.Chatter(_logger, "Stone rite dagger plunge refused: only one hand is on the handle.");
                }
                _position += Vector3.up * (surface - tip.y);
            }
            PinHands();
        }

        private void Plunge(SelectableCard sacrifice, ConfirmStoneButton stone, Vector3 tip, float surface, float scale)
        {
            Vector3 forward = _rotation * Vector3.forward;
            Vector3 stuckTip = new Vector3(tip.x, surface - EmbedMeters * scale, tip.z);
            _position = stuckTip - forward * _length;
            _state = KnifeState.Stuck;
            _stuckAt = Time.unscaledTime;
            _releasedAt = -1f;
            _stuckCard = sacrifice;
            _stuckCardPosition = sacrifice != null ? sacrifice.transform.position : Vector3.zero;
            for (int i = 0; i < _grips.Count; i++)
            {
                _grips[i].Hand.Play(Feel.Snap, 1f);
                _grips[i].Hand.Play(Feel.Thud, 0.8f);
            }
            VrSounds.Play("metal_object_short#1", MixerGroup.TableObjectsSFX, stuckTip, 0.7f);
            VrTips.Done("rite.knife");
            VrTips.Done("rite.knife.both");
            string name = sacrifice != null && sacrifice.Info != null ? sacrifice.Info.DisplayedNameLocalized : "the sacrifice";
            Confirm(stone);
            _logger.LogInfo("Stone rite: the dagger was plunged into " + name + " with both hands; the stones take the sacrifice.");
            PinHands();
            _knife.transform.SetPositionAndRotation(_position, _rotation);
        }

        private void TickStuck(VrHand left, VrHand right, float scale)
        {
            for (int i = _grips.Count - 1; i >= 0; i--)
            {
                VrHand hand = _grips[i].Hand;
                if (!hand.Tracked || (hand.Ready && !hand.Grip.Held))
                {
                    _grips.RemoveAt(i);
                    continue;
                }
                hand.Claimed = true;
                hand.SetHoldPose(HandHold.Fist);
            }
            if (_grips.Count > 0) PinHands();
            else if (_releasedAt < 0f) _releasedAt = Time.unscaledTime;
            float now = Time.unscaledTime;
            bool cardGone = _stuckCard == null || !_stuckCard.gameObject.activeInHierarchy ||
                (_stuckCard.transform.position - _stuckCardPosition).sqrMagnitude > Mathf.Pow(CardMovedMeters * scale, 2f);
            bool timedOut = now - _stuckAt > StuckSeconds || (_releasedAt >= 0f && now - _releasedAt > StuckAfterReleaseSeconds && now - _stuckAt > StuckAfterReleaseSeconds);
            if (!cardGone && !timedOut) return;
            _stuckCard = null;
            _state = _grips.Count > 0 ? KnifeState.Held : KnifeState.Returning;
            Glide();
            VrLog.Chatter(_logger, "Stone rite dagger came free " + (cardGone ? "as the sacrifice left" : "after a while") + (_grips.Count > 0 ? " and stays in hand." : " and goes back onto the altar."));
        }

        private bool EnsureKnife(CardGrabber grabber)
        {
            if (_knife != null) return true;
            if (_knifeFailed) return false;
            try
            {
                int layer = grabber.TableLayer;
                _knife = SacrificeKnife.CreateModel("InscryptionVR Stone Rite Dagger", layer, _logger, out _knifeRenderers);
                return true;
            }
            catch (System.Exception exception)
            {
                _knifeFailed = true;
                if (_knife != null) Object.Destroy(_knife);
                _knife = null;
                _logger.LogWarning("Stone rite dagger could not be built; the stones event uses its button: " + exception.Message);
                return false;
            }
        }

        private void DropKnife()
        {
            _grips.Clear();
            if (_knife != null && _knife.activeSelf) _knife.SetActive(false);
            _state = KnifeState.None;
            _stuckCard = null;
            _haveTip = false;
        }

        private void TickFire(VrHand left, VrHand right, ConfirmStoneButton stone, bool input, float scale)
        {
            Light fire = stone != null && _boost != null ? BoostLightField.GetValue(_boost) as Light : null;
            bool armed = input && stone != null && HandReach.Live(stone) && fire != null && fire.enabled && fire.gameObject.activeInHierarchy && fire.intensity >= LitIntensity;
            if (!armed)
            {
                _fireHeld = 0f;
                _fireArmed = false;
                return;
            }
            Vector3 point = fire.transform.position;
            float leftMeters = FireDistance(left, point) / Mathf.Max(scale, 1e-4f);
            float rightMeters = FireDistance(right, point) / Mathf.Max(scale, 1e-4f);
            if (!_fireArmed)
            {
                _fireArmed = true;
                _logger.LogInfo("Campfire rite armed: left hand " + leftMeters.ToString("0.00") + " m, right hand " + rightMeters.ToString("0.00") +
                    " m from the fire light; hold both within " + FireMeters.ToString("0.00") + " m for " + FireHoldSeconds.ToString("0.0") + " s.");
            }
            bool retrieve = BoostRetrieveField != null && HandReach.Active(BoostRetrieveField.GetValue(_boost) as Component);
            VrTips.Show(retrieve ? "rite.fire.again" : "rite.fire", point + Vector3.up * (0.15f * scale),
                retrieve ? "Hold both hands by the fire to push your luck, or take your card back to pull away." : "Hold both hands by the fire for a moment to warm the card.", 75);
            bool near = left.Ready && right.Ready && leftMeters <= FireMeters && rightMeters <= FireMeters;
            if (!near)
            {
                _fireNeedsAway = false;
                _fireHeld = 0f;
                return;
            }
            if (_fireNeedsAway) return;
            _fireHeld += Time.unscaledDeltaTime;
            float progress = Mathf.Clamp01(_fireHeld / FireHoldSeconds);
            float flicker = 0.8f + 0.2f * Mathf.PerlinNoise(Time.unscaledTime * 9f, 0.7f);
            left.Rumble((0.12f + 0.45f * progress) * flicker);
            right.Rumble((0.12f + 0.45f * progress) * flicker);
            if (_fireHeld < FireHoldSeconds) return;
            _fireHeld = 0f;
            _fireNeedsAway = true;
            left.Play(Feel.Thud, 0.8f);
            right.Play(Feel.Thud, 0.8f);
            VrTips.Done(retrieve ? "rite.fire.again" : "rite.fire");
            Confirm(stone);
            _logger.LogInfo(retrieve ? "Campfire rite: both hands held by the fire; you push your luck." : "Campfire rite: both hands held by the fire; the survivors warm the card.");
        }

        private static float FireDistance(VrHand hand, Vector3 fire)
        {
            if (!hand.Tracked) return float.MaxValue;
            float best = (hand.Position - fire).sqrMagnitude;
            best = Mathf.Min(best, (hand.GripPosition - fire).sqrMagnitude);
            if (hand.HasIndexTip) best = Mathf.Min(best, (hand.IndexTip - fire).sqrMagnitude);
            if (hand.HasFist) best = Mathf.Min(best, (hand.FistPosition - fire).sqrMagnitude);
            return Mathf.Sqrt(best);
        }
    }
}
