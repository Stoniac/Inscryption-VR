using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class SanctumHands : MonoBehaviour
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static readonly FieldInfo DeathCardField = typeof(DeathCardCreationSequencer).GetField("deathCard", Members);
        internal static readonly FieldInfo VictoryCardField = typeof(DeathCardCreationSequencer).GetField("deathcardVictory", Members);

        private const float HandOverCardHeights = 0.6f;
        private const float PutBackSeconds = 0.15f;
        private const float OfferReachMeters = 0.3f;
        private const float OfferPushMeters = 0.12f;
        private const float OfferConeDegrees = 45f;
        private const float OfferHoldSeconds = 0.25f;

        private sealed class Pull
        {
            internal SelectableCard Card;
            internal Carry Carry;
            internal float Size;
            internal bool Over;
            internal Vector3 LiftPosition;
            internal float OfferSince = -1f;
        }

        private ManualLogSource _logger;
        private VrInput _input;
        private CardGrabber _grabber;
        private bool _failed;
        private DeathCardCreationSequencer _sequencer;
        private float _nextRefresh;
        private readonly List<MainInputInteractable> _choices = new List<MainInputInteractable>();
        private readonly List<MainInputInteractable> _others = new List<MainInputInteractable>();
        private readonly List<SelectableCard> _children = new List<SelectableCard>();
        private Pull _leftPull;
        private Pull _rightPull;
        private MainInputInteractable _leftHover;
        private MainInputInteractable _rightHover;
        private bool _wasActive;
        private bool _loggedLeshy;
        private float _unavailableSince = -1f;
        private bool _frozen;
        private const float InputGraceSeconds = 1.5f;

        internal void Initialize(ManualLogSource logger, VrInput input, CardGrabber grabber)
        {
            _logger = logger;
            _input = input;
            _grabber = grabber;
            if (DeathCardField == null || VictoryCardField == null)
                _logger.LogWarning("Sanctum hands: the death card fields were not found; choose death card parts with the mouse.");
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                try { Release(); } catch (Exception) { }
                _logger.LogError("Sanctum hands stopped after an error; use the mouse there: " + exception);
            }
        }

        private void Tick()
        {
            if (!VrScenes.InSanctum)
            {
                if (_wasActive) Release();
                _wasActive = false;
                return;
            }
            _input.Refresh();
            bool input = _input.Active && _input.InputAvailable && Time.timeScale > 0f && CursorAllows();
            if (!_input.Active)
            {
                Release();
                return;
            }
            if (!_wasActive)
            {
                _wasActive = true;
                _nextRefresh = 0f;
                _logger.LogInfo("Sanctum hands ready: pick up a card and hold it out toward Leshy (or let go of it over your death card) to hand it over; grip anything else to use it.");
            }
            float scale = _input.Scale;
            Refresh();
            Collect();
            if (_input.InputAvailable) _unavailableSince = -1f;
            else if (_unavailableSince < 0f)
            {
                _unavailableSince = Time.unscaledTime;
                if (_leftPull != null || _rightPull != null) _logger.LogInfo("SteamVR input blipped while holding a death card choice; keeping it in hand for up to " + InputGraceSeconds + " s.");
            }
            _frozen = !_input.InputAvailable && Time.unscaledTime - _unavailableSince < InputGraceSeconds;
            VrHand left = _input.Left, right = _input.Right;
            Process(left, ref _leftPull, ref _leftHover, _rightHover, input && left.Tracked, scale);
            Process(right, ref _rightPull, ref _rightHover, _leftHover, input && right.Tracked, scale);
            left.Claimed = _leftPull != null;
            right.Claimed = _rightPull != null;
            if (input && _leftPull == null && _rightPull == null && _choices.Count > 0)
            {
                Bounds bounds;
                MainInputInteractable first = _choices[0];
                Vector3 anchor = HandReach.RendererBounds(first, out bounds) ? new Vector3(bounds.center.x, bounds.max.y, bounds.center.z) : first.transform.position;
                VrTips.Show("deathcard.choose", anchor, "Pick a card up and hold it out toward Leshy to hand it over.", 45);
            }
            if (_leftHover != null) _leftHover.CursorStay();
            if (_rightHover != null && _rightHover != _leftHover) _rightHover.CursorStay();
        }

        private static bool CursorAllows()
        {
            InteractionCursor cursor = Singleton<InteractionCursor>.Instance;
            return cursor == null || !cursor.InteractionDisabled;
        }

        private void Refresh()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;
            if (_sequencer == null) _sequencer = UnityEngine.Object.FindObjectOfType<DeathCardCreationSequencer>();
        }

        private void Collect()
        {
            _choices.Clear();
            _others.Clear();
            SelectableCard death = Field(DeathCardField);
            SelectableCard victory = Field(VictoryCardField);
            if (HandReach.Active(_sequencer))
            {
                _sequencer.GetComponentsInChildren(false, _children);
                for (int i = 0; i < _children.Count; i++)
                {
                    SelectableCard card = _children[i];
                    if (card == death || card == victory || !HandReach.Live(card) || !card.Enabled) continue;
                    _choices.Add(card);
                }
                _children.Clear();
            }
            MainInputInteractable[] all = Interactables.All;
            for (int i = 0; i < all.Length; i++)
            {
                MainInputInteractable target = all[i];
                if (!Interactables.Usable(target) || target is SelectableCard) continue;
                _others.Add(target);
            }
        }

        private SelectableCard Field(FieldInfo field)
        {
            if (field == null || _sequencer == null) return null;
            return field.GetValue(_sequencer) as SelectableCard;
        }

        private SelectableCard DeathCard()
        {
            SelectableCard card = Field(DeathCardField);
            return card != null && card.gameObject.activeInHierarchy ? card : null;
        }

        private void Process(VrHand hand, ref Pull pull, ref MainInputInteractable hover, MainInputInteractable other, bool ready, float scale)
        {
            if (pull != null)
            {
                SetHover(ref hover, null, other);
                Hold(hand, ref pull, ready);
                return;
            }
            if (!ready || hand.Busy)
            {
                SetHover(ref hover, null, other);
                return;
            }
            bool touch;
            MainInputInteractable chosen = Nearest(hand, scale, out touch);
            SetHover(ref hover, chosen, other);
            if (chosen == null) return;
            hand.Highlight();
            GrabGlow.Mark(chosen);
            if (hand.GripEdge != 1) return;
            hand.GripEdge = 0;
            SelectableCard card = chosen as SelectableCard;
            if (card != null && _choices.Contains(card)) Lift(hand, card, touch, ref pull, ref hover, other, scale);
            else Press(hand, chosen, touch);
        }

        private MainInputInteractable Nearest(VrHand hand, float scale, out bool touch)
        {
            float reach = _grabber.ReachMeters * scale;
            float best = reach * reach;
            MainInputInteractable chosen = null;
            Consider(_choices, hand, ref best, ref chosen);
            Consider(_others, hand, ref best, ref chosen);
            touch = chosen != null;
            if (chosen != null) return chosen;
            Collider hit = HandReach.Pointed(hand, scale);
            if (hit == null) return null;
            chosen = Hit(_choices, hit);
            return chosen != null ? chosen : Hit(_others, hit);
        }

        private static void Consider(List<MainInputInteractable> list, VrHand hand, ref float best, ref MainInputInteractable chosen)
        {
            for (int i = 0; i < list.Count; i++)
            {
                float distance = HandReach.InteractableSqrDistance(list[i], hand.Position);
                if (hand.HasIndexTip) distance = Mathf.Min(distance, HandReach.InteractableSqrDistance(list[i], hand.IndexTip));
                if (distance < best)
                {
                    best = distance;
                    chosen = list[i];
                }
            }
        }

        private static MainInputInteractable Hit(List<MainInputInteractable> list, Collider hit)
        {
            MainInputInteractable parent = null;
            for (int i = 0; i < list.Count; i++)
            {
                Transform target = list[i].transform;
                if (hit.transform == target) return list[i];
                if (parent == null && hit.transform.IsChildOf(target)) parent = list[i];
            }
            return parent;
        }

        private void Lift(VrHand hand, SelectableCard card, bool touch, ref Pull pull, ref MainInputInteractable hover, MainInputInteractable other, float scale)
        {
            SetHover(ref hover, null, other);
            Pull started = new Pull();
            started.Card = card;
            started.Size = Mathf.Max(HandReach.Size(card), 0.05f * scale);
            started.Carry = new Carry(card.transform, hand, touch, hand.HoldOffset(0.03f, scale), true);
            started.LiftPosition = hand.Position;
            pull = started;
            hand.Claimed = true;
            hand.SetHoldPose(HandHold.Pinch);
            hand.Play(Feel.Tick);
            VrSounds.Play("card", MixerGroup.CardPaperSFX, card.transform.position, 1f);
            _logger.LogInfo("Death card choice lifted with the " + hand.Name + " hand (" + (touch ? "touch" : "aim") + "): " + CardName(card) + ".");
        }

        private void Hold(VrHand hand, ref Pull pull, bool ready)
        {
            Pull p = pull;
            if (p.Card == null || !p.Card.gameObject.activeInHierarchy || !HandReach.Live(p.Card))
            {
                pull = null;
                return;
            }
            if (_frozen && hand.Tracked)
            {
                hand.Claimed = true;
                hand.SetHoldPose(HandHold.Pinch);
                p.Carry.Update();
                return;
            }
            if (!ready || !hand.Grip.Held)
            {
                pull = null;
                if (p.Over)
                {
                    HandOver(hand, p);
                    return;
                }
                p.Carry.PutBack(PutBackSeconds);
                hand.Play(Feel.Tick, 0.6f);
                return;
            }
            hand.Claimed = true;
            hand.SetHoldPose(HandHold.Pinch);
            p.Carry.Update();
            SelectableCard death = DeathCard();
            Vector3 leshy;
            bool haveLeshy = LeshyPoint(death, out leshy);
            if (haveLeshy)
                VrTips.Show("deathcard.handover", leshy, "Hold the card out toward Leshy to hand it over.", 40);
            if (haveLeshy && Offering(hand, p, leshy))
            {
                if (p.OfferSince < 0f)
                {
                    p.OfferSince = Time.unscaledTime;
                    hand.Play(Feel.Tick, 0.8f);
                }
                if (death != null) GrabGlow.Mark(death);
                hand.Rumble(0.15f + 0.35f * Mathf.Clamp01((Time.unscaledTime - p.OfferSince) / OfferHoldSeconds));
                if (Time.unscaledTime - p.OfferSince >= OfferHoldSeconds)
                {
                    pull = null;
                    HandOver(hand, p);
                    return;
                }
            }
            else p.OfferSince = -1f;
            bool over = death != null && OverCard(death, p);
            if (over) GrabGlow.Mark(death);
            if (over == p.Over) return;
            p.Over = over;
            if (over) hand.Play(Feel.Tick, 0.8f);
        }

        private bool Offering(VrHand hand, Pull p, Vector3 leshy)
        {
            Vector3 head;
            Quaternion view;
            float unitsPerMeter;
            StereoRenderer renderer = _grabber.Renderer;
            if (renderer == null || !renderer.TryGetHeadPose(out head, out view, out unitsPerMeter) || unitsPerMeter <= 0f) return false;
            Vector3 toLeshy = leshy - head;
            if (toLeshy.sqrMagnitude < 1e-6f) return false;
            if (!_loggedLeshy)
            {
                _loggedLeshy = true;
                _logger.LogInfo("Death card hand-over aims at a point " + (toLeshy.magnitude / unitsPerMeter).ToString("0.00") + " m from your eyes, " +
                    (toLeshy.y / unitsPerMeter).ToString("+0.00;-0.00") + " m above them.");
            }
            Vector3 direction = toLeshy.normalized;
            Vector3 held = hand.Position - head;
            if (held.magnitude < OfferReachMeters * unitsPerMeter) return false;
            if (Vector3.Angle(held, toLeshy) > OfferConeDegrees) return false;
            return Vector3.Dot(hand.Position - p.LiftPosition, direction) >= OfferPushMeters * unitsPerMeter;
        }

        private static bool LeshyPoint(SelectableCard death, out Vector3 point)
        {
            OpponentAnimationController opponent = OpponentAnimationController.Instance;
            Transform head = opponent != null && opponent.isActiveAndEnabled ? opponent.Head : null;
            if (head != null && head.gameObject.activeInHierarchy && head.gameObject.scene == SceneManagerActive())
            {
                point = head.position;
                return true;
            }
            if (death != null)
            {
                Transform holder = death.transform.parent != null ? death.transform.parent : death.transform;
                point = holder.position;
                return true;
            }
            point = Vector3.zero;
            return false;
        }

        private static UnityEngine.SceneManagement.Scene SceneManagerActive()
        {
            return UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        }

        private static bool OverCard(SelectableCard death, Pull p)
        {
            Bounds bounds;
            if (!HandReach.CardBounds(death, out bounds)) return false;
            float limit = HandOverCardHeights * p.Size;
            return bounds.SqrDistance(p.Card.transform.position) <= limit * limit;
        }

        private void HandOver(VrHand hand, Pull p)
        {
            SelectableCard card = p.Card;
            string name = CardName(card);
            card.CursorSelectStart();
            if (card != null) card.CursorSelectEnd();
            hand.Play(Feel.Thud);
            VrTips.Done("deathcard.choose");
            VrTips.Done("deathcard.handover");
            _logger.LogInfo("Handed " + name + " to the death card with the " + hand.Name + " hand.");
        }

        private void Press(VrHand hand, MainInputInteractable target, bool touch)
        {
            string name = target.name;
            target.CursorSelectStart();
            if (target != null) target.CursorSelectEnd();
            hand.Play(Feel.Tap);
            _logger.LogInfo("Sanctum: " + name + " used with the " + hand.Name + " hand (" + (touch ? "touch" : "aim") + ").");
        }

        private static void SetHover(ref MainInputInteractable mine, MainInputInteractable target, MainInputInteractable other)
        {
            Interactables.SetHover(ref mine, target, other);
        }

        private static string CardName(SelectableCard card)
        {
            return card != null && card.Info != null ? card.Info.name : card != null ? card.name : "a card";
        }

        private void Release()
        {
            PutBack(ref _leftPull);
            PutBack(ref _rightPull);
            SetHover(ref _leftHover, null, _rightHover);
            SetHover(ref _rightHover, null, _leftHover);
            _sequencer = null;
            _choices.Clear();
            _others.Clear();
        }

        private static void PutBack(ref Pull pull)
        {
            Pull p = pull;
            pull = null;
            if (p != null && p.Card != null && HandReach.Live(p.Card)) p.Carry.PutBack(PutBackSeconds);
        }
    }

    internal static class SanctumShutter
    {
        private static VrInput _input;
        private static FieldInfo _snapping;
        private static int _snapFrame = -1;

        internal static void Install(ManualLogSource logger, VrInput input)
        {
            _input = input;
            try
            {
                Type closures = typeof(SanctumSceneSequencer).GetNestedType("<>c", BindingFlags.Public | BindingFlags.NonPublic);
                if (closures == null) throw new MissingMemberException(typeof(SanctumSceneSequencer).FullName, "<>c");
                _snapping = typeof(SanctumSceneSequencer).GetField("allowCameraSnapping", BindingFlags.Instance | BindingFlags.NonPublic);
                int count = 0;
                foreach (FieldInfo field in closures.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.FieldType != typeof(Func<bool>) || !field.Name.StartsWith("<>9__")) continue;
                    field.SetValue(null, new Func<bool>(Pressed));
                    count++;
                }
                if (count == 0) throw new MissingFieldException(closures.FullName, "camera button wait");
                logger.LogInfo("Sanctum camera: either trigger works the shutter (" + count + " waits).");
            }
            catch (Exception exception)
            {
                logger.LogWarning("Sanctum camera controller input unavailable; click the mouse there instead: " + exception.Message);
            }
        }

        private static bool Pressed()
        {
            if (InputButtons.GetButtonDown(global::Button.Select)) return true;
            if (_input == null || !VrScenes.InSanctum) return false;
            _input.Refresh();
            bool pressed = _input.Active && (_input.Left.TriggerEdge == 1 || _input.Right.TriggerEdge == 1);
            if (pressed) Snap();
            return pressed;
        }

        private static void Snap()
        {
            if (_snapFrame == Time.frameCount) return;
            _snapFrame = Time.frameCount;
            SanctumSceneSequencer sequencer = UnityEngine.Object.FindObjectOfType<SanctumSceneSequencer>();
            if (sequencer == null || _snapping == null || !(bool)_snapping.GetValue(sequencer)) return;
            if (AudioController.Instance != null) AudioController.Instance.PlaySound2D("camera_photo_click");
            UIManager ui = Singleton<UIManager>.Instance;
            CrackedLensEffect lens = ui != null && ui.Effects != null ? ui.Effects.GetEffect<CrackedLensEffect>() : null;
            if (lens != null) lens.SnapShutter();
        }
    }
}
