using System.Collections.Generic;
using BepInEx.Logging;
using DiskCardGame;
using Pixelplacement;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class BoardLift
    {
        private sealed class Lift
        {
            internal PlayableCard Card;
            internal VrHand Hand;
            internal Transform Parent;
            internal Vector3 RestLocalPosition;
            internal Quaternion RestLocalRotation;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal Vector3 From;
            internal Quaternion FromRotation;
            internal float Progress;
            internal bool Returning;
        }

        private const float PullSeconds = 0.16f;
        private const float ReturnSeconds = 0.25f;

        private readonly ManualLogSource _logger;
        private readonly List<Lift> _lifts = new List<Lift>();
        private readonly List<PlayableCard> _candidates = new List<PlayableCard>();
        private PlayableCard _leftHover;
        private PlayableCard _rightHover;

        internal BoardLift(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal bool Holds(Component target)
        {
            for (int i = 0; i < _lifts.Count; i++)
            {
                PlayableCard card = _lifts[i].Card;
                if (card != null && target != null && (target == card || target.transform.IsChildOf(card.transform))) return true;
            }
            return false;
        }

        internal void Tick(CardGrabber grabber, VrHand left, VrHand right, bool canHold, float scale)
        {
            Prune();
            UpdateHand(grabber, left, canHold, scale);
            UpdateHand(grabber, right, canHold, scale);
            Animate();
        }

        private void UpdateHand(CardGrabber grabber, VrHand state, bool canHold, float scale)
        {
            Lift held = HeldBy(state);
            if (held != null)
            {
                SetHover(state, null);
                if (!canHold || !state.Ready || !state.Grip.Held)
                {
                    StartReturn(held);
                    return;
                }
                state.SetHoldPose(HandHold.Pinch);
                return;
            }
            if (!canHold || !state.Ready || state.Busy || state.HoverCard != null)
            {
                SetHover(state, null);
                return;
            }
            float reach = grabber.ReachMeters * scale;
            PlayableCard hover = Nearest(CardGrabber.CardPoint(state), reach, Hover(state));
            if (hover != null)
            {
                state.Highlight();
                if (grabber.CardHoverGlow) GrabGlow.Mark(hover);
                if (hover != Hover(state)) state.Play(Feel.Tick, 0.5f);
            }
            SetHover(state, hover);
            if (hover != null && state.GripEdge == 1) Grab(state, hover, scale);
        }

        private PlayableCard Hover(VrHand state) { return state.IsLeft ? _leftHover : _rightHover; }

        private void SetHover(VrHand state, PlayableCard card)
        {
            if (state.IsLeft) _leftHover = card;
            else _rightHover = card;
        }

        private PlayableCard Nearest(Vector3 point, float reach, PlayableCard current)
        {
            Collect();
            PlayableCard best = null;
            float bestSqr = float.MaxValue;
            float reachSqr = reach * reach;
            float keep = reach * CardHover.KeepFactor;
            float keepSqr = keep * keep;
            for (int i = 0; i < _candidates.Count; i++)
            {
                PlayableCard card = _candidates[i];
                float distance = CardGrabber.BoxSqrDistance(card, point);
                if (distance > (card == current ? keepSqr : reachSqr) || distance >= bestSqr) continue;
                best = card;
                bestSqr = distance;
            }
            return best;
        }

        private void Collect()
        {
            _candidates.Clear();
            BoardManager board = Singleton<BoardManager>.Instance;
            if (board != null)
            {
                List<CardSlot> slots = board.AllSlotsCopy;
                for (int i = 0; i < slots.Count; i++)
                    if (slots[i] != null) Consider(slots[i].Card);
            }
            TurnManager turn = Singleton<TurnManager>.Instance;
            Opponent opponent = turn != null ? turn.Opponent : null;
            List<PlayableCard> queue = opponent != null ? opponent.Queue : null;
            if (queue != null)
                for (int i = 0; i < queue.Count; i++) Consider(queue[i]);
        }

        private void Consider(PlayableCard card)
        {
            if (card == null || !card.gameObject.activeInHierarchy || card.Dead || _candidates.Contains(card)) return;
            Lift lift = Find(card);
            if (lift != null && !lift.Returning) return;
            _candidates.Add(card);
        }

        private void Grab(VrHand state, PlayableCard card, float scale)
        {
            Lift lift = Find(card);
            if (lift == null)
            {
                lift = new Lift();
                lift.Card = card;
                lift.Parent = card.transform.parent;
                lift.RestLocalPosition = card.transform.localPosition;
                lift.RestLocalRotation = card.transform.localRotation;
                _lifts.Add(lift);
            }
            Tween.Stop(card.transform.GetInstanceID());
            lift.Hand = state;
            lift.Returning = false;
            lift.From = card.transform.position;
            lift.FromRotation = card.transform.rotation;
            lift.Progress = 0f;
            CardGrabber.PinchPose(state, card, card.transform.rotation, scale, out lift.LocalPosition, out lift.LocalRotation);
            state.Lifted = card;
            SetHover(state, null);
            state.Play(Feel.Tick);
            VrSounds.Play("card", MixerGroup.CardPaperSFX, card.transform.position, 0.7f);
            _logger.LogInfo("Lifted " + card.name + " off the board with the " + state.Name + " hand to look at it.");
        }

        private void StartReturn(Lift lift)
        {
            if (lift.Hand != null && lift.Hand.Lifted == lift.Card) lift.Hand.Lifted = null;
            lift.Hand = null;
            if (lift.Card == null) return;
            lift.Returning = true;
            lift.From = lift.Card.transform.position;
            lift.FromRotation = lift.Card.transform.rotation;
            lift.Progress = 0f;
        }

        private void Animate()
        {
            float dt = Time.unscaledDeltaTime;
            for (int i = _lifts.Count - 1; i >= 0; i--)
            {
                Lift lift = _lifts[i];
                Transform t = lift.Card.transform;
                Tween.Stop(t.GetInstanceID());
                if (lift.Returning)
                {
                    lift.Progress = Mathf.Min(1f, lift.Progress + dt / ReturnSeconds);
                    float k = Mathf.SmoothStep(0f, 1f, lift.Progress);
                    Vector3 rest = lift.Parent != null ? lift.Parent.TransformPoint(lift.RestLocalPosition) : lift.RestLocalPosition;
                    Quaternion restRotation = lift.Parent != null ? lift.Parent.rotation * lift.RestLocalRotation : lift.RestLocalRotation;
                    t.SetPositionAndRotation(Vector3.Lerp(lift.From, rest, k), Quaternion.Slerp(lift.FromRotation, restRotation, k));
                    if (lift.Progress >= 1f)
                    {
                        t.localPosition = lift.RestLocalPosition;
                        t.localRotation = lift.RestLocalRotation;
                        VrSounds.Play("cardquick", MixerGroup.CardPaperSFX, t.position, 0.5f);
                        _lifts.RemoveAt(i);
                    }
                    continue;
                }
                VrHand hand = lift.Hand;
                Vector3 target = hand.Position + hand.Rotation * lift.LocalPosition;
                Quaternion targetRotation = hand.Rotation * lift.LocalRotation;
                if (lift.Progress < 1f)
                {
                    lift.Progress = Mathf.Min(1f, lift.Progress + dt / PullSeconds);
                    float k = Mathf.SmoothStep(0f, 1f, lift.Progress);
                    target = Vector3.Lerp(lift.From, target, k);
                    targetRotation = Quaternion.Slerp(lift.FromRotation, targetRotation, k);
                    if (lift.Progress >= 1f) hand.Play(Feel.Tick, 0.8f);
                }
                t.SetPositionAndRotation(target, targetRotation);
            }
        }

        private void Prune()
        {
            for (int i = _lifts.Count - 1; i >= 0; i--)
            {
                Lift lift = _lifts[i];
                bool gone = lift.Card == null || !lift.Card.gameObject.activeInHierarchy || lift.Card.transform.parent != lift.Parent;
                if (!gone) continue;
                if (lift.Hand != null && lift.Hand.Lifted == lift.Card) lift.Hand.Lifted = null;
                if (lift.Card != null) _logger.LogInfo("The game moved " + lift.Card.name + " while it was lifted; letting it go.");
                _lifts.RemoveAt(i);
            }
        }

        private Lift HeldBy(VrHand state)
        {
            for (int i = 0; i < _lifts.Count; i++)
                if (!_lifts[i].Returning && _lifts[i].Hand == state) return _lifts[i];
            if (state.Lifted is PlayableCard) state.Lifted = null;
            return null;
        }

        private Lift Find(PlayableCard card)
        {
            for (int i = 0; i < _lifts.Count; i++) if (_lifts[i].Card == card) return _lifts[i];
            return null;
        }

        internal void Release()
        {
            for (int i = 0; i < _lifts.Count; i++)
            {
                Lift lift = _lifts[i];
                if (lift.Hand != null && lift.Hand.Lifted == lift.Card) lift.Hand.Lifted = null;
                if (lift.Card == null || lift.Card.transform.parent != lift.Parent) continue;
                Tween.Stop(lift.Card.transform.GetInstanceID());
                lift.Card.transform.localPosition = lift.RestLocalPosition;
                lift.Card.transform.localRotation = lift.RestLocalRotation;
            }
            _lifts.Clear();
            _leftHover = null;
            _rightHover = null;
        }
    }
}
