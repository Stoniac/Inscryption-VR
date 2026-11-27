using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class TotemPieces
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo InventoryField = typeof(BuildTotemSequencer).GetField("inventorySlots", Members);
        private static readonly FieldInfo TopField = typeof(BuildTotemSequencer).GetField("selectedTop", Members);
        private static readonly FieldInfo BottomField = typeof(BuildTotemSequencer).GetField("selectedBottom", Members);
        private static readonly FieldInfo PhaseField = typeof(BuildTotemSequencer).GetField("phase", Members);
        private static readonly FieldInfo CompleteSlotField = typeof(BuildTotemSequencer).GetField("completeTotemSlot", Members);

        private const float PieceReachMeters = 0.07f;
        private const float SpotMeters = 0.12f;
        private const float SpotAboveMeters = 0.25f;
        private const float PutBackSeconds = 0.2f;
        private const float PlaceSeconds = 0.18f;

        internal static bool Available
        {
            get { return InventoryField != null && TopField != null && BottomField != null && PhaseField != null && CompleteSlotField != null; }
        }

        private sealed class Piece
        {
            internal SelectableItemSlot Slot;
            internal Item Item;
            internal Carry Carry;
            internal bool Top;
            internal bool OverSpot;
        }

        private readonly ManualLogSource _logger;
        private readonly Dictionary<SelectableItemSlot, RendererHider> _hidden = new Dictionary<SelectableItemSlot, RendererHider>();
        private BuildTotemSequencer _sequencer;
        private List<SelectableItemSlot> _inventory;
        private SelectableItemSlot _top;
        private SelectableItemSlot _bottom;
        private SelectableItemSlot _spot;
        private Piece _left;
        private Piece _right;
        private MainInputInteractable _leftHover;
        private MainInputInteractable _rightHover;
        private HandProp _headProp;
        private HandProp _baseProp;
        private bool _active;

        internal TotemPieces(ManualLogSource logger)
        {
            _logger = logger;
            if (!Available) _logger.LogWarning("Totem pieces by hand unavailable: BuildTotemSequencer internals not found; pieces are picked with a grip press.");
        }

        internal bool Active { get { return _active; } }

        internal HandProp HeadProp { get { return _headProp != null && _headProp.Alive ? _headProp : null; } }

        internal bool Holds(Component target)
        {
            if (!_active || target == null || _inventory == null) return false;
            Transform t = target.transform;
            for (int i = 0; i < _inventory.Count; i++)
            {
                SelectableItemSlot slot = _inventory[i];
                if (slot == null) continue;
                if (t == slot.transform || t.IsChildOf(slot.transform)) return true;
                if (slot.Item != null && t.IsChildOf(slot.Item.transform)) return true;
            }
            return false;
        }

        internal void Assembled()
        {
            if (_headProp != null) _headProp.Destroy();
            if (_baseProp != null) _baseProp.Destroy();
            _headProp = null;
            _baseProp = null;
        }

        internal void Release()
        {
            PutBack(ref _left);
            PutBack(ref _right);
            Interactables.SetHover(ref _leftHover, null, _rightHover);
            Interactables.SetHover(ref _rightHover, null, _leftHover);
            Assembled();
            foreach (KeyValuePair<SelectableItemSlot, RendererHider> pair in _hidden) pair.Value.Forget();
            _hidden.Clear();
            _sequencer = null;
            _inventory = null;
            _top = null;
            _bottom = null;
            _spot = null;
            _active = false;
        }

        internal void Tick(BuildTotemSequencer sequencer, CardGrabber grabber, VrHand left, VrHand right, bool input, float scale)
        {
            if (!Available || sequencer == null || !sequencer.isActiveAndEnabled || System.Convert.ToInt32(PhaseField.GetValue(sequencer)) != 2)
            {
                if (_active || _hidden.Count > 0 || _headProp != null || _baseProp != null) Release();
                return;
            }
            _active = true;
            _sequencer = sequencer;
            _inventory = InventoryField.GetValue(sequencer) as List<SelectableItemSlot>;
            _top = TopField.GetValue(sequencer) as SelectableItemSlot;
            _bottom = BottomField.GetValue(sequencer) as SelectableItemSlot;
            _spot = CompleteSlotField.GetValue(sequencer) as SelectableItemSlot;
            if (_inventory == null)
            {
                Release();
                return;
            }
            UpdateHidden();
            if (_bottom == null && _baseProp != null && (_spot == null || _spot.Item == null))
            {
                _baseProp.Destroy();
                _baseProp = null;
            }
            Hold(left, ref _left, scale);
            Hold(right, ref _right, scale);
            Offer(grabber, left, ref _left, ref _leftHover, _rightHover, input, scale);
            Offer(grabber, right, ref _right, ref _rightHover, _leftHover, input, scale);
            if (_left == null && _right == null && _bottom == null && _top == null && HasFreePiece())
                VrTips.Show("totem.pieces", ShelfAnchor(scale), "Pick up a base and set it down on the stump, then grab a head.", 60);
        }

        private bool HasFreePiece()
        {
            for (int i = 0; i < _inventory.Count; i++) if (Grabbable(_inventory[i])) return true;
            return false;
        }

        private Vector3 ShelfAnchor(float scale)
        {
            for (int i = 0; i < _inventory.Count; i++)
            {
                SelectableItemSlot slot = _inventory[i];
                if (!Grabbable(slot)) continue;
                Bounds bounds;
                if (HandReach.RendererBounds(slot.Item, out bounds)) return new Vector3(bounds.center.x, bounds.max.y + 0.05f * scale, bounds.center.z);
                return slot.transform.position + Vector3.up * (0.08f * scale);
            }
            return _sequencer.transform.position;
        }

        private void UpdateHidden()
        {
            for (int i = 0; i < _inventory.Count; i++)
            {
                SelectableItemSlot slot = _inventory[i];
                if (slot == null) continue;
                bool selected = slot.Item != null && (slot == _top || slot == _bottom);
                RendererHider hider;
                bool known = _hidden.TryGetValue(slot, out hider);
                if (selected)
                {
                    if (!known)
                    {
                        hider = new RendererHider();
                        _hidden[slot] = hider;
                    }
                    if (!hider.Any) hider.Hide(slot.Item);
                    else hider.Keep();
                }
                else if (known)
                {
                    hider.Restore();
                    _hidden.Remove(slot);
                }
            }
        }

        private bool Grabbable(SelectableItemSlot slot)
        {
            if (slot == null || slot.Item == null || !slot.Selectable || !HandReach.Live(slot)) return false;
            if (slot == _top || slot == _bottom) return false;
            return !Carried(slot);
        }

        private bool Carried(SelectableItemSlot slot)
        {
            return (_left != null && _left.Slot == slot) || (_right != null && _right.Slot == slot);
        }

        private void Offer(CardGrabber grabber, VrHand hand, ref Piece piece, ref MainInputInteractable hover, MainInputInteractable other, bool input, float scale)
        {
            if (piece != null || !input || !hand.Ready || hand.Busy)
            {
                if (piece == null) Interactables.SetHover(ref hover, null, other);
                return;
            }
            float reach = Mathf.Max(grabber.ReachMeters, PieceReachMeters) * scale;
            float best = reach * reach;
            SelectableItemSlot chosen = null;
            for (int i = 0; i < _inventory.Count; i++)
            {
                SelectableItemSlot slot = _inventory[i];
                if (!Grabbable(slot)) continue;
                float distance = PieceSqrDistance(slot.Item, hand);
                if (distance >= best) continue;
                best = distance;
                chosen = slot;
            }
            if (chosen != hover && chosen != null) hand.Play(Feel.Tick, 0.4f);
            Interactables.SetHover(ref hover, chosen, other);
            if (chosen == null) return;
            hand.Highlight();
            GrabGlow.Mark(chosen.Item);
            if (hand.GripEdge != 1) return;
            hand.GripEdge = 0;
            Interactables.SetHover(ref hover, null, other);

            Piece started = new Piece();
            started.Slot = chosen;
            started.Item = chosen.Item;
            started.Top = chosen.Item.Data is TotemTopData;
            started.Carry = new Carry(chosen.Item.transform, hand, true, hand.HoldOffset(0.02f, scale), true);
            piece = started;
            hand.Claimed = true;
            hand.SetHoldPose(HandHold.Wrap);
            hand.Play(Feel.Tap);
            chosen.Item.PlayPickUpSound();
            VrTips.Done("totem.pieces");
            _logger.LogInfo("Totem " + (started.Top ? "head" : "base") + " " + chosen.Item.name + " picked up with the " + hand.Name + " hand.");
            if (started.Top && _bottom != null) SelectHead(hand, ref piece);
        }

        private static float PieceSqrDistance(Item item, VrHand hand)
        {
            Renderer[] renderers = item.GetComponentsInChildren<Renderer>();
            Vector3 tip = CardGrabber.CardPoint(hand);
            float best = float.MaxValue;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer is ParticleSystemRenderer || GrabGlow.IsShell(renderer)) continue;
                best = Mathf.Min(best, Mathf.Min(TotemHands.OrientedSqrDistance(renderer, tip), TotemHands.OrientedSqrDistance(renderer, hand.Position)));
            }
            return best;
        }

        private void Hold(VrHand hand, ref Piece piece, float scale)
        {
            Piece p = piece;
            if (p == null) return;
            if (p.Item == null || p.Slot == null || p.Slot.Item != p.Item || !HandReach.Live(p.Slot))
            {
                PutBack(ref piece);
                return;
            }
            if (!hand.Ready)
            {
                hand.Claimed = true;
                hand.SetHoldPose(HandHold.Wrap);
                return;
            }
            if (!hand.Grip.Held)
            {
                piece = null;
                if (!p.Top && NearSpot(p, scale))
                {
                    PlaceBase(hand, p);
                    return;
                }
                if (!p.Top && _spot != null)
                    VrLog.Chatter(_logger, "Totem base put back; it was " + (Vector3.Distance(p.Item.transform.position, _spot.transform.position) / Mathf.Max(scale, 0.0001f)).ToString("0.00") + " m from the stump.");
                p.Carry.PutBack(PutBackSeconds);
                hand.Play(Feel.Tick, 0.5f);
                return;
            }
            hand.Claimed = true;
            hand.SetHoldPose(HandHold.Wrap);
            p.Carry.Update();
            if (p.Top) return;
            bool over = NearSpot(p, scale);
            if (over == p.OverSpot) return;
            p.OverSpot = over;
            if (over) hand.Play(Feel.Tick, 0.7f);
        }

        private bool NearSpot(Piece p, float scale)
        {
            if (_spot == null) return false;
            Bounds bounds;
            if (!HandReach.RendererBounds(p.Item, out bounds)) bounds = new Bounds(p.Item.transform.position, Vector3.zero);
            Vector3 spot = _spot.transform.position;
            Vector3 flat = bounds.center - spot;
            flat.y = 0f;
            float above = bounds.min.y - spot.y;
            return flat.sqrMagnitude <= SpotMeters * SpotMeters * scale * scale && above <= SpotAboveMeters * scale && above >= -0.05f * scale;
        }

        private void PlaceBase(VrHand hand, Piece p)
        {
            Transform item = p.Item.transform;
            Transform parent = item.parent;
            Quaternion rest = parent != null ? parent.rotation * p.Carry.RestLocalRotation : p.Carry.RestLocalRotation;
            Quaternion held = item.rotation;
            item.rotation = rest;
            Bounds bounds;
            Vector3 lift = Vector3.zero;
            if (HandReach.RendererBounds(p.Item, out bounds)) lift = item.position - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            item.rotation = held;

            HandProp prop = HandProp.Copy(item, "totem base");
            if (prop != null) prop.GlideTo(_spot.transform.position + lift, rest, PlaceSeconds);
            if (_baseProp != null) _baseProp.Destroy();
            _baseProp = prop;
            Select(p);
            hand.Play(Feel.Thud, 0.7f);
            p.Item.PlayPlacedSound();
            _logger.LogInfo("Totem base " + p.Item.name + " set down on the stump with the " + hand.Name + " hand.");
            if (_left != null && _left.Top) SelectHead(_left.Carry.Hand, ref _left);
            else if (_right != null && _right.Top) SelectHead(_right.Carry.Hand, ref _right);
        }

        private void SelectHead(VrHand hand, ref Piece piece)
        {
            Piece p = piece;
            piece = null;
            HandProp prop = HandProp.Copy(p.Item.transform, "totem head");
            if (prop != null) prop.Follow(hand, HandHold.Wrap);
            if (_headProp != null) _headProp.Destroy();
            _headProp = prop;
            Select(p);
            _logger.LogInfo("Totem head " + p.Item.name + " chosen in the " + hand.Name + " hand; screw it onto the base.");
        }

        private void Select(Piece p)
        {
            RendererHider hider = new RendererHider();
            hider.Hide(p.Item);
            _hidden[p.Slot] = hider;
            p.Carry.Restore();
            p.Slot.CursorSelectStart();
            if (p.Slot != null) p.Slot.CursorSelectEnd();
            _top = TopField.GetValue(_sequencer) as SelectableItemSlot;
            _bottom = BottomField.GetValue(_sequencer) as SelectableItemSlot;
        }

        private void PutBack(ref Piece piece)
        {
            Piece p = piece;
            piece = null;
            if (p == null) return;
            if (p.Item != null) p.Carry.PutBack(PutBackSeconds);
        }
    }
}
