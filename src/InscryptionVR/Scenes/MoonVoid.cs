using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace InscryptionVR
{
    public sealed class MoonVoid : MonoBehaviour
    {
        private const string MoonCardName = "!GIANTCARD_MOON";
        private const string MoonPrefabPath = "Prefabs/Environment/TableEffects/MoonTableEffects";
        private const string SlamClip = "map_slam";
        private const float SlamFallbackSeconds = 0.8f;
        private const float ScanSeconds = 0.5f;
        private const float DepthMeters = 150f;
        private const float AngularRadiusDegrees = 55f;
        private const float StarShellRatio = 0.95f;
        private const float FarClipSlack = 1.1f;
        private const int BackdropQueue = 1000;
        private const float KeepMarginMeters = 0.35f;
        private const float KeepAboveMeters = 0.9f;
        private const float KeepBelowMeters = 0.1f;
        private const float RootSlackMeters = 1.5f;
        private const float MaxTableMeters = 4f;
        private const float BoardFallbackMeters = 0.6f;
        private const float MoonCompanionRatio = 1.5f;
        private const float AnimatorWarmSeconds = 10f;
        private const float AnimatorWarmStep = 0.25f;
        private static readonly Color MoonLightColor = new Color(0.86f, 0.89f, 1f, 1f);
        private const float MoonLightIntensity = 1.1f;

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private bool _enabled;
        private bool _failed;
        private bool _active;
        private float _moonSeenAt = -1f;
        private readonly HashSet<int> _soundsBefore = new HashSet<int>();
        private float _nextScan;
        private GameObject _anchor;
        private int _moonLayer = -1;
        private bool _savedFog;
        private Scene _scene;
        private float _farthest;
        private MoonStars _stars;
        private Bounds _tight;
        private Bounds _slack;
        private readonly List<Transform> _keepRoots = new List<Transform>();
        private readonly List<Transform> _stageRoots = new List<Transform>();
        private readonly List<Plane[]> _offscreen = new List<Plane[]>();
        private readonly List<string> _offscreenNames = new List<string>();
        private Vector3 _tableCenter;
        private readonly HashSet<Renderer> _kept = new HashSet<Renderer>();
        private readonly HashSet<Renderer> _hidden = new HashSet<Renderer>();
        private readonly List<string> _boxKeptNames = new List<string>();

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, bool enabled)
        {
            _logger = logger;
            _renderer = renderer;
            _enabled = enabled;
            _stars = new MoonStars(renderer);
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) Restore("turned off");
        }

        private void Update()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception) { Fail(exception); }
        }

        private void Tick()
        {
            bool moon = _enabled && MoonOnBoard();
            if (_active)
            {
                if (!moon)
                {
                    Restore(_enabled ? "the moon left the table" : "turned off");
                    return;
                }
                if (Time.unscaledTime >= _nextScan)
                {
                    _nextScan = Time.unscaledTime + ScanSeconds;
                    HideCabin();
                }
                return;
            }
            if (!moon)
            {
                _moonSeenAt = -1f;
                return;
            }
            if (_moonSeenAt < 0f)
            {
                _moonSeenAt = Time.unscaledTime;
                SnapshotSounds();
                return;
            }
            bool slam = SlamHeard();
            if (slam || Time.unscaledTime - _moonSeenAt >= SlamFallbackSeconds) Activate(slam);
        }

        private static bool MoonOnBoard()
        {
            if (!VrScenes.InCabin) return false;
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            if (flow == null || flow.CurrentGameState != GameState.CardBattle) return false;
            TurnManager turns = Singleton<TurnManager>.Instance;
            if (turns == null || !(turns.Opponent is LeshyBossOpponent)) return false;
            BoardManager board = Singleton<BoardManager>.Instance;
            if (board == null) return false;
            foreach (CardSlot slot in board.OpponentSlotsCopy)
            {
                if (slot == null || slot.Card == null || slot.Card.Info == null) continue;
                if (slot.Card.Info.name == MoonCardName) return true;
            }
            return false;
        }

        private static List<AudioSource> Sounds() { return GameHooks.ActiveSfx(); }

        private void SnapshotSounds()
        {
            _soundsBefore.Clear();
            List<AudioSource> sounds = Sounds();
            if (sounds == null) return;
            foreach (AudioSource source in sounds)
                if (source != null) _soundsBefore.Add(source.GetInstanceID());
        }

        private bool SlamHeard()
        {
            List<AudioSource> sounds = Sounds();
            if (sounds == null) return false;
            foreach (AudioSource source in sounds)
            {
                if (source == null || source.clip == null || _soundsBefore.Contains(source.GetInstanceID())) continue;
                if (source.clip.name.StartsWith(SlamClip, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void Activate(bool slam)
        {
            _active = true;
            _scene = SceneManager.GetActiveScene();
            _nextScan = Time.unscaledTime + ScanSeconds;
            float upm = _renderer.UnitsPerMeter;
            Vector3 tableCenter;
            float tableTop;
            string boxSource = MeasureTable(upm, out tableCenter, out tableTop);
            _tableCenter = tableCenter;
            CollectKeepRoots();
            _kept.Clear();
            _hidden.Clear();
            _boxKeptNames.Clear();
            int hidden = HideCabin();
            _savedFog = RenderSettings.fog;
            RenderSettings.fog = false;
            string moonReport = BuildMoon(upm, tableCenter, tableTop);
            _renderer.MinFarClip = _farthest * FarClipSlack;
            string stars = "stars shown";
            try { _stars.Show(_farthest * StarShellRatio); }
            catch (Exception exception) { stars = "no stars (" + exception.Message + ")"; }
            _renderer.BlackBackground = true;
            _renderer.BackdropLayers = _moonLayer >= 0 ? 1 << _moonLayer : 0;
            _logger.LogInfo("Moon void: the cabin went dark as the moon card landed (" + (slam ? "on the slam" : "slam not heard, after " +
                SlamFallbackSeconds.ToString("0.0") + " s") + "). Hid " + hidden + " renderer(s), kept " + _kept.Count + "; table box from " + boxSource +
                " " + _tight.size.ToString("F2") + " at " + _tight.center.ToString("F2") + ", fog was " + (_savedFog ? "on" : "off") + ". " + moonReport +
                " " + stars + "." + " Offscreen stages kept: " + (_stageRoots.Count > 0 ? "card render stage" : "no card render stage") +
                (_offscreenNames.Count > 0 ? ", cameras " + string.Join(", ", _offscreenNames.ToArray()) : "") + "." +
                (_boxKeptNames.Count > 0 ? " Kept by the table box only: " + string.Join(", ", _boxKeptNames.ToArray()) + "." : ""));
        }

        private string MeasureTable(float upm, out Vector3 center, out float top)
        {
            BoardManager board = Singleton<BoardManager>.Instance;
            bool anySlot = false;
            Bounds slots = new Bounds();
            if (board != null)
            {
                foreach (CardSlot slot in board.PlayerSlotsCopy) Encapsulate(ref slots, ref anySlot, slot != null ? slot.transform.position : (Vector3?)null);
                foreach (CardSlot slot in board.OpponentSlotsCopy) Encapsulate(ref slots, ref anySlot, slot != null ? slot.transform.position : (Vector3?)null);
            }
            top = anySlot ? slots.center.y : 0f;
            TableVisualEffectsManager effects = Singleton<TableVisualEffectsManager>.Instance;
            Transform table = effects != null ? effects.Table : null;
            bool anyTable = false;
            Bounds tableBounds = new Bounds();
            if (table != null)
            {
                foreach (Renderer renderer in table.GetComponentsInChildren<Renderer>())
                {
                    if (!anyTable) tableBounds = renderer.bounds;
                    else tableBounds.Encapsulate(renderer.bounds);
                    anyTable = true;
                }
            }
            string source;
            Bounds box;
            float limit = MaxTableMeters * upm;
            if (anyTable && tableBounds.size.x <= limit && tableBounds.size.z <= limit)
            {
                box = tableBounds;
                if (anySlot) box.Encapsulate(slots);
                source = "'" + table.name + "'";
            }
            else if (anySlot)
            {
                box = slots;
                box.Expand(new Vector3(BoardFallbackMeters, 0f, BoardFallbackMeters) * 2f * upm);
                box.Encapsulate(new Vector3(box.center.x, top - 0.8f * upm, box.center.z));
                source = anyTable ? "the board ('" + table.name + "' was " + tableBounds.size.ToString("F1") + ", too big)" : "the board";
            }
            else
            {
                Camera source3 = _renderer.SourceCamera;
                Vector3 eye = source3 != null ? source3.transform.position : Vector3.zero;
                box = new Bounds(eye + Vector3.down * 0.3f * upm, new Vector3(1.5f, 1.2f, 1.5f) * upm);
                source = "the camera (no table or board found)";
            }
            if (!anySlot) top = box.max.y;
            Vector3 min = box.min;
            Vector3 max = box.max;
            float margin = KeepMarginMeters * upm;
            min.x -= margin;
            min.z -= margin;
            max.x += margin;
            max.z += margin;
            min.y -= KeepBelowMeters * upm;
            max.y = Mathf.Max(max.y, top + KeepAboveMeters * upm);
            _tight = new Bounds();
            _tight.SetMinMax(min, max);
            _slack = _tight;
            _slack.Expand(RootSlackMeters * 2f * upm);
            center = new Vector3(_tight.center.x, top, _tight.center.z);
            return source;
        }

        private static void Encapsulate(ref Bounds bounds, ref bool any, Vector3? point)
        {
            if (!point.HasValue) return;
            if (!any) bounds = new Bounds(point.Value, Vector3.zero);
            else bounds.Encapsulate(point.Value);
            any = true;
        }

        private void CollectKeepRoots()
        {
            _keepRoots.Clear();
            TableVisualEffectsManager effects = Singleton<TableVisualEffectsManager>.Instance;
            if (effects != null) AddRoot(effects.Table);
            AddRoot(Singleton<BoardManager>.Instance);
            AddRoot(Singleton<OpponentAnimationController>.Instance);
            AddRoot(Singleton<CandleHolder>.Instance);
            AddRoot(Singleton<CardDrawPiles>.Instance);
            LifeManager life = Singleton<LifeManager>.Instance;
            if (life != null) AddRoot(life.Scales);
            AddRoot(Singleton<ItemsManager>.Instance);
            AddRoot(Singleton<ResourcesManager>.Instance);
            AddRoot(Singleton<TableRuleBook>.Instance);
            AddRoot(Singleton<PlayerHand>.Instance);
        }

        private void AddRoot(Component component)
        {
            if (component != null) AddRoot(component.transform);
        }

        private void AddRoot(Transform transform)
        {
            if (transform != null && !_keepRoots.Contains(transform)) _keepRoots.Add(transform);
        }

        private void CollectStages(Vector3 tableCenter)
        {
            _stageRoots.Clear();
            CardRenderCamera cardCamera = Singleton<CardRenderCamera>.Instance;
            if (cardCamera != null)
            {
                _stageRoots.Add(cardCamera.transform);
                Transform parent = cardCamera.transform.parent;
                if (parent != null && !HoldsCabin(parent)) _stageRoots.Add(parent);
            }
            _offscreen.Clear();
            _offscreenNames.Clear();
            Scene active = SceneManager.GetActiveScene();
            Camera source = _renderer.SourceCamera;
            foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (camera == null || camera == source || camera.targetTexture == null || camera.gameObject.scene != active) continue;
                if (camera.transform.root.name.StartsWith("InscryptionVR", StringComparison.Ordinal)) continue;
                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
                if (GeometryUtility.TestPlanesAABB(planes, new Bounds(tableCenter, Vector3.one * 0.01f))) continue;
                _offscreen.Add(planes);
                if (_offscreenNames.Count < 8 && !_offscreenNames.Contains(camera.name)) _offscreenNames.Add(camera.name);
            }
        }

        private bool HoldsCabin(Transform candidate)
        {
            TableVisualEffectsManager effects = Singleton<TableVisualEffectsManager>.Instance;
            if (effects != null && effects.Table != null && effects.Table.IsChildOf(candidate)) return true;
            Camera source = _renderer.SourceCamera;
            if (source != null && source.transform.IsChildOf(candidate)) return true;
            BoardManager board = Singleton<BoardManager>.Instance;
            return board != null && board.transform.IsChildOf(candidate);
        }

        private int HideCabin()
        {
            CollectStages(_tableCenter);
            int count = 0;
            Scene active = SceneManager.GetActiveScene();
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (renderer == null) continue;
                if (_hidden.Contains(renderer))
                {
                    if (renderer.enabled) renderer.enabled = false;
                    continue;
                }
                if (!renderer.enabled || _kept.Contains(renderer)) continue;
                if (Keep(renderer, active))
                {
                    _kept.Add(renderer);
                    continue;
                }
                renderer.enabled = false;
                _hidden.Add(renderer);
                count++;
            }
            return count;
        }

        private bool Keep(Renderer renderer, Scene active)
        {
            GameObject owner = renderer.gameObject;
            if (owner.scene != active) return true;
            int layer = owner.layer;
            if (layer == HologramHands.Layer || layer == VrDialogue.SubtitleLayer || layer == 31 || layer == _moonLayer) return true;
            Transform root = renderer.transform.root;
            if (root.name.StartsWith("InscryptionVR", StringComparison.Ordinal)) return true;
            if (renderer.GetComponentInParent<Card>() != null) return true;
            foreach (Transform stage in _stageRoots)
                if (stage != null && renderer.transform.IsChildOf(stage)) return true;
            Camera source = _renderer.SourceCamera;
            if (source != null && (source.cullingMask & (1 << layer)) == 0) return true;
            Bounds bounds = renderer.bounds;
            foreach (Plane[] planes in _offscreen)
                if (GeometryUtility.TestPlanesAABB(planes, bounds)) return true;
            foreach (Transform keep in _keepRoots)
            {
                if (keep == null || !renderer.transform.IsChildOf(keep)) continue;
                if (_slack.Contains(bounds.center)) return true;
                break;
            }
            if (_tight.Contains(bounds.min) && _tight.Contains(bounds.max))
            {
                if (_boxKeptNames.Count < 16) _boxKeptNames.Add(renderer.name);
                return true;
            }
            return false;
        }

        private string BuildMoon(float upm, Vector3 tableCenter, float tableTop)
        {
            _moonLayer = FreeLayer();
            if (_anchor != null) Destroy(_anchor);
            _anchor = new GameObject("InscryptionVR Moon Void");
            _anchor.SetActive(false);
            GameObject prefab = null;
            try { prefab = ResourceBank.Get<GameObject>(MoonPrefabPath); }
            catch (Exception) { }
            if (prefab == null) prefab = Resources.Load<GameObject>(MoonPrefabPath);
            GameObject clone = null;
            string origin;
            if (prefab != null)
            {
                clone = (GameObject)Instantiate(prefab, _anchor.transform, false);
                clone.name = "Moon";
                Strip(clone);
                origin = "the game's '" + prefab.name + "'";
            }
            else
            {
                clone = FallbackMoon();
                clone.transform.SetParent(_anchor.transform, false);
                origin = "a fallback sphere ('" + MoonPrefabPath + "' did not load)";
            }
            SetLayer(clone.transform, _moonLayer >= 0 ? _moonLayer : clone.layer);
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            _anchor.SetActive(true);
            WarmAnimators(clone);
            string probe = Probe(clone.transform);
            Renderer moon = PickMoon(clone);
            if (moon == null)
            {
                Destroy(clone);
                clone = FallbackMoon();
                clone.transform.SetParent(_anchor.transform, false);
                SetLayer(clone.transform, _moonLayer >= 0 ? _moonLayer : clone.layer);
                moon = clone.GetComponent<Renderer>();
                origin += ", which had no moon renderer, so a fallback sphere";
            }
            int dropped = DropStrays(clone, moon);
            Bounds moonBounds = moon.bounds;
            Vector3 moonCenter = moonBounds.center;
            float moonRadius = Mathf.Max(0.001f, Mathf.Max(moonBounds.extents.x, Mathf.Max(moonBounds.extents.y, moonBounds.extents.z)));
            Vector3 front = Flat(tableCenter - moonCenter);
            if (front.sqrMagnitude < 1e-6f) front = Vector3.forward;
            front.Normalize();
            Quaternion face = Quaternion.FromToRotation(front, Vector3.up);
            Vector3 textureUp = Flat(face * Vector3.up);
            Vector3 away = PlayerForward(tableCenter);
            Quaternion twist = textureUp.sqrMagnitude > 1e-6f ? Quaternion.FromToRotation(textureUp.normalized, away) : Quaternion.identity;
            Quaternion rotation = twist * face;
            float depth = DepthMeters * upm;
            float radius = depth * Mathf.Tan(AngularRadiusDegrees * Mathf.Deg2Rad);
            float scale = radius / moonRadius;
            Vector3 target = new Vector3(tableCenter.x, tableTop - depth, tableCenter.z);
            _anchor.transform.localScale = Vector3.one * scale;
            _anchor.transform.rotation = rotation;
            _anchor.transform.position = target - rotation * (moonCenter * scale);
            _farthest = Farthest(clone, tableCenter, depth, radius);
            string queues = SendToBack(clone);
            AddLight(away);
            return "Moon: " + origin + ", moon renderer '" + moon.name + "' (" + ShaderName(moon) + ", radius " + moonRadius.ToString("F2") + ") faced " +
                front.ToString("F2") + " toward the table, now " + DepthMeters.ToString("0") + " m below with a " + (radius / upm).ToString("0") + " m radius (" +
                AngularRadiusDegrees.ToString("0") + " degrees, x" + scale.ToString("0") + "), far clip " + (_farthest * FarClipSlack / upm).ToString("0") + " m, layer " +
                _moonLayer + ", drawn as background (queues were " + queues + "), " + dropped + " unrelated part(s) hidden. Prefab tree:" + probe;
        }

        private static float Farthest(GameObject clone, Vector3 tableCenter, float depth, float radius)
        {
            float farthest = Mathf.Sqrt(depth * depth + 2f * radius * radius);
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled) continue;
                Bounds bounds = renderer.bounds;
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                    farthest = Mathf.Max(farthest, (corner - tableCenter).magnitude);
                }
            }
            return farthest;
        }

        private static string SendToBack(GameObject clone)
        {
            var seen = new List<string>();
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.materials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (material == null) continue;
                    string entry = material.renderQueue.ToString();
                    if (!seen.Contains(entry)) seen.Add(entry);
                    material.renderQueue = BackdropQueue;
                    if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
                }
                renderer.materials = materials;
            }
            return seen.Count > 0 ? string.Join("/", seen.ToArray()) : "none";
        }

        private static void Strip(GameObject clone)
        {
            foreach (MonoBehaviour behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                try { DestroyImmediate(behaviour); }
                catch (Exception) { }
            }
            foreach (AudioSource audio in clone.GetComponentsInChildren<AudioSource>(true))
            {
                try { DestroyImmediate(audio); }
                catch (Exception) { }
            }
            foreach (Rigidbody body in clone.GetComponentsInChildren<Rigidbody>(true))
            {
                try { DestroyImmediate(body); }
                catch (Exception) { }
            }
            foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
            {
                try { DestroyImmediate(collider); }
                catch (Exception) { }
            }
            foreach (Light light in clone.GetComponentsInChildren<Light>(true)) light.enabled = false;
            foreach (Animator animator in clone.GetComponentsInChildren<Animator>(true)) animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        private static void WarmAnimators(GameObject clone)
        {
            foreach (Animator animator in clone.GetComponentsInChildren<Animator>())
            {
                if (!animator.isActiveAndEnabled) continue;
                for (float t = 0f; t < AnimatorWarmSeconds; t += AnimatorWarmStep) animator.Update(AnimatorWarmStep);
            }
        }

        private static Renderer PickMoon(GameObject clone)
        {
            Renderer named = null;
            Renderer largest = null;
            float namedSize = -1f;
            float largestSize = -1f;
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>())
            {
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer) continue;
                float size = renderer.bounds.size.sqrMagnitude;
                if (size <= 0f) continue;
                if (renderer.name.IndexOf("moon", StringComparison.OrdinalIgnoreCase) >= 0 && size > namedSize)
                {
                    named = renderer;
                    namedSize = size;
                }
                if (size > largestSize)
                {
                    largest = renderer;
                    largestSize = size;
                }
            }
            return named != null ? named : largest;
        }

        private static int DropStrays(GameObject clone, Renderer moon)
        {
            Bounds moonBounds = moon.bounds;
            float reach = moonBounds.extents.magnitude * MoonCompanionRatio;
            float maxSize = moonBounds.size.magnitude * 2f;
            int dropped = 0;
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == moon || renderer.transform.IsChildOf(moon.transform) || moon.transform.IsChildOf(renderer.transform)) continue;
                if (renderer.name.IndexOf("moon", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Bounds bounds = renderer.bounds;
                if ((bounds.center - moonBounds.center).magnitude <= reach && bounds.size.magnitude <= maxSize) continue;
                renderer.enabled = false;
                dropped++;
            }
            return dropped;
        }

        private static GameObject FallbackMoon()
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Fallback Moon";
            Collider collider = sphere.GetComponent<Collider>();
            if (collider != null) DestroyImmediate(collider);
            Renderer renderer = sphere.GetComponent<Renderer>();
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Hidden/Internal-Colored");
            if (shader != null)
            {
                var material = new Material(shader);
                material.name = "InscryptionVR Fallback Moon";
                material.color = new Color(0.85f, 0.86f, 0.8f, 1f);
                renderer.sharedMaterial = material;
            }
            return sphere;
        }

        private void AddLight(Vector3 away)
        {
            if (_moonLayer < 0) return;
            var lightObject = new GameObject("Moon Light");
            lightObject.transform.SetParent(_anchor.transform, false);
            lightObject.transform.rotation = Quaternion.LookRotation(Vector3.down + away * 0.35f, away);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = MoonLightColor;
            light.intensity = MoonLightIntensity;
            light.shadows = LightShadows.None;
            light.cullingMask = 1 << _moonLayer;
            light.renderMode = LightRenderMode.ForcePixel;
        }

        private Vector3 PlayerForward(Vector3 tableCenter)
        {
            Camera source = _renderer.SourceCamera;
            Vector3 forward = source != null ? Flat(tableCenter - source.transform.position) : Vector3.zero;
            if (forward.sqrMagnitude < 1e-6f && source != null) forward = Flat(source.transform.forward);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            return forward.normalized;
        }

        private static int FreeLayer()
        {
            for (int layer = 28; layer >= 20; layer--)
            {
                if (layer == HologramHands.Layer || layer == VrDialogue.SubtitleLayer) continue;
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer))) return layer;
            }
            return -1;
        }

        private static void SetLayer(Transform node, int layer)
        {
            node.gameObject.layer = layer;
            foreach (Transform child in node) SetLayer(child, layer);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        private static string ShaderName(Renderer renderer)
        {
            Material material = renderer.sharedMaterial;
            return material != null && material.shader != null ? material.shader.name : "no material";
        }

        private static string Probe(Transform root)
        {
            var text = new StringBuilder();
            ProbeNode(root, 0, text);
            return text.ToString();
        }

        private static void ProbeNode(Transform node, int depth, StringBuilder text)
        {
            if (depth > 4) return;
            text.Append("\n  ").Append(new string(' ', depth * 2)).Append(node.name).Append(" @").Append(node.localPosition.ToString("F2"));
            if (!node.gameObject.activeSelf) text.Append(" (inactive)");
            Renderer renderer = node.GetComponent<Renderer>();
            if (renderer != null)
                text.Append(" [").Append(renderer.GetType().Name).Append(" ").Append(ShaderName(renderer)).Append(" size ").Append(renderer.bounds.size.ToString("F2")).Append("]");
            if (node.GetComponent<Animator>() != null) text.Append(" [anim]");
            int shown = 0;
            foreach (Transform child in node)
            {
                if (shown++ >= 12)
                {
                    text.Append("\n  ").Append(new string(' ', depth * 2 + 2)).Append("...");
                    break;
                }
                ProbeNode(child, depth + 1, text);
            }
        }

        private void Restore(string reason)
        {
            _moonSeenAt = -1f;
            if (!_active) return;
            _active = false;
            int restored = 0;
            foreach (Renderer renderer in _hidden)
            {
                if (renderer == null) continue;
                renderer.enabled = true;
                restored++;
            }
            _hidden.Clear();
            _kept.Clear();
            _keepRoots.Clear();
            if (SceneManager.GetActiveScene() == _scene) RenderSettings.fog = _savedFog;
            if (_anchor != null) Destroy(_anchor);
            _anchor = null;
            if (_stars != null) _stars.Hide();
            if (_renderer != null)
            {
                _renderer.MinFarClip = 0f;
                _renderer.BlackBackground = false;
                _renderer.BackdropLayers = 0;
            }
            _logger.LogInfo("Moon void: the cabin is back (" + reason + "; " + restored + " renderer(s) shown again).");
        }

        private void Fail(Exception exception)
        {
            _failed = true;
            try { Restore("error"); } catch (Exception) { }
            _logger.LogError("Moon void turned off after an error: " + exception);
        }

        private void OnDestroy()
        {
            try { Restore("plugin unloaded"); } catch (Exception) { }
            try { if (_stars != null) _stars.Dispose(); } catch (Exception) { }
        }
    }
}
