using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class CabinWalk : MonoBehaviour
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo CanStandMethod = typeof(GameFlowManager).GetMethod("CanTransitionToFirstPerson", Members, null, Type.EmptyTypes, null);
        private static readonly FieldInfo AllowedViewsField = typeof(ViewController).GetField("allowedViews", Members);
        private static readonly FieldInfo HintShownField = typeof(UIManager).GetField("rightHintShown", Members);
        private static readonly FieldInfo HintRightField = typeof(UIManager).GetField("controlsHintRight", Members);
        private static readonly FieldInfo DownHintSpriteField = typeof(UIManager).GetField("downViewButtonSprite", Members);
        private const float ScriptedStandCooldown = 1f;
        private float _scriptedStandAt = -10f;
        private bool _loggedScriptedRefusal;

        private const float FlickAxis = 0.7f;
        private const float LookSwitchDegrees = 55f;
        private const float TouchReachMeters = 0.12f;
        private const float SitDownSeconds = 0.4f;

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private VrInput _input;
        private CardGrabber _grabber;
        private VrDialogue _dialogue;
        private bool _failed;
        private float _dialogueEndedAt = -10f;
        private const float RunIntroReviewSeconds = 4f;
        private float _reviewIdleSince = -1f;
        private float _reviewSeenAt = -10f;
        private bool _wasWalking;
        private float _sittingSince = -1f;
        private bool _loggedRefusal;
        private float _snapDegrees = 90f;
        private readonly List<MainInputInteractable> _targets = new List<MainInputInteractable>();
        private MainInputInteractable _leftHover;
        private MainInputInteractable _rightHover;
        private CabinDials _dials;
        private string _scene;

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, VrInput input, CardGrabber grabber, VrDialogue dialogue)
        {
            _logger = logger;
            _renderer = renderer;
            _input = input;
            _grabber = grabber;
            _dialogue = dialogue;
            if (CanStandMethod == null) _logger.LogWarning("Cabin walking: GameFlowManager.CanTransitionToFirstPerson was not found; standing up will be tried without the game's own check.");
            if (HintShownField == null || HintRightField == null || DownHintSpriteField == null)
                _logger.LogWarning("Cabin walking: UIManager's look-down hint fields were not found; when Leshy tells you to get up mid-battle, press S on the keyboard.");
            if (AllowedViewsField == null) _logger.LogWarning("Deck review: ViewController.allowedViews was not found; use the S key to leave the deck review.");
        }

        internal void SetSnapDegrees(float degrees) { _snapDegrees = Mathf.Clamp(degrees, 15f, 180f); }

        internal void EnableDials() { _dials = new CabinDials(_logger); }

        private void LateUpdate()
        {
            if (_logger == null || _failed) return;
            try
            {
                GameKeys.Tick();
                Tick();
            }
            catch (Exception exception)
            {
                _failed = true;
                try { GameKeys.ReleaseAll(); } catch (Exception) { }
                try { ClearHover(); } catch (Exception) { }
                try { _renderer.EndWalk(); } catch (Exception) { }
                _logger.LogError("Cabin walking stopped after an error; use the keyboard to stand up and walk: " + exception);
            }
        }

        private void Tick()
        {
            string scene = VrScenes.Current;
            if (scene != _scene)
            {
                _scene = scene;
                Leave();
            }
            if (VrScenes.InFinale)
            {
                TickFinale();
                return;
            }
            if (!VrScenes.InCabin)
            {
                Leave();
                return;
            }
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            FirstPersonController walker = Singleton<FirstPersonController>.Instance;
            bool walking = flow != null && walker != null && walker.isActiveAndEnabled && flow.CurrentGameState == GameState.FirstPerson3D;
            if (walking)
            {
                _sittingSince = -1f;
                if (!_wasWalking)
                {
                    _wasWalking = true;
                    _renderer.BeginWalk();
                }
            }
            else if (_wasWalking)
            {
                ClearHover();
                if (_sittingSince < 0f) _sittingSince = Time.unscaledTime;
                if (Time.unscaledTime - _sittingSince >= SitDownSeconds)
                {
                    _wasWalking = false;
                    _sittingSince = -1f;
                    _renderer.EndWalk();
                }
            }
            _input.Refresh();
            if (!_input.Active || !_input.InputAvailable || Time.timeScale <= 0f || flow == null || RuleBookHands.Open)
            {
                ClearHover();
                return;
            }
            VrHand left = _input.Left, right = _input.Right, main = _input.Main, off = _input.Off;
            if (!walking)
            {
                if (_wasWalking) return;
                if (InDeckReview())
                {
                    TryLeaveDeckReview(left, right, main);
                    return;
                }
                TryStand(flow, main);
                return;
            }
            if (!flow.Transitioning)
            {
                ShowWalkTip(WalkTip, Capital(_input.Off.Name) + " stick: walk. " + Capital(_input.Main.Name) + " stick left/right: turn. " +
                    Capital(_input.Main.Name) + " stick up: interact ahead (sit back down facing the table). Grip: use things.");
                SyncLook(walker);
                Navigate(flow, walker, off, main);
            }
            Interact(left, right);
        }

        private void TickFinale()
        {
            FirstPersonController walker = Singleton<FirstPersonController>.Instance;
            if (walker == null)
            {
                ClearHover();
                return;
            }
            if (!_wasWalking)
            {
                _wasWalking = true;
                _sittingSince = -1f;
                _renderer.BeginWalk(walker.transform.eulerAngles.y, "VR seat follows you along the finale path; walk with the sticks like in the cabin.");
            }
            _input.Refresh();
            ViewManager views = Singleton<ViewManager>.Instance;
            bool atTable = _renderer.FixedSeat || (views != null && views.CurrentView == View.GiantHandTable);
            if (!_input.Active || !_input.InputAvailable || Time.timeScale <= 0f || RuleBookHands.Open || atTable || !walker.isActiveAndEnabled)
            {
                ClearHover();
                return;
            }
            ShowWalkTip(WalkTip, Capital(_input.Off.Name) + " stick: walk. " + Capital(_input.Main.Name) + " stick left/right: turn. " +
                Capital(_input.Main.Name) + " stick up: go toward what's ahead.");
            SyncLook(walker);
            Navigate(null, walker, _input.Off, _input.Main);
            Interact(_input.Left, _input.Right);
        }

        private const string WalkTip = "cabin.walk";

        private void ShowWalkTip(string id, string text)
        {
            Vector3 head;
            Quaternion view;
            float unitsPerMeter;
            if (!_renderer.TryGetHeadPose(out head, out view, out unitsPerMeter)) return;
            Vector3 forward = view * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return;
            VrTips.Show(id, head + forward.normalized * (0.9f * unitsPerMeter) - Vector3.up * (0.15f * unitsPerMeter), text, 10);
        }

        private void Leave()
        {
            if (_wasWalking)
            {
                _wasWalking = false;
                _sittingSince = -1f;
                _renderer.EndWalk();
            }
            ClearHover();
        }

        private void OnDestroy()
        {
            GameKeys.ReleaseAll();
        }

        private void TryStand(GameFlowManager flow, VrHand stickHand)
        {
            if (flow.Transitioning) return;
            bool mapStand = flow.CurrentGameState == GameState.Map && StandAllowed(flow);
            if (!mapStand && flow.CurrentGameState != GameState.FirstPerson3D && GameWantsLookDown())
            {
                TryScriptedStand(stickHand);
                return;
            }
            if (flow.CurrentGameState != GameState.Map) return;
            bool able = mapStand;
            if (able)
            {
                GameMap map = Singleton<GameMap>.Instance;
                if (map != null) VrTips.Show("cabin.stand", map.transform.position + Vector3.up * (0.25f * _input.Scale), "Push the " + stickHand.Name + " stick down to get up from the table.", 10);
            }
            if (!stickHand.Tracked || stickHand.StickFlick.y > -FlickAxis) return;
            if (!able)
            {
                stickHand.Play(Feel.Reject, 0.6f);
                if (!_loggedRefusal)
                {
                    _loggedRefusal = true;
                    _logger.LogInfo(Capital(stickHand.Name) + " stick down: the game won't let you stand up right now (map still moving, not unrolled, or the region is ending).");
                }
                return;
            }
            stickHand.Play(Feel.Tap);
            VrTips.Done("cabin.stand");
            _logger.LogInfo(Capital(stickHand.Name) + " stick down: getting up from the table.");
            flow.TransitionToFirstPerson();
        }

        private void TryScriptedStand(VrHand stickHand)
        {
            ShowWalkTip("cabin.stand", "Push the " + stickHand.Name + " stick down to get up from the table.");
            if (!stickHand.Tracked || stickHand.StickFlick.y > -FlickAxis) return;
            stickHand.StickFlick = Vector2.zero;
            if (Time.unscaledTime - _scriptedStandAt < ScriptedStandCooldown) return;
            _scriptedStandAt = Time.unscaledTime;
            string key;
            if (!GameKeys.PressAction("LookDown", KeyCode.S, out key))
            {
                stickHand.Play(Feel.Reject, 0.6f);
                if (!_loggedScriptedRefusal)
                {
                    _loggedScriptedRefusal = true;
                    _logger.LogWarning(Capital(stickHand.Name) + " stick down: Leshy wants you to get up, but the game window isn't focused, so the look-down key couldn't be sent. Click the game window, or press S.");
                }
                return;
            }
            stickHand.Play(Feel.Tap);
            VrTips.Done("cabin.stand");
            _logger.LogInfo(Capital(stickHand.Name) + " stick down: the game asked you to look down to get up, so its look-down key (" + key + ") was pressed for it.");
        }

        private static bool GameWantsLookDown()
        {
            if (HintShownField == null || HintRightField == null || DownHintSpriteField == null) return false;
            UIManager ui = Singleton<UIManager>.Instance;
            if (ui == null) return false;
            object shown = HintShownField.GetValue(ui);
            if (!(shown is bool) || !(bool)shown) return false;
            Animator hint = HintRightField.GetValue(ui) as Animator;
            Sprite down = DownHintSpriteField.GetValue(ui) as Sprite;
            if (hint == null || down == null) return false;
            AnimatingSprite sprite = hint.GetComponent<AnimatingSprite>();
            return sprite != null && sprite.frames != null && sprite.frames.Count > 0 && sprite.frames[0] == down;
        }

        private static bool InDeckReview()
        {
            ViewManager views = Singleton<ViewManager>.Instance;
            return views != null && views.CurrentView == View.MapDeckReview;
        }

        private void TryLeaveDeckReview(VrHand left, VrHand right, VrHand stickHand)
        {
            bool talking = _dialogue != null && _dialogue.IsInputDialogue;
            if (talking) _dialogueEndedAt = Time.unscaledTime;
            bool stick = stickHand.Tracked && stickHand.StickFlick.y <= -FlickAxis;
            bool trigger = !talking && Time.unscaledTime - _dialogueEndedAt > 0.3f &&
                ((left.Tracked && left.TriggerEdge == 1) || (right.Tracked && right.TriggerEdge == 1));
            VrHand hand = stick ? stickHand : left.TriggerEdge == 1 ? left : right;
            GameMap map = Singleton<GameMap>.Instance;
            if (!talking && map != null)
                VrTips.Show("map.deck", map.transform.position + Vector3.up * (0.25f * _input.Scale), "Push the " + stickHand.Name + " stick down or pull a trigger to go back to the map.", 15);
            bool auto = RunIntroReviewDone(talking);
            if (!stick && !trigger && !auto) return;
            if (stick) stickHand.StickFlick = Vector2.zero;
            ViewManager views = Singleton<ViewManager>.Instance;
            if (auto && !stick && !trigger)
            {
                View next;
                if (views.Controller == null || views.Controller.LockState == ViewLockState.Locked || !ViewBelow(views, out next)) return;
                _reviewIdleSince = -1f;
                views.SwitchToView(next);
                VrTips.Done("map.deck");
                _logger.LogInfo("Run intro deck review closed after " + RunIntroReviewSeconds + " s; back to " + next + ".");
                return;
            }
            View back;
            if (views.Controller == null || views.Controller.LockState == ViewLockState.Locked || !ViewBelow(views, out back))
            {
                hand.Play(Feel.Reject, 0.5f);
                return;
            }
            if (trigger) hand.TriggerEdge = 0;
            hand.Play(Feel.Tap);
            views.SwitchToView(back);
            VrTips.Done("map.deck");
            _logger.LogInfo("Deck review closed with the " + (stick ? stickHand.Name + " stick" : hand.Name + " trigger") + "; back to " + back + ".");
        }

        private bool RunIntroReviewDone(bool talking)
        {
            float now = Time.unscaledTime;
            bool stale = now - _reviewSeenAt > 0.25f;
            _reviewSeenAt = now;
            ViewManager views = Singleton<ViewManager>.Instance;
            bool intro = RunState.Run != null && !RunState.Run.runIntroCompleted;
            bool waiting = intro && !talking && views.Controller != null && views.Controller.LockState != ViewLockState.Locked;
            if (!waiting || stale)
            {
                _reviewIdleSince = waiting ? now : -1f;
                return false;
            }
            if (_reviewIdleSince < 0f) _reviewIdleSince = now;
            return now - _reviewIdleSince >= RunIntroReviewSeconds;
        }

        private static bool ViewBelow(ViewManager views, out View below)
        {
            below = View.MapDefault;
            List<View> allowed = AllowedViewsField != null ? AllowedViewsField.GetValue(views.Controller) as List<View> : null;
            if (allowed == null) return false;
            int index = allowed.IndexOf(views.CurrentView);
            if (index <= 0) return false;
            below = allowed[index - 1];
            return true;
        }

        private static bool StandAllowed(GameFlowManager flow)
        {
            ViewManager views = Singleton<ViewManager>.Instance;
            if (views == null || (views.CurrentView != View.MapDefault && views.CurrentView != View.Default)) return false;
            MapNodeManager nodes = Singleton<MapNodeManager>.Instance;
            if (nodes != null && nodes.MovingNodes) return false;
            if (CanStandMethod == null) return true;
            object result = CanStandMethod.Invoke(flow, null);
            return result is bool && (bool)result;
        }

        private void SyncLook(FirstPersonController walker)
        {
            if (walker.LookLocked) return;
            float yaw;
            if (!HeadYaw(out yaw)) return;
            float current = (int)walker.LookDirection * 90f;
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, current)) < LookSwitchDegrees) return;
            LookDirection facing = Quantize(yaw);
            if (facing == walker.LookDirection) return;
            walker.LookAtDirection(facing, true);
        }

        private void Navigate(GameFlowManager flow, FirstPersonController walker, VrHand mover, VrHand turner)
        {
            Vector2 turn = turner.Tracked ? turner.StickFlick : Vector2.zero;
            Vector2 flick = mover.Tracked ? mover.StickFlick : Vector2.zero;
            if ((turn != Vector2.zero || flick != Vector2.zero) && (walker.MoveLocked || walker.LookLocked) && Unzoom())
            {
                (turn != Vector2.zero ? turner : mover).Play(Feel.Tap);
                return;
            }
            if (Mathf.Abs(turn.x) > FlickAxis)
            {
                if (walker.LookLocked)
                {
                    turner.Play(Feel.Reject, 0.5f);
                    return;
                }
                _renderer.TurnWalk(turn.x > 0f ? _snapDegrees : -_snapDegrees);
                FirstPersonController.PlayLookWhooshSound();
                turner.Play(Feel.Tick);
                VrTips.Done("cabin.walk");
                return;
            }
            if (turn.y > FlickAxis)
            {
                Step(flow, walker, turner, walker.LookDirection, true);
                return;
            }
            Vector2 move = mover.Tracked ? mover.StickFlick : Vector2.zero;
            if (move == Vector2.zero) return;
            float yaw;
            if (!HeadYaw(out yaw)) return;
            LookDirection direction = Quantize(yaw + Mathf.Atan2(move.x, move.y) * Mathf.Rad2Deg);
            Step(flow, walker, mover, direction, false);
            VrTips.Done("cabin.walk");
        }

        private void Step(GameFlowManager flow, FirstPersonController walker, VrHand hand, LookDirection direction, bool interact)
        {
            if (walker.MoveLocked)
            {
                hand.Play(Feel.Reject, 0.5f);
                return;
            }
            NavigationZone3D zone = walker.CurrentZone;
            int fired = 0;
            if (direction == walker.LookDirection) fired = FireEvents(zone, direction);
            if (fired > 0)
            {
                hand.Play(Feel.Tap);
                _logger.LogInfo(Place() + ": " + fired + " event(s) ahead of '" + (zone != null ? zone.name : "?") + "' (" + direction + ") triggered.");
                if ((flow != null && flow.Transitioning) || !walker.isActiveAndEnabled || walker.MoveLocked) return;
            }
            NavigationGrid grid = NavigationGrid.instance;
            NavigationZone3D next = grid != null && zone != null ? grid.GetZoneInDirection(direction, zone) as NavigationZone3D : null;
            if (next == null)
            {
                if (fired == 0) hand.Play(Feel.Reject, interact ? 0.6f : 0.4f);
                return;
            }
            walker.SetZone(next);
            hand.Play(Feel.Tick);
            if (VrScenes.InFinale) VrLog.Chatter(_logger, "Finale: stepped " + direction + " to '" + next.name + "'.");
        }

        private static string Place() { return VrScenes.InFinale ? "Finale" : "Cabin"; }

        private bool Unzoom()
        {
            MainInputInteractable[] all = Interactables.All;
            bool found = false;
            for (int i = 0; i < all.Length; i++)
            {
                ZoomInteractable zoom = all[i] as ZoomInteractable;
                if (zoom == null || !zoom.Zoomed) continue;
                zoom.SetZoomed(false);
                found = true;
                _logger.LogInfo("Cabin: stepped back from " + zoom.name + " (stick flick leaves the game's zoom).");
            }
            return found;
        }

        private static int FireEvents(NavigationZone3D zone, LookDirection direction)
        {
            if (zone == null || zone.events == null) return 0;
            int fired = 0;
            NavigationEvent[] events = zone.events.ToArray();
            for (int i = 0; i < events.Length; i++)
            {
                NavigationEvent e = events[i];
                if (e == null || e.disabled || e.direction != direction || e.eventToTrigger == null) continue;
                e.eventToTrigger();
                fired++;
            }
            return fired;
        }

        private bool HeadYaw(out float yaw)
        {
            yaw = 0f;
            Vector3 head;
            Quaternion view;
            float unitsPerMeter;
            if (!_renderer.TryGetHeadPose(out head, out view, out unitsPerMeter)) return false;
            Vector3 forward = view * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = view * Vector3.up;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-4f) return false;
            }
            yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            return true;
        }

        private static LookDirection Quantize(float yaw)
        {
            int quarter = Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 90f) % 4;
            return (LookDirection)quarter;
        }

        private void Interact(VrHand left, VrHand right)
        {
            Collect();
            float scale = _input.Scale;
            if (_dials != null && EventStyle.Simplified) _dials.Release();
            else if (_dials != null)
            {
                try { _dials.Tick(left, right, _grabber != null ? _grabber.ReachMeters : TouchReachMeters, scale); }
                catch (Exception exception)
                {
                    _logger.LogError("Cabin dials turned off after an error; click the dials and knobs instead: " + exception);
                    try { _dials.Release(); } catch (Exception) { }
                    _dials = null;
                }
            }
            Process(left, ref _leftHover, _rightHover, scale);
            Process(right, ref _rightHover, _leftHover, scale);
            if (_leftHover != null) _leftHover.CursorStay();
            if (_rightHover != null && _rightHover != _leftHover) _rightHover.CursorStay();
        }

        private void Collect()
        {
            _targets.Clear();
            MainInputInteractable[] all = Interactables.All;
            for (int i = 0; i < all.Length; i++)
                if (Interactables.Usable(all[i])) _targets.Add(all[i]);
        }

        private void Process(VrHand hand, ref MainInputInteractable hover, MainInputInteractable other, float scale)
        {
            if (!hand.Tracked || hand.Busy || (_dials != null && _dials.Hovering(hand)))
            {
                SetHover(ref hover, null, other);
                return;
            }
            bool touch;
            MainInputInteractable chosen = Nearest(hand, scale, out touch);
            SetHover(ref hover, chosen, other);
            if (chosen == null) return;
            hand.Highlight();
            if (hand.GripEdge != 1) return;
            hand.GripEdge = 0;
            string name = chosen.name;
            chosen.CursorSelectStart();
            if (chosen != null) chosen.CursorSelectEnd();
            hand.Play(Feel.Tap);
            _logger.LogInfo(Place() + ": " + name + " used with the " + hand.Name + " hand (" + (touch ? "touch" : "aim") + ").");
        }

        private MainInputInteractable Nearest(VrHand hand, float scale, out bool touch)
        {
            float reach = (_grabber != null ? _grabber.ReachMeters : TouchReachMeters) * scale;
            float best = reach * reach;
            MainInputInteractable chosen = null;
            for (int i = 0; i < _targets.Count; i++)
            {
                float distance = HandReach.InteractableSqrDistance(_targets[i], hand.Position);
                if (hand.HasIndexTip) distance = Mathf.Min(distance, HandReach.InteractableSqrDistance(_targets[i], hand.IndexTip));
                if (distance < best)
                {
                    best = distance;
                    chosen = _targets[i];
                }
            }
            touch = chosen != null;
            if (chosen != null) return chosen;
            Collider hit = HandReach.Pointed(hand, scale);
            if (hit == null) return null;
            MainInputInteractable parent = null;
            for (int i = 0; i < _targets.Count; i++)
            {
                Transform target = _targets[i].transform;
                if (hit.transform == target) return _targets[i];
                if (parent == null && hit.transform.IsChildOf(target)) parent = _targets[i];
            }
            return parent;
        }

        private static void SetHover(ref MainInputInteractable mine, MainInputInteractable target, MainInputInteractable other)
        {
            Interactables.SetHover(ref mine, target, other);
        }

        private static string Capital(string name)
        {
            return string.IsNullOrEmpty(name) ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        private void ClearHover()
        {
            SetHover(ref _leftHover, null, _rightHover);
            SetHover(ref _rightHover, null, _leftHover);
            if (_dials != null) _dials.Release();
        }
    }
}
