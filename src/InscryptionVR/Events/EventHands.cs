using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class EventHands
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo ChoiceCardsField = typeof(CardChoicesSequencer).GetField("selectableCards", Members);
        private static readonly FieldInfo ChosenRewardField = typeof(CardSingleChoicesSequencer).GetField("chosenReward", Members);
        private static readonly FieldInfo RerollField = typeof(CardSingleChoicesSequencer).GetField("rerollInteractable", Members);
        private static readonly FieldInfo SelectorField = typeof(SelectCardFromDeckSlot).GetField("cardSelector", Members);
        private static readonly FieldInfo DisplayedField = typeof(SelectableCardArray).GetField("displayedCards", Members);
        private static readonly FieldInfo PairsField = typeof(SelectableCardPairArray).GetField("pairs", Members);
        private static readonly FieldInfo RetrieveField = typeof(CardStatBoostSequencer).GetField("retrieveCardInteractable", Members);
        private static readonly FieldInfo ControlModeField = typeof(ViewController).GetField("controlMode", Members);
        private static readonly FieldInfo HandshakeField = typeof(OpponentArmController).GetField("handshakeInteractable", Members);
        private static readonly FieldInfo OliveField = typeof(LeshyAnimationController).GetField("oliveBranchInteractable", Members);
        private static readonly FieldInfo ItemSlotsField = typeof(SelectItemsSequencer).GetField("slots", Members);

        private const float CommitCardHeights = 0.8f;
        private const float SlotDropCardHeights = 0.6f;
        private const float StoneTouchMeters = 0.04f;
        private const float PutBackSeconds = 0.15f;
        private const float PackRatReachMeters = 0.4f;
        private const float PullItemSizes = 0.9f;
        private const float MinPullMeters = 0.06f;
        private const float KnockMeters = 0.012f;
        private const float KnockClearMeters = 0.03f;
        private const float KnockMetersPerSecond = 0.3f;
        private const float DoubleKnockSeconds = 0.9f;

        internal static bool Available
        {
            get { return ChoiceCardsField != null && ChosenRewardField != null && SelectorField != null && DisplayedField != null; }
        }

        private enum Kind { Reward, Altar, Press, Pull, Knock }

        private sealed class Candidate
        {
            internal MainInputInteractable Target;
            internal Kind Kind;
            internal Component Owner;
            internal SelectCardFromDeckSlot Slot;
        }

        private sealed class Pull
        {
            internal Candidate Source;
            internal SelectableCard Card;
            internal Carry Carry;
            internal float Size;
            internal bool OverSlot;
        }

        private readonly ManualLogSource _logger;
        private CardChoicesSequencer[] _sequencers = new CardChoicesSequencer[0];
        private SelectCardFromDeckSlot[] _slots = new SelectCardFromDeckSlot[0];
        private ConfirmStoneButton[] _stones = new ConfirmStoneButton[0];
        private CardStatBoostSequencer[] _boosts = new CardStatBoostSequencer[0];
        private OpponentArmController[] _arms = new OpponentArmController[0];
        private bool _battleMode;
        private float _nextRefresh;
        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly HashSet<MainInputInteractable> _known = new HashSet<MainInputInteractable>();
        private MainInputInteractable _leftHover;
        private MainInputInteractable _rightHover;
        private float _stoneCooldownUntil;
        private Pull _leftPull;
        private Pull _rightPull;
        private bool _leftInStone;
        private bool _rightInStone;
        private SelectItemsSequencer[] _itemSequencers = new SelectItemsSequencer[0];
        private readonly bool _pullItems;
        private readonly bool _knock;
        private bool _leftKnocking;
        private bool _rightKnocking;
        private MainInputInteractable _lastKnocked;
        private float _lastKnockTime = -10f;
        private readonly System.Action<MainInputInteractable> _slotSelected;
        private readonly Dictionary<SelectCardFromDeckSlot, System.Action<MainInputInteractable>> _wired = new Dictionary<SelectCardFromDeckSlot, System.Action<MainInputInteractable>>();
        private SelectCardFromDeckSlot _selecting;

        internal EventHands(ManualLogSource logger, bool pullItems, bool knock)
        {
            _logger = logger;
            _slotSelected = OnSlotSelected;
            _pullItems = pullItems && ItemSlotsField != null;
            _knock = knock;
            if (pullItems && ItemSlotsField == null) _logger.LogWarning("Event hands: item choice slots not found; backpack items are taken with a grip instead of a pull.");
            if (RerollField == null) _logger.LogWarning("Event hands: clover reroll not found; use the pointer to reroll.");
            if (PairsField == null) _logger.LogWarning("Event hands: mycologist card pairs not found; use the pointer to choose a pair.");
            if (RetrieveField == null) _logger.LogWarning("Event hands: campfire card retrieval not found; use the pointer to pull your card back.");
        }

        private bool Knocking { get { return _knock && !EventStyle.Simplified; } }

        private bool Pulling { get { return _pullItems && !EventStyle.Simplified; } }

        internal bool Holds(Component target)
        {
            return (_leftPull != null && _leftPull.Carry.Holds(target)) || (_rightPull != null && _rightPull.Carry.Holds(target));
        }

        internal void Tick(CardGrabber grabber, VrHand left, VrHand right, bool input, float scale)
        {
            bool inEvent = input && InEvent();
            bool inBattle = input && !inEvent && InBattle();
            if (!inEvent && !inBattle)
            {
                Release();
                return;
            }
            if (inBattle != _battleMode) _nextRefresh = 0f;
            _battleMode = inBattle;
            Refresh();
            if (inEvent) WatchSlots();
            _candidates.Clear();
            _known.Clear();
            if (inEvent) Collect(_candidates);
            else CollectBattle(_candidates);
            if (_candidates.Count == 0 && _leftPull == null && _rightPull == null)
            {
                Release();
                return;
            }
            Process(grabber, left, ref _leftPull, ref _leftInStone, ref _leftHover, _rightHover, scale);
            Process(grabber, right, ref _rightPull, ref _rightInStone, ref _rightHover, _leftHover, scale);
            if (inEvent && Knocking)
            {
                Knock(left, ref _leftKnocking, scale);
                Knock(right, ref _rightKnocking, scale);
            }
            if (inEvent && _leftPull == null && _rightPull == null && _candidates.Count > 0)
            {
                Bounds bounds;
                MainInputInteractable first = _candidates[0].Target;
                Vector3 anchor = HandReach.RendererBounds(first, out bounds) ? new Vector3(bounds.center.x, bounds.max.y, bounds.center.z) : first.transform.position;
                VrTips.Show("events", anchor, "Reach out and grab a card or object to choose it.", 45);
            }
            if (_leftHover != null) _leftHover.CursorStay();
            if (_rightHover != null && _rightHover != _leftHover) _rightHover.CursorStay();
        }

        internal void Release()
        {
            PutBack(ref _leftPull);
            PutBack(ref _rightPull);
            _leftInStone = false;
            _rightInStone = false;
            _leftKnocking = false;
            _rightKnocking = false;
            _lastKnocked = null;
            SetHover(ref _leftHover, null, _rightHover);
            SetHover(ref _rightHover, null, _leftHover);
        }

        private static void SetHover(ref MainInputInteractable mine, MainInputInteractable target, MainInputInteractable other)
        {
            Interactables.SetHover(ref mine, target, other);
        }

        private static bool InEvent()
        {
            if (VrScenes.InFinale)
            {
                InteractionCursor finaleCursor = Singleton<InteractionCursor>.Instance;
                return FinaleTrial.Active && (finaleCursor == null || !finaleCursor.InteractionDisabled);
            }
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            if (flow == null || flow.Transitioning || flow.CurrentGameState != GameState.SpecialCardSequence) return false;
            InteractionCursor cursor = Singleton<InteractionCursor>.Instance;
            return cursor != null && !cursor.InteractionDisabled;
        }

        private static bool InBattle()
        {
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            if (flow == null || flow.Transitioning || flow.CurrentGameState != GameState.CardBattle) return false;
            InteractionCursor cursor = Singleton<InteractionCursor>.Instance;
            return cursor != null && !cursor.InteractionDisabled;
        }

        private void CollectBattle(List<Candidate> list)
        {
            BoardManager board = Singleton<BoardManager>.Instance;
            if (board != null)
            {
                SelectableCardArray selector = board.CardSelector;
                if (HandReach.Active(selector))
                {
                    List<SelectableCard> shown = DisplayedField.GetValue(selector) as List<SelectableCard>;
                    if (shown != null)
                        for (int c = 0; c < shown.Count; c++)
                            if (HandReach.Live(shown[c])) Add(list, shown[c], Kind.Press, selector, null);
                }
                ViewManager views = Singleton<ViewManager>.Instance;
                if (ControlModeField != null && views != null && views.Controller != null &&
                    (ViewController.ControlMode)ControlModeField.GetValue(views.Controller) == ViewController.ControlMode.TraderCardsForPeltsPhase)
                {
                    List<CardSlot> slots = board.OpponentSlotsCopy;
                    for (int i = 0; i < slots.Count; i++)
                        if (slots[i].Card != null && HandReach.Live(slots[i])) Add(list, slots[i], Kind.Press, board, null);
                    List<HighlightedInteractable> queue = board.OpponentQueueSlots;
                    for (int i = 0; i < queue.Count; i++)
                        if (HandReach.Live(queue[i])) Add(list, queue[i], Kind.Press, board, null);
                }
            }
            if (HandshakeField != null)
                for (int i = 0; i < _arms.Length; i++)
                {
                    MainInputInteractable shake = _arms[i] != null ? HandshakeField.GetValue(_arms[i]) as MainInputInteractable : null;
                    if (HandReach.Live(shake)) Add(list, shake, Kind.Press, _arms[i], null);
                }
            LeshyAnimationController leshy = LeshyAnimationController.Instance as LeshyAnimationController;
            if (OliveField != null && leshy != null)
            {
                MainInputInteractable olive = OliveField.GetValue(leshy) as MainInputInteractable;
                if (HandReach.Live(olive) && olive.GetComponent<Renderer>() != null && olive.GetComponent<Renderer>().enabled)
                    Add(list, olive, Kind.Press, leshy, null);
            }
        }

        private void Refresh()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;
            if (_battleMode)
            {
                _arms = HandshakeField != null ? Object.FindObjectsOfType<OpponentArmController>() : new OpponentArmController[0];
                _stones = new ConfirmStoneButton[0];
                return;
            }
            _sequencers = Object.FindObjectsOfType<CardChoicesSequencer>();
            _slots = Object.FindObjectsOfType<SelectCardFromDeckSlot>();
            _wired.Clear();
            _stones = Object.FindObjectsOfType<ConfirmStoneButton>();
            _boosts = RetrieveField != null ? Object.FindObjectsOfType<CardStatBoostSequencer>() : new CardStatBoostSequencer[0];
            _itemSequencers = _pullItems ? Object.FindObjectsOfType<SelectItemsSequencer>() : new SelectItemsSequencer[0];
        }

        private void Process(CardGrabber grabber, VrHand state, ref Pull pull, ref bool inStone, ref MainInputInteractable hover, MainInputInteractable other, float scale)
        {
            if (pull != null)
            {
                inStone = false;
                SetHover(ref hover, null, other);
                Hold(state, ref pull);
                return;
            }
            if (!state.Ready)
            {
                inStone = false;
                SetHover(ref hover, null, other);
                return;
            }
            bool free = !state.Busy;
            TouchStones(state, ref inStone, free, scale);
            if (!free)
            {
                SetHover(ref hover, null, other);
                return;
            }
            TryGrab(grabber, state, ref pull, ref hover, other, scale, state.GripEdge == 1);
        }

        private void TouchStones(VrHand state, ref bool inStone, bool free, float scale)
        {
            float reach = StoneTouchMeters * scale;
            ConfirmStoneButton touched = null;
            for (int i = 0; i < _stones.Length; i++)
            {
                ConfirmStoneButton stone = _stones[i];
                if (!HandReach.Live(stone) || StoneRites.Hides(stone)) continue;
                if (HandReach.InteractableSqrDistance(stone, state.Position) <= reach * reach ||
                    (state.HasIndexTip && HandReach.InteractableSqrDistance(stone, state.IndexTip) <= reach * reach))
                {
                    touched = stone;
                    break;
                }
            }
            bool entered = touched != null && !inStone;
            inStone = touched != null;
            if (!entered || !free || state.Grip.Held || Time.unscaledTime < _stoneCooldownUntil) return;
            _stoneCooldownUntil = Time.unscaledTime + 1f;
            Press(state, touched, "touched");
        }

        private void TryGrab(CardGrabber grabber, VrHand state, ref Pull pull, ref MainInputInteractable hover, MainInputInteractable other, float scale, bool grab)
        {
            List<Candidate> candidates = _candidates;
            if (candidates.Count == 0)
            {
                SetHover(ref hover, null, other);
                return;
            }
            float reach = grabber.ReachMeters * scale;
            float best = float.MaxValue;
            Candidate chosen = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (Pulled(candidates[i].Target)) continue;
                float distance = HandReach.InteractableSqrDistance(candidates[i].Target, state.Position);
                float limit = IsPackRat(candidates[i].Target) ? Mathf.Max(reach, PackRatReachMeters * scale) : reach;
                if (distance <= limit * limit && distance < best)
                {
                    best = distance;
                    chosen = candidates[i];
                }
            }
            bool touch = chosen != null;
            if (!touch) chosen = PointedCandidate(candidates, HandReach.Pointed(state, scale));
            SetHover(ref hover, chosen != null ? chosen.Target : null, other);
            if (chosen != null) state.Highlight();
            if (!grab || chosen == null) return;
            state.GripEdge = 0;

            SelectableCard card = chosen.Target as SelectableCard;
            string how = touch ? "touch" : "aim";
            if (chosen.Kind == Kind.Knock)
            {
                state.Play(Feel.Reject, 0.4f);
                VrTips.Show("events.knock", KnockTipAnchor(chosen.Target, scale), "Knock twice on a boulder with your knuckles to show where to strike.", 75);
                return;
            }
            if (chosen.Kind == Kind.Pull)
            {
                StartItemPull(state, chosen, ref pull, ref hover, other, touch, scale);
                return;
            }
            if (chosen.Kind == Kind.Press)
            {
                Press(state, chosen.Target, "pressed (" + how + ")");
                return;
            }
            if (chosen.Kind == Kind.Reward && card != null && card.Flipped)
            {
                Press(state, chosen.Target, "turned over (" + how + ")");
                return;
            }
            SetHover(ref hover, null, other);

            SelectableCardPair pair = chosen.Target as SelectableCardPair;
            if (pair != null) card = pair.LeftCard;
            float size = HandReach.Size(chosen.Target);
            if (size <= 0f) size = 0.1f * scale;
            Pull started = new Pull();
            started.Source = chosen;
            started.Card = card;
            started.Size = size;
            started.Carry = new Carry(chosen.Target.transform, state, touch, state.HoldOffset(0.03f, scale), true);
            pull = started;
            state.Claimed = true;
            if (card != null) VrSounds.Play("card", MixerGroup.CardPaperSFX, chosen.Target.transform.position, 1f);
            state.Play(card != null ? Feel.Tick : Feel.Tap);
            state.SetHoldPose(card != null ? HandHold.Pinch : HandHold.Wrap);
            VrTips.Done("events");
            string where = "";
            if (chosen.Kind == Kind.Altar && chosen.Slot != null)
                where = "; altar slot is " + (HandReach.RendererSqrDistance(chosen.Slot, state.Position) > 0f
                    ? (Mathf.Sqrt(HandReach.RendererSqrDistance(chosen.Slot, state.Position)) / Mathf.Max(scale, 0.0001f)).ToString("0.00") + " m away"
                    : "under the hand");
            _logger.LogInfo((chosen.Kind == Kind.Reward ? "Reward card" : "Deck card") + " lifted with the " + state.Name + " hand (" + (touch ? "touch" : "point") + "): " +
                chosen.Target.name + where + ".");
        }

        private bool Pulled(MainInputInteractable target)
        {
            return (_leftPull != null && _leftPull.Source.Target == target) || (_rightPull != null && _rightPull.Source.Target == target);
        }

        private void StartItemPull(VrHand state, Candidate chosen, ref Pull pull, ref MainInputInteractable hover, MainInputInteractable other, bool touch, float scale)
        {
            SelectableItemSlot slot = chosen.Target as SelectableItemSlot;
            Item item = slot != null ? slot.Item : null;
            if (item == null) return;
            if (slot.CursorSelectStarted == null)
            {
                state.Play(Feel.Reject, 0.4f);
                return;
            }
            SetHover(ref hover, null, other);
            float size = HandReach.Size(item);
            Pull started = new Pull();
            started.Source = chosen;
            started.Size = Mathf.Max(size * PullItemSizes, MinPullMeters * scale);
            started.Carry = new Carry(item.transform, state, touch, state.HoldOffset(0.03f, scale), true);
            pull = started;
            state.Claimed = true;
            state.Play(Feel.Tap);
            state.SetHoldPose(HandHold.Wrap);
            item.PlayPickUpSound();
            VrTips.Done("events");
            VrTips.Show("events.pull", item.transform.position + Vector3.up * (0.08f * scale), "Pull it out toward you to take it.", 70);
            _logger.LogInfo("Item choice " + item.name + " gripped with the " + state.Name + " hand (" + (touch ? "touch" : "point") + "); pull it clear to take it.");
        }

        private void CommitItemPull(VrHand state, Pull p)
        {
            SelectableItemSlot slot = p.Source.Target as SelectableItemSlot;
            if (slot == null || slot.Item == null || slot.CursorSelectStarted == null)
            {
                p.Carry.PutBack(PutBackSeconds);
                state.Play(Feel.Reject);
                return;
            }
            Item item = slot.Item;
            string name = item.name;
            HandProp prop = HandProp.Copy(item.transform, name);
            if (prop != null) prop.Follow(state, HandHold.Wrap);
            new RendererHider().Hide(item);
            slot.CursorSelectStart();
            if (slot != null) slot.CursorSelectEnd();
            state.Play(Feel.Thud);
            VrTips.Done("events.pull");
            _logger.LogInfo("Item choice " + name + " pulled clear and taken with the " + state.Name + " hand.");
        }

        private static Vector3 KnockTipAnchor(Component target, float scale)
        {
            Bounds bounds;
            if (HandReach.CardBounds(target, out bounds)) return new Vector3(bounds.center.x, bounds.max.y + 0.06f * scale, bounds.center.z);
            return target.transform.position + Vector3.up * (0.06f * scale);
        }

        private void Knock(VrHand state, ref bool touching, float scale)
        {
            if (!state.Ready || state.Busy)
            {
                touching = false;
                return;
            }
            MainInputInteractable hit = null;
            Vector3 contact = state.Position;
            float best = float.MaxValue;
            Vector3 center = Vector3.zero;
            for (int i = 0; i < _candidates.Count; i++)
            {
                Candidate candidate = _candidates[i];
                if (candidate.Kind != Kind.Knock) continue;
                Bounds bounds;
                if (!HandReach.CardBounds(candidate.Target, out bounds)) continue;
                Consider(bounds, state.Position, candidate.Target, ref best, ref hit, ref contact, ref center);
                if (state.HasFist) Consider(bounds, state.FistPosition, candidate.Target, ref best, ref hit, ref contact, ref center);
                if (state.HasIndexTip) Consider(bounds, state.IndexTip, candidate.Target, ref best, ref hit, ref contact, ref center);
            }
            float distance = Mathf.Sqrt(best);
            float knock = KnockMeters * scale;
            if (hit != null && _candidates.Count > 0 && touching == false && distance <= knock)
            {
                touching = true;
                Vector3 velocity = state.Velocity;
                float speed = velocity.magnitude / Mathf.Max(scale, 0.0001f);
                if (speed >= KnockMetersPerSecond && Vector3.Dot(velocity, center - contact) > 0f) Knocked(state, hit, contact, speed);
                return;
            }
            if (hit == null || distance > KnockClearMeters * scale) touching = false;
        }

        private static void Consider(Bounds bounds, Vector3 point, MainInputInteractable target, ref float best, ref MainInputInteractable hit, ref Vector3 contact, ref Vector3 center)
        {
            float distance = bounds.SqrDistance(point);
            if (distance >= best) return;
            best = distance;
            hit = target;
            contact = point;
            center = bounds.center;
        }

        private void Knocked(VrHand state, MainInputInteractable target, Vector3 contact, float speed)
        {
            float strength = Mathf.Clamp(0.5f + speed * 0.4f, 0.5f, 1f);
            state.Play(Feel.Thud, strength);
            VrSounds.Play("stone_object_hit", MixerGroup.TableObjectsSFX, contact, 0.35f + 0.3f * strength);
            float now = Time.unscaledTime;
            if (_lastKnocked == target && now - _lastKnockTime <= DoubleKnockSeconds)
            {
                _lastKnocked = null;
                string name = target.name;
                target.CursorSelectStart();
                if (target != null) target.CursorSelectEnd();
                VrTips.Done("events.knock");
                VrTips.Done("events");
                _logger.LogInfo("Boulder " + name + " knocked twice with the " + state.Name + " hand; the Prospector strikes there.");
                return;
            }
            _lastKnocked = target;
            _lastKnockTime = now;
            VrLog.Chatter(_logger, "Knocked on boulder " + target.name + " with the " + state.Name + " hand.");
        }

        private static bool IsPackRat(MainInputInteractable target)
        {
            SelectableCard card = target as SelectableCard;
            return card != null && card.Info != null && card.Info.name == "PackRat";
        }

        private static Candidate PointedCandidate(List<Candidate> candidates, Collider hit)
        {
            if (hit == null) return null;
            Candidate parent = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                Transform target = candidates[i].Target.transform;
                if (hit.transform == target) return candidates[i];
                if (parent == null && hit.transform.IsChildOf(target)) parent = candidates[i];
            }
            return parent;
        }

        private void Collect(List<Candidate> list)
        {
            for (int i = 0; i < _sequencers.Length; i++)
            {
                CardChoicesSequencer sequencer = _sequencers[i];
                if (!HandReach.Active(sequencer)) continue;
                List<SelectableCard> cards = ChoiceCardsField.GetValue(sequencer) as List<SelectableCard>;
                Kind cardKind = sequencer is CardSingleChoicesSequencer ? Kind.Reward : Knocking && sequencer is BoulderChoiceSequencer ? Kind.Knock : Kind.Press;
                if (cards != null)
                    for (int c = 0; c < cards.Count; c++)
                        if (HandReach.Live(cards[c])) Add(list, cards[c], cardKind, sequencer, null);
                if (RerollField != null && sequencer is CardSingleChoicesSequencer)
                {
                    MainInputInteractable reroll = RerollField.GetValue(sequencer) as MainInputInteractable;
                    if (HandReach.Live(reroll)) Add(list, reroll, Kind.Press, sequencer, null);
                }
            }
            for (int i = 0; i < _slots.Length; i++)
            {
                SelectCardFromDeckSlot slot = _slots[i];
                if (!HandReach.Active(slot)) continue;
                if (HandReach.Live(slot)) Add(list, slot, Kind.Press, slot, slot);
                SelectableCardArray selector = SelectorField.GetValue(slot) as SelectableCardArray;
                if (!HandReach.Active(selector)) continue;
                SelectCardFromDeckSlot destination = DestinationFor(slot, selector);
                List<SelectableCard> shown = DisplayedField.GetValue(selector) as List<SelectableCard>;
                if (shown != null)
                    for (int c = 0; c < shown.Count; c++)
                        if (HandReach.Live(shown[c])) Add(list, shown[c], Kind.Altar, selector, destination);
                if (PairsField != null && selector is SelectableCardPairArray)
                {
                    List<SelectableCardPair> pairs = PairsField.GetValue(selector) as List<SelectableCardPair>;
                    if (pairs != null)
                        for (int c = 0; c < pairs.Count; c++)
                            if (HandReach.Live(pairs[c])) Add(list, pairs[c], Kind.Altar, selector, destination);
                }
            }
            for (int i = 0; Pulling && i < _itemSequencers.Length; i++)
            {
                SelectItemsSequencer items = _itemSequencers[i];
                if (!HandReach.Active(items)) continue;
                List<SelectableItemSlot> itemSlots = ItemSlotsField.GetValue(items) as List<SelectableItemSlot>;
                if (itemSlots == null) continue;
                for (int s = 0; s < itemSlots.Count; s++)
                {
                    SelectableItemSlot itemSlot = itemSlots[s];
                    if (HandReach.Live(itemSlot) && itemSlot.Item != null) Add(list, itemSlot, Kind.Pull, items, null);
                }
            }
            for (int i = 0; i < _stones.Length; i++)
            {
                if (StoneRites.Hides(_stones[i])) _known.Add(_stones[i]);
                else if (HandReach.Live(_stones[i])) Add(list, _stones[i], Kind.Press, _stones[i], null);
            }
            for (int i = 0; i < _boosts.Length; i++)
            {
                if (!HandReach.Active(_boosts[i])) continue;
                MainInputInteractable retrieve = RetrieveField.GetValue(_boosts[i]) as MainInputInteractable;
                if (HandReach.Live(retrieve)) Add(list, retrieve, Kind.Press, _boosts[i], null);
            }
            MainInputInteractable[] all = Interactables.All;
            for (int i = 0; i < all.Length; i++)
            {
                MainInputInteractable target = all[i];
                if (!HandReach.Live(target) || _known.Contains(target)) continue;
                if (Interactables.OnExcludedLayer(target)) continue;
                if (target is CardSlot || target is PlayableCard || CardGrabber.IsHeld(target)) continue;
                Add(list, target, Kind.Press, target, null);
            }
        }

        private SelectCardFromDeckSlot DestinationFor(SelectCardFromDeckSlot slot, SelectableCardArray selector)
        {
            if (_selecting == null || _selecting == slot || !HandReach.Active(_selecting)) return slot;
            return ReferenceEquals(SelectorField.GetValue(_selecting), selector) ? _selecting : slot;
        }

        private void WatchSlots()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                SelectCardFromDeckSlot slot = _slots[i];
                if (slot == null || !SharesSelector(slot)) continue;
                System.Action<MainInputInteractable> current = slot.CursorSelectStarted;
                System.Action<MainInputInteractable> wired;
                if (_wired.TryGetValue(slot, out wired) && ReferenceEquals(wired, current)) continue;
                System.Action<MainInputInteractable> rest = (System.Action<MainInputInteractable>)System.Delegate.Remove(current, _slotSelected);
                slot.CursorSelectStarted = (System.Action<MainInputInteractable>)System.Delegate.Combine(rest, _slotSelected);
                _wired[slot] = slot.CursorSelectStarted;
            }
        }

        private bool SharesSelector(SelectCardFromDeckSlot slot)
        {
            object selector = SelectorField.GetValue(slot);
            if (selector == null) return false;
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i] != null && _slots[i] != slot && ReferenceEquals(SelectorField.GetValue(_slots[i]), selector)) return true;
            return false;
        }

        private void OnSlotSelected(MainInputInteractable target)
        {
            SelectCardFromDeckSlot slot = target as SelectCardFromDeckSlot;
            if (slot == null || slot == _selecting) return;
            _selecting = slot;
            VrLog.Chatter(_logger, "Altar slot " + slot.name + " opened the deck; carried cards go there.");
        }

        private void Add(List<Candidate> list, MainInputInteractable target, Kind kind, Component owner, SelectCardFromDeckSlot slot)
        {
            if (!_known.Add(target)) return;
            Candidate candidate = new Candidate();
            candidate.Target = target;
            candidate.Kind = kind;
            candidate.Owner = owner;
            candidate.Slot = slot;
            list.Add(candidate);
        }

        private void Press(VrHand state, MainInputInteractable target, string what)
        {
            string name = target.name;
            target.CursorSelectStart();
            if (target != null) target.CursorSelectEnd();
            state.Play(Feel.Tap);
            VrTips.Done("events");
            _logger.LogInfo("Event: " + name + " " + what + " with " + state.Name + " hand.");
        }

        private void Hold(VrHand state, ref Pull pull)
        {
            Pull p = pull;
            if (p.Carry.Target == null || !HandReach.Live(p.Source.Target))
            {
                ClearHighlight(p);
                if (p.Source.Kind == Kind.Pull && p.Carry.Target != null) p.Carry.PutBack(PutBackSeconds);
                pull = null;
                return;
            }
            if (!state.Ready || !state.Grip.Held)
            {
                pull = null;
                if (p.Source.Kind == Kind.Altar && p.OverSlot)
                {
                    CommitAltar(state, p);
                    return;
                }
                ClearHighlight(p);
                p.Carry.PutBack(PutBackSeconds);
                state.Play(Feel.Tick, 0.6f);
                return;
            }
            state.Claimed = true;
            state.SetHoldPose(p.Card != null ? HandHold.Pinch : HandHold.Wrap);
            p.Carry.Update();
            if (p.Source.Kind == Kind.Pull)
            {
                float pulled = Vector3.Distance(p.Carry.Target.position, p.Carry.RestPosition);
                state.Rumble(Mathf.Clamp01(pulled / Mathf.Max(p.Size, 0.0001f)) * 0.15f);
                if (pulled >= p.Size)
                {
                    pull = null;
                    CommitItemPull(state, p);
                }
                return;
            }
            if (p.Source.Kind == Kind.Reward)
            {
                if (Vector3.Distance(p.Carry.Target.position, p.Carry.RestPosition) >= CommitCardHeights * p.Size)
                {
                    pull = null;
                    CommitReward(state, p);
                }
                return;
            }
            bool over = OverSlot(p);
            if (over == p.OverSlot) return;
            p.OverSlot = over;
            SetSlotHighlight(p.Source.Slot, over);
            if (over) state.Play(Feel.Tick, 0.7f);
        }

        private static bool OverSlot(Pull p)
        {
            SelectCardFromDeckSlot slot = p.Source.Slot;
            if (!HandReach.Active(slot)) return false;
            float limit = SlotDropCardHeights * p.Size;
            return HandReach.RendererSqrDistance(slot, p.Carry.Target.position) <= limit * limit;
        }

        private static void SetSlotHighlight(SelectCardFromDeckSlot slot, bool highlighted)
        {
            if (!HandReach.Active(slot)) return;
            slot.ShowState(highlighted ? HighlightedInteractable.State.Highlighted : HighlightedInteractable.State.NonInteractable);
        }

        private static void ClearHighlight(Pull p)
        {
            if (!p.OverSlot) return;
            p.OverSlot = false;
            SetSlotHighlight(p.Source.Slot, false);
        }

        private void CommitAltar(VrHand state, Pull p)
        {
            ClearHighlight(p);
            string name = p.Source.Target.name;
            p.Source.Target.CursorSelectStart();
            state.Play(Feel.Thud);
            _logger.LogInfo("Placed " + name + " on the altar slot with " + state.Name + " hand.");
        }

        private void CommitReward(VrHand state, Pull p)
        {
            SelectableCard card = p.Card;
            if (card == null)
            {
                p.Carry.PutBack(PutBackSeconds);
                return;
            }
            string name = card.name;
            CardSingleChoicesSequencer single = p.Source.Owner as CardSingleChoicesSequencer;
            bool before = single != null && (ChosenRewardField.GetValue(single) as SelectableCard) == card;
            card.CursorSelectStart();
            if (card != null) card.CursorSelectEnd();
            bool now = single != null && card != null && (ChosenRewardField.GetValue(single) as SelectableCard) == card;
            bool taken = single != null ? now : !HandReach.Live(card);
            if (!taken)
            {
                p.Carry.PutBack(PutBackSeconds);
                state.Play(Feel.Reject);
                _logger.LogInfo("Reward card refused by the game; put back: " + name);
                return;
            }
            CardChoice choice = card != null ? card.ChoiceInfo : null;
            bool reveal = !before && choice != null && (choice.resourceType != ResourceType.None || choice.tribe != Tribe.None);
            state.Play(Feel.Thud);
            if (reveal)
            {
                p.Carry.PutBack(PutBackSeconds);
                _logger.LogInfo("Reward chosen with " + state.Name + " hand; its creature is revealed in the row, pull it clear again to take it: " + name);
                return;
            }
            _logger.LogInfo("Reward card taken with " + state.Name + " hand: " + name);
        }

        private static void PutBack(ref Pull pull)
        {
            Pull p = pull;
            pull = null;
            if (p == null) return;
            ClearHighlight(p);
            if (HandReach.Live(p.Source.Target)) p.Carry.PutBack(PutBackSeconds);
        }
    }
}
