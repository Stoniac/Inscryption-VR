using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class SpecialEventInput
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Type _handlerType;
        private readonly FieldInfo _reward, _merger, _rewardCards, _reroll;
        private readonly FieldInfo _sacrifice, _host, _confirm, _stones, _selector, _displayedCards;
        private readonly PropertyInfo _slotCard, _enabled;
        private readonly FieldInfo _boost, _boostSlot, _boostStone, _boostRetrieve;
        private readonly List<Component> _campfireTargets = new List<Component>();
        private Component _handler;
        private float _nextSearch;

        internal SpecialEventInput()
        {
            Assembly game = null;
            foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
                if (candidate.GetType("DiskCardGame.SpecialNodeHandler", false) != null) { game = candidate; break; }
            if (game == null) throw new InvalidOperationException("Inscryption event assembly not loaded");
            _handlerType = game.GetType("DiskCardGame.SpecialNodeHandler", true);
            _reward = Field(_handlerType, "cardChoiceSequencer");
            _merger = Field(_handlerType, "cardMerger");
            _rewardCards = Field(game.GetType("DiskCardGame.CardChoicesSequencer", true), "selectableCards");
            _reroll = Field(_reward.FieldType, "rerollInteractable");
            _sacrifice = Field(_merger.FieldType, "sacrificeSlot");
            _host = Field(_merger.FieldType, "hostSlot");
            _confirm = Field(_merger.FieldType, "confirmStone");
            _stones = Field(_merger.FieldType, "stoneCircleAnim");
            _selector = Field(_sacrifice.FieldType, "cardSelector");
            _displayedCards = Field(_selector.FieldType, "displayedCards");
            _slotCard = _sacrifice.FieldType.GetProperty("Card", Members);
            _enabled = game.GetType("DiskCardGame.InteractableBase", true).GetProperty("Enabled", Members);
            if (_slotCard == null || _enabled == null) throw new MissingMemberException("Inscryption event interaction contract changed");
            _boost = _handlerType.GetField("cardStatBoostSequencer", Members);
            if (_boost != null)
            {
                _boostSlot = _boost.FieldType.GetField("selectionSlot", Members);
                _boostStone = _boost.FieldType.GetField("confirmStone", Members);
                _boostRetrieve = _boost.FieldType.GetField("retrieveCardInteractable", Members);
            }
            if (_boost == null || _boostSlot == null || _boostStone == null || _boostRetrieve == null) _boost = null;
        }

        internal bool IsAvailable(int view)
        {
            if (view != 1 && view != 2 && view != 3 && view != 8 && view != 9) return false;
            if (!Resolve()) return false;
            Component campfire = ActiveCampfire();
            if (campfire != null && CampfireTargets(campfire).Count > 0) return true;
            if (view == 1) return false;
            if (view == 3)
            {
                Component reward = _reward.GetValue(_handler) as Component;
                return Active(reward) && (AnyLive(_rewardCards.GetValue(reward)) || Live(_reroll.GetValue(reward) as Component));
            }
            Component merger = ActiveMerger();
            if (merger == null) return false;
            Component sacrifice = _sacrifice.GetValue(merger) as Component;
            Component host = _host.GetValue(merger) as Component;
            if (view == 2) return AnyLive(CardsInSelector(sacrifice)) || AnyLive(CardsInSelector(host));
            return Live(sacrifice) || Live(host) || Live(SlotCard(sacrifice)) || Live(SlotCard(host)) ||
                Live(_confirm.GetValue(merger) as Component);
        }

        internal bool AllowsTarget(Component target, int view)
        {
            if (!Live(target) || StoneRites.Hides(target) || !IsAvailable(view)) return false;
            Component campfire = ActiveCampfire();
            if (campfire != null && CampfireTargets(campfire).Contains(target)) return true;
            if (view == 1) return false;
            if (view == 3)
            {
                Component reward = _reward.GetValue(_handler) as Component;
                return Contains(_rewardCards.GetValue(reward), target) || target == (_reroll.GetValue(reward) as Component);
            }
            Component merger = ActiveMerger();
            if (merger == null) return false;
            Component sacrifice = _sacrifice.GetValue(merger) as Component;
            Component host = _host.GetValue(merger) as Component;
            if (view == 2) return Contains(CardsInSelector(sacrifice), target) || Contains(CardsInSelector(host), target);
            return target == sacrifice || target == host || target == SlotCard(sacrifice) || target == SlotCard(host) ||
                target == (_confirm.GetValue(merger) as Component);
        }

        private bool Resolve()
        {
            if (!Active(_handler) && Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + 0.25f;
                _handler = UnityEngine.Object.FindObjectOfType(_handlerType) as Component;
            }
            return Active(_handler);
        }

        private Component ActiveMerger()
        {
            Component merger = _merger.GetValue(_handler) as Component;
            return Active(merger) && Active(_stones.GetValue(merger) as Component) ? merger : null;
        }

        private Component ActiveCampfire()
        {
            if (_boost == null) return null;
            Component boost = _boost.GetValue(_handler) as Component;
            return Active(boost) && Active(_boostSlot.GetValue(boost) as Component) ? boost : null;
        }

        private List<Component> CampfireTargets(Component boost)
        {
            _campfireTargets.Clear();
            Component slot = _boostSlot.GetValue(boost) as Component;
            AddLive(slot);
            AddLive(SlotCard(slot));
            AddLive(_boostStone.GetValue(boost) as Component);
            AddLive(_boostRetrieve.GetValue(boost) as Component);
            IEnumerable cards = CardsInSelector(slot) as IEnumerable;
            if (cards != null) foreach (object card in cards) AddLive(card as Component);
            return _campfireTargets;
        }

        private void AddLive(Component component)
        {
            if (Live(component)) _campfireTargets.Add(component);
        }

        private object CardsInSelector(Component slot)
        {
            if (!Active(slot)) return null;
            Component selector = _selector.GetValue(slot) as Component;
            return Active(selector) ? _displayedCards.GetValue(selector) : null;
        }

        private Component SlotCard(Component slot)
        {
            return slot == null ? null : _slotCard.GetValue(slot, null) as Component;
        }

        private bool AnyLive(object collection)
        {
            IEnumerable items = collection as IEnumerable;
            if (items != null) foreach (object item in items) if (Live(item as Component)) return true;
            return false;
        }

        private static bool Contains(object collection, Component target)
        {
            IEnumerable items = collection as IEnumerable;
            if (items != null) foreach (object item in items) if ((item as Component) == target) return true;
            return false;
        }

        private bool Live(Component component) { return Active(component) && (bool)_enabled.GetValue(component, null); }
        private static bool Active(Component component) { return component != null && component.gameObject.activeInHierarchy; }
        private static FieldInfo Field(Type type, string name)
        {
            FieldInfo field = type.GetField(name, Members);
            if (field == null) throw new MissingFieldException(type.FullName, name);
            return field;
        }
    }
}
