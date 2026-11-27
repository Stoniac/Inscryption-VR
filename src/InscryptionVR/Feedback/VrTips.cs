using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace InscryptionVR
{
    public sealed class VrTips : MonoBehaviour
    {
        private const float PanelWidthMeters = 0.2f;
        private const float PixelsWide = 640f;
        private const float PixelsTall = 150f;
        private const float RiseMeters = 0.09f;
        private const float FadeSeconds = 0.25f;
        private const float RetireSeconds = 25f;
        private const float PointerPixels = 6f;

        private sealed class Offer
        {
            internal string Id;
            internal string Text;
            internal Vector3 Anchor;
            internal int Priority;
            internal int Frame;
        }

        private static VrTips _instance;

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private ConfigEntry<string> _seenEntry;
        private bool _enabled;
        private bool _failed;
        private readonly HashSet<string> _seen = new HashSet<string>();
        private readonly Dictionary<string, Offer> _offers = new Dictionary<string, Offer>();
        private readonly Dictionary<string, float> _visibleTime = new Dictionary<string, float>();
        private readonly List<string> _stale = new List<string>();

        private GameObject _panel;
        private Material _material;
        private CanvasGroup _group;
        private Text _text;
        private RectTransform _pointer;
        private Offer _shown;
        private float _alpha;

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, bool enabled, ConfigEntry<string> seen)
        {
            _logger = logger;
            _renderer = renderer;
            _enabled = enabled;
            _seenEntry = seen;
            _instance = this;
            string[] ids = (seen.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < ids.Length; i++) _seen.Add(ids[i].Trim());
            _logger.LogInfo("First-run tips " + (enabled ? "on" : "off") + " (" + _seen.Count + " already seen).");
        }

        public void SetEnabled(bool enabled) { _enabled = enabled; }

        public void ResetSeen()
        {
            _seen.Clear();
            _visibleTime.Clear();
            _seenEntry.Value = "";
            _logger.LogInfo("First-run tips reset; each one shows again the next time it comes up.");
        }

        internal static void Show(string id, Vector3 anchor, string text, int priority)
        {
            VrTips tips = _instance;
            if (tips == null || !tips._enabled || tips._failed || tips._seen.Contains(id)) return;
            Offer offer;
            if (!tips._offers.TryGetValue(id, out offer))
            {
                offer = new Offer();
                offer.Id = id;
                tips._offers[id] = offer;
            }
            offer.Text = text;
            offer.Anchor = anchor;
            offer.Priority = priority;
            offer.Frame = Time.frameCount;
        }

        internal static bool Seen(string id)
        {
            VrTips tips = _instance;
            return tips == null || !tips._enabled || tips._seen.Contains(id);
        }

        internal static void Done(string id)
        {
            VrTips tips = _instance;
            if (tips == null) return;
            tips.MarkSeen(id, "done");
        }

        private void MarkSeen(string id, string why)
        {
            if (!_seen.Add(id)) return;
            _offers.Remove(id);
            _seenEntry.Value = string.Join(",", new List<string>(_seen).ToArray());
            _logger.LogInfo("Tip '" + id + "' retired (" + why + ").");
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                if (_panel != null) _panel.SetActive(false);
                _logger.LogWarning("First-run tips disabled after an error: " + exception);
            }
        }

        private void Tick()
        {
            Vector3 head = Vector3.zero;
            Quaternion headRotation = Quaternion.identity;
            float unitsPerMeter = 1f;
            bool active = _enabled && VrScenes.InVrScene && Time.timeScale > 0f &&
                _renderer.TryGetHeadPose(out head, out headRotation, out unitsPerMeter);
            Prune();
            Offer best = null;
            if (active)
            {
                if (_shown != null && Live(_shown)) best = _shown;
                else
                    foreach (Offer offer in _offers.Values)
                        if (Live(offer) && (best == null || offer.Priority > best.Priority)) best = offer;
            }
            float dt = Time.unscaledDeltaTime;
            if (best != _shown)
            {
                _alpha = Mathf.MoveTowards(_alpha, 0f, dt / FadeSeconds);
                if (_alpha <= 0f) _shown = best;
            }
            else if (_shown != null) _alpha = Mathf.MoveTowards(_alpha, 1f, dt / FadeSeconds);
            if (_shown == null || !active)
            {
                if (_panel != null && _panel.activeSelf && (!active || _alpha <= 0f)) _panel.SetActive(false);
                if (!active) _alpha = 0f;
                return;
            }
            float visible;
            _visibleTime.TryGetValue(_shown.Id, out visible);
            visible += dt;
            _visibleTime[_shown.Id] = visible;
            if (visible >= RetireSeconds) MarkSeen(_shown.Id, "shown long enough");
            Place(_shown, head, headRotation, unitsPerMeter);
        }

        private static bool Live(Offer offer)
        {
            return Time.frameCount - offer.Frame <= 2;
        }

        private void Prune()
        {
            _stale.Clear();
            foreach (KeyValuePair<string, Offer> pair in _offers)
                if (Time.frameCount - pair.Value.Frame > 120 && pair.Value != _shown) _stale.Add(pair.Key);
            for (int i = 0; i < _stale.Count; i++) _offers.Remove(_stale[i]);
        }

        private void Place(Offer offer, Vector3 head, Quaternion headRotation, float unitsPerMeter)
        {
            if (_panel == null) CreatePanel();
            if (_text.text != offer.Text) _text.text = offer.Text;
            Vector3 center = offer.Anchor + Vector3.up * (RiseMeters * unitsPerMeter);
            Transform panel = _panel.transform;
            Vector3 toPanel = center - head;
            if (toPanel.sqrMagnitude < 1e-6f) toPanel = headRotation * Vector3.forward;
            panel.position = center;
            panel.rotation = Quaternion.LookRotation(toPanel, Vector3.up);
            float metersPerPixel = PanelWidthMeters / PixelsWide;
            panel.localScale = Vector3.one * (metersPerPixel * unitsPerMeter);
            Vector3 local = panel.InverseTransformPoint(offer.Anchor);
            Vector2 start = new Vector2(0f, -PixelsTall * 0.5f);
            Vector2 delta = new Vector2(local.x, local.y) - start;
            float length = Mathf.Max(0f, delta.magnitude - 8f);
            _pointer.anchoredPosition = start;
            _pointer.sizeDelta = new Vector2(PointerPixels, length);
            _pointer.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.x, -delta.y) * Mathf.Rad2Deg);
            _group.alpha = Mathf.SmoothStep(0f, 1f, _alpha);
            if (!_panel.activeSelf)
            {
                _panel.SetActive(true);
                Canvas.ForceUpdateCanvases();
            }
        }

        private void CreatePanel()
        {
            Shader shader = Shader.Find("UI/Default");
            if (shader == null) throw new InvalidOperationException("Unity UI shader unavailable");
            _material = new Material(shader);
            _material.name = "InscryptionVR Tip Material";
            _material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            _panel = new GameObject("InscryptionVR Tip", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
            _panel.layer = VrDialogue.SubtitleLayer;
            DontDestroyOnLoad(_panel);
            ((RectTransform)_panel.transform).sizeDelta = new Vector2(PixelsWide, PixelsTall);
            Canvas canvas = _panel.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 30500;
            _group = _panel.GetComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;

            GameObject pointer = Child("Pointer");
            _pointer = (RectTransform)pointer.transform;
            _pointer.pivot = new Vector2(0.5f, 1f);
            Image line = pointer.AddComponent<Image>();
            line.color = new Color(1f, 0.86f, 0.55f, 0.85f);
            line.raycastTarget = false;
            line.material = _material;

            GameObject background = Child("Background");
            RectTransform backgroundRect = (RectTransform)background.transform;
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;
            Image image = background.AddComponent<Image>();
            image.color = new Color(0.04f, 0.03f, 0.02f, 0.88f);
            image.raycastTarget = false;
            image.material = _material;

            GameObject edge = Child("Edge");
            RectTransform edgeRect = (RectTransform)edge.transform;
            edgeRect.anchorMin = new Vector2(0f, 0f);
            edgeRect.anchorMax = new Vector2(0f, 1f);
            edgeRect.pivot = new Vector2(0f, 0.5f);
            edgeRect.sizeDelta = new Vector2(8f, 0f);
            Image accent = edge.AddComponent<Image>();
            accent.color = new Color(1f, 0.78f, 0.4f, 0.95f);
            accent.raycastTarget = false;
            accent.material = _material;

            GameObject label = Child("Text");
            RectTransform labelRect = (RectTransform)label.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(30f, 12f);
            labelRect.offsetMax = new Vector2(-22f, -12f);
            _text = label.AddComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _text.fontSize = 40;
            _text.alignment = TextAnchor.MiddleLeft;
            _text.color = new Color(1f, 0.94f, 0.82f, 1f);
            _text.horizontalOverflow = HorizontalWrapMode.Wrap;
            _text.verticalOverflow = VerticalWrapMode.Truncate;
            _text.resizeTextForBestFit = true;
            _text.resizeTextMinSize = 20;
            _text.resizeTextMaxSize = 40;
            _text.raycastTarget = false;
            _text.material = _material;
            _panel.SetActive(false);
        }

        private GameObject Child(string name)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.layer = VrDialogue.SubtitleLayer;
            child.transform.SetParent(_panel.transform, false);
            return child;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_panel != null) Destroy(_panel);
            if (_material != null) Destroy(_material);
        }
    }
}
