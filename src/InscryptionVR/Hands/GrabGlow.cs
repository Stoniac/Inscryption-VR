using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace InscryptionVR
{
    internal static class GrabGlow
    {
        internal const string ShellName = "InscryptionVR Grab Glow";

        private const float InSeconds = 0.08f;
        private const float OutSeconds = 0.18f;
        private const float Inflate = 1.08f;
        private const float PulseHz = 1.3f;
        private const float HaloStrength = 0.7f;
        private const float TintStrength = 0.16f;
        private static readonly Color GlowColor = new Color(1f, 0.8f, 0.45f, 1f);

        private sealed class Entry
        {
            internal Transform Target;
            internal readonly List<GameObject> Shells = new List<GameObject>();
            internal readonly List<Renderer> Halos = new List<Renderer>();
            internal readonly List<Renderer> Tints = new List<Renderer>();
            internal float Level;
            internal bool Marked;
        }

        private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
        private static readonly List<int> Dead = new List<int>();
        private static readonly List<MeshFilter> Filters = new List<MeshFilter>();
        private static readonly List<SkinnedMeshRenderer> Skins = new List<SkinnedMeshRenderer>();
        private static Material _halo;
        private static Material _tint;
        private static MaterialPropertyBlock _block;
        private static bool _failed;
        private static bool _enabled = true;

        internal static bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        internal static bool IsShell(Renderer renderer)
        {
            return renderer != null && renderer.name == ShellName;
        }

        internal static void Mark(Component target)
        {
            if (_failed || !_enabled || target == null) return;
            int id = target.transform.GetInstanceID();
            Entry entry;
            if (!Entries.TryGetValue(id, out entry))
            {
                entry = new Entry();
                entry.Target = target.transform;
                Entries[id] = entry;
            }
            entry.Marked = true;
        }

        internal static void Flush(float dt)
        {
            if (_failed || Entries.Count == 0) return;
            try
            {
                float pulse = 0.8f + 0.2f * Mathf.Sin(Time.unscaledTime * PulseHz * 2f * Mathf.PI);
                Dead.Clear();
                foreach (KeyValuePair<int, Entry> pair in Entries)
                {
                    Entry entry = pair.Value;
                    if (entry.Target == null)
                    {
                        Clear(entry);
                        Dead.Add(pair.Key);
                        continue;
                    }
                    entry.Level = Mathf.MoveTowards(entry.Level, entry.Marked ? 1f : 0f, dt / (entry.Marked ? InSeconds : OutSeconds));
                    entry.Marked = false;
                    if (entry.Level <= 0f)
                    {
                        Clear(entry);
                        Dead.Add(pair.Key);
                        continue;
                    }
                    if (entry.Shells.Count == 0) Build(entry);
                    float level = Mathf.SmoothStep(0f, 1f, entry.Level) * pulse;
                    Apply(entry.Halos, level * HaloStrength);
                    Apply(entry.Tints, level * TintStrength);
                }
                for (int i = 0; i < Dead.Count; i++) Entries.Remove(Dead[i]);
                Dead.Clear();
            }
            catch (Exception)
            {
                _failed = true;
                ClearAll();
                throw;
            }
        }

        internal static void ClearAll()
        {
            foreach (Entry entry in Entries.Values) Clear(entry);
            Entries.Clear();
        }

        private static void Apply(List<Renderer> renderers, float strength)
        {
            Color color = new Color(GlowColor.r * strength, GlowColor.g * strength, GlowColor.b * strength, 1f);
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                _block.Clear();
                _block.SetColor("_Color", color);
                renderer.SetPropertyBlock(_block);
            }
        }

        private static void Build(Entry entry)
        {
            EnsureMaterials();
            entry.Target.GetComponentsInChildren(false, Filters);
            for (int i = 0; i < Filters.Count; i++)
            {
                MeshFilter filter = Filters[i];
                if (filter == null || filter.sharedMesh == null || filter.name == ShellName) continue;
                MeshRenderer source = filter.GetComponent<MeshRenderer>();
                if (source == null || !source.enabled) continue;
                int count = Mathf.Max(1, filter.sharedMesh.subMeshCount);
                Vector3 center = filter.sharedMesh.bounds.center;
                entry.Tints.Add(MeshShell(entry, filter, count, _tint, Vector3.zero, 1f));
                entry.Halos.Add(MeshShell(entry, filter, count, _halo, center * (1f - Inflate), Inflate));
            }
            Filters.Clear();
            entry.Target.GetComponentsInChildren(false, Skins);
            for (int i = 0; i < Skins.Count; i++)
            {
                SkinnedMeshRenderer source = Skins[i];
                if (source == null || !source.enabled || source.sharedMesh == null || source.name == ShellName) continue;
                GameObject shell = NewShell(entry, source.transform, source.gameObject.layer, Vector3.zero, 1f);
                SkinnedMeshRenderer skin = shell.AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh = source.sharedMesh;
                skin.rootBone = source.rootBone;
                skin.bones = source.bones;
                skin.localBounds = source.localBounds;
                skin.updateWhenOffscreen = source.updateWhenOffscreen;
                skin.sharedMaterials = Repeat(_tint, Mathf.Max(1, source.sharedMesh.subMeshCount));
                Quiet(skin);
                entry.Tints.Add(skin);
            }
            Skins.Clear();
        }

        private static Renderer MeshShell(Entry entry, MeshFilter filter, int submeshes, Material material, Vector3 offset, float scale)
        {
            GameObject shell = NewShell(entry, filter.transform, filter.gameObject.layer, offset, scale);
            shell.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            MeshRenderer renderer = shell.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = Repeat(material, submeshes);
            Quiet(renderer);
            return renderer;
        }

        private static GameObject NewShell(Entry entry, Transform parent, int layer, Vector3 offset, float scale)
        {
            GameObject shell = new GameObject(ShellName);
            shell.layer = layer;
            shell.transform.SetParent(parent, false);
            shell.transform.localPosition = offset;
            shell.transform.localRotation = Quaternion.identity;
            shell.transform.localScale = Vector3.one * scale;
            entry.Shells.Add(shell);
            return shell;
        }

        private static void Quiet(Renderer renderer)
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;
        }

        private static Material[] Repeat(Material material, int count)
        {
            Material[] materials = new Material[count];
            for (int i = 0; i < count; i++) materials[i] = material;
            return materials;
        }

        private static void Clear(Entry entry)
        {
            for (int i = 0; i < entry.Shells.Count; i++)
                if (entry.Shells[i] != null) UnityEngine.Object.Destroy(entry.Shells[i]);
            entry.Shells.Clear();
            entry.Halos.Clear();
            entry.Tints.Clear();
        }

        private static void EnsureMaterials()
        {
            if (_halo != null && _tint != null) return;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) throw new InvalidOperationException("the built-in colored shader is missing");
            _halo = MakeMaterial(shader, "InscryptionVR Grab Halo", CullMode.Front, 3010);
            _tint = MakeMaterial(shader, "InscryptionVR Grab Tint", CullMode.Back, 3011);
            _block = new MaterialPropertyBlock();
        }

        private static Material MakeMaterial(Shader shader, string name, CullMode cull, int queue)
        {
            Material material = new Material(shader);
            material.name = name;
            material.hideFlags = HideFlags.HideAndDontSave;
            material.SetInt("_SrcBlend", (int)BlendMode.One);
            material.SetInt("_DstBlend", (int)BlendMode.One);
            material.SetInt("_ZWrite", 0);
            material.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            material.SetInt("_Cull", (int)cull);
            material.SetColor("_Color", Color.black);
            material.renderQueue = queue;
            return material;
        }
    }
}
