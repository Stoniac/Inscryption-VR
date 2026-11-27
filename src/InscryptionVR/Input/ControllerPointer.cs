using System;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class ControllerPointer : MonoBehaviour
    {
        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private VrInput _input;
        private VrDialogue _dialogue;
        private ControllerVisual _visual;
        private GameCursorAdapter _game;
        private StatusLine _status;
        private bool _failed;
        private bool _userEnabled = true;
        private bool _laser;
        private bool _ownsCursor;
        private bool _originalCursorEnabled;
        private readonly ControllerInputRouter _mainRouter = new ControllerInputRouter();
        private readonly ControllerInputRouter _offRouter = new ControllerInputRouter();
        private int _suspendedView = -1;
        private uint _controllerIndex = uint.MaxValue;
        private VrHand _laserHand;
        private MainInputInteractable _hover;
        private MainInputInteractable _down;
        private int _seatRevision = -1;

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, VrInput input, VrDialogue dialogue, bool laser)
        {
            _logger = logger;
            _laser = laser;
            _renderer = renderer;
            _input = input;
            _dialogue = dialogue;
            _status = new StatusLine(logger, "Controller pointer");
            try
            {
                _game = new GameCursorAdapter();
                _visual = new ControllerVisual(transform);
                _logger.LogInfo("Pointer ready: either trigger advances dialogue" + (_laser ? "; the main-hand laser selects what it touches" : "; laser off") + ". F10 toggles it.");
            }
            catch (Exception exception) { Fail(exception); }
        }

        internal void SetLaser(bool laser)
        {
            if (_laser == laser) return;
            _laser = laser;
            if (!laser && _logger != null && !_failed) Relinquish();
            if (_logger != null) _logger.LogInfo("Controller laser " + (laser ? "on" : "off") + ".");
        }

        private void Update()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception) { Fail(exception); }
        }

        private void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F10))
            {
                _userEnabled = !_userEnabled;
                Relinquish();
                _logger.LogInfo("Controller pointer " + (_userEnabled ? "enabled" : "disabled; desktop cursor restored") + ".");
            }
            if (!_userEnabled) { _status.Set("disabled by F10"); return; }
            if (Input.GetKeyDown(KeyCode.F8) || Input.GetKeyDown(KeyCode.F9) || Input.GetKeyDown(KeyCode.T) || Input.GetKeyDown(KeyCode.S))
            {
                Relinquish();
                _status.Set("VR keyboard view/recenter/stop requested; current press cancelled");
                return;
            }
            if (_renderer != null && _seatRevision != _renderer.SeatRevision)
            {
                Relinquish();
                _seatRevision = _renderer.SeatRevision;
            }
            _input.Refresh();
            if (!_input.Active)
            {
                Relinquish();
                _status.Set("waiting for VR tracking and recenter anchor");
                return;
            }
            bool cabin = VrScenes.InCabin;
            if (!VrScenes.InVrScene || Time.timeScale == 0f || (cabin && !_game.Resolve()) || GameHooks.IsPaused)
            {
                Relinquish();
                _status.Set("desktop input: unsupported scene, pause or transition");
                return;
            }
            if (!_input.InputAvailable) { Relinquish(); _status.Set("SteamVR input unavailable or overlay owns input"); return; }

            if (_dialogue != null && _dialogue.IsInputDialogue)
            {
                Relinquish(false);
                _status.Set("dialogue: release either trigger to advance");
                int mainEdge = _mainRouter.Sample(ControllerInputContext.Dialogue, _input.Main.TriggerDigital, _input.Main.TriggerValue);
                int offEdge = _offRouter.Sample(ControllerInputContext.Dialogue, _input.Off.TriggerDigital, _input.Off.TriggerValue);
                if ((mainEdge == -1 || offEdge == -1) && _dialogue.TryAdvance(true))
                    VrLog.Chatter(_logger, "Dialogue advanced with the " + (mainEdge == -1 ? _input.Main : _input.Off).Name + " trigger.");
                return;
            }
            _offRouter.Cancel();
            if (!cabin) { Relinquish(); _status.Set("outside the cabin: triggers advance dialogue, hands do the rest"); return; }
            if (!_laser) { Relinquish(); _status.Set("laser off; hands only"); return; }

            int view = _game.CurrentView;
            if (!_game.IsPlayablePhase || (!_game.IsSpecialEvent && !IsSupportedView(view)))
            {
                Relinquish();
                _status.Set("waiting for a playable phase (view " + (View)view + ")");
                return;
            }
            if (_suspendedView >= 0)
            {
                if (_suspendedView == view) { Relinquish(); _status.Set("desktop cursor requested by the game in view " + (View)view); return; }
                _suspendedView = -1;
            }
            if (_ownsCursor && _game.Cursor.enabled)
            {
                _suspendedView = view;
                Relinquish();
                _logger.LogInfo("Game re-enabled its desktop cursor; controller takeover suspended until the table view changes.");
                return;
            }
            VrHand hand = _input.Main;
            if (!hand.Tracked) { Relinquish(); _status.Set("waiting for the " + hand.Name + " controller"); return; }
            if (_laserHand != hand || _controllerIndex != hand.Index)
            {
                Relinquish();
                _laserHand = hand;
                _controllerIndex = hand.Index;
            }
            if (!TakeCursor()) return;
            _status.Set("active in view " + (View)view);
            Point(hand);
        }

        private bool TakeCursor()
        {
            if (_ownsCursor) return true;
            if (!_game.Cursor.enabled || GameHooks.Down(_game.Cursor) != null || Input.GetMouseButton(0))
            {
                _visual.SetVisible(false);
                _status.Set("waiting for the desktop cursor to finish its interaction");
                return false;
            }
            _mainRouter.Cancel();
            _originalCursorEnabled = _game.Cursor.enabled;
            _game.Cursor.enabled = false;
            _ownsCursor = true;
            MainInputInteractable desktopHover = GameHooks.Current(_game.Cursor);
            if (desktopHover != null && _game.CanDispatch(desktopHover)) desktopHover.CursorExit();
            GameHooks.SetCurrent(_game.Cursor, null);
            return true;
        }

        private void Point(VrHand hand)
        {
            InteractionCursor cursor = _game.Cursor;
            if (_hover != null && GameHooks.Current(cursor) != _hover) _hover = null;
            if (_down != null && GameHooks.Down(cursor) != _down) _down = null;
            if (hand.Busy)
            {
                _visual.SetVisible(false);
                DragOff();
                ChangeHover(null);
                _mainRouter.Cancel();
                _status.Set(hand.Name + " hand is holding something");
                return;
            }
            float scale = _input.Scale;
            float range = 10f * scale;
            Vector3 position = hand.AimOrigin;
            Vector3 direction = hand.AimDirection;
            int mask = GameHooks.RaycastMask(cursor) & ~(1 << 2);
            RaycastHit hit;
            bool hitSomething = Physics.Raycast(position, direction, out hit, range, mask, QueryTriggerInteraction.UseGlobal);
            float travelled = 0f;
            for (int skip = 0; hitSomething && skip < 4 && CardGrabber.IsHeld(hit.collider); skip++)
            {
                travelled += hit.distance + 0.001f * scale;
                hitSomething = travelled < range && Physics.Raycast(position + direction * travelled, direction, out hit, range - travelled, mask, QueryTriggerInteraction.UseGlobal);
            }
            if (hitSomething && CardGrabber.IsHeld(hit.collider)) hitSomething = false;
            MainInputInteractable target = hitSomething ? hit.transform.GetComponent<MainInputInteractable>() : null;
            if (target != null && !_game.CanDispatch(target)) target = null;
            ChangeHover(target);
            if (_hover != null) _hover.CursorStay();
            _visual.Draw(position, hitSomething ? hit.point : position + direction * (2f * scale), _hover != null, scale);

            int edge = _mainRouter.Sample(ControllerInputContext.World, hand.TriggerDigital, hand.TriggerValue);
            if (edge == 1 && _hover != null)
            {
                _down = _hover;
                GameHooks.SetDown(cursor, _down);
                _hover.CursorSelectStart();
                VrLog.Chatter(_logger, "Controller select start: " + Label(_hover));
            }
            if (edge == -1)
            {
                if (_hover != null && _game.CanDispatch(_hover))
                {
                    _hover.CursorSelectEnd();
                    VrLog.Chatter(_logger, "Controller select end: " + Label(_hover));
                }
                if (GameHooks.Down(cursor) == _down) GameHooks.SetDown(cursor, null);
                _down = null;
            }
            if (_down != null && _down != _hover) DragOff();
        }

        private void DragOff()
        {
            if (_down == null) return;
            if (_game.CanDispatch(_down)) _down.CursorDragOff();
            if (_game.Cursor != null && GameHooks.Down(_game.Cursor) == _down) GameHooks.SetDown(_game.Cursor, null);
            _down = null;
        }

        private void ChangeHover(MainInputInteractable target)
        {
            if (_hover == target) return;
            if (_hover != null && _game.CanDispatch(_hover)) _hover.CursorExit();
            _hover = target;
            GameHooks.SetCurrent(_game.Cursor, target);
            if (_hover != null) _hover.CursorEnter();
        }

        private static bool IsSupportedView(int view)
        {
            switch ((View)view)
            {
                case View.Hand: case View.Default: case View.Board: case View.CardPiles: case View.OpponentQueue: case View.Scales:
                case View.MapCloseIn: case View.MapDefault: case View.MapArial: case View.Consumables: case View.BoardCentered: case View.ConsumablesOnly:
                    return true;
                default: return false;
            }
        }

        private void Relinquish(bool cancelInput = true)
        {
            if (cancelInput)
            {
                _mainRouter.Cancel();
                _offRouter.Cancel();
            }
            if (_visual != null) _visual.SetVisible(false);
            if (!_ownsCursor) return;
            InteractionCursor cursor = _game.Cursor;
            try
            {
                if (_down != null && _game.CanDispatch(_down)) _down.CursorDragOff();
                if (_hover != null && cursor != null && GameHooks.Current(cursor) == _hover && _game.CanDispatch(_hover)) _hover.CursorExit();
            }
            catch (Exception exception)
            {
                if (_logger != null) _logger.LogWarning("Controller cursor cleanup callback failed: " + exception.Message);
            }
            finally
            {
                if (cursor != null)
                {
                    if (GameHooks.Current(cursor) == _hover) GameHooks.SetCurrent(cursor, null);
                    if (GameHooks.Down(cursor) == _down) GameHooks.SetDown(cursor, null);
                    cursor.enabled = _originalCursorEnabled;
                }
                _ownsCursor = false;
                _hover = null;
                _down = null;
            }
        }

        private void Fail(Exception exception)
        {
            _failed = true;
            try { Relinquish(); }
            catch
            {
                if (_game != null && _game.Cursor != null && _ownsCursor) _game.Cursor.enabled = _originalCursorEnabled;
                _ownsCursor = false;
            }
            if (_logger != null) _logger.LogWarning("Controller pointer stopped: " + exception);
        }

        private static string Label(Component target)
        {
            return target == null ? "destroyed target" : target.GetType().Name + " (" + target.name + ")";
        }

        private void OnDisable() { if (_game != null) Relinquish(); }

        private void OnDestroy()
        {
            if (_game != null) Relinquish();
            if (_visual != null) _visual.Destroy();
        }
    }

    internal sealed class GameCursorAdapter
    {
        private readonly SpecialEventInput _events = new SpecialEventInput();
        private ViewManager _view;
        internal InteractionCursor Cursor;

        internal GameCursorAdapter()
        {
            GameHooks.Require();
        }

        internal bool Resolve()
        {
            if (Cursor == null || !Cursor.gameObject.activeInHierarchy)
            {
                InteractionCursor main = Singleton<InteractionCursor>.Instance;
                Cursor = main != null && main.gameObject.activeInHierarchy ? main : UnityEngine.Object.FindObjectOfType<InteractionCursor>();
            }
            if (_view == null) _view = Singleton<ViewManager>.Instance;
            return Cursor != null && Cursor.gameObject.activeInHierarchy && _view != null;
        }

        internal int CurrentView { get { return _view != null ? (int)_view.CurrentView : -1; } }

        internal bool IsPlayablePhase
        {
            get
            {
                GameFlowManager flow = Singleton<GameFlowManager>.Instance;
                if (flow == null || flow.Transitioning) return false;
                switch (flow.CurrentGameState)
                {
                    case GameState.CardBattle:
                        TurnManager turn = Singleton<TurnManager>.Instance;
                        return turn != null && turn.IsMainOrDrawPhaseNotUpkeep && !turn.IsSetupPhase && !turn.GameEnding && !turn.GameEnded;
                    case GameState.Map:
                        GameMap map = Singleton<GameMap>.Instance;
                        MapNodeManager nodes = Singleton<MapNodeManager>.Instance;
                        return map != null && nodes != null && map.FullyUnrolled && !nodes.MovingNodes;
                    case GameState.SpecialCardSequence:
                        return _events.IsAvailable(CurrentView);
                    default:
                        return false;
                }
            }
        }

        internal bool IsSpecialEvent
        {
            get
            {
                GameFlowManager flow = Singleton<GameFlowManager>.Instance;
                return flow != null && flow.CurrentGameState == GameState.SpecialCardSequence;
            }
        }

        internal bool CanDispatch(MainInputInteractable target)
        {
            return target != null && Cursor != null && target.gameObject.activeInHierarchy && target.Enabled &&
                !GameHooks.Disabled(Cursor, target) && (!IsSpecialEvent || _events.AllowsTarget(target, CurrentView));
        }
    }
}
