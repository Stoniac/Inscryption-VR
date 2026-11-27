using System;
using System.Reflection;
using DiskCardGame;
using UnityEngine;
using UnityEngine.UI;

namespace InscryptionVR
{
    internal sealed class LanePreview
    {
        private const float PixelsWide = 300f;
        private const float OutlinePixels = 9f;
        private const float FadeSeconds = 0.12f;
        private static Texture2D _shadowTexture;
        private static readonly PropertyInfo MainTextureIdProperty = typeof(RenderStatsLayer).GetProperty("MainTextureId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo SlotStateField = typeof(HighlightedInteractable).GetField("currentState", BindingFlags.Instance | BindingFlags.NonPublic);
        internal const string Ghost = "Ghost";
        internal const string Glow = "Glow";
        internal const string Off = "Off";
        internal static readonly string[] Styles = { Ghost, Glow, Off };

        private readonly string _name;
        private GameObject _ghost;
        private GameObject _shadow;
        private Material _material;
        private CanvasGroup _ghostGroup;
        private CanvasGroup _shadowGroup;
        private RawImage _art;
        private RawImage _shadowImage;
        private GameObject _outline;
        private PlayableCard _artCard;
        private float _alpha;
        private bool _failed;
        private CardSlot _glowSlot;
        private HighlightedInteractable.State _glowRestore = HighlightedInteractable.State.Interactable;

        internal LanePreview(string name)
        {
            _name = name;
        }

        internal void Show(CardGrabber grabber, PlayableCard card, CardSlot slot, bool refused, Vector3 cardPosition, Quaternion cardRotation, float dt, bool glow)
        {
            if (_failed) return;
            try
            {
                Vector2 size;
                if (!CardSize(card, out size)) { Hide(); return; }
                if (_ghost == null) Create(grabber.TableLayer);
                _alpha = Mathf.MoveTowards(_alpha, 1f, dt / FadeSeconds);

                float lift = 0.0015f * grabber.Spacing;
                float floor = grabber.TableY + 0.004f * grabber.Spacing;
                Bounds occupant;
                if (slot.Card != null && HandReach.CardBounds(slot.Card, out occupant)) floor = Mathf.Max(floor, occupant.max.y);
                Vector3 toward = grabber.TowardPlayer;
                if (glow)
                {
                    if (_ghost.activeSelf) _ghost.SetActive(false);
                    if (refused) Unglow();
                    else LightSlot(slot);
                }
                else
                {
                    Unglow();
                    Vector3 center = slot.transform.position;
                    center.y = floor + lift * 2f;
                    Pose(_ghost.transform, center, Quaternion.LookRotation(Vector3.down, -toward), size);
                    SetArt(card);
                    if (_outline.activeSelf != refused) _outline.SetActive(refused);
                    _ghostGroup.alpha = _alpha;
                    if (!_ghost.activeSelf) _ghost.SetActive(true);
                }

                Vector3 under = new Vector3(cardPosition.x, floor + lift, cardPosition.z);
                float height = Mathf.Max(0f, cardPosition.y - under.y);
                float spread = 1f + Mathf.Clamp01(height / (1.5f * grabber.Spacing)) * 0.35f;
                Vector3 along = Vector3.ProjectOnPlane(cardRotation * LongAxis(card), Vector3.up);
                if (along.sqrMagnitude < 1e-6f) along = -toward;
                if (Vector3.Dot(along, -toward) < 0f) along = -along;
                Pose(_shadow.transform, under, Quaternion.LookRotation(Vector3.down, along.normalized), size * spread);
                _shadowGroup.alpha = _alpha * 0.5f * Mathf.Clamp01(1f - height / (2f * grabber.Spacing));
                if (!_shadow.activeSelf) _shadow.SetActive(true);
            }
            catch (Exception)
            {
                _failed = true;
                Destroy();
                throw;
            }
        }

        internal void Hide()
        {
            _alpha = 0f;
            _artCard = null;
            Unglow();
            if (_ghost != null && _ghost.activeSelf) _ghost.SetActive(false);
            if (_shadow != null && _shadow.activeSelf) _shadow.SetActive(false);
        }

        private void LightSlot(CardSlot slot)
        {
            if (_glowSlot != slot)
            {
                Unglow();
                _glowSlot = slot;
                HighlightedInteractable.State before = SlotState(slot);
                _glowRestore = before == HighlightedInteractable.State.Highlighted ? HighlightedInteractable.State.Interactable : before;
            }
            if (SlotState(slot) != HighlightedInteractable.State.Highlighted) slot.ShowState(HighlightedInteractable.State.Highlighted, false, 0.075f);
        }

        private void Unglow()
        {
            CardSlot slot = _glowSlot;
            _glowSlot = null;
            if (slot == null || SlotState(slot) != HighlightedInteractable.State.Highlighted) return;
            slot.ShowState(_glowRestore);
        }

        private static HighlightedInteractable.State SlotState(CardSlot slot)
        {
            if (SlotStateField == null) return HighlightedInteractable.State.Interactable;
            object value = SlotStateField.GetValue(slot);
            return value is HighlightedInteractable.State ? (HighlightedInteractable.State)value : HighlightedInteractable.State.Interactable;
        }

        internal void Destroy()
        {
            Unglow();
            if (_ghost != null) UnityEngine.Object.Destroy(_ghost);
            if (_shadow != null) UnityEngine.Object.Destroy(_shadow);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            _ghost = null;
            _shadow = null;
            _material = null;
        }

        private static void Pose(Transform target, Vector3 position, Quaternion rotation, Vector2 size)
        {
            target.SetPositionAndRotation(position, rotation);
            float scale = size.x / PixelsWide;
            target.localScale = Vector3.one * scale;
            ((RectTransform)target).sizeDelta = new Vector2(PixelsWide, PixelsWide * size.y / Mathf.Max(size.x, 1e-5f));
        }

        private void SetArt(PlayableCard card)
        {
            if (card == _artCard) return;
            _artCard = card;
            Texture texture = null;
            try
            {
                RenderStatsLayer layer = card.StatsLayer;
                Material material = layer != null ? layer.Material : null;
                if (material != null)
                {
                    string id = MainTextureIdProperty != null ? MainTextureIdProperty.GetValue(layer, null) as string : null;
                    texture = !string.IsNullOrEmpty(id) && material.HasProperty(id) ? material.GetTexture(id) : material.mainTexture;
                }
            }
            catch (Exception) { texture = null; }
            _art.texture = texture;
            _art.enabled = texture != null;
        }

        private static bool CardSize(PlayableCard card, out Vector2 size)
        {
            size = Vector2.zero;
            BoxCollider box = card != null ? card.GetComponent<BoxCollider>() : null;
            if (box == null) return false;
            Vector3 lossy = card.transform.lossyScale;
            Vector3 extent = new Vector3(Mathf.Abs(box.size.x * lossy.x), Mathf.Abs(box.size.y * lossy.y), Mathf.Abs(box.size.z * lossy.z));
            int thin = 0;
            for (int i = 1; i < 3; i++) if (extent[i] < extent[thin]) thin = i;
            float a = extent[(thin + 1) % 3], b = extent[(thin + 2) % 3];
            size = new Vector2(Mathf.Min(a, b), Mathf.Max(a, b));
            return size.x > 1e-5f;
        }

        private static Vector3 LongAxis(PlayableCard card)
        {
            BoxCollider box = card.GetComponent<BoxCollider>();
            Vector3 lossy = card.transform.lossyScale;
            Vector3 extent = new Vector3(Mathf.Abs(box.size.x * lossy.x), Mathf.Abs(box.size.y * lossy.y), Mathf.Abs(box.size.z * lossy.z));
            int longest = 0;
            for (int i = 1; i < 3; i++) if (extent[i] > extent[longest]) longest = i;
            Vector3 axis = Vector3.zero;
            axis[longest] = 1f;
            return axis;
        }

        private void Create(int layer)
        {
            Shader shader = Shader.Find("UI/Default");
            if (shader == null) throw new InvalidOperationException("Unity UI shader unavailable");
            _material = new Material(shader);
            _material.name = "InscryptionVR Lane Preview Material";
            _material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.LessEqual);

            _ghost = MakeCanvas("InscryptionVR " + _name + " Lane Ghost", layer, out _ghostGroup);
            Image paper = Fill(_ghost.transform, "Paper", layer).AddComponent<Image>();
            paper.color = new Color(0.93f, 0.87f, 0.72f, 0.3f);
            paper.raycastTarget = false;
            paper.material = _material;
            _art = Fill(_ghost.transform, "Art", layer).AddComponent<RawImage>();
            _art.color = new Color(1f, 1f, 1f, 0.55f);
            _art.raycastTarget = false;
            _art.material = _material;
            _outline = Fill(_ghost.transform, "Outline", layer);
            Edge(_outline.transform, layer, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, OutlinePixels));
            Edge(_outline.transform, layer, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, OutlinePixels));
            Edge(_outline.transform, layer, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(OutlinePixels, 0f));
            Edge(_outline.transform, layer, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(OutlinePixels, 0f));
            _outline.SetActive(false);

