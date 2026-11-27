using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;
using UnityEngine.UI;

namespace InscryptionVR
{
    internal sealed class CardInspector
    {
        private const float ShowMeters = 0.3f;
        private const float HideMeters = 0.38f;
        private const float PanelWidthMeters = 0.17f;
        private const float GapMeters = 0.015f;
        private const float PixelsWide = 600f;
        private const float PixelsTall = 560f;
        private const float RefreshSeconds = 1f;
        private static readonly Regex Tags = new Regex(@"\[[a-zA-Z]{1,2}:[^\]]*\]");

        private readonly ManualLogSource _logger;
        private readonly List<Ability> _abilities = new List<Ability>();
        private GameObject _panel;
        private Material _material;
        private Text _title;
        private Text _body;
        private PlayableCard _shown;
        private float _nextRefresh;
        private bool _failed;

        internal CardInspector(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal void Tick(StereoRenderer renderer, VrHand left, VrHand right, PlayableCard offered)
        {
            if (_failed) return;
            try { Refresh(renderer, left, right, offered); }
            catch (Exception exception)
            {
                _failed = true;
                Hide();
                _logger.LogWarning("Card inspection disabled: " + exception);
            }
        }

        internal void Hide()
        {
            _shown = null;
            if (_panel != null) _panel.SetActive(false);
        }

        internal void Destroy()
        {
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            _panel = null;
            _material = null;
        }

        private void Refresh(StereoRenderer renderer, VrHand left, VrHand right, PlayableCard offered)
        {
            Vector3 head;
            Quaternion rotation;
            float unitsPerMeter;
            if (renderer == null || !renderer.TryGetHeadPose(out head, out rotation, out unitsPerMeter)) { Hide(); return; }
            Bounds bounds = new Bounds();
            PlayableCard card = null;
            float best = float.MaxValue;
            Consider(HeldCard(left), head, rotation, unitsPerMeter, ref card, ref bounds, ref best);
            Consider(HeldCard(right), head, rotation, unitsPerMeter, ref card, ref bounds, ref best);
            Consider(offered, head, rotation, unitsPerMeter, ref card, ref bounds, ref best);
            if (card == null) { Hide(); return; }
            Show(card, bounds, head, rotation, unitsPerMeter);
        }

        private static PlayableCard HeldCard(VrHand hand)
        {
            CardGrabber.CardBody body = hand.Held;
            return body != null && body.Mode == CardGrabber.BodyMode.Held ? body.Card : hand.Lifted as PlayableCard;
        }

        private void Consider(PlayableCard candidate, Vector3 head, Quaternion rotation, float unitsPerMeter, ref PlayableCard card, ref Bounds bounds, ref float best)
        {
            if (candidate == null || !candidate.gameObject.activeInHierarchy || candidate.Info == null) return;
            Bounds candidateBounds;
            if (!HandReach.CardBounds(candidate, out candidateBounds)) candidateBounds = new Bounds(candidate.transform.position, Vector3.zero);
            Vector3 toCard = candidateBounds.center - head;
            if (Vector3.Dot(rotation * Vector3.forward, toCard) <= 0f) return;
            float meters = toCard.magnitude / unitsPerMeter;
            float limit = candidate == _shown ? HideMeters : ShowMeters;
            if (meters > limit || meters >= best) return;
            best = meters;
            card = candidate;
            bounds = candidateBounds;
        }

        private void Show(PlayableCard card, Bounds bounds, Vector3 head, Quaternion rotation, float unitsPerMeter)
        {
            if (_panel == null) CreatePanel();
            if (card != _shown || Time.unscaledTime >= _nextRefresh)
            {
                if (card != _shown) _logger.LogInfo("Inspecting " + card.name + " up close.");
                _shown = card;
                _nextRefresh = Time.unscaledTime + RefreshSeconds;
                _title.text = card.Info.DisplayedNameLocalized;
                _body.text = Describe(card);
            }
            Vector3 right = rotation * Vector3.right;
            Vector3 extents = bounds.extents;
            float cardHalf = Mathf.Abs(right.x) * extents.x + Mathf.Abs(right.y) * extents.y + Mathf.Abs(right.z) * extents.z;
            float side = Vector3.Dot(bounds.center - head, right) > 0f ? -1f : 1f;
            Vector3 center = bounds.center + right * side * (cardHalf + (PanelWidthMeters * 0.5f + GapMeters) * unitsPerMeter);
            Transform panel = _panel.transform;
            panel.position = center;
            panel.rotation = Quaternion.LookRotation(center - head, rotation * Vector3.up);
            panel.localScale = Vector3.one * (PanelWidthMeters * unitsPerMeter / PixelsWide);
            if (!_panel.activeSelf)
            {
                _panel.SetActive(true);
                Canvas.ForceUpdateCanvases();
            }
        }

        private string Describe(PlayableCard card)
        {
            CardInfo info = card.Info;
            var text = new StringBuilder();
            text.Append(Cost(info)).Append("     ").Append("Power ").Append(card.Attack).Append("   Health ").Append(card.Health);
            string creature = info.DisplayedNameLocalized;
            if (info.SpecialStatIcon != SpecialStatIcon.None)
            {
                StatIconInfo stat = StatIconInfo.GetIconInfo(info.SpecialStatIcon);
                if (stat != null) Entry(text, Localization.Translate(stat.rulebookName), Localization.Translate(stat.rulebookDescription), creature);
            }
            CollectAbilities(card);
            for (int i = 0; i < _abilities.Count; i++)
            {
                AbilityInfo ability = AbilitiesUtil.GetInfo(_abilities[i]);
                if (ability == null) continue;
                Entry(text, Localization.Translate(ability.rulebookName), ability.LocalizedRulebookDescription, creature);
            }
            if (_abilities.Count == 0 && info.SpecialStatIcon == SpecialStatIcon.None) text.Append("\n\nNo sigils.");
            return text.ToString();
        }

        private static string Cost(CardInfo info)
        {
            var parts = new List<string>();
            if (info.BloodCost > 0) parts.Add(info.BloodCost + " blood");
            if (info.BonesCost > 0) parts.Add(info.BonesCost + " bones");
            if (info.EnergyCost > 0) parts.Add(info.EnergyCost + " energy");
            if (info.GemsCost != null && info.GemsCost.Count > 0) parts.Add(info.GemsCost.Count + " gems");
            return parts.Count == 0 ? "Free" : "Cost " + string.Join(", ", parts.ToArray());
        }

        private static void Entry(StringBuilder text, string name, string description, string creature)
        {
            text.Append("\n\n<b>").Append(Clean(name, creature)).Append("</b>");
            string body = Clean(description, creature);
            if (body.Length > 0) text.Append("\n").Append(body);
        }

        private static string Clean(string value, string creature)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return Tags.Replace(value.Replace("[creature]", creature), "").Trim();
        }

