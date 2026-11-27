using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;
using UnityEngine.Rendering;

namespace InscryptionVR
{
    public sealed class EndlessScenery : MonoBehaviour
    {
        private const float ScanSeconds = 0.5f;
        private const float StartTimeoutSeconds = 2f;
        private const int MaxGroupDepth = 3;
        private const float EvenEndsRatio = 0.8f;

        private static readonly FieldInfo SceneryField = typeof(Part1BossOpponent).GetField("sceneryObject", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly Type ConveyorType = typeof(Part1BossOpponent).Assembly.GetType("RepeatingConveyor");
        private static readonly FieldInfo SpanField = Field("span");
        private static readonly FieldInfo ObjectsField = Field("objects");

        private sealed class Layer
        {
            internal Transform Source;
            internal Transform Clone;
            internal Vector3 Offset;
            internal Component Conveyor;
            internal readonly List<Transform> SourceItems = new List<Transform>();
            internal readonly List<Transform> CloneItems = new List<Transform>();
            internal float Phase;
            internal float Span;
        }

        private ManualLogSource _logger;
        private Func<int> _layerCount;
        private bool _failed;
        private float _nextScan;
        private GameObject _scenery;
        private float _seenAt;
        private bool _built;
        private int _builtCount;
        private readonly List<Layer> _layers = new List<Layer>();
        private readonly HashSet<string> _probed = new HashSet<string>();

        private static FieldInfo Field(string name)
        {
            return ConveyorType != null ? ConveyorType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) : null;
        }

        internal void Initialize(ManualLogSource logger, Func<int> layerCount)
        {
            _logger = logger;
            _layerCount = layerCount;
            if (SceneryField == null) _logger.LogWarning("Endless scenery: Part1BossOpponent.sceneryObject not found; boss scenery stays as the game builds it.");
            if (ConveyorType == null || SpanField == null || ObjectsField == null) _logger.LogInfo("Endless scenery: RepeatingConveyor members not found; conveyor copies will stand still.");
        }

