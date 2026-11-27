using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class SacrificeKnife
    {
        private enum KnifeState { Stored, Pickup, Held, Returning, Falling }

        private static readonly FieldInfo CancelField = typeof(BoardManager).GetField("cancelledPlacementWithInput", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SacrificesField = typeof(BoardManager).GetField("currentSacrifices", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo SacrificeValueMethod = typeof(BoardManager).GetMethod("GetValueOfSacrifices", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(List<CardSlot>) }, null);

        private const float GripCenterFraction = 0.21f;
        private const float BladeStartFraction = 0.49f;
        private const float BladeRadiusMeters = 0.012f;
        private const float PickupSeconds = 0.12f;
        private const float ReturnSeconds = 0.3f;
        private const float StabMetersPerSecond = 0.35f;
        private const float StickSteepness = 0.7071f;
        private const float LeanSteepness = 0.656f;
        private const float CarveSteepness = 0.25f;
        private const float CarveDepthMeters = 0.008f;
        private const float CarveLiftMeters = 0.015f;
        private const float CarveGritMetersPerSecond = 0.6f;
        private const float EmbedMeters = 0.02f;
        private const float PullOutMeters = 0.05f;
        private const float StoredEmbedFraction = 0.06f;
        private const float StoredTowardMeters = 0.15f;
        private const float StuckCardMoveLanes = 0.2f;
        private const float EnterSeconds = 0.6f;
        private const float LeaveSeconds = 0.4f;
        private const float AwaySideMeters = 0.65f;
        private const float AwayUpMeters = 0.25f;
        private const float AwayTiltDegrees = 35f;
        private const float FlatLiftFraction = 0.03f;
        private const float SettleSeconds = 0.18f;
        private const float MaxFallSeconds = 4f;
        private const float AirDrag = 0.4f;
        private const float HoverHeightMeters = 0.2f;
        private const int BladeSamples = 7;
        private const int BodySamples = 9;
        private const int SweepSteps = 3;

        private readonly ManualLogSource _logger;
        private readonly float _lengthMeters;
        private readonly float _lanesSide;
        private bool _rightSide;
        private readonly float _lanesToward;

        private GameObject _root;
        private Renderer[] _renderers = new Renderer[0];
        private bool _buildFailed;

        private bool _entered;
        private float _enter = 1f;
        private bool _leaving;
        private float _leave;
        private Vector3 _leaveFrom;
        private Quaternion _leaveFromRotation = Quaternion.identity;
        private Vector3 _leaveTo;
        private Quaternion _leaveToRotation = Quaternion.identity;
        private Vector3 _awaySide = Vector3.right;

        private KnifeState _state;
        private VrHand _holder;
        private float _progress;
        private Vector3 _fromPosition;
        private Quaternion _fromRotation = Quaternion.identity;
        private Vector3 _position;
        private Quaternion _rotation = Quaternion.identity;
        private float _length = 0.3f;
        private float _scale = 1f;

        private bool _stuck;
        private float _stuckSince;
        private Vector3 _stuckTip;
        private bool _stuckInCard;
        private PlayableCard _stuckCard;
        private Vector3 _stuckCardPosition;

        private bool _carving;
        private Vector3 _carveTip;
        private Vector3 _carveHeading;
        private bool _carveOnCard;

        private bool _rest;
        private Vector3 _restPosition;
        private Quaternion _restRotation = Quaternion.identity;
        private float _returnSeconds = ReturnSeconds;
        private Vector3 _velocity;
        private Vector3 _lastPosition;
        private bool _haveVelocity;
        private float _fallTime;

        private bool _haveTip;
        private Vector3 _lastTip;
        private bool _havePrevious;
        private readonly Vector3[] _previous = new Vector3[BladeSamples];
        private readonly Vector3[] _current = new Vector3[BladeSamples];
        private float _lastContactTime = -10f;
        private float _nextMissLog;
        private List<CardSlot> _allSlots = new List<CardSlot>();

        private HashSet<PlayableCard> _touching = new HashSet<PlayableCard>();
        private HashSet<PlayableCard> _touchingNow = new HashSet<PlayableCard>();
        private readonly HashSet<CardSlot> _marked = new HashSet<CardSlot>();
        private PlayableCard _hoverCard;

        internal SacrificeKnife(ManualLogSource logger, float lengthMeters, float lanesSide, float lanesToward)
        {
            _logger = logger;
            _lengthMeters = Mathf.Clamp(lengthMeters, 0.1f, 0.6f);
            _lanesSide = Mathf.Clamp(lanesSide, 0f, 6f);
            _lanesToward = Mathf.Clamp(lanesToward, -3f, 3f);
            if (CancelField == null) _logger.LogWarning("Sacrifice knife: the game's cancel flag was not found; stabbing an empty spot cannot cancel.");
            _gaze = new SacrificeGaze(logger);
            _isMarked = delegate(CardSlot slot) { return _board != null && IsMarked(_board, slot); };
        }

        private readonly SacrificeGaze _gaze;
        private readonly Predicate<CardSlot> _isMarked;
        private BoardManager _board;

        internal void ConfigureGaze(string shake, string marker, bool heartbeat) { _gaze.Configure(shake, marker, heartbeat); }

        private Vector3 Forward { get { return _rotation * Vector3.forward; } }

        internal bool Holding { get { return _state == KnifeState.Pickup || _state == KnifeState.Held; } }

        internal VrHand Holder { get { return Holding ? _holder : null; } }

        internal void SetRightSide(bool rightSide)
        {
            if (_rightSide == rightSide) return;
            _rightSide = rightSide;
            Forget();
        }

        private Vector3 PointAt(float fraction) { return _position + Forward * (fraction * _length); }

        private float HandGap(VrHand hand)
        {
            float gap = Mathf.Min(KnifeDistance(hand.Position), KnifeDistance(hand.GripPosition));
            if (hand.HasFist) gap = Mathf.Min(gap, KnifeDistance(hand.FistPosition));
            if (hand.HasIndexTip) gap = Mathf.Min(gap, KnifeDistance(hand.IndexTip));
            return gap;
        }

        private void ReportMiss(VrHand knifeHand, bool canGrab, float gap, float reach, float scale)
        {
            if (Time.unscaledTime < _nextMissLog) return;
            _nextMissLog = Time.unscaledTime + 1f;
            float s = scale > 0f ? scale : 1f;
            string message = "Sacrifice knife grip missed: gap " + (gap / s).ToString("F3") + " m (reach " + (reach / s).ToString("F3") + " m), canGrab=" + canGrab +
                ", ready=" + knifeHand.Ready + ", claimed=" + knifeHand.Claimed + ", holdingCard=" + (knifeHand.Held != null) + ", pile=" + (knifeHand.Pile != null) +
                ", pommel=" + _position.ToString("F2") + ", tip=" + PointAt(1f).ToString("F2") + ", hand=" + knifeHand.Position.ToString("F2") +
                ", grip=" + knifeHand.GripPosition.ToString("F2") + (knifeHand.HasFist ? ", fist=" + knifeHand.FistPosition.ToString("F2") : "") + ".";
            if (gap <= reach * 2f) _logger.LogInfo(message);
            else VrLog.Chatter(_logger, message);
        }

        private float KnifeDistance(Vector3 point)
        {
            Vector3 a = _position;
            Vector3 ab = Forward * (BladeStartFraction * _length);
            float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(point, a + ab * t);
        }

        private void Offer(VrHand candidate, VrHand exclude, bool canGrab, float reach, float scale, ref VrHand grabbing, ref float grabGap)
        {
            if (candidate == null || candidate == exclude) return;
            float gap = HandGap(candidate);
            bool reachable = canGrab && candidate.Ready && !candidate.Busy && gap <= reach;
            if (reachable)
            {
                candidate.Highlight();
                GrabGlow.Mark(_root.transform);
            }
            if (canGrab && gap <= reach * 4f) candidate.BlockAimGrab();
            if (!reachable && !candidate.Busy && candidate.GripEdge == 1 && gap <= reach * 5f) ReportMiss(candidate, canGrab, gap, reach, scale);
            if (reachable && candidate.GripEdge == 1 && (grabbing == null || gap < grabGap))
            {
                grabbing = candidate;
                grabGap = gap;
            }
        }

        private bool TryPass(VrHand from, VrHand to, VrHand exclude, BoardManager board, float reach)
        {
            if (_state != KnifeState.Held || to == null || to == from || to == exclude || board.ChoosingSacrifices || board.ChoosingSlot) return false;
            if (!to.Ready || to.Busy || to.GripEdge != 1 || HandGap(to) > reach) return false;
            to.GripEdge = 0;
            _holder = to;
            _carving = false;
            _havePrevious = false;
            _haveTip = false;
            from.Play(Feel.Tick, 0.6f);
            to.Play(Feel.Tap);
            _logger.LogInfo("Sacrifice knife passed from the " + from.Name + " hand to the " + to.Name + " hand.");
            return true;
        }

        internal void Tick(CardGrabber grabber, VrHand left, VrHand right, VrHand preferred, VrHand exclude, PlayerHand hand, BoardManager board, bool show, bool canGrab,
            bool keep, bool offerTip, float scale, float reach, float dt)
        {
            if (_leaving)
            {
                if (!show) return;
                Forget();
            }
            VrHand second = preferred == left ? right : left;
            if (!show || grabber.PlayerSlots.Count == 0 || !EnsureModel(grabber.TableLayer))
            {
                Hide();
                return;
            }
            if (!_root.activeSelf) _root.SetActive(true);
            _scale = scale;
            _length = _lengthMeters * scale;
            _root.transform.localScale = Vector3.one * _length;
            _allSlots = board.AllSlotsCopy;
            _board = board;

            Vector3 storedPosition;
            Quaternion storedRotation;
            StoredPose(grabber, out storedPosition, out storedRotation);
            if (!_entered)
            {
                _entered = true;
                _enter = _state == KnifeState.Stored && !_rest ? 0f : 1f;
                if (_enter < 1f) VrLog.Chatter(_logger, "Sacrifice knife slides onto the table.");
            }
            bool arrived = _enter >= 1f;
            bool open = board.ChoosingSacrifices && !SacrificesSatisfied(board);
            if (!board.ChoosingSacrifices) _marked.Clear();

            switch (_state)
            {
                case KnifeState.Stored:
                    if (_rest)
                    {
                        _position = _restPosition;
                        _rotation = _restRotation;
                        if (!Supported(grabber))
                        {
                            BeginFall(Vector3.zero);
                            break;
                        }
                    }
                    else if (arrived)
                    {
                        _position = storedPosition;
                        _rotation = storedRotation;
                    }
                    else
                    {
                        _enter = Mathf.Min(1f, _enter + dt / EnterSeconds);
                        float eased = 1f - (1f - _enter) * (1f - _enter);
                        Vector3 awayPosition;
                        Quaternion awayRotation;
                        AwayPose(storedPosition, storedRotation, out awayPosition, out awayRotation);
                        _position = Vector3.LerpUnclamped(awayPosition, storedPosition, eased);
                        _rotation = Quaternion.Slerp(awayRotation, storedRotation, eased);
                    }
                    VrHand knifeHand = null;
                    float grabGap = 0f;
                    Offer(preferred, exclude, canGrab && arrived, reach, scale, ref knifeHand, ref grabGap);
                    Offer(second, exclude, canGrab && arrived, reach, scale, ref knifeHand, ref grabGap);
                    if (offerTip && arrived && (preferred.Ready || second.Ready))
                        VrTips.Show("knife.grab", PointAt(0f) + Vector3.up * (0.03f * scale),
                            "To pay blood, hold the card in one hand and grab the dagger with the other.", 80);
                    if (knifeHand != null)
                    {
                        knifeHand.GripEdge = 0;
                        _holder = knifeHand;
                        _state = _stuck ? KnifeState.Held : KnifeState.Pickup;
                        _stuckSince = Time.unscaledTime;
                        _rest = false;
                        _progress = 0f;
                        _fromPosition = _position;
                        _fromRotation = _rotation;
                        _havePrevious = false;
                        _haveTip = false;
                        _haveVelocity = false;
                        _velocity = Vector3.zero;
                        _touching.Clear();
                        knifeHand.Play(Feel.Tap);
                        VrSounds.Play("sharp_object_up#1", MixerGroup.TableObjectsSFX, PointAt(1f), 0.5f);
                        VrTips.Done("knife.grab");
                        _logger.LogInfo("Sacrifice knife picked up by the handle with the " + knifeHand.Name + " hand.");
                    }
                    break;
                case KnifeState.Pickup:
                case KnifeState.Held:
                    VrHand holder = _holder;
                    if (holder == null)
                    {
                        Release(null);
                        break;
                    }
                    if (!holder.Tracked)
                    {
                        Release(holder.Name + " controller lost tracking");
                        break;
                    }
                    if (!keep)
                    {
                        Release("the battle ended");
                        break;
                    }
                    if (TryPass(holder, holder == left ? right : left, exclude, board, reach)) holder = _holder;
                    if (!holder.Ready)
                    {
                        holder.Claimed = true;
                        break;
                    }
                    if (!holder.Grip.Held)
                    {
                        if (open) Cancel(board, holder, "knife released");
                        Release(null);
                        break;
                    }
                    holder.Claimed = true;
                    holder.SetHoldPose(HandHold.Fist);
                    Held(grabber, holder, hand, board, open, scale, dt);
                    TrackVelocity(dt);
                    if (_state == KnifeState.Held && open && !_stuck && grabber.PlayerSlots.Count > 0)
                        VrTips.Show("knife.cut", grabber.PlayerRowCenter + Vector3.up * (0.03f * scale), "Stab one of your creatures, or drag the blade across several, to sacrifice them. Let go to cancel.", 85);
                    break;
            }

            if (_state == KnifeState.Falling) Fall(grabber, scale, dt);

            if (_state == KnifeState.Returning)
            {
                Vector3 targetPosition = _rest ? _restPosition : storedPosition;
                Quaternion targetRotation = _rest ? _restRotation : storedRotation;
                _progress = Mathf.Min(1f, _progress + dt / _returnSeconds);
                float k = Mathf.SmoothStep(0f, 1f, _progress);
                _position = Vector3.Lerp(_fromPosition, targetPosition, k);
                _rotation = Quaternion.Slerp(_fromRotation, targetRotation, k);
                if (_progress >= 1f) _state = KnifeState.Stored;
            }

            bool hovering = Holding && grabber.HaveTable && board.ChoosingSacrifices && !SacrificesSatisfied(board);
            PlayableCard knifeCard = hovering ? CardAbove(PointAt(1f), HoverHeightMeters * scale) : null;
            SetHover(_gaze.Tick(_allSlots, _isMarked, grabber.HaveTable && open, Holding, _holder, PointAt(0f), PointAt(1f), knifeCard, scale));

            _root.transform.SetPositionAndRotation(_position, _rotation);
        }

        internal void Leave(float dt)
        {
            if (!_leaving)
            {
                if (_root == null || !_root.activeSelf || !_entered)
                {
                    Forget();
                    return;
                }
                _leaving = true;
                _leave = 0f;
                _leaveFrom = _position;
                _leaveFromRotation = _rotation;
                AwayPose(_position, _rotation, out _leaveTo, out _leaveToRotation);
                if (Holding) _logger.LogInfo("Sacrifice knife slipped out of the " + (_holder != null ? _holder.Name : "") + " hand: the battle ended.");
                _state = KnifeState.Stored;
                _holder = null;
                _rest = false;
                Unstick();
                _touching.Clear();
                _marked.Clear();
                SetHover(null);
                VrLog.Chatter(_logger, "Sacrifice knife slides off the table.");
            }
            if (_root == null)
            {
                _leaving = false;
                return;
            }
            _leave = Mathf.Min(1f, _leave + dt / LeaveSeconds);
            float eased = _leave * _leave;
            _root.transform.SetPositionAndRotation(Vector3.LerpUnclamped(_leaveFrom, _leaveTo, eased), Quaternion.Slerp(_leaveFromRotation, _leaveToRotation, eased));
            if (_leave < 1f) return;
            Forget();
        }

        private void AwayPose(Vector3 position, Quaternion rotation, out Vector3 awayPosition, out Quaternion awayRotation)
        {
            awayPosition = position + (_awaySide * AwaySideMeters + Vector3.up * AwayUpMeters) * _scale;
            Vector3 lean = Vector3.Slerp(Vector3.up, _awaySide, AwayTiltDegrees / 90f);
            awayRotation = Quaternion.FromToRotation(Vector3.up, lean) * rotation;
        }

        private void Held(CardGrabber grabber, VrHand knifeHand, PlayerHand hand, BoardManager board, bool open, float scale, float dt)
        {
            Vector3 handPosition;
            Quaternion handRotation;
            HandPose(knifeHand, out handPosition, out handRotation);

            if (_state == KnifeState.Pickup)
            {
                _progress = Mathf.Min(1f, _progress + dt / PickupSeconds);
                float k = Mathf.SmoothStep(0f, 1f, _progress);
                _position = Vector3.Lerp(_fromPosition, handPosition, k);
                _rotation = Quaternion.Slerp(_fromRotation, handRotation, k);
                if (_progress >= 1f) _state = KnifeState.Held;
                return;
            }

            HeldPose(grabber, knifeHand, hand, board, open, handPosition, handRotation, scale, dt);
            knifeHand.Pin(handPosition, handRotation, _position, _rotation);
        }

        private void HeldPose(CardGrabber grabber, VrHand knifeHand, PlayerHand hand, BoardManager board, bool open, Vector3 handPosition, Quaternion handRotation,
            float scale, float dt)
        {
            if (_stuck && UpdateStuck(grabber, knifeHand, handPosition, handRotation, scale)) return;
            if (_carving && UpdateCarve(grabber, knifeHand, board, open, handPosition, handRotation, scale, dt)) return;

            _position = handPosition;
            _rotation = handRotation;
            if (!grabber.HaveTable) return;

            Vector3 tip = PointAt(1f);
            float downSpeed = _haveTip && dt > 0f ? (_lastTip.y - tip.y) / dt : 0f;
            _lastTip = tip;
            _haveTip = true;
            if (grabber.InsideTable(tip))
            {
                CardSlot under;
                float surface = SurfaceAt(grabber, tip, out under);
                if (tip.y <= surface && downSpeed > StabMetersPerSecond * scale && Forward.y < -CarveSteepness)
                {
                    if (Forward.y < -StickSteepness) Stab(grabber, knifeHand, hand, board, open, under, tip, surface, scale);
                    else
                    {
                        BeginCarve(knifeHand, tip, under != null, "a slanted stab");
                        knifeHand.Play(Feel.Snap, 0.5f);
                        VrSounds.Play("metal_object_short#1", MixerGroup.TableObjectsSFX, tip, 0.5f);
                        UpdateCarve(grabber, knifeHand, board, open, handPosition, handRotation, scale, dt);
                    }
                    return;
                }
            }
            Block(grabber);
            if (open) DetectSlash(grabber, knifeHand, board, scale);
        }

        private void HandPose(VrHand knifeHand, out Vector3 position, out Quaternion rotation)
        {
            if (knifeHand.HasFist)
            {
                rotation = knifeHand.FistRotation;
                position = knifeHand.FistPosition - rotation * Vector3.forward * (GripCenterFraction * _length);
                return;
            }
            Vector3 direction = knifeHand.GripRotation * Vector3.back;
            rotation = Quaternion.LookRotation(direction, knifeHand.GripRotation * Vector3.down);
            position = knifeHand.GripPosition - direction * (GripCenterFraction * _length);
        }

        private void StoredPose(CardGrabber grabber, out Vector3 position, out Quaternion rotation)
        {
            List<CardSlot> slots = grabber.PlayerSlots;
            Vector3 toward = grabber.TowardPlayer;
            Vector3 side = -Vector3.Cross(Vector3.up, -toward) * (_rightSide ? -1f : 1f);
            if (side.sqrMagnitude > 1e-6f) _awaySide = side.normalized;
            Transform extreme = slots[0].transform;
            for (int i = 1; i < slots.Count; i++)
                if (slots[i] != null && Vector3.Dot(slots[i].transform.position, side) > Vector3.Dot(extreme.position, side)) extreme = slots[i].transform;
            Vector3 spot = extreme.position + side * (_lanesSide * grabber.Spacing) + toward * (_lanesToward * grabber.Spacing + StoredTowardMeters * _scale);
            spot.y = grabber.TableY;
            Vector3 tip = spot - Vector3.up * (StoredEmbedFraction * _length);
            Vector3 direction = (Vector3.down - toward * 0.3f).normalized;
            rotation = Quaternion.LookRotation(direction, toward);
            position = tip - direction * _length;
        }

        private float SurfaceAt(CardGrabber grabber, Vector3 point, out CardSlot under)
        {
            under = null;
            float best = float.NegativeInfinity;
            float margin = BladeRadiusMeters * _scale;
            float cap = grabber.TableY + 0.1f * grabber.Spacing;
            for (int i = 0; i < _allSlots.Count; i++)
            {
                CardSlot slot = _allSlots[i];
                if (slot == null || slot.Card == null) continue;
                Bounds bounds;
                if (!HandReach.CardBounds(slot.Card, out bounds)) continue;
                if (point.x < bounds.min.x - margin || point.x > bounds.max.x + margin || point.z < bounds.min.z - margin || point.z > bounds.max.z + margin) continue;
                float top = Mathf.Min(bounds.max.y, cap);
                if (top <= best) continue;
                best = top;
                under = slot;
            }
            return Mathf.Max(grabber.TableY, best);
        }

        private void Block(CardGrabber grabber)
        {
            float lift = 0f;
            CardSlot under;
            for (int i = 0; i < BodySamples; i++)
            {
                Vector3 point = PointAt(i / (float)(BodySamples - 1));
                if (!grabber.InsideTable(point)) continue;
                lift = Mathf.Max(lift, SurfaceAt(grabber, point, out under) - point.y);
            }
            if (lift > 0f) _position += Vector3.up * lift;
        }

        private void Embed(CardSlot under, Vector3 tip, float surface, float scale)
        {
            _stuck = true;
            _stuckTip = new Vector3(tip.x, surface - EmbedMeters * scale, tip.z);
            _stuckInCard = under != null && under.Card != null;
            _stuckCard = _stuckInCard ? under.Card : null;
            _stuckCardPosition = _stuckInCard ? _stuckCard.transform.position : Vector3.zero;
            _havePrevious = false;
            _haveTip = false;
            _position = _stuckTip - Forward * _length;
            _stuckSince = Time.unscaledTime;
        }

        private void Stab(CardGrabber grabber, VrHand knifeHand, PlayerHand hand, BoardManager board, bool open, CardSlot under, Vector3 tip, float surface, float scale)
        {
            Embed(under, tip, surface, scale);
            if (_stuckInCard) knifeHand.Play(Feel.Snap, 0.8f);
            else
            {
                knifeHand.Play(Feel.Thunk, 0.75f);
                VrSounds.Play("metal_object_short#1", MixerGroup.TableObjectsSFX, tip, 0.8f);
            }

            if (!open) return;
            if (_stuckInCard && grabber.PlayerSlots.Contains(under))
            {
                _lastContactTime = Time.unscaledTime;
                Cut(under, knifeHand, board);
                return;
            }
            if (_stuckInCard) return;
            if (Time.unscaledTime - _lastContactTime > 0.25f && !NearOccupiedSlot(tip, grabber.Spacing) &&
                !NearCard(hand.ChoosingSlotCard, tip, BladeRadiusMeters * scale * 3f))
                Cancel(board, knifeHand, "empty spot stabbed");
        }

        private bool UpdateStuck(CardGrabber grabber, VrHand knifeHand, Vector3 handPosition, Quaternion handRotation, float scale)
        {
            if (_stuckInCard && (_stuckCard == null || !_stuckCard.gameObject.activeInHierarchy ||
                (_stuckCard.transform.position - _stuckCardPosition).sqrMagnitude > Mathf.Pow(StuckCardMoveLanes * grabber.Spacing, 2f)))
            {
                Unstick();
                return false;
            }
            Vector3 handForward = handRotation * Vector3.forward;
            Vector3 grip = handPosition + handForward * (GripCenterFraction * _length);
            Vector3 toTip = _stuckTip - grip;
            float distance = toTip.magnitude;
            float seated = (1f - GripCenterFraction) * _length;
            if (distance > seated + PullOutMeters * scale)
            {
                Vector3 tip = _stuckTip;
                Unstick();
                knifeHand.Play(Feel.Snap, 0.9f);
                VrSounds.Play("sharp_object_up#2", MixerGroup.TableObjectsSFX, tip, 0.8f);
                VrTips.Done("knife.stuck");
                _logger.LogInfo("Sacrifice knife pulled free.");
                return false;
            }
            Vector3 direction = distance > 1e-5f ? toTip / distance : handForward;
            if (direction.y > -LeanSteepness)
            {
                PlayableCard card = _stuckInCard ? _stuckCard : null;
                Vector3 tip = _stuckTip;
                Unstick();
                if (card != null) _touching.Add(card);
                BeginCarve(knifeHand, tip, card != null, "leaned out of the " + (card != null ? "card" : "table"));
                knifeHand.Play(Feel.Tick, 0.7f);
                VrTips.Done("knife.stuck");
                return false;
            }
            float strain = Mathf.Clamp01((distance - seated) / (PullOutMeters * scale));
            if (strain > 0.05f) knifeHand.Grit(0.15f + 0.85f * strain);
            if (Time.unscaledTime - _stuckSince > 1.2f)
                VrTips.Show("knife.stuck", _stuckTip + Vector3.up * (0.06f * scale), "The knife is stuck. Pull it straight out, or lean it over to drag it.", 90);
            _rotation = Quaternion.FromToRotation(handForward, direction) * handRotation;
            _position = _stuckTip - direction * _length;
            return true;
        }

        private void Unstick()
        {
            _stuck = false;
            _stuckInCard = false;
            _stuckCard = null;
            _carving = false;
            _havePrevious = false;
            _haveTip = false;
        }

        private void BeginCarve(VrHand knifeHand, Vector3 tip, bool onCard, string reason)
        {
            _carving = true;
            _carveTip = tip;
            _carveOnCard = onCard;
            Vector3 heading = Vector3.ProjectOnPlane(Forward, Vector3.up);
            _carveHeading = heading.sqrMagnitude > 1e-8f ? heading.normalized : Vector3.forward;
            _havePrevious = false;
            _haveTip = false;
            _logger.LogInfo("Sacrifice knife dragging along the " + (onCard ? "card" : "table") + " (" + reason + ") with the " + knifeHand.Name + " hand.");
        }

        private void EndCarve(VrHand knifeHand, string reason)
        {
            _carving = false;
            _havePrevious = false;
            _haveTip = false;
            knifeHand.Play(Feel.Tick, 0.5f);
            VrLog.Chatter(_logger, "Sacrifice knife stopped dragging (" + reason + ").");
        }

        private bool UpdateCarve(CardGrabber grabber, VrHand knifeHand, BoardManager board, bool open, Vector3 handPosition, Quaternion handRotation, float scale, float dt)
        {
            if (!grabber.HaveTable)
            {
                EndCarve(knifeHand, "no table found");
                return false;
            }
            Vector3 handForward = handRotation * Vector3.forward;
            Vector3 grip = handPosition + handForward * (GripCenterFraction * _length);
            float seated = (1f - GripCenterFraction) * _length;
            float depth = CarveDepthMeters * scale;
            CardSlot under;
            float plane = SurfaceAt(grabber, _carveTip, out under) - depth;
            float height = grip.y - plane;
            if (height > seated + CarveLiftMeters * scale)
            {
                EndCarve(knifeHand, "lifted");
                return false;
            }

            Vector3 flat = _carveTip - grip;
            flat.y = 0f;
            if (flat.sqrMagnitude > 1e-8f) _carveHeading = flat.normalized;
            float rise = Mathf.Clamp(height, 0f, seated);
            float spread = Mathf.Sqrt(seated * seated - rise * rise);
            Vector3 tip = grip + _carveHeading * spread;
            if (!grabber.InsideTable(tip))
            {
                EndCarve(knifeHand, "it left the table");
                return false;
            }
            tip.y = SurfaceAt(grabber, tip, out under) - depth;

            bool onCard = under != null;
            if (onCard != _carveOnCard) knifeHand.Play(Feel.Tick, 0.5f);
            _carveOnCard = onCard;
            Vector3 moved = tip - _carveTip;
            moved.y = 0f;
            float speed = dt > 0f ? moved.magnitude / dt : 0f;
            if (speed > 0.02f * scale)
                knifeHand.Grit((onCard ? 0.25f : 0.15f) + (onCard ? 0.75f : 0.5f) * Mathf.Clamp01(speed / (CarveGritMetersPerSecond * scale)));
            _carveTip = tip;

            Vector3 toTip = tip - grip;
            float distance = toTip.magnitude;
            Vector3 direction = distance > 1e-5f ? toTip / distance : handForward;
            _rotation = Quaternion.FromToRotation(handForward, direction) * handRotation;
            _position = tip - direction * _length;
            if (open) DetectSlash(grabber, knifeHand, board, scale);
            return true;
        }

        private void DetectSlash(CardGrabber grabber, VrHand knifeHand, BoardManager board, float scale)
        {
            FillSamples(_current);
            float radius = BladeRadiusMeters * scale;
            _touchingNow.Clear();
            List<CardSlot> slots = grabber.PlayerSlots;
            for (int i = 0; i < slots.Count; i++)
            {
                CardSlot slot = slots[i];
                if (slot == null || slot.Card == null) continue;
                PlayableCard card = slot.Card;
                if (!Touches(card, radius)) continue;
                _touchingNow.Add(card);
                _lastContactTime = Time.unscaledTime;
                if (_touching.Contains(card)) continue;
                Cut(slot, knifeHand, board);
            }
            HashSet<PlayableCard> swap = _touching;
            _touching = _touchingNow;
            _touchingNow = swap;
            Array.Copy(_current, _previous, BladeSamples);
            _havePrevious = true;
        }

        private void Cut(CardSlot slot, VrHand knifeHand, BoardManager board)
        {
            PlayableCard card = slot.Card;
            if (card == null) return;
            if (!card.CanBeSacrificed)
            {
                slot.CursorSelectStart();
                knifeHand.Play(Feel.Reject);
                return;
            }
            if (IsMarked(board, slot))
            {
                knifeHand.Play(Feel.Tick, 0.6f);
                return;
            }
            slot.CursorSelectStart();
            if (SacrificesField != null && !IsMarked(board, slot)) return;
            _marked.Add(slot);
            knifeHand.Play(Feel.Snap);
            VrTips.Done("knife.cut");
            _logger.LogInfo("Sacrifice knife cut " + card.name + " in lane " + slot.Index + ".");
        }

        private void FillSamples(Vector3[] samples)
        {
            for (int i = 0; i < BladeSamples; i++)
                samples[i] = PointAt(Mathf.Lerp(BladeStartFraction, 1f, i / (float)(BladeSamples - 1)));
        }

        private bool Touches(PlayableCard card, float radius)
        {
            Bounds bounds;
            bool haveBounds = HandReach.CardBounds(card, out bounds);
            float limit = radius * radius;
            int steps = _havePrevious ? SweepSteps : 0;
            for (int step = 0; step <= steps; step++)
            {
                float t = steps == 0 ? 1f : (step + 1) / (float)(steps + 1);
                for (int i = 0; i < BladeSamples; i++)
                {
                    Vector3 sample = _havePrevious ? Vector3.Lerp(_previous[i], _current[i], t) : _current[i];
                    Vector3 closest = haveBounds ? bounds.ClosestPoint(sample) : card.transform.position;
                    float allowed = haveBounds ? limit : limit * 16f;
                    if ((closest - sample).sqrMagnitude <= allowed) return true;
                }
            }
            return false;
        }

        private bool NearOccupiedSlot(Vector3 point, float spacing)
        {
            float limit = 0.5f * spacing;
            for (int i = 0; i < _allSlots.Count; i++)
            {
                CardSlot slot = _allSlots[i];
                if (slot == null || slot.Card == null) continue;
                Vector3 offset = slot.transform.position - point;
                offset.y = 0f;
                if (offset.sqrMagnitude < limit * limit) return true;
            }
            return false;
        }

        private PlayableCard CardAbove(Vector3 tip, float height)
        {
            PlayableCard best = null;
            float bestDistance = float.PositiveInfinity;
            float margin = BladeRadiusMeters * _scale;
            for (int i = 0; i < _allSlots.Count; i++)
            {
                CardSlot slot = _allSlots[i];
                if (slot == null || !slot.IsPlayerSlot || slot.Card == null || !slot.Card.CanBeSacrificed) continue;
                Bounds bounds;
                if (!HandReach.CardBounds(slot.Card, out bounds)) continue;
                if (tip.x < bounds.min.x - margin || tip.x > bounds.max.x + margin || tip.z < bounds.min.z - margin || tip.z > bounds.max.z + margin) continue;
                if (tip.y > bounds.max.y + height) continue;
                Vector3 offset = bounds.center - tip;
                offset.y = 0f;
                float distance = offset.sqrMagnitude;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = slot.Card;
            }
            return best;
        }

        private void SetHover(PlayableCard card)
        {
            if (card == _hoverCard) return;
            if (_hoverCard != null && _hoverCard.Anim != null) _hoverCard.Anim.SetSacrificeHoverMarkerShown(false);
            _hoverCard = card;
            if (card != null && card.Anim != null) card.Anim.SetSacrificeHoverMarkerShown(true);
        }

        private static bool NearCard(PlayableCard card, Vector3 point, float distance)
        {
            Bounds bounds;
            if (!HandReach.CardBounds(card, out bounds)) return false;
            return bounds.SqrDistance(point) <= distance * distance;
        }

        private bool IsMarked(BoardManager board, CardSlot slot)
        {
            List<CardSlot> sacrifices = SacrificesField != null ? SacrificesField.GetValue(board) as List<CardSlot> : null;
            return sacrifices != null ? sacrifices.Contains(slot) : _marked.Contains(slot);
        }

        private static bool SacrificesSatisfied(BoardManager board)
        {
            PlayableCard card = board.CurrentSacrificeDemandingCard;
            if (card == null || card.Info == null || SacrificesField == null || SacrificeValueMethod == null) return false;
            List<CardSlot> sacrifices = SacrificesField.GetValue(board) as List<CardSlot>;
            if (sacrifices == null) return false;
            return (int)SacrificeValueMethod.Invoke(board, new object[] { sacrifices }) >= card.Info.BloodCost;
        }

        internal void Cancel(BoardManager board, VrHand feel, string reason)
        {
            if (CancelField == null || !board.ChoosingSacrifices || (bool)CancelField.GetValue(board) || SacrificesSatisfied(board)) return;
            CancelField.SetValue(board, true);
            feel.Play(Feel.Reject);
            _logger.LogInfo("Sacrifice cancelled (" + reason + "); the card returns to the hand.");
        }

        private void BeginReturn(string reason)
        {
            _rest = false;
            _returnSeconds = ReturnSeconds;
            _state = KnifeState.Returning;
            _progress = 0f;
            _fromPosition = _position;
            _fromRotation = _rotation;
            Unstick();
            _touching.Clear();
            if (reason != null) _logger.LogInfo("Sacrifice knife returned to its spot (" + reason + ").");
        }

        private void TrackVelocity(float dt)
        {
            if (_haveVelocity && dt > 0f)
                _velocity = Vector3.Lerp(_velocity, (_position - _lastPosition) / dt, 0.5f);
            _lastPosition = _position;
            _haveVelocity = true;
        }

        private void Release(string reason)
        {
            _touching.Clear();
            if (_stuck)
            {
                RestHere();
                _logger.LogInfo("Sacrifice knife left stuck in the " + (_stuckInCard ? "card" : "table") + (reason != null ? " (" + reason + ")" : "") + ".");
                return;
            }
            BeginFall(_haveVelocity ? _velocity : Vector3.zero);
            if (reason != null) _logger.LogInfo("Sacrifice knife dropped (" + reason + ").");
        }

        private void RestHere()
        {
            _rest = true;
            _restPosition = _position;
            _restRotation = _rotation;
            _state = KnifeState.Stored;
        }

        private void BeginFall(Vector3 velocity)
        {
            Unstick();
            _rest = false;
            _state = KnifeState.Falling;
            _velocity = velocity;
            _fallTime = 0f;
        }

        private void Fall(CardGrabber grabber, float scale, float dt)
        {
            if (dt <= 0f) return;
            if (!grabber.HaveTable)
            {
                BeginReturn("no table found");
                return;
            }
            _fallTime += dt;
            _velocity += Vector3.down * (9.81f * scale * dt);
            _velocity *= 1f / (1f + AirDrag * dt);
            _position += _velocity * dt;

            Vector3 tip = PointAt(1f);
            CardSlot under;
            if (grabber.InsideTable(tip))
            {
                float surface = SurfaceAt(grabber, tip, out under);
                if (tip.y <= surface && -_velocity.y > StabMetersPerSecond * scale && Forward.y < -StickSteepness)
                {
                    Embed(under, tip, surface, scale);
                    RestHere();
                    VrSounds.Play("metal_object_short#1", MixerGroup.TableObjectsSFX, tip, 0.7f);
                    _logger.LogInfo("Sacrifice knife fell and stuck in the " + (_stuckInCard ? "card" : "table") + ".");
                    return;
                }
            }

            for (int i = 0; i < BodySamples; i++)
            {
                Vector3 point = PointAt(i / (float)(BodySamples - 1));
                if (!grabber.InsideTable(point)) continue;
                if (point.y > SurfaceAt(grabber, point, out under)) continue;
                LandFlat(grabber);
                return;
            }

            if (_position.y < grabber.TableY - 1.5f * grabber.Spacing || _fallTime > MaxFallSeconds)
                BeginReturn("it fell off the table");
        }

        private void LandFlat(CardGrabber grabber)
        {
            Vector3 along = Vector3.ProjectOnPlane(Forward, Vector3.up);
            if (along.sqrMagnitude < 1e-6f) along = Vector3.ProjectOnPlane(_rotation * Vector3.up, Vector3.up);
            if (along.sqrMagnitude < 1e-6f) along = grabber.TowardPlayer;
            along.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, along);
            if (Vector3.Dot(side, _rotation * Vector3.up) < 0f) side = -side;
            Quaternion rotation = Quaternion.LookRotation(along, side);
            Vector3 middle = PointAt(0.5f);
            Vector3 position = middle - along * (0.5f * _length);
            position.y = FlatHeight(grabber, position, along);

            _rest = true;
            _restPosition = position;
            _restRotation = rotation;
            _returnSeconds = SettleSeconds;
            _state = KnifeState.Returning;
            _progress = 0f;
            _fromPosition = _position;
            _fromRotation = _rotation;
            VrSounds.Play("metal_object_short#1", MixerGroup.TableObjectsSFX, middle, 0.45f);
            _logger.LogInfo("Sacrifice knife fell onto the table and lies where it landed.");
        }

        private float FlatHeight(CardGrabber grabber, Vector3 pommel, Vector3 along)
        {
            CardSlot under;
            float surface = grabber.TableY;
            for (int i = 0; i < BodySamples; i++)
            {
                Vector3 point = pommel + along * (_length * i / (BodySamples - 1));
                if (grabber.InsideTable(point)) surface = Mathf.Max(surface, SurfaceAt(grabber, point, out under));
            }
            return surface + FlatLiftFraction * _length;
        }

        private bool Supported(CardGrabber grabber)
        {
            if (!grabber.HaveTable) return true;
            if (_stuck)
            {
                if (!_stuckInCard) return true;
                return _stuckCard != null && _stuckCard.gameObject.activeInHierarchy &&
                    (_stuckCard.transform.position - _stuckCardPosition).sqrMagnitude <= Mathf.Pow(StuckCardMoveLanes * grabber.Spacing, 2f);
            }
            Vector3 along = _restRotation * Vector3.forward;
            float height = FlatHeight(grabber, _restPosition, along);
            float lift = FlatLiftFraction * _length;
            if (height > _restPosition.y + 0.5f * lift)
            {
                _restPosition.y = height;
                _position = _restPosition;
                return true;
            }
            return height >= _restPosition.y - 0.3f * lift;
        }

        internal void Forget()
        {
            _rest = false;
            _leaving = false;
            _entered = false;
            _enter = 1f;
            Hide();
        }

        internal void Hide()
        {
            if (_state != KnifeState.Stored || !_rest)
            {
                _state = KnifeState.Stored;
                _rest = false;
                Unstick();
            }
            _touching.Clear();
            _marked.Clear();
            SetHover(null);
            if (_root != null && _root.activeSelf) _root.SetActive(false);
        }

        internal void Dispose()
        {
            SetHover(null);
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        private bool EnsureModel(int layer)
        {
            if (_root != null) return true;
            if (_buildFailed) return false;
            try
            {
                Build(layer);
                return true;
            }
            catch (Exception exception)
            {
                _buildFailed = true;
                if (_root != null) UnityEngine.Object.Destroy(_root);
                _root = null;
                _logger.LogWarning("Sacrifice knife could not be built: " + exception.Message);
                return false;
            }
        }

        private void Build(int layer)
        {
            _root = CreateModel("InscryptionVR Sacrifice Knife", layer, _logger, out _renderers);
        }

        internal static GameObject CreateModel(string name, int layer, ManualLogSource logger, out Renderer[] renderers)
        {
            GameObject root = new GameObject(name);
            root.SetActive(false);
            GameObject holder = new GameObject("Model Holder");
            holder.SetActive(false);
            holder.transform.SetParent(root.transform, false);

            bool built = false;
            GameObject prefab = LoadDaggerPrefab();
            if (prefab != null)
            {
                GameObject model = UnityEngine.Object.Instantiate(prefab, holder.transform, false);
                Strip(model);
                if (Fit(model.transform, logger, name)) built = true;
                else UnityEngine.Object.DestroyImmediate(model);
            }
            if (!built)
            {
                BuildFallback(holder.transform);
                logger.LogInfo(name + " uses a simple stand-in model; the game's dagger was not available.");
            }

            SetLayer(root.transform, layer);
            renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderers[i].receiveShadows = true;
            }
            holder.SetActive(true);
            return root;
        }

        private static bool Fit(Transform model, ManualLogSource logger, string name)
        {
            MeshFilter filter = null;
            MeshFilter[] filters = model.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i].sharedMesh == null) continue;
                if (filter == null || filters[i].sharedMesh.name == "dagger") filter = filters[i];
            }
            if (filter == null) return false;
            model.localPosition = Vector3.zero;
            model.localRotation = Quaternion.identity;
            model.localScale = Vector3.one;
            Bounds bounds = filter.sharedMesh.bounds;
            Vector3 pommel = model.InverseTransformPoint(filter.transform.TransformPoint(bounds.center + Vector3.up * bounds.extents.y));
            Vector3 tip = model.InverseTransformPoint(filter.transform.TransformPoint(bounds.center - Vector3.up * bounds.extents.y));
            Vector3 axis = tip - pommel;
            float length = axis.magnitude;
            if (length < 1e-4f) return false;
            axis /= length;
            Vector3 spine = Vector3.ProjectOnPlane(Vector3.left, axis);
            if (spine.sqrMagnitude < 1e-6f) return false;
            Quaternion align = Quaternion.Inverse(Quaternion.LookRotation(axis, spine.normalized));
            model.localRotation = align;
            model.localScale = Vector3.one / length;
            model.localPosition = -(align * pommel) / length;
            logger.LogInfo(name + " uses the game's dagger (" + filter.sharedMesh.name + "), pommel at the hand's index side and blade out of the little-finger side.");
            return true;
        }

        private static GameObject LoadDaggerPrefab()
        {
            try
            {
                GameObject prefab = ResourceBank.Get<GameObject>("Prefabs/Items/SpecialDaggerItem");
                if (prefab != null) return prefab;
                ConsumableItemData data = ItemsUtil.GetConsumableByName("SpecialDagger");
                return data != null && !string.IsNullOrEmpty(data.PrefabId) ? ResourceBank.Get<GameObject>("Prefabs/Items/" + data.PrefabId) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void Strip(GameObject model)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                Component[] components = model.GetComponentsInChildren<Component>(true);
                for (int i = components.Length - 1; i >= 0; i--)
                {
                    Component component = components[i];
                    if (component == null || component is Transform || component is MeshFilter || component is MeshRenderer || component is SkinnedMeshRenderer) continue;
                    if (pass == 0 && !(component is MonoBehaviour)) continue;
                    UnityEngine.Object.DestroyImmediate(component);
                }
            }
        }

        private static void BuildFallback(Transform parent)
        {
            GameObject model = new GameObject("Fallback Knife");
            model.transform.SetParent(parent, false);
            AddPart(model.transform, PrimitiveType.Cube, new Vector3(0f, 0f, 0.745f), new Vector3(0.012f, 0.05f, 0.51f), Quaternion.identity, new Color(0.62f, 0.6f, 0.56f));
            AddPart(model.transform, PrimitiveType.Cube, new Vector3(0f, 0f, 0.475f), new Vector3(0.04f, 0.13f, 0.03f), Quaternion.identity, new Color(0.3f, 0.26f, 0.2f));
            AddPart(model.transform, PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.24f), new Vector3(0.05f, 0.22f, 0.05f), Quaternion.Euler(90f, 0f, 0f), new Color(0.24f, 0.15f, 0.09f));
            AddPart(model.transform, PrimitiveType.Sphere, new Vector3(0f, 0f, 0.025f), new Vector3(0.06f, 0.06f, 0.05f), Quaternion.identity, new Color(0.3f, 0.26f, 0.2f));
        }

        private static void AddPart(Transform parent, PrimitiveType type, Vector3 position, Vector3 scale, Quaternion rotation, Color color)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            Collider collider = part.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localRotation = rotation;
            part.transform.localScale = scale;
            Renderer renderer = part.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null)
            {
                Material material = new Material(renderer.sharedMaterial);
                if (material.HasProperty("_Color")) material.color = color;
                renderer.sharedMaterial = material;
            }
        }

        private static void SetLayer(Transform transform, int layer)
        {
            transform.gameObject.layer = layer;
            for (int i = 0; i < transform.childCount; i++) SetLayer(transform.GetChild(i), layer);
        }
    }
}