            _shadow = MakeCanvas("InscryptionVR " + _name + " Card Shadow", layer, out _shadowGroup);
            _shadowImage = Fill(_shadow.transform, "Shadow", layer).AddComponent<RawImage>();
            _shadowImage.texture = ShadowTexture();
            _shadowImage.color = Color.black;
            _shadowImage.raycastTarget = false;
            _shadowImage.material = _material;
            _ghost.SetActive(false);
            _shadow.SetActive(false);
        }

        private static GameObject MakeCanvas(string name, int layer, out CanvasGroup group)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
            root.layer = layer;
            UnityEngine.Object.DontDestroyOnLoad(root);
            Canvas canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            group = root.GetComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            return root;
        }

        private static GameObject Fill(Transform parent, string name, int layer)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.layer = layer;
            child.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)child.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return child;
        }

        private void Edge(Transform parent, int layer, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size)
        {
            var child = new GameObject("Edge", typeof(RectTransform));
            child.layer = layer;
            child.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)child.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            Image image = child.AddComponent<Image>();
            image.color = new Color(0.92f, 0.12f, 0.08f, 0.95f);
            image.raycastTarget = false;
            image.material = _material;
        }

        private static Texture2D ShadowTexture()
        {
            if (_shadowTexture != null) return _shadowTexture;
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.name = "InscryptionVR Card Shadow";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.HideAndDontSave;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = Mathf.Abs((x + 0.5f) / size * 2f - 1f);
                    float v = Mathf.Abs((y + 0.5f) / size * 2f - 1f);
                    float edge = Mathf.Max(Mathf.Pow(u, 6f), Mathf.Pow(v, 6f));
                    float alpha = Mathf.Clamp01(1f - edge) * Mathf.Clamp01((1f - Mathf.Max(u, v)) * 6f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            _shadowTexture = texture;
            return texture;
        }
    }
}