        internal void Refresh()
        {
            _built = false;
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed || SceneryField == null) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                try { Clear(); } catch (Exception) { }
                _logger.LogWarning("Endless scenery disabled after an error: " + exception);
            }
        }

        private void Tick()
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanSeconds;
                GameObject scenery = CurrentScenery();
                if (scenery != _scenery)
                {
                    Clear();
                    _scenery = scenery;
                    _seenAt = Time.unscaledTime;
                }
                if (_scenery != null && _built && _builtCount != _layerCount())
                {
                    Clear();
                    _built = false;
                }
            }
            if (_scenery == null)
            {
                if (_layers.Count > 0) Clear();
                return;
            }
            if (!_built && (ConveyorsStarted(_scenery) || Time.unscaledTime - _seenAt > StartTimeoutSeconds))
            {
                Clear();
                Build(_scenery, _layerCount());
            }
            Follow();
        }

        private static GameObject CurrentScenery()
        {
            if (!VrScenes.InCabin) return null;
            TurnManager turns = Singleton<TurnManager>.Instance;
            Part1BossOpponent boss = turns != null ? turns.Opponent as Part1BossOpponent : null;
            if (boss == null) return null;
            GameObject scenery = SceneryField.GetValue(boss) as GameObject;
            return scenery != null ? scenery : null;
        }

        private static bool ConveyorsStarted(GameObject scenery)
        {
            if (ConveyorType == null || ObjectsField == null) return true;
            foreach (Component conveyor in scenery.GetComponentsInChildren(ConveyorType, true))
            {
                IList items = ObjectsField.GetValue(conveyor) as IList;
                if (items == null || items.Count == 0) return false;
            }
            return true;
        }

        private void Build(GameObject scenery, int count)
        {
            _built = true;
            _builtCount = count;
            Probe(scenery);
            if (count <= 0) return;
            Vector3 center = SceneryCenter(scenery);
            var groups = new List<List<Transform>>();
            FindGroups(scenery.transform, 0, groups);
            var report = new StringBuilder();
            foreach (List<Transform> group in groups) Extend(group, center, count, report);
            _logger.LogInfo("Endless scenery: '" + scenery.name + "' got " + _layers.Count + " extra layer(s) in " + groups.Count + " group(s)" +
                (report.Length > 0 ? ": " + report : "; nothing repeats in it, so it stays as the game built it."));
        }

        private void FindGroups(Transform parent, int depth, List<List<Transform>> groups)
        {
            if (depth >= MaxGroupDepth) return;
            var byName = new Dictionary<string, List<Transform>>();
            var order = new List<string>();
            foreach (Transform child in parent)
            {
                if (child.name.IndexOf("(Clone)", StringComparison.Ordinal) >= 0 || child.name.StartsWith("Endless ", StringComparison.Ordinal)) continue;
                if (!child.gameObject.activeSelf || child.GetComponentInChildren<Renderer>(true) == null) continue;
                string key = BaseName(child.name);
                List<Transform> list;
                if (!byName.TryGetValue(key, out list))
                {
                    list = new List<Transform>();
                    byName[key] = list;
                    order.Add(key);
                }
                list.Add(child);
            }
            var grouped = new HashSet<Transform>();
            foreach (string key in order)
            {
                List<Transform> list = byName[key];
                if (list.Count < 2 || !Distinct(list)) continue;
                groups.Add(list);
                foreach (Transform member in list) grouped.Add(member);
            }
            foreach (Transform child in parent)
            {
                if (grouped.Contains(child) || IsConveyor(child)) continue;
                FindGroups(child, depth + 1, groups);
            }
        }

        private static bool IsConveyor(Transform transform)
        {
            return ConveyorType != null && transform.GetComponent(ConveyorType) != null;
        }

        private static string BaseName(string name)
        {
            string trimmed = name.Trim();
            if (trimmed.EndsWith(")", StringComparison.Ordinal))
            {
                int open = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
                if (open > 0)
                {
                    int number;
                    if (int.TryParse(trimmed.Substring(open + 2, trimmed.Length - open - 3), out number)) return trimmed.Substring(0, open);
                }
            }
            return trimmed;
        }

        private static bool Distinct(List<Transform> list)
        {
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    if ((list[i].localPosition - list[j].localPosition).sqrMagnitude < 1e-6f) return false;
            return true;
        }

        private void Extend(List<Transform> group, Vector3 center, int count, StringBuilder report)
        {
            Transform parent = group[0].parent;
            Vector3 first = group[0].localPosition;
            Vector3 axis = Vector3.zero;
            foreach (Transform member in group)
            {
                Vector3 d = member.localPosition - first;
                if (d.sqrMagnitude > axis.sqrMagnitude) axis = d;
            }
            axis.Normalize();
            group.Sort(delegate(Transform a, Transform b) { return Vector3.Dot(a.localPosition, axis).CompareTo(Vector3.Dot(b.localPosition, axis)); });
            Transform low = group[0];
            Transform high = group[group.Count - 1];
            float lowDistance = Flat(low.position - center).magnitude;
            float highDistance = Flat(high.position - center).magnitude;
            bool extendHigh = highDistance >= lowDistance * EvenEndsRatio;
            bool extendLow = lowDistance >= highDistance * EvenEndsRatio;
            int added = 0;
            if (extendHigh) added += AddLayers(parent, high, high.localPosition - group[group.Count - 2].localPosition, count);
            if (extendLow) added += AddLayers(parent, low, low.localPosition - group[1].localPosition, count);
            if (report.Length > 0) report.Append("; ");
            report.Append(Path(parent) + "/" + BaseName(group[0].name) + " x" + group.Count + " +" + added + (extendHigh && extendLow ? " (both ends)" : ""));
        }

        private int AddLayers(Transform parent, Transform source, Vector3 step, int count)
        {
            if (step.sqrMagnitude < 1e-8f) return 0;
            for (int k = 1; k <= count; k++)
            {
                GameObject copy = Instantiate(source.gameObject, parent, false);
                copy.name = "Endless " + source.name + " " + k;
                var layer = new Layer();
                layer.Source = source;
                layer.Clone = copy.transform;
                layer.Offset = step * k;
                PrepareClone(layer, source.gameObject, copy);
                _layers.Add(layer);
                Place(layer);
            }
            return count;
        }

        private static void PrepareClone(Layer layer, GameObject source, GameObject copy)
        {
            foreach (Renderer renderer in copy.GetComponentsInChildren<Renderer>(true)) renderer.shadowCastingMode = ShadowCastingMode.Off;
            if (ConveyorType == null) return;
            Component sourceConveyor = source.GetComponent(ConveyorType);
            Component[] copies = copy.GetComponentsInChildren(ConveyorType, true);
            foreach (Component c in copies) DestroyImmediate(c);
            if (sourceConveyor == null || SpanField == null || ObjectsField == null) return;
            IList items = ObjectsField.GetValue(sourceConveyor) as IList;
            if (items == null) return;
            Transform sourceRoot = source.transform;
            foreach (object item in items)
            {
                Transform original = item as Transform;
                if (original == null || original.parent != sourceRoot) continue;
                int index = original.GetSiblingIndex();
                if (index >= copy.transform.childCount) continue;
                layer.SourceItems.Add(original);
                layer.CloneItems.Add(copy.transform.GetChild(index));
            }
            layer.Conveyor = sourceConveyor;
            layer.Span = Mathf.Max(0.0001f, (float)SpanField.GetValue(sourceConveyor));
            layer.Phase = UnityEngine.Random.Range(0f, layer.Span);
        }

        private void Follow()
        {
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                Layer layer = _layers[i];
                if (layer.Clone == null || layer.Source == null)
                {
                    if (layer.Clone != null) Destroy(layer.Clone.gameObject);
                    _layers.RemoveAt(i);
                    continue;
                }
                Place(layer);
            }
        }

        private static void Place(Layer layer)
        {
            Transform source = layer.Source;
            Transform clone = layer.Clone;
            clone.localPosition = source.localPosition + layer.Offset;
            clone.localRotation = source.localRotation;
            clone.localScale = source.localScale;
            if (clone.gameObject.activeSelf != source.gameObject.activeSelf) clone.gameObject.SetActive(source.gameObject.activeSelf);
            if (layer.Conveyor == null) return;
            float half = layer.Span * 0.5f;
            for (int i = 0; i < layer.SourceItems.Count; i++)
            {
                Transform original = layer.SourceItems[i];
                Transform item = layer.CloneItems[i];
                if (original == null || item == null) continue;
                Vector3 p = original.localPosition;
                p.y = Mathf.Repeat(p.y + layer.Phase + half, layer.Span) - half;
                item.localPosition = p;
                item.localRotation = original.localRotation;
                if (item.gameObject.activeSelf != original.gameObject.activeSelf) item.gameObject.SetActive(original.gameObject.activeSelf);
            }
        }

        private static Vector3 SceneryCenter(GameObject scenery)
        {
            bool any = false;
            Bounds bounds = new Bounds();
            foreach (Renderer renderer in scenery.GetComponentsInChildren<Renderer>())
            {
                if (!any) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                any = true;
            }
            return any ? bounds.center : scenery.transform.position;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        private static string Path(Transform transform)
        {
            string path = transform.name;
            Transform t = transform.parent;
            int guard = 0;
            while (t != null && guard++ < 6)
            {
                path = t.name + "/" + path;
                t = t.parent;
            }
            return path;
        }

        private void Probe(GameObject scenery)
        {
            if (!_probed.Add(scenery.name)) return;
            var text = new StringBuilder();
            ProbeNode(scenery.transform, 0, text);
            _logger.LogInfo("Endless scenery probe for '" + scenery.name + "':" + text);
        }

        private void ProbeNode(Transform node, int depth, StringBuilder text)
        {
            if (depth > 4) return;
            text.Append("\n  ").Append(new string(' ', depth * 2)).Append(node.name).Append(" @").Append(node.localPosition.ToString("F2"));
            if (!node.gameObject.activeSelf) text.Append(" (inactive)");
            if (IsConveyor(node))
            {
                Component conveyor = node.GetComponent(ConveyorType);
                IList items = ObjectsField != null ? ObjectsField.GetValue(conveyor) as IList : null;
                text.Append(" [conveyor span ").Append(SpanField != null ? ((float)SpanField.GetValue(conveyor)).ToString("F2") : "?")
                    .Append(", ").Append(items != null ? items.Count : -1).Append(" items]");
            }
            if (node.GetComponent<Renderer>() != null) text.Append(" [mesh]");
            if (node.GetComponent<Animator>() != null || node.GetComponent<Animation>() != null) text.Append(" [anim]");
            int shown = 0;
            foreach (Transform child in node)
            {
                if (child.name.IndexOf("(Clone)", StringComparison.Ordinal) >= 0) continue;
                if (shown++ >= 12)
                {
                    text.Append("\n  ").Append(new string(' ', depth * 2 + 2)).Append("...");
                    break;
                }
                ProbeNode(child, depth + 1, text);
            }
        }

        private void Clear()
        {
            foreach (Layer layer in _layers)
                if (layer.Clone != null) Destroy(layer.Clone.gameObject);
            _layers.Clear();
        }

        private void OnDestroy()
        {
            try { Clear(); } catch (Exception) { }
        }
    }
}