        private void CollectAbilities(PlayableCard card)
        {
            _abilities.Clear();
            AddAbilities(card.Info.Abilities);
            List<CardModificationInfo> temporary = card.TemporaryMods;
            if (temporary != null)
                for (int i = 0; i < temporary.Count; i++)
                    if (temporary[i] != null) AddAbilities(temporary[i].abilities);
            List<Ability> hidden = card.Status != null ? card.Status.hiddenAbilities : null;
            if (hidden != null) _abilities.RemoveAll(hidden.Contains);
        }

        private void AddAbilities(List<Ability> abilities)
        {
            if (abilities == null) return;
            for (int i = 0; i < abilities.Count; i++)
                if (!_abilities.Contains(abilities[i])) _abilities.Add(abilities[i]);
        }

        private void CreatePanel()
        {
            Shader shader = Shader.Find("UI/Default");
            if (shader == null) throw new InvalidOperationException("Unity UI shader unavailable");
            _material = new Material(shader);
            _material.name = "InscryptionVR Card Inspector Material";
            _material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            _panel = new GameObject("InscryptionVR Card Inspector", typeof(RectTransform), typeof(Canvas));
            _panel.layer = VrDialogue.SubtitleLayer;
            UnityEngine.Object.DontDestroyOnLoad(_panel);
            ((RectTransform)_panel.transform).sizeDelta = new Vector2(PixelsWide, PixelsTall);
            Canvas canvas = _panel.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 31000;
            GameObject background = Child("Background");
            RectTransform backgroundRect = (RectTransform)background.transform;
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;
            Image image = background.AddComponent<Image>();
            image.color = new Color(0.03f, 0.025f, 0.02f, 0.9f);
            image.raycastTarget = false;
            image.material = _material;
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _title = MakeText("Title", font, new Vector2(0f, PixelsTall * 0.5f - 45f), new Vector2(PixelsWide - 40f, 70f), 36, 20, TextAnchor.MiddleCenter);
            _title.fontStyle = FontStyle.Bold;
            _title.color = new Color(1f, 0.93f, 0.78f, 1f);
            _body = MakeText("Body", font, new Vector2(0f, -40f), new Vector2(PixelsWide - 48f, PixelsTall - 110f), 28, 14, TextAnchor.UpperLeft);
            _body.supportRichText = true;
            _panel.SetActive(false);
        }

        private GameObject Child(string name)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.layer = VrDialogue.SubtitleLayer;
            child.transform.SetParent(_panel.transform, false);
            return child;
        }

        private Text MakeText(string name, Font font, Vector2 position, Vector2 size, int maxSize, int minSize, TextAnchor anchor)
        {
            GameObject child = Child(name);
            RectTransform rectangle = (RectTransform)child.transform;
            rectangle.anchoredPosition = position;
            rectangle.sizeDelta = size;
            Text text = child.AddComponent<Text>();
            text.font = font;
            text.fontSize = maxSize;
            text.alignment = anchor;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = minSize;
            text.resizeTextMaxSize = maxSize;
            text.raycastTarget = false;
            text.material = _material;
            return text;
        }
    }
}
