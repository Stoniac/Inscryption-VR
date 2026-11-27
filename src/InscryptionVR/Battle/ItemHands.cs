using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class ItemHands
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo SlotsField = typeof(ItemsManager).GetField("consumableSlots", Members);
        private static readonly MethodInfo ValidTargetsMethod = typeof(TargetSlotItem).GetMethod("GetValidTargets", Members);
        private static readonly FieldInfo PixelCameraField = typeof(FirstPersonAnimationController).GetField("pixelCamera", Members);

        private const float PutBackSeconds = 0.2f;
        private const float MouthReachMeters = 0.07f;
        private const float EyeReachMeters = 0.08f;
        private const float FlipDot = -0.6f;
        private const float WindDegrees = 450f;
        private const float WindDecayDegreesPerSecond = 120f;
        private const float WaveMetersPerSecond = 0.7f;
        private const float WaveGapSeconds = 1.2f;
        private const float HammerMetersPerSecond = 0.5f;
        private const float TouchPadMeters = 0.01f;
        private const float RaiseMeters = 0.03f;
        private const float TargetOpenSeconds = 0.3f;
        private const float TargetPointMeters = 10f;
        private const float SelectCooldownSeconds = 0.5f;
        private static readonly Vector3 MouthOffsetMeters = new Vector3(0f, -0.08f, 0.07f);
        private static readonly Vector3 EyeOffsetMeters = new Vector3(0f, 0f, 0.02f);

        internal static bool Available { get { return SlotsField != null; } }

        private enum Use { Trigger, Mouth, Slam, Flip, Eye, Wind, Wave, Target }

        private sealed class Held
        {
            internal ConsumableItemSlot Slot;
            internal ConsumableItem Item;
            internal Carry Carry;
            internal Use Use;
            internal float SlamSpeed;
            internal bool Glass;
            internal bool Hammer;
            internal bool Raised;
            internal Vector3 UpLocal;
            internal Quaternion LastHandRotation;
            internal float Twist;
            internal readonly int[] WaveSign = new int[2];
            internal readonly float[] LastWave = new float[2];
        }

        private sealed class Pending
        {
            internal ConsumableItemSlot Slot;
            internal Transform Item;
            internal ConsumableItem Consumable;
            internal HandProp Prop;
            internal readonly RendererHider Hidden = new RendererHider();
            internal bool Cancelled;
            internal TargetSlotItem Targeting;
            internal Vector3 RestLocalPosition;
            internal Quaternion RestLocalRotation;
            internal CardSlot Target;
            internal bool TargetSent;
            internal bool AnnouncedOpen;
            internal float Start;
            internal float SelectedAt = -10f;
        }

        private readonly ManualLogSource _logger;
        private readonly float _slamSpeed;
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly List<CardSlot> _targets = new List<CardSlot>();
        private readonly HashSet<int> _cloneBaseline = new HashSet<int>();
        private readonly List<GameObject> _clones = new List<GameObject>();
        private Held _left;
        private Held _right;
        private CardSlot _leftAim;
        private CardSlot _rightAim;

        internal ItemHands(ManualLogSource logger, float slamMetersPerSecond)
        {
            _logger = logger;
            _slamSpeed = Mathf.Clamp(slamMetersPerSecond, 0.3f, 5f);
            if (ValidTargetsMethod == null)
                _logger.LogWarning("Item hands: target list for scissors, hook, knife and hammer not found; those items use the trigger and the pointer instead.");
            if (PixelCameraField == null)
                _logger.LogWarning("Item hands: the first-person animation camera was not found; the game's floating item copies stay visible.");
        }

        internal bool Holds(Component target)
        {
            return (_left != null && _left.Carry.Holds(target)) || (_right != null && _right.Carry.Holds(target));
        }

        internal void Tick(CardGrabber grabber, VrHand left, VrHand right, bool battle, float scale)
        {
            UpdatePending();
            HideClones();
            if (!battle)
            {
                PutBack(ref _left);
                PutBack(ref _right);
                ClearAim();
                return;
            }
            Pending choosing = OpenTargeting();
            if (choosing != null)
            {
                PutBack(ref _left);
                PutBack(ref _right);
                ChooseByHand(grabber, choosing, left, ref _leftAim, _rightAim, scale);
                ChooseByHand(grabber, choosing, right, ref _rightAim, _leftAim, scale);
                if (_targets.Count > 0) VrTips.Show("items.target", TipAnchor(_targets[0], scale), "Touch or point at a card, then squeeze to choose it.", 75);
                return;
            }
            ClearAim();
            List<ConsumableItemSlot> slots = Slots();
            if (slots == null) return;
            Process(grabber, left, ref _left, slots, scale);
            Process(grabber, right, ref _right, slots, scale);
            if (_left == null && _right == null && !left.Busy && !right.Busy) OfferGrabTip(slots, scale);
        }

        private void OfferGrabTip(List<ConsumableItemSlot> slots, float scale)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                ConsumableItemSlot slot = slots[i];
                if (!HandReach.Live(slot) || slot.Item == null || Busy(slot)) continue;
                ConsumableItem item = slot.Item as ConsumableItem;
                if (item == null || !item.CanActivate()) continue;
                VrTips.Show("items.grab", TipAnchor(item, scale), "Pick up an item with grip to use it.", 30);
                return;
            }
        }

        private static Vector3 TipAnchor(Component target, float scale)
        {
            Bounds bounds;
            if (!HandReach.RendererBounds(target, out bounds)) return target.transform.position + Vector3.up * (0.03f * scale);
            return new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
        }

        private static string UseTip(Held h, out string id)
        {
            switch (h.Use)
            {
                case Use.Mouth: id = "items.mouth"; return "Bring the pliers up to your mouth.";
                case Use.Slam: id = "items.slam"; return "Raise it, then slam it down on the table.";
                case Use.Flip: id = "items.flip"; return "Turn it upside down.";
                case Use.Eye: id = "items.eye"; return "Hold it up to your eye.";
                case Use.Wind: id = "items.wind"; return "Twist your wrist to wind it.";
                case Use.Wave: id = "items.wave"; return "Wave it once, up and down or side to side.";
                case Use.Target:
                    id = h.Hammer ? "items.hammer" : "items.touch";
                    return h.Hammer ? "Swing it down onto a card." : "Touch it to a card.";
                default: id = "items.trigger"; return "Pull the trigger to use it.";
            }
        }

        internal void Release()
        {
            PutBack(ref _left);
            PutBack(ref _right);
            ClearAim();
            for (int i = 0; i < _pending.Count; i++) EndPending(_pending[i]);
            _pending.Clear();
        }

        private static Transform CloneParent()
        {
            if (PixelCameraField == null) return null;
            FirstPersonController person = Singleton<FirstPersonController>.Instance;
            if (person == null || person.AnimController == null) return null;
            Camera camera = PixelCameraField.GetValue(person.AnimController) as Camera;
            return camera != null ? camera.transform : null;
        }

        private void MarkClones()
        {
            if (_pending.Count > 0) return;
            _cloneBaseline.Clear();
            Transform parent = CloneParent();
            if (parent == null) return;
            for (int i = 0; i < parent.childCount; i++) _cloneBaseline.Add(parent.GetChild(i).gameObject.GetInstanceID());
        }

        private void HideClones()
        {
            if (_pending.Count > 0)
            {
                Transform parent = CloneParent();
                if (parent != null)
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        GameObject child = parent.GetChild(i).gameObject;
                        if (_cloneBaseline.Contains(child.GetInstanceID()) || _clones.Contains(child)) continue;
                        _clones.Add(child);
                        _logger.LogInfo("The game's first-person copy " + child.name + " is hidden; the real item stays in your hand.");
                    }
            }
            for (int i = _clones.Count - 1; i >= 0; i--)
            {
                GameObject clone = _clones[i];
                if (clone == null)
                {
                    _clones.RemoveAt(i);
                    continue;
                }
                Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++) if (renderers[r] != null && renderers[r].enabled) renderers[r].enabled = false;
            }
        }

        private Pending OpenTargeting()
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                Pending p = _pending[i];
                if (p.Targeting == null || p.Slot == null || !p.Slot.ActivatingItem) continue;
                if (Time.unscaledTime - p.Start < TargetOpenSeconds || Time.unscaledTime - p.SelectedAt < SelectCooldownSeconds) continue;
                if (!CollectTargets(p)) continue;
                if (!p.AnnouncedOpen)
                {
                    p.AnnouncedOpen = true;
                    _logger.LogInfo("Item target choice open: touch or point at a card and squeeze grip or trigger.");
                }
                return p;
            }
            return null;
        }

        private bool CollectTargets(Pending p)
        {
            _targets.Clear();
            List<CardSlot> valid = null;
            if (ValidTargetsMethod != null && p.Targeting != null)
            {
                try { valid = ValidTargetsMethod.Invoke(p.Targeting, null) as List<CardSlot>; }
                catch (TargetInvocationException) { valid = null; }
            }
            if (valid == null)
            {
                BoardManager board = Singleton<BoardManager>.Instance;
                valid = board != null ? board.AllSlotsCopy : null;
            }
            if (valid == null) return false;
            for (int i = 0; i < valid.Count; i++)
            {
                CardSlot slot = valid[i];
                if (slot != null && HandReach.Active(slot) && slot.CursorSelectStarted != null) _targets.Add(slot);
            }
            return _targets.Count > 0;
        }

        private void ChooseByHand(CardGrabber grabber, Pending p, VrHand state, ref CardSlot aim, CardSlot otherAim, float scale)
        {
            if (!state.Ready || Time.unscaledTime - p.SelectedAt < SelectCooldownSeconds)
            {
                SetAim(ref aim, null, otherAim);
                return;
            }
            state.Claimed = true;
            CardSlot chosen = TouchedSlot(state, grabber.ReachMeters * scale);
            if (chosen == null) chosen = PointedSlot(state, scale);
            SetAim(ref aim, chosen, otherAim);
            if (chosen == null) return;
            state.Highlight();
            if (state.GripEdge != 1 && state.TriggerEdge != 1) return;
            state.GripEdge = 0;
            state.TriggerEdge = 0;
            p.TargetSent = true;
            p.SelectedAt = Time.unscaledTime;
            SetAim(ref aim, null, otherAim);
            chosen.CursorSelectStart();
            state.Play(Feel.Tap);
            VrTips.Done("items.target");
            _logger.LogInfo("Item target chosen by the " + state.Name + " hand: " + (chosen.Card != null ? chosen.Card.name : "lane " + chosen.Index) + ".");
        }

        private CardSlot TouchedSlot(VrHand state, float reach)
        {
            float best = reach * reach;
            CardSlot chosen = null;
            for (int i = 0; i < _targets.Count; i++)
            {
                Bounds bounds;
                if (!SlotBounds(_targets[i], out bounds)) continue;
                float distance = bounds.SqrDistance(state.Position);
                if (state.HasIndexTip) distance = Mathf.Min(distance, bounds.SqrDistance(state.IndexTip));
                if (distance < best)
                {
                    best = distance;
                    chosen = _targets[i];
                }
            }
            return chosen;
        }

        private CardSlot PointedSlot(VrHand state, float scale)
        {
            float best = TargetPointMeters * scale;
            CardSlot chosen = null;
            Ray ray = state.PointerRay;
            for (int i = 0; i < _targets.Count; i++)
            {
                Bounds bounds;
                float distance;
                if (!SlotBounds(_targets[i], out bounds) || !bounds.IntersectRay(ray, out distance) || distance >= best) continue;
                best = distance;
                chosen = _targets[i];
            }
            return chosen;
        }

        private static bool SlotBounds(CardSlot slot, out Bounds bounds)
        {
            if (slot.Card != null && HandReach.CardBounds(slot.Card, out bounds)) return true;
            Collider collider = slot.GetComponent<Collider>();
            if (collider != null && collider.enabled)
            {
                bounds = collider.bounds;
                return true;
            }
            return HandReach.RendererBounds(slot, out bounds);
        }

        private static void SetAim(ref CardSlot mine, CardSlot target, CardSlot other)
        {
            if (mine == target) return;
            CardSlot previous = mine;
            mine = target;
            if (previous != null && previous != other && HandReach.Active(previous)) previous.CursorExit();
            if (target != null && target != other) target.CursorEnter();
        }

        private void ClearAim()
        {
            SetAim(ref _leftAim, null, _rightAim);
            SetAim(ref _rightAim, null, _leftAim);
        }

        private static List<ConsumableItemSlot> Slots()
        {
            ItemsManager manager = Singleton<ItemsManager>.Instance;
            return manager != null ? SlotsField.GetValue(manager) as List<ConsumableItemSlot> : null;
        }

        private void Process(CardGrabber grabber, VrHand state, ref Held held, List<ConsumableItemSlot> slots, float scale)
        {
            if (held != null)
            {
                Hold(grabber, state, ref held, scale);
                return;
            }
            if (!state.Ready || state.Busy) return;
            TryGrab(grabber, state, ref held, slots, scale, state.GripEdge == 1);
        }

        private bool Busy(ConsumableItemSlot slot)
        {
            if ((_left != null && _left.Slot == slot) || (_right != null && _right.Slot == slot)) return true;
            for (int i = 0; i < _pending.Count; i++) if (_pending[i].Slot == slot) return true;
            return slot.ActivatingItem;
        }

        private void TryGrab(CardGrabber grabber, VrHand state, ref Held held, List<ConsumableItemSlot> slots, float scale, bool grab)
        {
            float reach = grabber.ReachMeters * scale;
            float best = reach * reach;
            ConsumableItemSlot chosen = null;
            for (int i = 0; i < slots.Count; i++)
            {
                ConsumableItemSlot slot = slots[i];
                if (!HandReach.Active(slot) || slot.Item == null || Busy(slot)) continue;
                float distance = Mathf.Min(HandReach.RendererSqrDistance(slot.Item, state.Position), HandReach.InteractableSqrDistance(slot, state.Position));
                if (distance < best)
                {
                    best = distance;
                    chosen = slot;
                }
            }
            bool touch = chosen != null;
            if (touch && grabber.HandCardWithin(state, best)) return;
            if (!touch && !state.AimGrabBlocked)
            {
                Collider hit = HandReach.Pointed(state, scale);
                ConsumableItemSlot pointed = hit != null ? hit.GetComponentInParent<ConsumableItemSlot>() : null;
                if (pointed != null && slots.Contains(pointed) && pointed.Item != null && !Busy(pointed)) chosen = pointed;
            }
            if (chosen == null) return;
            ConsumableItem candidate = chosen.Item as ConsumableItem;
            if (candidate != null && HandReach.Live(chosen) && candidate.CanActivate())
            {
                if (touch) state.Highlight();
                GrabGlow.Mark(candidate);
            }
            if (!grab) return;
            ConsumableItem item = chosen.Item as ConsumableItem;
            if (item == null) return;
            state.GripEdge = 0;
            if (!HandReach.Live(chosen) || !item.CanActivate())
            {
                if (HandReach.Live(chosen)) chosen.CursorSelectStart();
                state.Play(Feel.Reject);
                _logger.LogInfo("Item " + item.name + " can't be used right now.");
                return;
            }

            Held started = new Held();
            started.Slot = chosen;
            started.Item = item;
            started.Carry = new Carry(item.transform, state, touch, state.HoldOffset(0.02f, scale), true);
            Classify(started);
            started.UpLocal = Quaternion.Inverse(item.transform.rotation) * Vector3.up;
            started.LastHandRotation = state.Rotation;
            held = started;
            state.Claimed = true;
            item.ShowHighlighted(false);
            item.PlayPickUpSound();
            state.Play(Feel.Tap);
            state.SetHoldPose(HandHold.Wrap);
            VrTips.Done("items.grab");
            _logger.LogInfo("Item " + item.name + " picked up with the " + state.Name + " hand (" + (touch ? "touch" : "point") + "); use: " + started.Use + ".");
        }

        private void Classify(Held held)
        {
            ConsumableItem item = held.Item;
            held.SlamSpeed = _slamSpeed;
            if (item is PliersItem) held.Use = Use.Mouth;
            else if (item is CardBottleItem || item is GooBottleItem) { held.Use = Use.Slam; held.Glass = true; }
            else if (item is PiggyBankItem) { held.Use = Use.Slam; held.SlamSpeed = _slamSpeed * 1.2f; }
            else if (item is HourglassItem) held.Use = Use.Flip;
            else if (item is MagnifyingGlassItem || item is SpecialDaggerItem) held.Use = Use.Eye;
            else if (item is PocketWatchItem) held.Use = Use.Wind;
            else if (item is BirdLegFanItem) held.Use = Use.Wave;
            else if (item is TargetSlotItem && ValidTargetsMethod != null) { held.Use = Use.Target; held.Hammer = item is HammerItem; }
            else held.Use = Use.Trigger;
        }

        private void Hold(CardGrabber grabber, VrHand state, ref Held held, float scale)
        {
            Held h = held;
            if (h.Item == null || h.Slot == null || h.Slot.Item != h.Item)
            {
                held = null;
                return;
            }
            if (!state.Ready || !state.Grip.Held || !h.Item.CanActivate())
            {
                PutBack(ref held);
                state.Play(Feel.Tick, 0.6f);
                return;
            }
            state.Claimed = true;
            state.SetHoldPose(HandHold.Wrap);
            h.Carry.Update();
            string tipId;
            string tip = UseTip(h, out tipId);
            VrTips.Show(tipId, TipAnchor(h.Item, scale), tip, 78);

            CardSlot target = null;
            string action = Detect(grabber, state, h, scale, out target);
            if (action == null) return;
            held = null;
            if (Activate(state, h, target, action)) VrTips.Done(tipId);
        }

        private string Detect(CardGrabber grabber, VrHand state, Held h, float scale, out CardSlot target)
        {
            target = null;
            Bounds bounds;
            if (!HandReach.RendererBounds(h.Item, out bounds)) bounds = new Bounds(h.Item.transform.position, Vector3.one * 0.02f * scale);
            Vector3 velocity = state.Velocity;
            Vector3 head = Vector3.zero;
            Quaternion headRotation = Quaternion.identity;
            float unitsPerMeter = scale;
            bool haveHead = grabber.Renderer != null && grabber.Renderer.TryGetHeadPose(out head, out headRotation, out unitsPerMeter);
            if (unitsPerMeter <= 0f) unitsPerMeter = scale;

            switch (h.Use)
            {
                case Use.Mouth:
                    if (!haveHead) return Fallback(state);
                    float mouth = MouthReachMeters * unitsPerMeter;
                    return bounds.SqrDistance(head + headRotation * MouthOffsetMeters * unitsPerMeter) <= mouth * mouth ? "brought to the mouth" : null;
                case Use.Eye:
                    if (!haveHead) return Fallback(state);
                    float eye = EyeReachMeters * unitsPerMeter;
                    bool near = bounds.SqrDistance(head + headRotation * EyeOffsetMeters * unitsPerMeter) <= eye * eye;
                    return near && Vector3.Dot(headRotation * Vector3.forward, bounds.center - head) > 0f ? "held to the eye" : null;
                case Use.Slam:
                    return Slam(grabber, h, bounds, velocity, scale);
                case Use.Flip:
                    return Vector3.Dot(h.Item.transform.rotation * h.UpLocal, Vector3.up) < FlipDot ? "turned over" : null;
                case Use.Wind:
                    return Wind(state, h) ? "wound" : null;
                case Use.Wave:
                    return Wave(h, velocity, haveHead ? headRotation * Vector3.right : Vector3.right, scale) ? "waved" : null;
                case Use.Target:
                    target = TouchedTarget(h, bounds, velocity, scale);
                    return target != null ? (h.Hammer ? "swung onto " : "touched to ") + (target.Card != null ? target.Card.name : "lane " + target.Index) : null;
                default:
                    return Fallback(state);
            }
        }

        private static string Fallback(VrHand state)
        {
            return state.TriggerEdge == 1 ? "used with the trigger" : null;
        }

        private static string Slam(CardGrabber grabber, Held h, Bounds bounds, Vector3 velocity, float scale)
        {
            if (!grabber.HaveTable || !grabber.InsideTable(bounds.center)) return null;
            float surface = grabber.TableY;
            if (bounds.min.y > surface + RaiseMeters * scale) h.Raised = true;
            if (bounds.min.y > surface) return null;
            if (h.Raised && -velocity.y >= h.SlamSpeed * scale) return "slammed on the table";
            h.Carry.Lift(surface - bounds.min.y);
            return null;
        }

        private static bool Wind(VrHand state, Held h)
        {
            Quaternion delta = Quaternion.Inverse(h.LastHandRotation) * state.Rotation;
            h.LastHandRotation = state.Rotation;
            float twist = 2f * Mathf.Atan2(delta.z, delta.w) * Mathf.Rad2Deg;
            if (twist > 180f) twist -= 360f;
            if (twist < -180f) twist += 360f;
            h.Twist = Mathf.Max(0f, h.Twist - WindDecayDegreesPerSecond * Time.unscaledDeltaTime) + Mathf.Abs(twist);
            return h.Twist >= WindDegrees;
        }

        private static bool Wave(Held h, Vector3 velocity, Vector3 across, float scale)
        {
            across.y = 0f;
            if (across.sqrMagnitude < 1e-6f) across = Vector3.right;
            across.Normalize();
            return WaveStroke(h, 0, Vector3.Dot(velocity, across), scale) | WaveStroke(h, 1, velocity.y, scale);
        }

        private static bool WaveStroke(Held h, int axis, float speed, float scale)
        {
            float now = Time.unscaledTime;
            if (now - h.LastWave[axis] > WaveGapSeconds) h.WaveSign[axis] = 0;
            if (Mathf.Abs(speed) < WaveMetersPerSecond * scale) return false;
            int sign = speed > 0f ? 1 : -1;
            if (sign == h.WaveSign[axis])
            {
                h.LastWave[axis] = now;
                return false;
            }
            bool reversed = h.WaveSign[axis] != 0;
            h.WaveSign[axis] = sign;
            h.LastWave[axis] = now;
            return reversed;
        }

        private static CardSlot TouchedTarget(Held h, Bounds bounds, Vector3 velocity, float scale)
        {
            if (h.Hammer && -velocity.y < HammerMetersPerSecond * scale) return null;
            List<CardSlot> targets = ValidTargetsMethod.Invoke(h.Item, null) as List<CardSlot>;
            if (targets == null) return null;
            float pad = TouchPadMeters * scale;
            for (int i = 0; i < targets.Count; i++)
            {
                CardSlot slot = targets[i];
                if (slot == null || slot.Card == null) continue;
                Bounds card;
                if (!HandReach.CardBounds(slot.Card, out card)) continue;
                card.Expand(pad * 2f);
                if (card.Intersects(bounds)) return slot;
            }
            return null;
        }

        private bool Activate(VrHand state, Held h, CardSlot target, string action)
        {
            Pending pending = new Pending();
            pending.Slot = h.Slot;
            pending.Item = h.Item.transform;
            pending.Consumable = h.Item;
            pending.Targeting = h.Item as TargetSlotItem;
            pending.RestLocalPosition = h.Carry.RestLocalPosition;
            pending.RestLocalRotation = h.Carry.RestLocalRotation;
            pending.Target = target;
            pending.Start = Time.unscaledTime;
            string name = h.Item.name;
            if (h.Glass) VrSounds.Play("glass_object_hit", MixerGroup.TableObjectsSFX, h.Item.transform.position, 1f);
            MarkClones();
            pending.Prop = h.Glass ? null : HandProp.Copy(h.Item.transform, name);
            pending.Hidden.Hide(h.Item);
            h.Slot.CursorSelectStart();
            if (!h.Slot.ActivatingItem)
            {
                EndPending(pending);
                state.Play(Feel.Reject);
                _logger.LogInfo("Item " + name + " " + action + " but the game did not start using it.");
                return false;
            }
            if (pending.Prop != null) pending.Prop.Follow(state, HandHold.Wrap);
            _pending.Add(pending);
            state.Play(h.Glass ? Feel.Shatter : h.Use == Use.Slam || h.Hammer ? Feel.Thunk : Feel.Thud);
            _logger.LogInfo("Item " + name + " " + action + " with the " + state.Name + " hand; using it.");
            return true;
        }

        private void UpdatePending()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                if (!p.Cancelled && p.Consumable != null && p.Consumable.ActivationCancelled)
                {
                    p.Cancelled = true;
                    EndPending(p);
                    _logger.LogInfo("Item " + p.Consumable.name + " was not used after all; it goes back to its spot.");
                }
                if (!p.Cancelled) p.Hidden.Keep();
                if (p.Target != null && !p.TargetSent && Time.unscaledTime - p.Start > TargetOpenSeconds && p.Target.CursorSelectStarted != null)
                {
                    p.TargetSent = true;
                    p.SelectedAt = Time.unscaledTime;
                    p.Target.CursorSelectStart();
                    _logger.LogInfo("Item target chosen: lane " + p.Target.Index + ".");
                }
                if (p.Slot != null && p.Slot.ActivatingItem) continue;
                EndPending(p);
                _pending.RemoveAt(i);
            }
        }

        private static void EndPending(Pending p)
        {
            if (p.Item == null)
            {
                p.Hidden.Forget();
                return;
            }
            if (p.Prop != null) p.Prop.Destroy();
            p.Prop = null;
            p.Hidden.Restore();
            p.Item.localPosition = p.RestLocalPosition;
            p.Item.localRotation = p.RestLocalRotation;
        }

        private static void PutBack(ref Held held)
        {
            Held h = held;
            held = null;
            if (h == null || h.Item == null) return;
            h.Carry.PutBack(PutBackSeconds);
        }
    }
}
