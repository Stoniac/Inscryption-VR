using BepInEx.Logging;
using DiskCardGame;
using Pixelplacement;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class SacrificeFlow
    {
        private const float PullSeconds = 0.16f;

        private readonly CardGrabber _grabber;
        private readonly ManualLogSource _logger;
        private readonly SacrificeKnife _knife;

        private PlayableCard _offeredCard;
        private Vector3 _offeredLocalPosition;
        private Quaternion _offeredLocalRotation;
        private Vector3 _offeredStartPosition;
        private Quaternion _offeredStartRotation;
        private float _offeredProgress;

        private PlayableCard _card;
        private VrHand _cardHand;
        private VrHand _knifeHand;
        private VrHand _offeredHand;
        private float _startTime;
        private bool _selected;
        private bool _placed;
        private bool _grip;
        private PlayableCard _refused;

        internal SacrificeFlow(CardGrabber grabber, ManualLogSource logger, SacrificeKnife knife)
        {
            _grabber = grabber;
            _logger = logger;
            _knife = knife;
        }

        internal bool Active { get { return _card != null; } }

        internal PlayableCard Offered { get { return _offeredCard; } }

        internal bool Owns(PlayableCard card) { return card != null && (card == _card || card == _offeredCard); }

        internal void Forget()
        {
            _offeredCard = null;
            _offeredHand = null;
            _card = null;
            _cardHand = null;
            _knifeHand = null;
            _selected = false;
            _placed = false;
            _grip = false;
            _refused = null;
        }

        internal static bool KnifeTurn(BoardManager board)
        {
            TurnManager turn = Singleton<TurnManager>.Instance;
            if (turn == null || turn.IsSetupPhase || turn.GameEnding || turn.GameEnded) return false;
            return turn.IsMainOrDrawPhaseNotUpkeep || board.ChoosingSacrifices || board.ChoosingSlot;
        }

        internal bool HoldsBloodCard(VrHand state)
        {
            PlayableCard card = CardGrabber.HeldCard(state);
            return _card == null && card != null && card.Info != null && card.Info.BloodCost > 0;
        }

        internal VrHand CardHand { get { return _card != null ? _cardHand : null; } }

        private VrHand CardHandFor(VrHand main, VrHand off)
        {
            if (_card != null && _cardHand != null) return _cardHand;
            return _knife != null && _knife.Holder == main ? off : main;
        }

        internal void Update(VrHand main, VrHand off, PlayerHand hand, BoardManager board, bool battle, float scale)
        {
            if (_knife == null) return;
            if (_card != null)
            {
                Track(_cardHand, _knifeHand, hand, board, battle, scale);
                return;
            }
            VrHand knifeHand = _knife.Holder;
            VrHand cardHand = knifeHand == main ? off : main;
            PlayableCard card = knifeHand != null && HoldsBloodCard(cardHand) ? CardGrabber.HeldCard(cardHand) : null;
            if (card == null)
            {
                _refused = null;
                return;
            }
            off = knifeHand;
            main = cardHand;
            if (card == _refused || !battle || !main.Ready || !_grabber.CanHold(hand, board)) return;
            TurnManager turn = Singleton<TurnManager>.Instance;
            if (turn == null || !turn.IsPlayerMainPhase || hand.PlayingLocked) return;
            if (!card.CanPlay())
            {
                _refused = card;
                if (card.Anim != null) card.Anim.LightNegationEffect();
                main.Play(Feel.Reject);
                _logger.LogInfo("Not enough blood on the board to sacrifice for " + card.name + ".");
                return;
            }

            _grabber.DetachHeld(main);
            _grabber.HidePreviews();
            _card = card;
            _cardHand = main;
            _knifeHand = off;
            _startTime = Time.unscaledTime;
            _selected = false;
            _placed = false;
            _grip = true;
            card.CursorSelectStart();
            card.CursorSelectEnd();
            off.Play(Feel.Tap, 0.6f);
            VrTips.Done("knife.grab");
            _logger.LogInfo("Card in the " + main.Name + " hand and knife in the " + off.Name + ": sacrificing for " + card.name + ".");
        }

        private void Track(VrHand main, VrHand off, PlayerHand hand, BoardManager board, bool battle, float scale)
        {
            PlayableCard card = _card;
            if (!_selected)
            {
                if (hand.ChoosingSlotCard == card) _selected = true;
                else
                {
                    if (Time.unscaledTime - _startTime > 2f)
                    {
                        _logger.LogInfo("The game did not begin the sacrifice for " + (card != null ? card.name : "the card") + ".");
                        End(main, hand, scale);
                    }
                    return;
                }
            }
            if (card == null || hand.ChoosingSlotCard != card)
            {
                End(main, hand, scale);
                return;
            }
            if (_placed || !battle) return;
            if (board.ChoosingSacrifices)
            {
                if (board.CurrentSacrificeDemandingCard != card) return;
                if (!main.Grip.Held) _knife.Cancel(board, main, "card released");
                else if (!_knife.Holding) _knife.Cancel(board, off, "knife put away");
                _grip = main.Grip.Held;
                return;
            }
            if (!board.ChoosingSlot || !main.Ready) return;
            bool grip = main.Grip.Held;
            if (_grip && !grip) TryPlace(card, main);
            _grip = grip;
            if (!_placed && _grabber.HaveTable)
                VrTips.Show("knife.place", _grabber.PlayerRowCenter + Vector3.up * (0.02f * _grabber.Spacing), "Let go of the card over an empty lane to play it.", 85);
        }

        private void TryPlace(PlayableCard card, VrHand main)
        {
            CardSlot slot = _grabber.FindDropSlot(card.transform.position);
            if (slot != null && slot.Card == null && slot.Chooseable)
            {
                _placed = true;
                _grabber.PreviewOf(main).Hide();
                slot.CursorSelectStart();
                main.Play(Feel.Thud);
                VrTips.Done("knife.place");
                _logger.LogInfo("Sacrificed-for card released over lane " + slot.Index + "; playing it: " + card.name);
                return;
            }
            if (card.Anim != null) card.Anim.LightNegationEffect();
            main.Play(Feel.Reject);
            _logger.LogInfo("The blood is paid, so " + card.name + " must be played; it stays in the " + main.Name + " hand until it is released over an empty lane.");
        }

        private void End(VrHand main, PlayerHand hand, float scale)
        {
            PlayableCard card = _card;
            bool placed = _placed;
            _card = null;
            _selected = false;
            _placed = false;
            _grip = false;
            if (placed || card == null || !card.gameObject.activeInHierarchy || !hand.CardsInHand.Contains(card)) return;
            if (!main.Ready || !main.Grip.Held || main.Busy) return;
            _refused = card;
            Vector3 position = card.transform.position;
            Quaternion rotation = card.transform.rotation;
            if (_grabber.Row != null)
            {
                _grabber.Row.SetNaturalScale(card.transform);
                _grabber.Row.Recall(card, out position, out rotation);
            }
            _grabber.HoldCard(main, card, position, rotation);
            _logger.LogInfo("Sacrifice ended without playing " + card.name + "; it stays in the " + main.Name + " hand.");
        }

        internal void HoldOffered(VrHand main, VrHand off, BoardManager board, bool shown, float scale)
        {
            PlayableCard card = null;
            if (_card != null) card = _placed ? null : _card;
            else if (board.ChoosingSacrifices) card = board.CurrentSacrificeDemandingCard;
            if (card != null && !card.gameObject.activeInHierarchy) card = null;
            main = card != null && card == _offeredCard && _offeredHand != null ? _offeredHand : CardHandFor(main, off);
            if (!shown || !main.Tracked) card = null;
            CardRow row = _grabber.Row;
            if (card != _offeredCard)
            {
                _offeredCard = card;
                _offeredHand = card != null ? main : null;
                if (card == null) return;
                Tween.Stop(card.transform.GetInstanceID());
                if (row != null)
                {
                    row.SetNaturalScale(card.transform);
                    row.Recall(card, out _offeredStartPosition, out _offeredStartRotation);
                }
                else
                {
                    _offeredStartPosition = card.transform.position;
                    _offeredStartRotation = card.transform.rotation;
                }
                _offeredProgress = 0f;
                CardGrabber.PinchPose(main, card, _offeredStartRotation, scale, out _offeredLocalPosition, out _offeredLocalRotation);
                main.Play(Feel.Tap, 0.6f);
                _logger.LogInfo("Card being paid for in blood is held in the " + main.Name + " hand: " + card.name);
            }
            if (card == null) return;
            main.Claimed = true;
            main.SetHoldPose(HandHold.Pinch);
            Tween.Stop(card.transform.GetInstanceID());
            Vector3 target = main.Position + main.Rotation * _offeredLocalPosition;
            Quaternion targetRotation = main.Rotation * _offeredLocalRotation;
            _offeredProgress = Mathf.Min(1f, _offeredProgress + Time.unscaledDeltaTime / PullSeconds);
            float k = Mathf.SmoothStep(0f, 1f, _offeredProgress);
            card.transform.SetPositionAndRotation(Vector3.Lerp(_offeredStartPosition, target, k), Quaternion.Slerp(_offeredStartRotation, targetRotation, k));
            if (row != null)
            {
                row.SetNaturalScale(card.transform);
                row.Remember(card);
            }
        }

        internal bool Preview(PlayerHand hand, BoardManager board, LanePreview preview, bool enabled, bool glow)
        {
            if (_card == null) return false;
            PlayableCard card = _offeredCard;
            bool placing = enabled && card != null && card == _card && !_placed && board.ChoosingSlot && hand.ChoosingSlotCard == card;
            CardSlot slot = placing ? _grabber.FindDropSlot(card.transform.position) : null;
            if (slot == null)
            {
                preview.Hide();
                return true;
            }
            Transform t = card.transform;
            preview.Show(_grabber, card, slot, slot.Card != null || !slot.Chooseable, t.position, t.rotation, Time.unscaledDeltaTime, glow);
            return true;
        }
    }
}
