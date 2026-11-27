using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using Pixelplacement;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class PileGrab
    {
        internal CardPile Pile;
        internal Transform Back;
        internal Vector3 RestLocalPosition;
        internal Quaternion RestLocalRotation;
        internal Vector3 RestPosition;
        internal Vector3 LocalPosition;
        internal Quaternion LocalRotation;
        internal bool Committed;
        internal float CommitTime;
        internal PlayableCard Incoming;
        internal readonly List<PlayableCard> HandBefore = new List<PlayableCard>();
    }

    internal sealed class PileDraw
    {
        private static readonly FieldInfo PileCardsField = typeof(CardPile).GetField("cards", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo PileCanDrawProperty = typeof(CardPile).GetProperty("CanDrawFromPile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo PileCanDrawGetter = PileCanDrawProperty != null ? PileCanDrawProperty.GetGetMethod(true) : null;

        private const float PileClearLanes = 1f;
        private const float PileClearMeters = 0.12f;
        private const float PileCommitTimeout = 3f;

        private sealed class PileWatch
        {
            internal CardPile Pile;
            internal Transform Top;
            internal int Count;
            internal Vector3 Position;
            internal Quaternion Rotation;
        }

        private readonly CardGrabber _grabber;
        private readonly VrInput _input;
        private readonly ManualLogSource _logger;
        private readonly PileWatch _deckWatch = new PileWatch();
        private readonly PileWatch _sideWatch = new PileWatch();

        internal PileDraw(CardGrabber grabber, VrInput input, ManualLogSource logger)
        {
            _grabber = grabber;
            _input = input;
            _logger = logger;
        }

        internal static bool Available { get { return PileCardsField != null && PileCanDrawGetter != null; } }

        internal static bool Holds(VrHand hand, Component target)
        {
            PileGrab grab = hand.Pile;
            if (grab == null) return false;
            if (grab.Incoming != null && (target == grab.Incoming || target.transform.IsChildOf(grab.Incoming.transform))) return true;
            return grab.Back != null && target.transform.IsChildOf(grab.Back);
        }

        internal bool IsIncoming(PlayableCard card)
        {
            PileGrab left = _input.Left.Pile, right = _input.Right.Pile;
            return (left != null && left.Incoming == card) || (right != null && right.Incoming == card);
        }

        private bool HeldBack(Transform back)
        {
            PileGrab left = _input.Left.Pile, right = _input.Right.Pile;
            return (left != null && left.Back == back) || (right != null && right.Back == back);
        }

        internal void Watch(bool active)
        {
            CardDrawPiles3D piles = CardDrawPiles3D.Instance;
            Watch(_deckWatch, piles != null ? piles.Pile : null, active);
            Watch(_sideWatch, piles != null ? piles.SidePile : null, active);
        }

        private void Watch(PileWatch watch, CardPile pile, bool active)
        {
            List<Transform> cards = PileCards(pile);
            CardRow row = _grabber.Row;
            if (!active || cards == null || row == null || !row.Active)
            {
                watch.Pile = null;
                watch.Top = null;
                return;
            }
            if (watch.Pile == pile && watch.Top != null && cards.Count == watch.Count - 1 && !cards.Contains(watch.Top) && !pile.DoingCardOperation && !HeldBack(watch.Top) && _grabber.HaveTable)
            {
                if (row.AddDrawOrigin(watch.Top, watch.Position, watch.Rotation, _grabber.FaceDownRotation))
                    _logger.LogInfo("Card drawn from the " + PileName(pile) + "; its back stays on the pile until the card arrives.");
            }
            watch.Pile = pile;
            watch.Count = cards.Count;
            watch.Top = cards.Count > 0 ? cards[cards.Count - 1] : null;
            if (watch.Top == null) return;
            watch.Position = watch.Top.position;
            watch.Rotation = watch.Top.rotation;
        }

        internal void Process(VrHand state, PlayerHand hand, bool available, float scale)
        {
            PileGrab grab = state.Pile;
            if (grab == null)
            {
                if (available && state.Ready && state.Held == null && !state.Claimed) TryGrab(state, hand, scale, state.GripEdge == 1);
                return;
            }
            if (grab.Committed)
            {
                UpdateCommitted(state, grab, hand);
                return;
            }
            if (grab.Back == null)
            {
                state.Pile = null;
                return;
            }
            if (!available || !state.Ready || !Ready(grab.Pile))
            {
                PutBack(state, "draw no longer available");
                return;
            }
            if (!state.Grip.Held)
            {
                PutBack(state, null);
                state.Play(Feel.Tick, 0.6f);
                return;
            }
            PoseBack(state, grab);
            float clear = _grabber.HaveTable ? PileClearLanes * _grabber.Spacing : PileClearMeters * scale;
            if ((grab.Back.position - grab.RestPosition).sqrMagnitude >= clear * clear) Commit(state, grab, hand);
        }

        internal bool Ready(CardPile pile)
        {
            if (pile == null || !Available || !pile.gameObject.activeInHierarchy) return false;
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            if (flow == null || flow.Transitioning || flow.CurrentGameState != GameState.CardBattle) return false;
            TurnManager turn = Singleton<TurnManager>.Instance;
            if (turn == null || !turn.IsPlayerDrawPhase || turn.GameEnding || turn.GameEnded) return false;
            if (pile.DoingCardOperation || pile.NumCards <= 0 || pile.CursorSelectEnded == null) return false;
            if (!(bool)PileCanDrawGetter.Invoke(pile, null)) return false;
            InteractionCursor cursor = Singleton<InteractionCursor>.Instance;
            return cursor != null && !cursor.InteractionDisabled;
        }

        internal CardPile ReadyPile()
        {
            CardDrawPiles3D piles = CardDrawPiles3D.Instance;
            if (piles == null) return null;
            return Ready(piles.Pile) ? piles.Pile : Ready(piles.SidePile) ? piles.SidePile : null;
        }

        private void TryGrab(VrHand state, PlayerHand hand, float scale, bool grab)
        {
            CardDrawPiles3D piles = CardDrawPiles3D.Instance;
            if (piles == null) return;
            float reach = _grabber.ReachMeters * scale;
            float best = reach * reach;
            CardPile chosen = null;
            Transform back = null;
            Consider(piles.Pile, state, ref best, ref chosen, ref back);
            Consider(piles.SidePile, state, ref best, ref chosen, ref back);
            if (chosen == null) return;
            float handDistance;
            if (_grabber.NearestHandCard(hand, CardGrabber.CardPoint(state), best, out handDistance) != null) return;
            state.Highlight();
            if (!grab) return;

            Tween.Stop(back.GetInstanceID());
            var pileGrab = new PileGrab();
            pileGrab.Pile = chosen;
            pileGrab.Back = back;
            pileGrab.RestLocalPosition = back.localPosition;
            pileGrab.RestLocalRotation = back.localRotation;
            pileGrab.RestPosition = back.position;
            Quaternion inverse = Quaternion.Inverse(state.Rotation);
            pileGrab.LocalPosition = inverse * (back.position - state.Position);
            pileGrab.LocalRotation = inverse * back.rotation;
            state.Pile = pileGrab;
            state.Play(Feel.Tick);
            VrSounds.Play("cardquick", MixerGroup.CardPaperSFX, back.position, 0.6f);
            _logger.LogInfo("Top card of the " + PileName(chosen) + " lifted with the " + state.Name + " hand; pull it clear to draw.");
        }

        private void Consider(CardPile pile, VrHand state, ref float best, ref CardPile chosen, ref Transform chosenBack)
        {
            if (!Ready(pile)) return;
            VrHand other = _input.Other(state);
            if (other.Pile != null && other.Pile.Pile == pile) return;
            Transform back = TopBack(pile);
            if (back == null) return;
            float distance = PileDistance(pile, back, state.Position);
            if (distance >= best) return;
            best = distance;
            chosen = pile;
            chosenBack = back;
        }

        private void Commit(VrHand state, PileGrab grab, PlayerHand hand)
        {
            CardPile pile = grab.Pile;
            CardDrawPiles3D piles = CardDrawPiles3D.Instance;
            BoonsHandler boons = Singleton<BoonsHandler>.Instance;
            if (piles != null && pile == piles.Pile && boons != null && boons.HasBoonOfType(BoonData.Type.TutorDraw))
            {
                PutBack(state, null);
                pile.CursorSelectStart();
                pile.CursorSelectEnd();
                state.Play(Feel.Tap);
                VrTips.Done("cards.draw");
                _logger.LogInfo("Deck pulled with the tutor boon active; choose the card to draw from the cards Leshy lays out.");
                return;
            }
            grab.HandBefore.Clear();
            grab.HandBefore.AddRange(hand.CardsInHand);
            grab.Committed = true;
            grab.CommitTime = Time.unscaledTime;
            pile.CursorSelectStart();
            pile.CursorSelectEnd();
            state.Play(Feel.Tap);
            VrTips.Done("cards.draw");
            _logger.LogInfo("Drew from the " + PileName(pile) + " by hand with the " + state.Name + " hand.");
        }

        private void UpdateCommitted(VrHand state, PileGrab grab, PlayerHand hand)
        {
            CardRow row = _grabber.Row;
            bool holding = _grabber.UserEnabled && state.Tracked && state.Grip.Held;
            Vector3 handPosition = state.Position + state.Rotation * grab.LocalPosition;
            Quaternion handRotation = state.Rotation * grab.LocalRotation;
            PlayableCard drawn = FindNewCard(grab, hand);
            if (drawn != null)
            {
                Vector3 position;
                Quaternion start;
                if (grab.Incoming == drawn && row != null) row.Recall(drawn, out position, out start);
                else if (holding)
                {
                    position = handPosition;
                    start = DrawnRotation(grab, handRotation);
                }
                else
                {
                    position = grab.Back != null ? grab.Back.position : handPosition;
                    start = DrawnRotation(grab, grab.Back != null ? grab.Back.rotation : handRotation);
                }
                grab.Incoming = null;
                state.Pile = null;
                HideBack(grab);
                if (holding && state.Held == null)
                {
                    _grabber.HoldCard(state, drawn, position, start);
                    _logger.LogInfo("Drawn card is now held in the " + state.Name + " hand: " + drawn.name);
                }
                return;
            }
            bool inPile = grab.Back != null && Contains(grab.Pile, grab.Back);
            if (Time.unscaledTime - grab.CommitTime > PileCommitTimeout && grab.Incoming == null)
            {
                if (inPile) PutBack(state, "drawn card never reached the hand");
                else
                {
                    HandOffBack(state, grab, grab.Back != null ? grab.Back.rotation : handRotation);
                    _logger.LogInfo("Drawn card did not reach the hand in time; it will arrive the normal way.");
                }
                return;
            }
            if (holding)
            {
                if (grab.Incoming == null && !inPile && row != null)
                {
                    var before = new HashSet<PlayableCard>(grab.HandBefore);
                    grab.Incoming = row.FindIncoming(hand, before);
                    if (grab.Incoming != null) _logger.LogInfo("Drawn card " + grab.Incoming.name + " takes the place of the pile card in the " + state.Name + " hand.");
                }
                if (grab.Incoming != null && grab.Incoming.gameObject.activeInHierarchy)
                {
                    HideBack(grab);
                    Transform t = grab.Incoming.transform;
                    Tween.Stop(t.GetInstanceID());
                    t.SetPositionAndRotation(handPosition, DrawnRotation(grab, handRotation));
                    if (row != null)
                    {
                        row.SetNaturalScale(t);
                        row.Remember(grab.Incoming);
                    }
                    return;
                }
                grab.Incoming = null;
                if (grab.Back == null) return;
                grab.Back.gameObject.SetActive(true);
                PoseBack(state, grab);
                return;
            }
            if (grab.Incoming != null)
            {
                grab.Incoming = null;
                HideBack(grab);
                state.Pile = null;
                return;
            }
            if (grab.Back == null)
            {
                state.Pile = null;
                return;
            }
            if (inPile)
            {
                Tween.Stop(grab.Back.GetInstanceID());
                grab.Back.gameObject.SetActive(true);
                return;
            }
            HandOffBack(state, grab, grab.Back.rotation);
        }

        private void HandOffBack(VrHand state, PileGrab grab, Quaternion backRotation)
        {
            state.Pile = null;
            grab.Incoming = null;
            if (grab.Back == null) return;
            CardRow row = _grabber.Row;
            if (row == null || !grab.Back.gameObject.activeSelf)
            {
                HideBack(grab);
                return;
            }
            row.AddDrawOrigin(grab.Back, grab.Back.position, grab.Back.rotation, DrawnRotation(grab, backRotation));
        }

        private Quaternion DrawnRotation(PileGrab grab, Quaternion backRotation)
        {
            Quaternion faceDown = _grabber.FaceDownRotation;
            if (!_grabber.HaveTable || grab.Back == null) return _grabber.HaveTable ? faceDown : backRotation;
            Transform parent = grab.Back.parent;
            Quaternion rest = parent != null ? parent.rotation * grab.RestLocalRotation : grab.RestLocalRotation;
            return backRotation * Quaternion.Inverse(rest) * faceDown;
        }

        internal void PutBack(VrHand state, string reason)
        {
            PileGrab grab = state.Pile;
            state.Pile = null;
            if (grab == null || grab.Back == null) return;
            Tween.Stop(grab.Back.GetInstanceID());
            if (grab.Pile == null || !Contains(grab.Pile, grab.Back))
            {
                grab.Back.gameObject.SetActive(false);
                return;
            }
            grab.Back.gameObject.SetActive(true);
            Tween.LocalPosition(grab.Back, grab.RestLocalPosition, 0.15f, 0f, Tween.EaseOut);
            Tween.LocalRotation(grab.Back, grab.RestLocalRotation.eulerAngles, 0.15f, 0f, Tween.EaseOut);
            if (reason != null) _logger.LogInfo("Pile card put back (" + reason + ").");
        }

        private static void HideBack(PileGrab grab)
        {
            if (grab.Back == null) return;
            Tween.Stop(grab.Back.GetInstanceID());
            grab.Back.gameObject.SetActive(false);
        }

        private static void PoseBack(VrHand state, PileGrab grab)
        {
            Tween.Stop(grab.Back.GetInstanceID());
            grab.Back.SetPositionAndRotation(state.Position + state.Rotation * grab.LocalPosition, state.Rotation * grab.LocalRotation);
        }

        private static PlayableCard FindNewCard(PileGrab grab, PlayerHand hand)
        {
            List<PlayableCard> cards = hand.CardsInHand;
            for (int i = 0; i < cards.Count; i++)
                if (cards[i] != null && !grab.HandBefore.Contains(cards[i])) return cards[i];
            return null;
        }

        private static List<Transform> PileCards(CardPile pile)
        {
            return PileCardsField != null && pile != null ? PileCardsField.GetValue(pile) as List<Transform> : null;
        }

        internal static Transform TopBack(CardPile pile)
        {
            List<Transform> cards = PileCards(pile);
            return cards != null && cards.Count > 0 ? cards[cards.Count - 1] : null;
        }

        private static bool Contains(CardPile pile, Transform back)
        {
            List<Transform> cards = PileCards(pile);
            return cards != null && cards.Contains(back);
        }

        private static float PileDistance(CardPile pile, Transform back, Vector3 point)
        {
            float best = (back.position - point).sqrMagnitude;
            Renderer[] renderers = back.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null && renderers[i].enabled) best = Mathf.Min(best, renderers[i].bounds.SqrDistance(point));
            Collider[] colliders = pile.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null && colliders[i].enabled) best = Mathf.Min(best, colliders[i].bounds.SqrDistance(point));
            return best;
        }

        private static string PileName(CardPile pile)
        {
            CardDrawPiles3D piles = CardDrawPiles3D.Instance;
            return piles != null && pile == piles.SidePile ? "squirrel pile" : "deck";
        }
    }
}
