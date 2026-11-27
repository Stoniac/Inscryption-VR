using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using Pixelplacement;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class CardRow
    {
        private struct Placement
        {
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal bool FaceDown;
        }

        private struct Rest
        {
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
        }

        private struct Seen
        {
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal float Time;
        }

        private struct Spot
        {
            internal Vector3 Center;
            internal Vector3 Right;
            internal Vector3 Toward;
            internal float Min;
            internal float Max;
            internal float ZMin;
            internal float ZMax;
        }

        private struct DrawOrigin
        {
            internal Transform Back;
            internal Vector3 Position;
            internal Quaternion BackRotation;
            internal Quaternion CardRotation;
            internal float Time;
        }

        private static readonly FieldInfo CardsParentField = typeof(PlayerHand).GetField("cardsParent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo ModelChooserField = typeof(PlayerHand3D).GetField("modelChooser", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo AboveHandField = typeof(PlayerHand3D).GetField("cardAboveHandMarker", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly MethodInfo SetCardPositionsMethod = typeof(PlayerHand).GetMethod("SetCardPositions", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private const float SlideRate = 14f;
        private const float StackLanes = 0.008f;
        private const float HoverLanes = 0.025f;
        private const float SpanLanesBeyondBoard = 1.5f;
        private const float PresentLanes = 0.35f;
        private const float PresentTiltDegrees = 55f;
        private const float SeenSeconds = 0.75f;
        private const float OriginSeconds = 3f;
        private const float DropInLanes = 0.3f;
        private const float RowDepthOverlap = 0.35f;
        private const float PileMarginWidths = 0.1f;

        private readonly ManualLogSource _logger;
        private float _rowLanes;
        private float _overlap;
        private bool _spread = true;
        private readonly Dictionary<PlayableCard, Placement> _placed = new Dictionary<PlayableCard, Placement>();
        private readonly Dictionary<PlayableCard, Rest> _rest = new Dictionary<PlayableCard, Rest>();
        private readonly Dictionary<PlayableCard, Seen> _seen = new Dictionary<PlayableCard, Seen>();
        private readonly List<DrawOrigin> _origins = new List<DrawOrigin>();
        private readonly List<PlayableCard> _row = new List<PlayableCard>();
        private readonly HashSet<PlayableCard> _incoming = new HashSet<PlayableCard>();
        private readonly List<PlayableCard> _stale = new List<PlayableCard>();
        private readonly List<Renderer> _hiddenModel = new List<Renderer>();
        private readonly List<Renderer> _modelRenderers = new List<Renderer>();
        private readonly HashSet<Renderer> _reported = new HashSet<Renderer>();

        private PlayerHand _hand;
        private Transform _parent;
        private Transform _aboveHand;
        private Transform _model;
        private bool _active;
        private bool _haveNatural;
        private Vector3 _naturalScale = Vector3.one;
        private readonly Dictionary<PlayableCard, Spot> _spots = new Dictionary<PlayableCard, Spot>();
        private HashSet<PlayableCard> _hover = new HashSet<PlayableCard>();
        private HashSet<PlayableCard> _hoverNext = new HashSet<PlayableCard>();
        private bool _haveLayout;
        private Vector3 _layoutCenter;
        private Vector3 _layoutRight;
        private Vector3 _layoutToward;
        private float _layoutWidth;
        private float _layoutLength;
        private float _layoutSpacing;
        private float _layoutSpan;
        private float _nearDepth;
        private float _farLo;
        private float _farHi;
        private float _nearLo;
        private float _nearHi;
        private bool _twoRows;
        private bool _loggedTwoRows;
        private float _loggedFarHi = float.NaN;
        private float _loggedFarLo = float.NaN;
        private Vector3 _gapPoint;
        private int _gapFrame = -10;
        private readonly List<PlayableCard> _others = new List<PlayableCard>();

        internal CardRow(ManualLogSource logger, float rowLanes, float overlap)
        {
            _logger = logger;
            SetRowLanes(rowLanes);
            SetOverlap(overlap);
        }

        internal static bool Available { get { return CardsParentField != null; } }
        internal bool Active { get { return _active; } }
        internal void SetRowLanes(float lanes) { _rowLanes = Mathf.Clamp(lanes, 0.8f, 3f); }

        internal void SetOverlap(float overlap) { _overlap = Mathf.Clamp(overlap, 0f, 0.8f); }

        internal const string Spread = "Spread";
        internal const string Rise = "Rise";
        internal static readonly string[] HoverStyles = { Spread, Rise };

        internal void SetHoverStyle(string style)
        {
            _spread = !string.Equals(style, Rise, StringComparison.OrdinalIgnoreCase);
        }

        private bool SpreadsFor(PlayableCard card, int s, int column, bool gap, int gapIndex)
        {
            if (!_hover.Contains(card)) return false;
            if (_aboveHand != null && card.transform.parent == _aboveHand) return false;
            return column != 0 && !(gap && s == gapIndex + 1);
        }

        private bool InRow(Transform card)
        {
            Transform parent = card.parent;
            return parent != null && (parent == _parent || parent == _aboveHand);
        }

        internal void Hover(PlayableCard card) { if (card != null) _hoverNext.Add(card); }

        internal void Gap(Vector3 point)
        {
            _gapPoint = point;
            _gapFrame = Time.frameCount;
        }

        private bool InRegion(Vector3 point)
        {
            if (!_haveLayout) return false;
            Vector3 offset = point - _layoutCenter;
            float x = Vector3.Dot(offset, _layoutRight);
            float z = Vector3.Dot(offset, _layoutToward);
            float dy = point.y - _layoutCenter.y;
            float zMax = 0.8f * _layoutLength + (_twoRows ? _nearDepth : 0f);
            return z >= -0.8f * _layoutLength && z <= zMax && Mathf.Abs(x) <= 0.5f * _layoutSpan + 0.5f * _layoutWidth &&
                dy >= -0.2f * _layoutSpacing && dy <= 2.5f * _layoutSpacing;
        }

        private float NaturalPitch { get { return Mathf.Max(_layoutWidth * (1f - _overlap), 1e-4f); } }

        private int FarCount(int slots)
        {
            int capacity = Mathf.Max(1, Mathf.FloorToInt((_farHi - _farLo - _layoutWidth) / NaturalPitch + 1e-3f) + 1);
            return Mathf.Min(slots, capacity);
        }

        private float RowPitch(int count, float lo, float hi)
        {
            float pitch = NaturalPitch;
            float room = hi - lo - _layoutWidth;
            if (count > 1 && (count - 1) * pitch > room) pitch = room / (count - 1);
            return Mathf.Max(pitch, 1e-4f);
        }

        private float RowOffset(int count, float pitch, float lo, float hi)
        {
            float half = 0.5f * ((count - 1) * pitch + _layoutWidth);
            if (hi - lo <= 2f * half) return 0.5f * (lo + hi);
            return Mathf.Clamp(0f, lo + half, hi - half);
        }

        private int GapIndex(Vector3 point, int others)
        {
            int slots = others + 1;
            int farCount = FarCount(slots);
            int nearCount = slots - farCount;
            Vector3 offset = point - _layoutCenter;
            float x = Vector3.Dot(offset, _layoutRight);
            float z = Vector3.Dot(offset, _layoutToward);
            bool near = nearCount > 0 && z > 0.5f * _nearDepth;
            int count = near ? nearCount : farCount;
            float lo = near ? _nearLo : _farLo;
            float hi = near ? _nearHi : _farHi;
            float pitch = RowPitch(count, lo, hi);
            float start = RowOffset(count, pitch, lo, hi);
            int column = Mathf.Clamp(Mathf.RoundToInt((x - start) / pitch + (count - 1) * 0.5f), 0, count - 1);
            return Mathf.Clamp((near ? farCount : 0) + column, 0, others);
        }

        private void PileLimits(float zCenter, out float lo, out float hi)
        {
            lo = -0.5f * _layoutSpan;
            hi = 0.5f * _layoutSpan;
            CardDrawPiles3D piles = CardDrawPiles3D.Instance;
            if (piles != null)
            {
                BlockPile(piles.Pile, zCenter, ref lo, ref hi);
                BlockPile(piles.SidePile, zCenter, ref lo, ref hi);
            }
            if (hi - lo < _layoutWidth)
            {
                float mid = 0.5f * (lo + hi);
                lo = mid - 0.5f * _layoutWidth;
                hi = mid + 0.5f * _layoutWidth;
            }
        }

        private void BlockPile(CardPile pile, float zCenter, ref float lo, ref float hi)
        {
            if (pile == null || !pile.gameObject.activeInHierarchy) return;
            Vector3 offset = pile.transform.position - _layoutCenter;
            float x = Vector3.Dot(offset, _layoutRight);
            float z = Vector3.Dot(offset, _layoutToward);
            if (Mathf.Abs(z - zCenter) >= _layoutLength) return;
            float reach = 0.5f * _layoutWidth + PileMarginWidths * _layoutWidth;
            if (x >= 0f) hi = Mathf.Min(hi, x - reach);
            else lo = Mathf.Max(lo, x + reach);
        }

        internal bool TryReorder(PlayerHand hand, PlayableCard card, Vector3 point)
        {
            if (hand == null || card == null || !InRegion(point)) return false;
            List<PlayableCard> cards = hand.CardsInHand;
            if (!cards.Contains(card)) return false;
            _others.Clear();
            for (int i = 0; i < _row.Count; i++)
                if (_row[i] != null && _row[i] != card) _others.Add(_row[i]);
            int index = GapIndex(point, _others.Count);
            int before = cards.IndexOf(card);
            cards.Remove(card);
            int at = -1;
            for (int j = index; j < _others.Count && at < 0; j++) at = cards.IndexOf(_others[j]);
            if (at < 0)
            {
                for (int j = Mathf.Min(index, _others.Count) - 1; j >= 0 && at < 0; j--)
                {
                    int found = cards.IndexOf(_others[j]);
                    if (found >= 0) at = found + 1;
                }
            }
            if (at < 0 || at > cards.Count) at = cards.Count;
            cards.Insert(at, card);
            if (at != before) _logger.LogInfo("Card " + card.name + " moved to place " + (index + 1) + " of " + (_others.Count + 1) + " in the row.");
            return true;
        }

        internal bool InRowSpot(PlayableCard card) { return card != null && _spots.ContainsKey(card); }

        internal bool SpotSqrDistance(PlayableCard card, Vector3 point, out float sqrDistance)
        {
            sqrDistance = float.MaxValue;
            Spot spot;
            if (card == null || !_spots.TryGetValue(card, out spot)) return false;
            Vector3 offset = point - spot.Center;
            float x = Vector3.Dot(offset, spot.Right);
            float z = Vector3.Dot(offset, spot.Toward);
            float y = offset.y;
            float dx = x < spot.Min ? spot.Min - x : x > spot.Max ? x - spot.Max : 0f;
            float dz = z < spot.ZMin ? spot.ZMin - z : z > spot.ZMax ? z - spot.ZMax : 0f;
            sqrDistance = dx * dx + dz * dz + y * y;
            return true;
        }

        internal void Remember(PlayableCard card)
        {
            if (card == null) return;
            Seen seen;
            seen.Position = card.transform.position;
            seen.Rotation = card.transform.rotation;
            seen.Time = Time.unscaledTime;
            _seen[card] = seen;
        }

        internal bool Recall(PlayableCard card, out Vector3 position, out Quaternion rotation)
        {
            Seen seen;
            if (card != null && _seen.TryGetValue(card, out seen) && Time.unscaledTime - seen.Time <= SeenSeconds)
            {
                position = seen.Position;
                rotation = seen.Rotation;
                return true;
            }
            position = card != null ? card.transform.position : Vector3.zero;
            rotation = card != null ? card.transform.rotation : Quaternion.identity;
            return false;
        }

        internal bool AddDrawOrigin(Transform back, Vector3 position, Quaternion backRotation, Quaternion cardRotation)
        {
            for (int i = 0; i < _origins.Count; i++)
                if (back != null && _origins[i].Back == back) return false;
            DrawOrigin origin;
            origin.Back = back;
            origin.Position = position;
            origin.BackRotation = backRotation;
            origin.CardRotation = cardRotation;
            origin.Time = Time.unscaledTime;
            _origins.Add(origin);
            if (back != null)
            {
                Tween.Stop(back.GetInstanceID());
                back.gameObject.SetActive(true);
                back.SetPositionAndRotation(position, backRotation);
            }
            return true;
        }

        internal PlayableCard FindIncoming(PlayerHand hand, ICollection<PlayableCard> exclude)
        {
            if (!Resolve(hand)) return null;
            List<PlayableCard> cards = hand.CardsInHand;
            for (int i = 0; i < _parent.childCount; i++)
            {
                PlayableCard card = _parent.GetChild(i).GetComponent<PlayableCard>();
                if (card == null || !card.gameObject.activeInHierarchy || cards.Contains(card)) continue;
                if (exclude != null && exclude.Contains(card)) continue;
                return card;
            }
            return null;
        }

        private void HoldOrigins()
        {
            for (int i = _origins.Count - 1; i >= 0; i--)
            {
                DrawOrigin origin = _origins[i];
                if (Time.unscaledTime - origin.Time > OriginSeconds)
                {
                    HideBack(origin.Back);
                    _origins.RemoveAt(i);
                    continue;
                }
                if (origin.Back == null) continue;
                Tween.Stop(origin.Back.GetInstanceID());
                origin.Back.SetPositionAndRotation(origin.Position, origin.BackRotation);
            }
        }

        private bool TakeOrigin(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (_origins.Count == 0) return false;
            DrawOrigin origin = _origins[0];
            _origins.RemoveAt(0);
            HideBack(origin.Back);
            position = origin.Position;
            rotation = origin.CardRotation;
            return true;
        }

        private static void HideBack(Transform back)
        {
            if (back == null) return;
            Tween.Stop(back.GetInstanceID());
            back.gameObject.SetActive(false);
        }

        private static bool Plausible(CardGrabber grabber, Vector3 position)
        {
            float spacing = grabber.Spacing;
            return grabber.InsideTable(position) && position.y >= grabber.TableY - 0.05f * spacing && position.y <= grabber.TableY + 4f * spacing;
        }

        private void ClearOrigins()
        {
            for (int i = 0; i < _origins.Count; i++) HideBack(_origins[i].Back);
            _origins.Clear();
        }

        internal void Layout(PlayerHand hand, CardGrabber grabber, Func<PlayableCard, bool> free, float dt)
        {
            if (!Resolve(hand) || !grabber.HaveTable)
            {
                Release();
                return;
            }
            CaptureNatural(hand);
            if (!_haveNatural)
            {
                Release();
                return;
            }
            if (!_active)
            {
                _hiddenModel.Clear();
                HideModel();
                _active = true;
                _logger.LogInfo("Unplayed cards now lie in a row on the table (" + _hiddenModel.Count + " game hand renderers hidden).");
            }
            else HideModel();
            HashSet<PlayableCard> swap = _hover;
            _hover = _hoverNext;
            _hoverNext = swap;
            _hoverNext.Clear();
            _spots.Clear();
            HoldOrigins();

            _row.Clear();
            _incoming.Clear();
            List<PlayableCard> cards = hand.CardsInHand;
            for (int i = 0; i < cards.Count; i++)
            {
                PlayableCard card = cards[i];
                if (card == null || !card.gameObject.activeInHierarchy || !InRow(card.transform) || !free(card)) continue;
                _row.Add(card);
            }
            for (int i = 0; i < _parent.childCount; i++)
            {
                PlayableCard card = _parent.GetChild(i).GetComponent<PlayableCard>();
                if (card == null || !card.gameObject.activeInHierarchy || cards.Contains(card) || !free(card)) continue;
                _row.Add(card);
                _incoming.Add(card);
            }
            ForgetMissing();
            if (_row.Count == 0) return;

            float spacing = grabber.Spacing;
            Vector3 toward = grabber.TowardPlayer;
            Vector3 right = Vector3.Cross(Vector3.up, -toward).normalized;
            Vector3 center = grabber.PlayerRowCenter + toward * (_rowLanes * spacing);
            center.y = grabber.TableY + 0.004f * spacing;
            Quaternion faceUp = grabber.BoardCardRotation;
            Quaternion faceDown = Quaternion.AngleAxis(180f, toward) * faceUp;
            Quaternion present = Quaternion.AngleAxis(-PresentTiltDegrees, right) * faceUp;
            float width, length;
            CardSize(_row[0], out width, out length);
            Vector3 flatToward = Vector3.ProjectOnPlane(toward, Vector3.up).normalized;
            _layoutCenter = center;
            _layoutRight = right;
            _layoutToward = flatToward;
            _layoutWidth = width;
            _layoutLength = length;
            _layoutSpacing = spacing;
            _layoutSpan = spacing * (grabber.PlayerSlots.Count - 1 + SpanLanesBeyondBoard) + width;
            _nearDepth = length * (1f - RowDepthOverlap);
            PileLimits(0f, out _farLo, out _farHi);
            PileLimits(_nearDepth, out _nearLo, out _nearHi);
            _haveLayout = true;
            _twoRows = FarCount(_row.Count + 1) < _row.Count + 1;
            bool gap = Time.frameCount - _gapFrame <= 1 && InRegion(_gapPoint);
            int slots = _row.Count + (gap ? 1 : 0);
            int gapIndex = gap ? GapIndex(_gapPoint, _row.Count) : -1;
            int farCount = FarCount(slots);
            int nearCount = slots - farCount;
            float farPitch = RowPitch(farCount, _farLo, _farHi);
            float nearPitch = nearCount > 0 ? RowPitch(nearCount, _nearLo, _nearHi) : farPitch;
            float farStart = RowOffset(farCount, farPitch, _farLo, _farHi);
            float nearStart = nearCount > 0 ? RowOffset(nearCount, nearPitch, _nearLo, _nearHi) : 0f;
            float farCovered = Mathf.Max(0f, width - farPitch);
            float nearCovered = Mathf.Max(0f, width - nearPitch);
            float depthCovered = length - _nearDepth;
            float stack = StackLanes * spacing;
            ReportRows(farCount, nearCount);
            float k = 1f - Mathf.Exp(-SlideRate * Mathf.Max(dt, 0f));
            float farShift = 0f, nearShift = 0f;
            if (_spread)
            {
                for (int i = 0; i < _row.Count; i++)
                {
                    int s = gap && i >= gapIndex ? i + 1 : i;
                    bool near = s >= farCount;
                    if (!SpreadsFor(_row[i], s, near ? s - farCount : s, gap, gapIndex)) continue;
                    if (near) nearShift += nearCovered;
                    else farShift += farCovered;
                }
            }

            for (int i = 0; i < _row.Count; i++)
            {
                PlayableCard card = _row[i];
                Transform t = card.transform;
                int s = gap && i >= gapIndex ? i + 1 : i;
                bool near = s >= farCount;
                int column = near ? s - farCount : s;
                int count = near ? nearCount : farCount;
                float pitch = near ? nearPitch : farPitch;
                float covered = near ? nearCovered : farCovered;
                bool leftOpen = column == 0 || (gap && s == gapIndex + 1);
                bool spreading = (near ? nearShift : farShift) > 0f && SpreadsFor(card, s, column, gap, gapIndex);
                if (spreading)
                {
                    if (near) nearShift -= covered;
                    else farShift -= covered;
                }
                float rowShift = near ? nearShift : farShift;
                float x = (near ? nearStart : farStart) + (column - (count - 1) * 0.5f) * pitch - rowShift;
                float y = (count - 1 - column) * stack + (near ? 0f : nearCount * stack);
                Vector3 target = center + right * x + flatToward * (near ? _nearDepth : 0f) + Vector3.up * y;
                Placement placement;
                if (!_placed.TryGetValue(card, out placement))
                {
                    placement = Arrive(card, grabber, target, faceDown);
                    if (!_rest.ContainsKey(card))
                    {
                        Rest rest;
                        rest.LocalPosition = t.localPosition;
                        rest.LocalRotation = t.localRotation;
                        _rest[card] = rest;
                    }
                }
                bool presenting = _aboveHand != null && t.parent == _aboveHand;
                if (placement.FaceDown)
                {
                    placement.FaceDown = false;
                    _logger.LogInfo("Card " + card.name + " turns face up as it joins the row.");
                }
                Quaternion pose = faceUp;
                if (presenting)
                {
                    pose = present;
                    target += Vector3.up * (PresentLanes * spacing) + toward * (0.25f * width);
                }
                else
                {
                    bool slid = near && _hover.Contains(card);
                    if (slid) target += flatToward * depthCovered;
                    Spot spot;
                    spot.Center = target;
                    spot.Right = right;
                    spot.Toward = flatToward;
                    spot.Min = leftOpen || spreading ? -0.5f * width : Mathf.Min(Mathf.Max(-0.5f * width, 0.5f * width - pitch), 0.5f * width);
                    spot.Max = 0.5f * width;
                    spot.ZMin = near && !slid ? -0.5f * length + depthCovered : -0.5f * length;
                    spot.ZMax = 0.5f * length;
                    _spots[card] = spot;
                    if (!_spread && _hover.Contains(card)) target += Vector3.up * (HoverLanes * spacing);
                }
                placement.Position = Vector3.Lerp(placement.Position, target, k);
                placement.Rotation = Quaternion.Slerp(placement.Rotation, pose, k);
                _placed[card] = placement;
                Tween.Stop(t.GetInstanceID());
                t.SetPositionAndRotation(placement.Position, placement.Rotation);
                SetNaturalScale(t);
                Remember(card);
            }
        }

        private void ReportRows(int farCount, int nearCount)
        {
            float full = 0.5f * _layoutSpan;
            if (Mathf.Abs(_farLo - _loggedFarLo) > 0.05f * _layoutWidth || Mathf.Abs(_farHi - _loggedFarHi) > 0.05f * _layoutWidth || float.IsNaN(_loggedFarLo))
            {
                _loggedFarLo = _farLo;
                _loggedFarHi = _farHi;
                _logger.LogInfo("Card row room: " + (_farLo / _layoutWidth).ToString("F2") + " to " + (_farHi / _layoutWidth).ToString("F2") +
                    " card widths from the centre (board allows " + (full / _layoutWidth).ToString("F2") + "); the near row gets " +
                    (_nearLo / _layoutWidth).ToString("F2") + " to " + (_nearHi / _layoutWidth).ToString("F2") + ". The row holds " + FarCount(int.MaxValue / 2) + " cards before it spills toward you.");
            }
            bool two = nearCount > 0;
            if (two == _loggedTwoRows) return;
            _loggedTwoRows = two;
            _logger.LogInfo(two ? "Hand spilled into a second row closer to the player (" + farCount + " far, " + nearCount + " near)." : "Hand fits in one row again.");
        }

        internal void SetNaturalScale(Transform card)
        {
            if (!_haveNatural || card == null) return;
            Transform parent = card.parent;
            Vector3 parentScale = parent != null ? parent.lossyScale : Vector3.one;
            card.localScale = Divide(_naturalScale, parentScale);
        }

        private void ForgetMissing()
        {
            _stale.Clear();
            foreach (PlayableCard card in _placed.Keys)
                if (card == null || !_row.Contains(card)) _stale.Add(card);
            for (int i = 0; i < _stale.Count; i++) _placed.Remove(_stale[i]);
            _stale.Clear();
            List<PlayableCard> cards = _hand != null ? _hand.CardsInHand : null;
            foreach (PlayableCard card in _rest.Keys)
                if (card == null || ((cards == null || !cards.Contains(card)) && !_row.Contains(card))) _stale.Add(card);
            for (int i = 0; i < _stale.Count; i++) _rest.Remove(_stale[i]);
            _stale.Clear();
            foreach (KeyValuePair<PlayableCard, Seen> entry in _seen)
                if (entry.Key == null || Time.unscaledTime - entry.Value.Time > SeenSeconds) _stale.Add(entry.Key);
            for (int i = 0; i < _stale.Count; i++) _seen.Remove(_stale[i]);
        }

        private Placement Arrive(PlayableCard card, CardGrabber grabber, Vector3 target, Quaternion faceDown)
        {
            Transform t = card.transform;
            Placement placement;
            Vector3 position;
            Quaternion rotation;
            string source;
            if (Recall(card, out position, out rotation)) source = null;
            else if (_incoming.Contains(card) && TakeOrigin(out position, out rotation)) source = "the pile it was drawn from";
            else if (Plausible(grabber, t.position))
            {
                position = t.position;
                rotation = t.rotation;
                source = null;
            }
            else
            {
                position = target + Vector3.up * (DropInLanes * grabber.Spacing);
                bool down = Vector3.Dot(t.rotation * grabber.FaceNormalLocal, Vector3.up) < -0.3f || _incoming.Contains(card);
                rotation = down ? faceDown : grabber.BoardCardRotation;
                source = "its row spot (the game had it at " + t.position.ToString("F2") + ")";
            }
            placement.Position = position;
            placement.Rotation = rotation;
            placement.FaceDown = Vector3.Dot(rotation * grabber.FaceNormalLocal, Vector3.up) < -0.3f;
            if (source != null) _logger.LogInfo("Card " + card.name + " enters the row from " + source + ".");
            return placement;
        }

        private void CaptureNatural(PlayerHand hand)
        {
            if (_haveNatural) return;
            List<PlayableCard> cards = hand.CardsInHand;
            for (int i = 0; i < cards.Count; i++)
            {
                PlayableCard card = cards[i];
                if (card == null || card.transform.parent != _parent) continue;
                Vector3 lossy = card.transform.lossyScale;
                if (Mathf.Abs(lossy.x) < 1e-5f || Mathf.Abs(lossy.y) < 1e-5f || Mathf.Abs(lossy.z) < 1e-5f) continue;
                _naturalScale = lossy;
                _haveNatural = true;
                return;
            }
        }

        private void CardSize(PlayableCard card, out float width, out float length)
        {
            width = 1f;
            length = 1.5f;
            BoxCollider box = card.GetComponent<Collider>() as BoxCollider;
            if (box == null) return;
            Vector3 size = Vector3.Scale(box.size, new Vector3(Mathf.Abs(_naturalScale.x), Mathf.Abs(_naturalScale.y), Mathf.Abs(_naturalScale.z)));
            float a = size.x, b = size.y, c = size.z;
            float max = Mathf.Max(a, Mathf.Max(b, c));
            float min = Mathf.Min(a, Mathf.Min(b, c));
            width = a + b + c - max - min;
            length = max;
        }

        private void HideModel()
        {
            if (_model == null) return;
            _model.GetComponentsInChildren<Renderer>(true, _modelRenderers);
            for (int i = 0; i < _modelRenderers.Count; i++)
            {
                Renderer renderer = _modelRenderers[i];
                if (renderer == null || !renderer.enabled || BelongsToCard(renderer.transform)) continue;
                renderer.enabled = false;
                if (!_hiddenModel.Contains(renderer)) _hiddenModel.Add(renderer);
                if (_active && _reported.Add(renderer)) _logger.LogInfo("Game hand renderer '" + renderer.name + "' turned back on; hidden again.");
            }
        }

        private bool BelongsToCard(Transform t)
        {
            if (_parent != null && t.IsChildOf(_parent)) return true;
            if (_aboveHand != null && t.IsChildOf(_aboveHand)) return true;
            for (Transform walk = t; walk != null && walk != _model; walk = walk.parent)
                if (walk.GetComponent<Card>() != null) return true;
            return false;
        }

        private void ShowModel()
        {
            for (int i = 0; i < _hiddenModel.Count; i++)
                if (_hiddenModel[i] != null) _hiddenModel[i].enabled = true;
            _hiddenModel.Clear();
        }

        internal void Release()
        {
            _hover.Clear();
            _hoverNext.Clear();
            _spots.Clear();
            _haveLayout = false;
            _twoRows = false;
            ClearOrigins();
            if (!_active) return;
            _active = false;
            ShowModel();
            if (_hand != null && _parent != null)
            {
                List<PlayableCard> cards = _hand.CardsInHand;
                for (int i = 0; i < cards.Count; i++)
                {
                    PlayableCard card = cards[i];
                    if (card == null || card.transform.parent != _parent) continue;
                    Rest rest;
                    if (_rest.TryGetValue(card, out rest))
                    {
                        card.transform.localPosition = rest.LocalPosition;
                        card.transform.localRotation = rest.LocalRotation;
                    }
                    SetNaturalScale(card.transform);
                }
                if (SetCardPositionsMethod != null)
                {
                    try { SetCardPositionsMethod.Invoke(_hand, null); }
                    catch (Exception exception) { _logger.LogWarning("Could not ask the game to re-layout the hand: " + exception.Message); }
                }
            }
            _placed.Clear();
            _rest.Clear();
            _logger.LogInfo("Card row released; cards returned to the game's hand.");
        }

        private bool Resolve(PlayerHand hand)
        {
            if (hand == null || CardsParentField == null) return false;
            if (hand != _hand || _parent == null)
            {
                Release();
                _hand = hand;
                _parent = CardsParentField.GetValue(hand) as Transform;
                _aboveHand = hand is PlayerHand3D && AboveHandField != null ? AboveHandField.GetValue(hand) as Transform : null;
                Component chooser = hand is PlayerHand3D && ModelChooserField != null ? ModelChooserField.GetValue(hand) as Component : null;
                _model = chooser != null ? chooser.transform : null;
            }
            return _parent != null;
        }

        private static Vector3 Divide(Vector3 value, Vector3 by)
        {
            return new Vector3(SafeDivide(value.x, by.x), SafeDivide(value.y, by.y), SafeDivide(value.z, by.z));
        }

        private static float SafeDivide(float value, float by)
        {
            return Mathf.Abs(by) < 1e-6f ? value : value / by;
        }
    }
}
