using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DiskCardGame;
using Pixelplacement;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class CardGrabber : MonoBehaviour
    {
        internal enum BodyMode { Held, Falling, Resting }

        internal sealed class CardBody
        {
            internal PlayableCard Card;
            internal BodyMode Mode;
            internal VrHand Hand;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal Vector3 PullStartPosition;
            internal Quaternion PullStartRotation;
            internal float PullProgress = 1f;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Vector3 Velocity;
            internal float FallTime;
            internal float SnapTime = -10f;
            internal float Lift;
            internal bool OnTable;
        }

        private const float PullSeconds = 0.16f;
        private const float PinchInsetMeters = 0.012f;
        private const float AirDrag = 1.6f;
        private const float TableFriction = 9f;
        private const float MaxThrowMetersPerSecond = 5f;
        private const float KnifeReachMeters = 0.1f;
        private const float WobbleDegrees = 11f;
        private const float WobbleSeconds = 0.22f;
        private const float LaneLiftMeters = 0.012f;
        private const float LiftSeconds = 0.1f;
        private const float RowGraceSeconds = 3f;

        private static CardGrabber _instance;

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private VrInput _input;
        private StatusLine _status;
        private bool _failed;
        private bool _userEnabled = true;
        private bool _inFinale;
        private bool _handednessChanged;
        private float _reachMeters;
        private float _tableHalfWidthLanes;
        private float _tableHalfDepthLanes;
        private readonly List<CardBody> _bodies = new List<CardBody>();
        private readonly List<CardBody> _returning = new List<CardBody>();
        private float _rowLostSince = -1f;

        private bool _previewEnabled = true;
        private bool _previewGlow;
        private readonly LanePreview _leftPreview = new LanePreview("Left");
        private readonly LanePreview _rightPreview = new LanePreview("Right");

        private BoardManager _tableBoard;
        private float _nextTableRefresh;
        private bool _haveTable;
        private bool _knifeEarly;
        private bool _toolsSeen;
        private int _toolsLogged = -1;
        private readonly List<CardSlot> _playerSlots = new List<CardSlot>();
        private Vector3 _tableCenter;
        private Vector3 _tableAcross;
        private Vector3 _tableDepth;
        private float _tableY;
        private float _spacing;
        private Vector3 _faceNormalLocal = Vector3.up;
        private Vector3 _playerRowCenter;
        private Vector3 _towardPlayer = Vector3.back;
        private Quaternion _boardCardRotation = Quaternion.identity;

        private PlayableCard _pendingCard;
        private CardSlot _pendingSlot;
        private float _pendingTime;
        private bool _pendingSelected;
        private bool _pendingSacrificeDone;
        private bool _pendingSlotDone;
        private Vector3 _pendingPosition;
        private Quaternion _pendingRotation = Quaternion.identity;

        private CardRow _row;
        private PileDraw _piles;
        private BellTouch _bell;
        private SacrificeFlow _sacrifice;
        private readonly CardHover _leftHover = new CardHover();
        private readonly CardHover _rightHover = new CardHover();
        private SacrificeKnife _knife;
        private EventHands _events;
        private MapFigurine _map;
        private ItemHands _items;
        private CardInspector _inspector;
        private bool _inspection;
        private BoardLift _lift;
        private ScaleTouch _scales;
        private CandleWarmth _candles;
        private RuleBookHands _book;
        private TotemHands _totems;
        private BoneTokens _bones;
        private EyeballHands _eyes;
        private CampfireWarmth _campfire;
        private StoneRites _rites;
        private TableTouch _tableTouch;
        private bool _tableStops;
        private Func<PlayableCard, bool> _rowFree;

        internal float ReachMeters { get { return _reachMeters; } }
        private bool _cardHoverGlow = true;
        internal bool CardHoverGlow { get { return _cardHoverGlow; } }
        public void SetCardHoverGlow(bool enabled) { _cardHoverGlow = enabled; }
        internal StereoRenderer Renderer { get { return _renderer; } }
        internal bool UserEnabled { get { return _userEnabled; } }
        internal CardRow Row { get { return _row; } }
        internal static CardGrabber Instance { get { return _instance; } }

        internal bool HaveTable { get { return _haveTable; } }
        internal float TableY { get { return _tableY; } }
        internal float Spacing { get { return _spacing; } }
        internal Vector3 PlayerRowCenter { get { return _playerRowCenter; } }
        internal Vector3 TowardPlayer { get { return _towardPlayer; } }
        internal Quaternion BoardCardRotation { get { return _boardCardRotation; } }
        internal Quaternion FaceDownRotation { get { return Quaternion.AngleAxis(180f, _towardPlayer) * _boardCardRotation; } }
        internal Vector3 FaceNormalLocal { get { return _faceNormalLocal; } }
        internal List<CardSlot> PlayerSlots { get { return _playerSlots; } }
        internal int TableLayer { get { return _playerSlots.Count > 0 && _playerSlots[0] != null ? _playerSlots[0].gameObject.layer : 0; } }
        internal bool PlayInProgress { get { return _pendingCard != null || _sacrifice.Active; } }

        public void SetReach(float meters) { _reachMeters = Mathf.Clamp(meters, 0.02f, 0.3f); }

        public void SetBellMinSlapSpeed(float metersPerSecond) { if (_bell != null) _bell.SetMinSlapSpeed(metersPerSecond); }

        public void SetCardRowLanes(float lanes) { if (_row != null) _row.SetRowLanes(lanes); }

        public void SetCardRowOverlap(float overlap) { if (_row != null) _row.SetOverlap(overlap); }

        public void SetCardRowHover(string style) { if (_row != null) _row.SetHoverStyle(style); }

        public void SetInspection(bool enabled)
        {
            _inspection = enabled;
            if (enabled && _inspector == null) _inspector = new CardInspector(_logger);
            if (!enabled && _inspector != null) _inspector.Hide();
        }

        public void SetLanePreview(string style)
        {
            bool enabled = style != LanePreview.Off;
            bool glow = style == LanePreview.Glow;
            if (enabled != _previewEnabled || glow != _previewGlow) HidePreviews();
            _previewEnabled = enabled;
            _previewGlow = glow;
        }

        internal void HidePreviews()
        {
            _leftPreview.Hide();
            _rightPreview.Hide();
        }

        internal LanePreview PreviewOf(VrHand state) { return state.IsLeft ? _leftPreview : _rightPreview; }

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, VrInput input, float reachMeters, float tableHalfWidthLanes, float tableHalfDepthLanes,
            bool bellEnabled, float bellReachMeters, float bellMinSlapSpeed)
        {
            _logger = logger;
            _renderer = renderer;
            _input = input;
            _status = new StatusLine(logger, "Hand interactions");
            _reachMeters = Mathf.Clamp(reachMeters, 0.02f, 0.3f);
            _tableHalfWidthLanes = Mathf.Clamp(tableHalfWidthLanes, 1f, 12f);
            _tableHalfDepthLanes = Mathf.Clamp(tableHalfDepthLanes, 1f, 10f);
            if (bellEnabled) _bell = new BellTouch(this, logger, bellReachMeters, bellMinSlapSpeed);
            _piles = new PileDraw(this, input, logger);
            _sacrifice = new SacrificeFlow(this, logger, null);
            _rowFree = RowFree;
            _instance = this;
            _input.HandednessChanged += OnHandednessChanged;
            if (!PileDraw.Available)
                _logger.LogWarning("Draw by hand unavailable: CardPile internals not found. Point at the piles to draw instead.");
            _logger.LogInfo("Hand interactions enabled: grab cards with grip, draw from the piles, " + (_bell != null ? "ring the bell by touch, " : "") + "F10 toggles.");
        }

        internal void ConfigureCards(float cardRowLanes, float cardRowOverlap, bool knife, float knifeLengthMeters,
            float knifeLanesSide, float knifeLanesTowardPlayer)
        {
            if (CardRow.Available) _row = new CardRow(_logger, cardRowLanes, cardRowOverlap);
            else _logger.LogWarning("Card row unavailable: PlayerHand internals not found; cards stay in the game's hand.");
            if (knife)
            {
                _knife = new SacrificeKnife(_logger, knifeLengthMeters, knifeLanesSide, knifeLanesTowardPlayer);
                _knife.SetRightSide(_input.LeftHanded);
            }
            _sacrifice = new SacrificeFlow(this, _logger, _knife);
        }

        internal void ConfigureSacrificeGaze(string shake, string marker, bool heartbeat)
        {
            if (_knife != null) _knife.ConfigureGaze(shake, marker, heartbeat);
        }

        internal void ConfigureEvents(bool eyeballs, bool campfire, bool rites)
        {
            if (rites)
            {
                if (StoneRites.Available) _rites = new StoneRites(_logger);
                else _logger.LogWarning("Stone rites unavailable: CardMergeSequencer or CardStatBoostSequencer internals not found; the confirm stones stay.");
            }
            if (eyeballs)
            {
                if (EyeballHands.Available) _eyes = new EyeballHands(_logger);
                else _logger.LogWarning("Eyeball socket unavailable: ChooseEyeballSequencer internals not found; grip an eye to choose it.");
            }
            if (campfire)
            {
                if (CampfireWarmth.Available) _campfire = new CampfireWarmth(_logger);
                else _logger.LogWarning("Campfire warmth unavailable: CardStatBoostSequencer internals not found.");
            }
        }

        internal void ConfigureWorld(bool eventHands, bool mapFigurine, bool itemHands, float slamMetersPerSecond, bool inspection, bool itemPull, bool boulderKnock)
        {
            SetInspection(inspection);
            if (eventHands)
            {
                if (EventHands.Available) _events = new EventHands(_logger, itemPull, boulderKnock);
                else _logger.LogWarning("Event hands unavailable: card choice or altar internals not found.");
            }
            if (mapFigurine) _map = new MapFigurine(_logger);
            if (itemHands)
            {
                if (ItemHands.Available) _items = new ItemHands(_logger, slamMetersPerSecond);
                else _logger.LogWarning("Item hands unavailable: ItemsManager internals not found.");
            }
        }

        internal void ConfigureTable(bool boardLift, bool scales, bool candles, bool tableStops, bool totems, bool bones)
        {
            if (boardLift) _lift = new BoardLift(_logger);
            if (totems) _totems = new TotemHands(_logger);
            if (bones)
            {
                if (BoneTokens.Available) _bones = new BoneTokens(_logger);
                else _logger.LogWarning("Bone tokens by hand unavailable: Part1ResourcesManager internals not found.");
            }
            if (scales)
            {
                if (ScaleTouch.Available) _scales = new ScaleTouch(_logger);
                else _logger.LogWarning("Scale touch unavailable: Scales3D internals not found.");
            }
            if (candles)
            {
                if (CandleWarmth.Available) _candles = new CandleWarmth(_logger);
                else _logger.LogWarning("Candle warmth unavailable: CandleHolder internals not found.");
            }
            _tableTouch = new TableTouch();
            SetTableStopsHands(tableStops);
        }

        public void SetTableStopsHands(bool enabled) { _tableStops = enabled; }

        internal void ConfigureRuleBook(bool enabled, float widthMeters)
        {
            if (!enabled) return;
            if (RuleBookHands.Available) _book = new RuleBookHands(_logger, _renderer, widthMeters);
            else _logger.LogWarning("Rule book in hand unavailable: RuleBookController internals not found.");
        }

        public void SetRuleBookWidth(float meters) { if (_book != null) _book.SetWidth(meters); }

        private void OnHandednessChanged() { _handednessChanged = true; }

        internal static bool IsHeld(Component target)
        {
            CardGrabber grabber = _instance;
            if (grabber == null || target == null) return false;
            for (int i = 0; i < grabber._bodies.Count; i++)
            {
                CardBody body = grabber._bodies[i];
                if (body.Mode != BodyMode.Held || body.Card == null) continue;
                if (target == body.Card || target.transform.IsChildOf(body.Card.transform)) return true;
            }
            if (PileDraw.Holds(grabber._input.Left, target) || PileDraw.Holds(grabber._input.Right, target)) return true;
            if (grabber._events != null && grabber._events.Holds(target)) return true;
            if (grabber._map != null && grabber._map.Holds(target)) return true;
            if (grabber._items != null && grabber._items.Holds(target)) return true;
            if (grabber._lift != null && grabber._lift.Holds(target)) return true;
            if (grabber._scales != null && grabber._scales.Holds(target)) return true;
            if (grabber._totems != null && grabber._totems.Holds(target)) return true;
            if (grabber._bones != null && grabber._bones.Holds(target)) return true;
            if (grabber._eyes != null && grabber._eyes.Holds(target)) return true;
            return false;
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed) return;
            PerfProbe.Begin(PerfProbe.Hands);
            try { Tick(); }
            catch (Exception exception) { Fail(exception); }
            PerfProbe.End(PerfProbe.Hands);
            try { GrabGlow.Flush(Time.unscaledDeltaTime); }
            catch (Exception exception) { _logger.LogWarning("Grab glow disabled after an error: " + exception.Message); }
        }

        private void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F10))
            {
                _userEnabled = !_userEnabled;
                _logger.LogInfo("Hand interactions " + (_userEnabled ? "enabled" : "disabled") + ".");
            }
            VrHand left = _input.Left, right = _input.Right;
            if (_handednessChanged)
            {
                _handednessChanged = false;
                ForgetAll();
                if (_knife != null) _knife.SetRightSide(_input.LeftHanded);
            }
            if (VrScenes.InFinale)
            {
                TickFinale(left, right);
                return;
            }
            _inFinale = false;
            if (!VrScenes.InCabin)
            {
                ForgetAll();
                ForgetWorld();
                if (_inspector != null) _inspector.Hide();
                left.Ready = right.Ready = false;
                _status.Set("inactive outside the cabin");
                return;
            }

            _input.Refresh();
            VrHand main = _input.Main, off = _input.Off;
            bool tracking = _input.Active;
            bool input = tracking && _userEnabled && Time.timeScale > 0f && _input.InputAvailable;
            float scale = _input.Scale;
            left.Ready = input && left.Tracked;
            right.Ready = input && right.Tracked;
            left.Claimed = false;
            right.Claimed = false;
            HandProp.TickAll(Time.unscaledDeltaTime);

            if (_book != null) _book.Tick(left, right, main, off, tracking && _userEnabled, input, _reachMeters * scale, scale);
            if (_totems != null)
            {
                try { _totems.TickBuild(this, left, right, main, tracking && _userEnabled && !EventStyle.Simplified, input, scale); }
                catch (Exception exception) { DisableTotems(exception); }
            }
            if (_eyes != null)
            {
                try { _eyes.Tick(this, left, right, tracking && _userEnabled && !EventStyle.Simplified, input, scale); }
                catch (Exception exception)
                {
                    _logger.LogError("Eyeball socket turned off after an error; grip an eye to choose it: " + exception);
                    EyeballHands eyes = _eyes;
                    _eyes = null;
                    try { eyes.Release(); } catch (Exception) { }
                }
            }
            if (_rites != null)
            {
                try { _rites.Tick(this, left, right, main, input, scale); }
                catch (Exception exception)
                {
                    _logger.LogError("Stone rites turned off after an error; the confirm stones are back: " + exception);
                    StoneRites rites = _rites;
                    _rites = null;
                    try { rites.Dispose(); } catch (Exception) { }
                }
            }
            if (_events != null) _events.Tick(this, left, right, input, scale);
            if (_map != null) _map.Tick(this, left, right, input, scale);
            if (_candles != null) _candles.Tick(left, right, input, scale);
            if (_campfire != null)
            {
                try { _campfire.Tick(left, right, input, scale); }
                catch (Exception exception)
                {
                    _logger.LogError("Campfire warmth turned off after an error: " + exception);
                    _campfire = null;
                }
            }

            PlayerHand hand = Singleton<PlayerHand>.Instance;
            BoardManager board = Singleton<BoardManager>.Instance;
            if (hand == null || board == null)
            {
                ForgetAll();
                if (_inspector != null) _inspector.Hide();
                _status.Set("waiting for the player hand and board");
                return;
            }

            PruneBodies(hand);
            UpdatePending(hand, board);
            _haveTable = RefreshTable(board);

            bool inBattle = _haveTable && InBattle();
            bool battle = input && inBattle;
            bool shown = tracking && _userEnabled && inBattle;
            FeedGap(left);
            FeedGap(right);
            UpdateRow(hand, _userEnabled && inBattle, tracking);
            if (_knife != null)
            {
                bool offerKnifeTip = battle && SacrificeFlow.KnifeTurn(board) && (_sacrifice.HoldsBloodCard(left) || _sacrifice.HoldsBloodCard(right));
                bool knifeOut = KnifeOut(inBattle, board, offerKnifeTip);
                if (!knifeOut) _knife.Leave(Time.unscaledDeltaTime);
                bool knifeShown = shown && knifeOut;
                _knife.Tick(this, left, right, off, _sacrifice.CardHand, hand, board, knifeShown, battle, knifeShown, offerKnifeTip && knifeShown, scale, KnifeReachMeters * scale, Time.unscaledDeltaTime);
                _sacrifice.Update(main, off, hand, board, battle, scale);
            }
            _sacrifice.HoldOffered(main, off, board, shown, scale);
            if (_totems != null)
            {
                try { _totems.TickBattle(this, left, right, shown, battle, scale); }
                catch (Exception exception) { DisableTotems(exception); }
            }
            if (_bones != null)
            {
                try { _bones.Tick(left, right, shown, battle, scale); }
                catch (Exception exception) { DisableBones(exception); }
            }
            if (_items != null) _items.Tick(this, left, right, battle, scale);

            bool canHold = input && _haveTable && CanHold(hand, board);
            _status.Set(!_userEnabled ? "disabled by F10" : !tracking ? "waiting for VR tracking" : !input ? "SteamVR input unavailable or paused" :
                canHold ? "ready" : "waiting for the player's main or draw phase");

            _piles.Process(left, hand, input && _haveTable, scale);
            _piles.Process(right, hand, input && _haveTable, scale);
            _piles.Watch(shown);
            ProcessHand(left, hand, board, canHold, scale);
            ProcessHand(right, hand, board, canHold, scale);
            if (_lift != null) _lift.Tick(this, left, right, canHold && !ItemActive(), scale);
            if (_scales != null)
            {
                if (inBattle) _scales.Tick(this, left, right, input, scale);
                else _scales.Release();
            }
            if (left.Held != null || left.Pile != null) left.SetHoldPose(HandHold.Pinch);
            if (right.Held != null || right.Pile != null) right.SetHoldPose(HandHold.Pinch);

            bool canRing = input && _bell != null && _bell.Refresh();
            if (_bell != null)
            {
                _bell.Process(left, hand, canRing, scale);
                _bell.Process(right, hand, canRing, scale);
            }

            Simulate(Time.deltaTime, scale);
            for (int i = 0; i < _returning.Count; i++) ReturnToHand(_returning[i], hand);
            _returning.Clear();
            HoldPendingCard(hand, shown);
            ApplyPoses();
            VrHand flowHand = _sacrifice.CardHand;
            if (flowHand != off) UpdatePreview(off, hand, board, canHold);
            if (flowHand != null) _sacrifice.Preview(hand, board, PreviewOf(flowHand), _previewEnabled, _previewGlow);
            if (flowHand != main) UpdatePreview(main, hand, board, canHold);
            OfferTips(left, right, hand, board, input, canHold, canRing);
            if (_inspector != null)
            {
                if (_inspection && shown) _inspector.Tick(_renderer, left, right, _sacrifice.Offered);
                else _inspector.Hide();
            }
            if (_tableTouch != null)
            {
                _tableTouch.Tick(this, left, tracking && _tableStops && inBattle, scale);
                _tableTouch.Tick(this, right, tracking && _tableStops && inBattle, scale);
            }
        }

        private void TickFinale(VrHand left, VrHand right)
        {
            if (!_inFinale)
            {
                _inFinale = true;
                ForgetWorld();
            }
            ForgetAll();
            if (_inspector != null) _inspector.Hide();
            _input.Refresh();
            VrHand main = _input.Main, off = _input.Off;
            bool tracking = _input.Active;
            bool input = tracking && _userEnabled && Time.timeScale > 0f && _input.InputAvailable;
            float scale = _input.Scale;
            left.Ready = input && left.Tracked;
            right.Ready = input && right.Tracked;
            left.Claimed = false;
            right.Claimed = false;
            if (_book != null) _book.Tick(left, right, main, off, tracking && _userEnabled, input, _reachMeters * scale, scale);
            if (_events != null) _events.Tick(this, left, right, input, scale);
            _status.Set(!_userEnabled ? "disabled by F10" : !tracking ? "waiting for VR tracking" : !input ? "SteamVR input unavailable or paused" :
                FinaleTrial.Active ? "finale deck trial" : "finale walk");
        }

        private static bool ItemActive()
        {
            ItemsManager items = Singleton<ItemsManager>.Instance;
            return items != null && items.ActivatingItem;
        }

        private void FeedGap(VrHand state)
        {
            if (_row == null || !_haveTable) return;
            CardBody body = state.Held;
            if (body == null || body.Mode != BodyMode.Held || body.Hand != state || body.Card == null) return;
            if (FindDropSlot(body.Position) == null) _row.Gap(body.Position);
        }

        private bool KnifeOut(bool inBattle, BoardManager board, bool wantsKnife)
        {
            if (!inBattle)
            {
                _knifeEarly = false;
                _toolsSeen = false;
                _toolsLogged = -1;
                return false;
            }
            bool bell = TableTools.BellOut(board);
            bool scales = TableTools.ScalesOut();
            bool tools = bell || scales;
            if (tools) _toolsSeen = true;
            if (!_toolsSeen && !_knifeEarly && (wantsKnife || board.ChoosingSacrifices))
            {
                _knifeEarly = true;
                _logger.LogInfo("Sacrifice knife slides in early: a blood card needs sacrifices before the bell or scales are out.");
            }
            bool knifeOut = tools || (_knifeEarly && !_toolsSeen);
            int state = (bell ? 1 : 0) | (scales ? 2 : 0) | (knifeOut ? 4 : 0);
            if (state != _toolsLogged)
            {
                _toolsLogged = state;
                _logger.LogInfo("Sacrifice knife follows the table tools: bell " + (bell ? "out" : "away") + ", scales " + (scales ? "out" : "away") + ", knife " + (knifeOut ? "out" : "away") + ".");
            }
            return knifeOut;
        }

        private static bool InBattle()
        {
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            return flow != null && !flow.Transitioning && flow.CurrentGameState == GameState.CardBattle;
        }

        private void UpdatePreview(VrHand state, PlayerHand hand, BoardManager board, bool canHold)
        {
            LanePreview preview = PreviewOf(state);
            CardBody body = state.Held;
            CardSlot slot = _previewEnabled && canHold && body != null && body.Mode == BodyMode.Held && body.Card != null ? FindDropSlot(body.Position) : null;
            if (slot == null)
            {
                preview.Hide();
                return;
            }
            preview.Show(this, body.Card, slot, !CanPlayInto(body.Card, slot, hand, board), body.Position, body.Rotation, Time.unscaledDeltaTime, _previewGlow);
        }

        private void OfferTips(VrHand left, VrHand right, PlayerHand hand, BoardManager board, bool input, bool canHold, bool canRing)
        {
            if (!input || !_haveTable) return;
            Vector3 up = Vector3.up * (0.02f * _spacing);
            CardBody held = left.Held != null ? left.Held : right.Held;
            if (canHold && held != null && held.Mode == BodyMode.Held && (_knife == null || held.Card == null || held.Card.Info == null || held.Card.Info.BloodCost <= 0))
                VrTips.Show("cards.play", _playerRowCenter + up, "Let go over one of your lanes to play the card.", 60);
            List<PlayableCard> cards = hand.CardsInHand;
            if (canHold && held == null && cards.Count > 0)
            {
                PlayableCard middle = cards[cards.Count / 2];
                if (middle != null && middle.gameObject.activeInHierarchy)
                    VrTips.Show("cards.grab", middle.transform.position + up, "Reach for a card and squeeze grip to pick it up.", 50);
            }
            CardPile pile = _piles.ReadyPile();
            Transform back = pile != null ? PileDraw.TopBack(pile) : null;
            if (back != null) VrTips.Show("cards.draw", back.position + up, "Grab the top card of a pile and pull it away to draw.", 70);
            if (canRing && left.Held == null && right.Held == null && _bell.Ready(hand) && (VrTips.Seen("cards.play") || !AnyPlayable(cards)))
            {
                Bounds bounds;
                if (_bell.Bounds(out bounds))
                    VrTips.Show("bell", new Vector3(bounds.center.x, bounds.max.y, bounds.center.z), "Slap the bell to end your turn.", 20);
            }
        }

        private static bool AnyPlayable(List<PlayableCard> cards)
        {
            for (int i = 0; i < cards.Count; i++)
                if (cards[i] != null && cards[i].CanPlay()) return true;
            return false;
        }

        private Vector3 PinchPoint(VrHand state)
        {
            return state.Position + state.Rotation * state.PinchOffset(_input.Scale);
        }

        private void UpdateRow(PlayerHand hand, bool want, bool tracking)
        {
            if (_row == null) return;
            if (want && tracking)
            {
                _rowLostSince = -1f;
                _row.Layout(hand, this, _rowFree, Time.unscaledDeltaTime);
                return;
            }
            if (want && _row.Active)
            {
                if (_rowLostSince < 0f) _rowLostSince = Time.unscaledTime;
                if (Time.unscaledTime - _rowLostSince < RowGraceSeconds) return;
            }
            _rowLostSince = -1f;
            _row.Release();
        }

        private void HoldPendingCard(PlayerHand hand, bool active)
        {
            PlayableCard card = _pendingCard;
            if (!active || card == null || !hand.CardsInHand.Contains(card) || FindBody(card) != null) return;
            Tween.Stop(card.transform.GetInstanceID());
            card.transform.SetPositionAndRotation(_pendingPosition, _pendingRotation);
            if (_row != null)
            {
                _row.SetNaturalScale(card.transform);
                _row.Remember(card);
            }
        }

        internal static PlayableCard HeldCard(VrHand state)
        {
            CardBody body = state.Held;
            return body != null && body.Mode == BodyMode.Held && body.Hand == state ? body.Card : null;
        }

        internal void DetachHeld(VrHand state)
        {
            CardBody body = state.Held;
            if (body == null) return;
            _bodies.Remove(body);
            state.Held = null;
            body.Hand = null;
        }

        private bool RowFree(PlayableCard card)
        {
            if (card == _pendingCard || _sacrifice.Owns(card) || FindBody(card) != null || _piles.IsIncoming(card)) return false;
            PlayerHand hand = Singleton<PlayerHand>.Instance;
            return hand == null || hand.ChoosingSlotCard != card;
        }

        internal bool CanHold(PlayerHand hand, BoardManager board)
        {
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            if (flow == null || flow.Transitioning || flow.CurrentGameState != GameState.CardBattle) return false;
            TurnManager turn = Singleton<TurnManager>.Instance;
            if (turn == null || !turn.IsMainOrDrawPhaseNotUpkeep || turn.IsSetupPhase || turn.GameEnding || turn.GameEnded) return false;
            if (PlayInProgress || hand.ChoosingSlot || hand.InspectingLocked || board.ChoosingSlot || board.ChoosingSacrifices) return false;
            if (!hand.gameObject.activeInHierarchy) return false;
            InteractionCursor cursor = Singleton<InteractionCursor>.Instance;
            return cursor != null && !cursor.InteractionDisabled;
        }

        private void ProcessHand(VrHand state, PlayerHand hand, BoardManager board, bool canHold, float scale)
        {
            if (state.Held != null && (state.Held.Card == null || state.Held.Mode != BodyMode.Held || state.Held.Hand != state))
                state.Held = null;
            CardHover tracker = HoverOf(state);
            if (state.Pile != null || state.Claimed || state.Lifted != null)
            {
                DropHover(state, tracker);
                return;
            }
            if (!state.Ready || !canHold)
            {
                DropHover(state, tracker);
                if (state.Held != null) ReturnToHand(state.Held, hand);
                return;
            }
            if (state.Held != null)
            {
                DropHover(state, tracker);
                if (!state.Grip.Held) Release(state, hand, board, scale);
                return;
            }
            float reach = _reachMeters * scale;
            float keep = reach * CardHover.KeepFactor;
            Vector3 point = CardPoint(state);
            float bestSqr;
            PlayableCard best = NearestHandCard(hand, point, keep * keep, out bestSqr);
            float currentSqr = CardSqrDistance(hand, tracker.Current, point);
            PlayableCard hover = tracker.Update(best, bestSqr, currentSqr, reach, scale, Time.unscaledTime);
            if (hover != null)
            {
                state.Highlight();
                if (_row != null) _row.Hover(hover);
                if (_cardHoverGlow) GrabGlow.Mark(hover);
                if (hover != state.HoverCard) state.Play(Feel.Tick, 0.5f);
            }
            state.HoverCard = hover;
            if (state.GripEdge == 1) TryGrab(state, hand, scale, tracker);
        }

        private CardHover HoverOf(VrHand state) { return state.IsLeft ? _leftHover : _rightHover; }

        private static void DropHover(VrHand state, CardHover tracker)
        {
            tracker.Clear();
            state.HoverCard = null;
        }

        internal static Vector3 CardPoint(VrHand state)
        {
            return state.HasIndexTip ? state.IndexTip : state.Position;
        }

        private float CardSqrDistance(PlayerHand hand, PlayableCard card, Vector3 point)
        {
            if (card == null || !card.gameObject.activeInHierarchy || !hand.CardsInHand.Contains(card)) return float.MaxValue;
            float distance;
            if (_row != null && _row.SpotSqrDistance(card, point, out distance)) return distance;
            return BoxSqrDistance(card, point);
        }

        internal bool HandCardWithin(VrHand state, float sqrDistance)
        {
            PlayerHand hand = Singleton<PlayerHand>.Instance;
            if (hand == null || !CanHold(hand, Singleton<BoardManager>.Instance)) return false;
            float distance;
            return NearestHandCard(hand, CardPoint(state), sqrDistance, out distance) != null;
        }

        internal PlayableCard NearestHandCard(PlayerHand hand, Vector3 point, float limit, out float bestDistance)
        {
            bestDistance = limit;
            PlayableCard best = null;
            List<PlayableCard> cards = hand.CardsInHand;
            for (int i = 0; i < cards.Count; i++)
            {
                PlayableCard card = cards[i];
                if (card == null || !card.gameObject.activeInHierarchy) continue;
                float distance;
                if (_row == null || !_row.SpotSqrDistance(card, point, out distance)) distance = BoxSqrDistance(card, point);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = card;
                }
            }
            return best;
        }

        internal void HoldCard(VrHand state, PlayableCard card, Vector3 position, Quaternion startRotation)
        {
            Tween.Stop(card.transform.GetInstanceID());
            CardBody body = AttachBody(card, state);
            if (_row != null) _row.SetNaturalScale(card.transform);
            PinchPose(state, card, startRotation, _input.Scale, out body.LocalPosition, out body.LocalRotation);
            body.PullStartPosition = position;
            body.PullStartRotation = startRotation;
            body.PullProgress = 0f;
            body.Position = position;
            body.Rotation = startRotation;
            state.Play(Feel.Tick);
        }

        private CardBody AttachBody(PlayableCard card, VrHand state)
        {
            CardBody body = FindBody(card);
            if (body == null)
            {
                body = new CardBody();
                body.Card = card;
                _bodies.Add(body);
            }
            if (body.Hand != null && body.Hand.Held == body) body.Hand.Held = null;
            body.Velocity = Vector3.zero;
            body.FallTime = 0f;
            body.SnapTime = -10f;
            body.Lift = 0f;
            body.OnTable = false;
            body.Mode = BodyMode.Held;
            body.Hand = state;
            state.Held = body;
            return body;
        }

        private void TryGrab(VrHand state, PlayerHand hand, float scale, CardHover tracker)
        {
            float slack = 2f * _reachMeters * scale;
            PlayableCard best = tracker.Intended(Time.unscaledTime);
            if (best != null && CardSqrDistance(hand, best, CardPoint(state)) > slack * slack) best = null;
            if (best == null) best = tracker.Current;
            bool near = best != null;
            if (!near)
            {
                best = RaycastCard(state, hand, scale);
                if (best != null && _row != null && _row.InRowSpot(best)) best = null;
            }
            if (best == null) return;
            tracker.Clear();
            state.HoverCard = null;

            CardBody previous = FindBody(best);
            VrHand giver = previous != null && previous.Mode == BodyMode.Held && previous.Hand != null && previous.Hand != state ? previous.Hand : null;
            HoldCard(state, best, best.transform.position, best.transform.rotation);
            VrTips.Done("cards.grab");
            if (giver != null)
            {
                giver.Play(Feel.Tick, 0.6f);
                _logger.LogInfo("Card passed from the " + giver.Name + " hand to the " + state.Name + " hand: " + best.name);
                return;
            }
            VrSounds.Play("card", MixerGroup.CardPaperSFX, PinchPoint(state), 1f);
            _logger.LogInfo("Card grabbed with the " + state.Name + " hand (" + (near ? "touch" : "point") + "): " + best.name);
        }

        private void Release(VrHand state, PlayerHand hand, BoardManager board, float scale)
        {
            CardBody body = state.Held;
            state.Held = null;
            body.Hand = null;
            if (body.Card == null)
            {
                _bodies.Remove(body);
                return;
            }

            CardSlot slot = FindDropSlot(body.Position);
            if (slot != null)
            {
                if (TryPlay(body, slot, hand, board))
                {
                    state.Play(Feel.Thud);
                    VrTips.Done("cards.play");
                    _logger.LogInfo("Card released over lane " + slot.Index + "; playing it: " + body.Card.name);
                    return;
                }
                PlayableCard refused = body.Card;
                ReturnToHand(body, hand);
                if (refused.Anim != null) refused.Anim.LightNegationEffect();
                state.Play(Feel.Reject);
                bool blood = _knife != null && refused.Info != null && refused.Info.BloodCost > 0;
                _logger.LogInfo((blood ? "Card needs blood; hold it in the " + _input.Main.Name + " hand and grab the knife with the " + _input.Off.Name + ". Returned to the hand: " :
                    "Card play refused by the game rules; returned to the hand: ") + refused.name);
                return;
            }

            if (_row != null && _row.TryReorder(hand, body.Card, body.Position))
            {
                PlayableCard moved = body.Card;
                ReturnToHand(body, hand);
                state.Play(Feel.Tick, 0.7f);
                VrSounds.Play("cardquick", MixerGroup.CardPaperSFX, moved.transform.position, 0.5f);
                return;
            }

            Vector3 velocity = state.Velocity;
            float limit = MaxThrowMetersPerSecond * scale;
            if (velocity.sqrMagnitude > limit * limit) velocity = velocity.normalized * limit;
            body.Velocity = velocity;
            body.FallTime = 0f;
            body.Mode = BodyMode.Falling;
            state.Play(Feel.Tick, 0.6f);
        }

        private bool CanPlayInto(PlayableCard card, CardSlot slot, PlayerHand hand, BoardManager board)
        {
            TurnManager turn = Singleton<TurnManager>.Instance;
            if (turn == null || !turn.IsPlayerMainPhase || hand.PlayingLocked) return false;
            if (hand.ChoosingSlot || board.ChoosingSlot || board.ChoosingSacrifices) return false;
            if (card.Info == null) return false;
            if (_knife != null && card.Info.BloodCost > 0) return false;
            if (slot.Card != null && card.Info.BloodCost <= 0) return false;
            return card.CanPlay();
        }

        private bool TryPlay(CardBody body, CardSlot slot, PlayerHand hand, BoardManager board)
        {
            PlayableCard card = body.Card;
            if (!CanPlayInto(card, slot, hand, board)) return false;

            ReturnToHand(body, hand);
            if (_row != null) _row.SetNaturalScale(card.transform);
            _pendingPosition = body.Position;
            _pendingRotation = body.Rotation;
            _pendingCard = card;
            _pendingSlot = slot;
            _pendingTime = Time.unscaledTime;
            _pendingSelected = false;
            _pendingSacrificeDone = false;
            _pendingSlotDone = false;
            card.CursorSelectStart();
            card.CursorSelectEnd();
            return true;
        }

        private void UpdatePending(PlayerHand hand, BoardManager board)
        {
            if (_pendingCard == null) return;
            if (!_pendingSelected)
            {
                if (hand.ChoosingSlotCard == _pendingCard) _pendingSelected = true;
                else
                {
                    if (Time.unscaledTime - _pendingTime > 2f)
                    {
                        _logger.LogInfo("The game did not begin slot selection for the dropped card; play request cleared.");
                        ClearPending();
                    }
                    return;
                }
            }
            if (hand.ChoosingSlotCard != _pendingCard || _pendingSlot == null)
            {
                ClearPending();
                return;
            }
            if (!_pendingSacrificeDone && board.ChoosingSacrifices && board.CurrentSacrificeDemandingCard == _pendingCard)
            {
                _pendingSacrificeDone = true;
                if (_knife == null && _pendingSlot.Card != null && _pendingSlot.Card.CanBeSacrificed)
                {
                    _pendingSlot.CursorSelectStart();
                    _logger.LogInfo("Marked the creature under the dropped card for sacrifice.");
                }
            }
            if (!_pendingSlotDone && board.ChoosingSlot)
            {
                _pendingSlotDone = true;
                CardSlot target = _pendingSlot.Card == null ? _pendingSlot : NearestFreeSlot(_pendingSlot);
                if (target != null)
                {
                    target.CursorSelectStart();
                    _logger.LogInfo("Placed the dropped card in lane " + target.Index + (target == _pendingSlot ? "." : " (nearest free lane to the drop)."));
                }
                else _logger.LogInfo("No free lane for the dropped card; choose a lane with the pointer.");
            }
        }

        private CardSlot NearestFreeSlot(CardSlot origin)
        {
            CardSlot best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _playerSlots.Count; i++)
            {
                CardSlot slot = _playerSlots[i];
                if (slot == null || slot.Card != null || !slot.Chooseable) continue;
                float distance = (slot.transform.position - origin.transform.position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = slot;
                }
            }
            return best;
        }

        private void ClearPending()
        {
            _pendingCard = null;
            _pendingSlot = null;
        }

        private void Simulate(float dt, float scale)
        {
            float gravity = 9.81f * scale;
            for (int i = 0; i < _bodies.Count; i++)
            {
                CardBody body = _bodies[i];
                if (body.Mode == BodyMode.Held) UpdateHeld(body);
                else if (body.Mode == BodyMode.Falling) Integrate(body, dt, gravity, scale);
                else if (body.Mode == BodyMode.Resting && _haveTable &&
                    (!InsideTable(body.Position) || SurfaceHeight(body, body.Position.y) < body.Position.y - 0.002f * _spacing))
                {
                    body.Mode = BodyMode.Falling;
                    body.FallTime = 0f;
                }
            }
        }

        private void UpdateHeld(CardBody body)
        {
            VrHand state = body.Hand;
            if (state == null) return;
            Vector3 target = state.Position + state.Rotation * body.LocalPosition;
            Quaternion targetRotation = state.Rotation * body.LocalRotation;
            float scale = _input.Scale;
            if (body.PullProgress < 1f)
            {
                body.PullProgress = Mathf.Min(1f, body.PullProgress + Time.unscaledDeltaTime / PullSeconds);
                float k = Mathf.SmoothStep(0f, 1f, body.PullProgress);
                body.Position = Vector3.Lerp(body.PullStartPosition, target, k);
                body.Rotation = Quaternion.Slerp(body.PullStartRotation, targetRotation, k);
                if (body.PullProgress >= 1f)
                {
                    body.SnapTime = Time.unscaledTime;
                    state.Play(Feel.Tick, 0.8f);
                }
            }
            else
            {
                body.Position = target;
                body.Rotation = targetRotation;
            }
            float since = Time.unscaledTime - body.SnapTime;
            if (since >= 0f && since < WobbleSeconds)
            {
                float angle = WobbleDegrees * Mathf.Exp(-since / 0.06f) * Mathf.Cos(since * Mathf.PI * 2f / 0.11f);
                Vector3 axis = state.Rotation * (state.LocalAimRotation * Vector3.right);
                Vector3 pivot = PinchPoint(state);
                Quaternion tilt = Quaternion.AngleAxis(angle, axis);
                body.Position = pivot + tilt * (body.Position - pivot);
                body.Rotation = tilt * body.Rotation;
            }
            bool overLane = FindDropSlot(body.Position) != null;
            body.Lift = Mathf.MoveTowards(body.Lift, overLane ? 1f : 0f, Time.unscaledDeltaTime / LiftSeconds);
            float lift = LaneLiftMeters * scale * Mathf.SmoothStep(0f, 1f, body.Lift);
            body.Position += Vector3.up * lift;
            bool contact = false;
            if (_haveTable && InsideTable(body.Position))
            {
                float floor = _tableY + RestHeight() + lift;
                if (body.Position.y < floor && body.Position.y > floor - 0.3f * _spacing)
                {
                    Vector3 pressed = body.Position;
                    body.Position.y = floor;
                    contact = true;
                    if (body.PullProgress >= 1f) state.Pin(pressed, body.Rotation, body.Position, body.Rotation);
                }
            }
            if (contact && !body.OnTable && body.PullProgress >= 1f)
            {
                state.Play(Feel.Tick, 0.6f);
                VrSounds.Play("cardquick", MixerGroup.CardPaperSFX, body.Position, 0.5f);
            }
            body.OnTable = contact;
        }

        private void Integrate(CardBody body, float dt, float gravity, float scale)
        {
            if (dt <= 0f) return;
            if (!_haveTable)
            {
                _returning.Add(body);
                return;
            }
            body.FallTime += dt;
            float previousY = body.Position.y;
            body.Velocity += Vector3.down * (gravity * dt);
            body.Velocity *= 1f / (1f + AirDrag * dt);
            body.Position += body.Velocity * dt;
            Quaternion flat = FlatRotation(body.Rotation);
            bool inside = InsideTable(body.Position);
            float surface = inside ? SurfaceHeight(body, previousY) : float.NegativeInfinity;
            if (inside && body.Position.y <= surface && previousY >= surface - 0.08f * _spacing)
            {
                body.Position.y = surface;
                if (body.Velocity.y < 0f) body.Velocity.y = body.Velocity.y < -0.6f * scale ? -body.Velocity.y * 0.2f : 0f;
                float friction = Mathf.Exp(-TableFriction * dt);
                body.Velocity.x *= friction;
                body.Velocity.z *= friction;
                body.Rotation = Quaternion.Slerp(body.Rotation, flat, 1f - Mathf.Exp(-18f * dt));
                float horizontal = new Vector2(body.Velocity.x, body.Velocity.z).magnitude;
                if (Mathf.Abs(body.Velocity.y) < 0.001f && horizontal < 0.02f * scale && Quaternion.Angle(body.Rotation, flat) < 0.5f)
                {
                    body.Rotation = flat;
                    body.Velocity = Vector3.zero;
                    body.Mode = BodyMode.Resting;
                    VrSounds.Play("cardquick", MixerGroup.CardPaperSFX, body.Position, 0.6f);
                    _returning.Add(body);
                }
                return;
            }
            body.Rotation = Quaternion.Slerp(body.Rotation, flat, 1f - Mathf.Exp(-2.5f * dt));
            if (body.Position.y < _tableY - 1.5f * _spacing || body.FallTime > 6f)
            {
                _returning.Add(body);
                _logger.LogInfo("Card fell off the table; returning it to the hand: " + body.Card.name);
            }
        }

        private float RestHeight() { return 0.004f * _spacing; }

        private float SurfaceHeight(CardBody body, float referenceY)
        {
            float height = _tableY + RestHeight();
            float radius = 0.6f * _spacing;
            for (int i = 0; i < _bodies.Count; i++)
            {
                CardBody other = _bodies[i];
                if (other == body || other.Mode != BodyMode.Resting || other.Position.y >= referenceY - 0.0005f * _spacing) continue;
                Vector3 offset = other.Position - body.Position;
                offset.y = 0f;
                if (offset.sqrMagnitude < radius * radius)
                    height = Mathf.Max(height, other.Position.y + 0.006f * _spacing);
            }
            return height;
        }

        private Quaternion FlatRotation(Quaternion rotation)
        {
            Vector3 normal = rotation * _faceNormalLocal;
            Vector3 target = Vector3.Dot(normal, Vector3.up) >= 0f ? Vector3.up : Vector3.down;
            return Quaternion.FromToRotation(normal, target) * rotation;
        }

        internal bool InsideTable(Vector3 position)
        {
            Vector3 offset = position - _tableCenter;
            return Mathf.Abs(Vector3.Dot(offset, _tableAcross)) <= _tableHalfWidthLanes * _spacing &&
                Mathf.Abs(Vector3.Dot(offset, _tableDepth)) <= _tableHalfDepthLanes * _spacing;
        }

        internal CardSlot FindDropSlot(Vector3 position)
        {
            if (!_haveTable) return null;
            if (position.y < _tableY - 0.2f * _spacing || position.y > _tableY + 2.5f * _spacing) return null;
            float limit = 0.55f * _spacing;
            float best = limit * limit;
            CardSlot result = null;
            for (int i = 0; i < _playerSlots.Count; i++)
            {
                CardSlot slot = _playerSlots[i];
                if (slot == null || !slot.gameObject.activeInHierarchy) continue;
                Vector3 offset = slot.transform.position - position;
                offset.y = 0f;
                if (offset.sqrMagnitude < best)
                {
                    best = offset.sqrMagnitude;
                    result = slot;
                }
            }
            return result;
        }

        private bool RefreshTable(BoardManager board)
        {
            if (board == _tableBoard && Time.unscaledTime < _nextTableRefresh) return _haveTable;
            _tableBoard = board;
            _nextTableRefresh = Time.unscaledTime + 0.5f;
            _playerSlots.Clear();
            List<CardSlot> slots = board.PlayerSlotsCopy;
            for (int i = 0; i < slots.Count; i++) if (slots[i] != null) _playerSlots.Add(slots[i]);
            if (_playerSlots.Count < 2) return false;
            Vector3 first = _playerSlots[0].transform.position;
            Vector3 last = _playerSlots[_playerSlots.Count - 1].transform.position;
            Vector3 across = last - first;
            across.y = 0f;
            if (across.sqrMagnitude < 0.0001f) return false;
            _spacing = across.magnitude / (_playerSlots.Count - 1);
            _tableAcross = across.normalized;
            _tableDepth = Vector3.Cross(_tableAcross, Vector3.up);
            Vector3 sum = Vector3.zero;
            int count = 0;
            float height = 0f;
            List<CardSlot> all = board.AllSlotsCopy;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null) continue;
                sum += all[i].transform.position;
                count++;
            }
            for (int i = 0; i < _playerSlots.Count; i++) height += _playerSlots[i].transform.position.y;
            _tableCenter = count > 0 ? sum / count : (first + last) * 0.5f;
            _tableY = height / _playerSlots.Count + 0.02f;
            Transform reference = _playerSlots[0].transform.childCount > 0 ? _playerSlots[0].transform.GetChild(0) : _playerSlots[0].transform;
            _faceNormalLocal = Quaternion.Inverse(reference.rotation) * Vector3.up;
            _boardCardRotation = reference.rotation;
            Vector3 row = Vector3.zero;
            for (int i = 0; i < _playerSlots.Count; i++) row += _playerSlots[i].transform.position;
            _playerRowCenter = row / _playerSlots.Count;
            _towardPlayer = _tableDepth * (Vector3.Dot(_playerRowCenter - _tableCenter, _tableDepth) >= 0f ? 1f : -1f);
            return true;
        }

        private void PruneBodies(PlayerHand hand)
        {
            List<PlayableCard> cards = hand.CardsInHand;
            for (int i = _bodies.Count - 1; i >= 0; i--)
            {
                CardBody body = _bodies[i];
                if (body.Card != null && cards.Contains(body.Card) && hand.ChoosingSlotCard != body.Card) continue;
                if (body.Hand != null && body.Hand.Held == body) body.Hand.Held = null;
                _bodies.RemoveAt(i);
            }
        }

        private void ReturnToHand(CardBody body, PlayerHand hand)
        {
            _bodies.Remove(body);
            if (body.Hand != null && body.Hand.Held == body) body.Hand.Held = null;
            body.Hand = null;
            PlayableCard card = body.Card;
            if (card == null) return;
            Transform t = card.transform;
            Vector3 local = t.localPosition;
            t.localPosition = new Vector3(local.x, local.y, 0f);
            if (hand != null && hand.CardsInHand.Contains(card)) hand.OnCardInspected(card);
        }

        private void ApplyPoses()
        {
            for (int i = 0; i < _bodies.Count; i++)
            {
                CardBody body = _bodies[i];
                if (body.Card == null) continue;
                body.Card.transform.SetPositionAndRotation(body.Position, body.Rotation);
                if (_row != null) _row.Remember(body.Card);
            }
        }

        private void ForgetAll()
        {
            _piles.PutBack(_input.Left, null);
            _piles.PutBack(_input.Right, null);
            _bodies.Clear();
            _returning.Clear();
            _input.Left.Held = null;
            _input.Right.Held = null;
            _input.Left.InBell = false;
            _input.Right.InBell = false;
            if (_bell != null) _bell.Forget();
            _tableBoard = null;
            _haveTable = false;
            ClearPending();
            _sacrifice.Forget();
            DropHover(_input.Left, _leftHover);
            DropHover(_input.Right, _rightHover);
            HidePreviews();
            if (_row != null) _row.Release();
            if (_knife != null) _knife.Forget();
            if (_items != null) _items.Release();
            if (_lift != null) _lift.Release();
            if (_scales != null) _scales.Release();
            if (_totems != null) _totems.ReleaseBattle();
            if (_bones != null) _bones.Release();
        }

        private void ForgetWorld()
        {
            if (_events != null) _events.Release();
            if (_map != null) _map.Release();
            if (_candles != null) _candles.Release();
            if (_book != null) _book.Release();
            if (_totems != null) _totems.Release();
            if (_eyes != null) _eyes.Release();
            if (_campfire != null) _campfire.Release();
            if (_rites != null) _rites.Release();
            HandProp.ClearAll();
        }

        private CardBody FindBody(PlayableCard card)
        {
            for (int i = 0; i < _bodies.Count; i++) if (_bodies[i].Card == card) return _bodies[i];
            return null;
        }

        internal static float BoxSqrDistance(PlayableCard card, Vector3 point)
        {
            BoxCollider box = card.GetComponent<BoxCollider>();
            if (box == null)
            {
                Bounds bounds;
                if (HandReach.CardBounds(card, out bounds)) return bounds.SqrDistance(point);
                return (card.transform.position - point).sqrMagnitude;
            }
            Transform t = box.transform;
            Vector3 local = t.InverseTransformPoint(point) - box.center;
            Vector3 half = box.size * 0.5f;
            Vector3 clamped = new Vector3(Mathf.Clamp(local.x, -half.x, half.x), Mathf.Clamp(local.y, -half.y, half.y), Mathf.Clamp(local.z, -half.z, half.z));
            return (t.TransformPoint(box.center + clamped) - point).sqrMagnitude;
        }

        internal static void PinchPose(VrHand state, PlayableCard card, Quaternion worldRotation, float scale, out Vector3 localPosition, out Quaternion localRotation)
        {
            Quaternion current = Quaternion.Inverse(state.Rotation) * worldRotation;
            BoxCollider box = card.GetComponent<Collider>() as BoxCollider;
            if (box == null)
            {
                localRotation = current;
                localPosition = state.PinchOffset(scale);
                return;
            }
            Vector3 size = box.size;
            Vector3 lossy = card.transform.lossyScale;
            int thin = 0;
            for (int i = 1; i < 3; i++) if (Mathf.Abs(size[i] * lossy[i]) < Mathf.Abs(size[thin] * lossy[thin])) thin = i;
            Vector3 extend = state.LocalAimRotation * Vector3.forward;
            Vector3 up = state.LocalAimRotation * Vector3.up;
            float bestAngle = float.MaxValue;
            localRotation = current;
            Vector3 bestEdge = Vector3.zero;
            int bestAxis = (thin + 1) % 3;
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == thin) continue;
                for (int edgeSign = -1; edgeSign <= 1; edgeSign += 2)
                {
                    Vector3 edge = Vector3.zero;
                    edge[axis] = edgeSign;
                    for (int faceSign = -1; faceSign <= 1; faceSign += 2)
                    {
                        Vector3 face = Vector3.zero;
                        face[thin] = faceSign;
                        Quaternion candidate = Quaternion.LookRotation(-extend, up) * Quaternion.Inverse(Quaternion.LookRotation(edge, face));
                        float angle = Quaternion.Angle(current, candidate);
                        if (angle >= bestAngle) continue;
                        bestAngle = angle;
                        localRotation = candidate;
                        bestEdge = edge;
                        bestAxis = axis;
                    }
                }
            }
            float axisScale = Mathf.Max(Mathf.Abs(lossy[bestAxis]), 1e-5f);
            float inset = Mathf.Min(PinchInsetMeters * scale / axisScale, size[bestAxis] * 0.4f);
            Vector3 pinchLocal = box.center + bestEdge * (size[bestAxis] * 0.5f - inset);
            localPosition = state.PinchOffset(scale) - localRotation * Vector3.Scale(lossy, pinchLocal);
        }

        private static PlayableCard RaycastCard(VrHand state, PlayerHand hand, float scale)
        {
            Collider hit = HandReach.Pointed(state, scale);
            PlayableCard card = hit != null ? hit.GetComponentInParent<PlayableCard>() : null;
            return card != null && hand.CardsInHand.Contains(card) ? card : null;
        }

        private void DisableTotems(Exception exception)
        {
            TotemHands totems = _totems;
            _totems = null;
            _logger.LogError("Totem hands turned off after an error; the totem works by touch again: " + exception);
            try { totems.Release(); }
            catch (Exception cleanup) { _logger.LogWarning("Totem hands cleanup failed: " + cleanup.Message); }
        }

        private void DisableBones(Exception exception)
        {
            BoneTokens bones = _bones;
            _bones = null;
            _logger.LogError("Bone tokens by hand turned off after an error: " + exception);
            try { bones.Release(); }
            catch (Exception cleanup) { _logger.LogWarning("Bone tokens cleanup failed: " + cleanup.Message); }
        }

        private void Fail(Exception exception)
        {
            _failed = true;
            if (_inspector != null) _inspector.Hide();
            GrabGlow.ClearAll();
            try
            {
                PlayerHand hand = Singleton<PlayerHand>.Instance;
                var bodies = new List<CardBody>(_bodies);
                for (int i = 0; i < bodies.Count; i++) ReturnToHand(bodies[i], hand);
                ForgetAll();
                ForgetWorld();
            }
            catch (Exception cleanup)
            {
                if (_logger != null) _logger.LogWarning("Hand interaction cleanup failed: " + cleanup.Message);
            }
            if (_logger != null) _logger.LogError("Hand interactions stopped after an error: " + exception);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_input != null) _input.HandednessChanged -= OnHandednessChanged;
            _leftPreview.Destroy();
            _rightPreview.Destroy();
            if (_row != null) _row.Release();
            if (_knife != null) _knife.Dispose();
            if (_items != null) _items.Release();
            if (_events != null) _events.Release();
            if (_map != null) _map.Release();
            if (_lift != null) _lift.Release();
            if (_scales != null) _scales.Release();
            if (_totems != null) _totems.Release();
            if (_bones != null) _bones.Release();
            if (_eyes != null) _eyes.Release();
            if (_candles != null) _candles.Release();
            if (_rites != null) _rites.Dispose();
            if (_inspector != null) _inspector.Destroy();
            HandProp.ClearAll();
            GrabGlow.ClearAll();
        }
    }
}
