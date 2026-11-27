using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class GameHands : MonoBehaviour
    {
        internal const string Hologram = "Hologram";
        internal const string Game = "Game";
        internal static readonly string[] Styles = { Game, Hologram };

        private const int Thumb = 0, Index = 1, Middle = 2, Ring = 3, Pinky = 4, Unnamed = 5;
        private static readonly string[][] FingerWords =
        {
            new[] { "thumb" },
            new[] { "index", "pointer" },
            new[] { "middle" },
            new[] { "ring" },
            new[] { "pinky", "pinkie", "little", "small" }
        };
        private static readonly string[] FingerNames = { "thumb", "index", "middle", "ring", "pinky", "finger" };

        private static readonly FieldInfo ChooserField = typeof(PlayerHand3D).GetField("modelChooser", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private const float FollowRate = 28f;
        private const float HoldBlendRate = 14f;

        private sealed class Chain
        {
            internal int Finger;
            internal readonly List<Transform> Bones = new List<Transform>();
            internal readonly List<Quaternion> Open = new List<Quaternion>();
            internal readonly List<Quaternion> Closed = new List<Quaternion>();
        }

        private sealed class Shape
        {
            internal Vector3 Knuckle;
            internal Vector3 Forward;
            internal Vector3 Back;
            internal float Length;
            internal bool RightHanded;
            internal bool Swapped;
            internal string Report;
        }

        private sealed class Rig
        {
            internal VrHand Hand;
            internal GameObject Grip;
            internal Transform Align;
            internal Transform Mirror;
            internal GameObject Clone;
            internal readonly List<Chain> Chains = new List<Chain>();
            internal Shape Shape;
            internal readonly float[] Curl = new float[6];
            internal HandHold Hold;
            internal float HoldWeight;
        }

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private VrInput _input;
        private bool _wanted;
        private bool _flip;
        private bool _failed;
        private bool _reported;
        private GameObject _root;
        private GameObject _holder;
        private GameObject _source;
        private Rig _left;
        private Rig _right;
        private float _nextSearch;

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, VrInput input, string style, bool flip)
        {
            _logger = logger;
            _renderer = renderer;
            _input = input;
            _flip = flip;
            SetStyle(style);
            _renderer.BeforeEyes += Place;
        }

        public void SetStyle(string style)
        {
            _wanted = style == Game;
            if (!_wanted) HideAll();
        }

        public void SetFlip(bool flip)
        {
            if (flip == _flip) return;
            _flip = flip;
            if (_left != null) Align(_left);
            if (_right != null) Align(_right);
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception) { Fail(exception); }
        }

        private void Tick()
        {
            if (!_wanted || !VrScenes.InCabin)
            {
                HideAll();
                return;
            }
            EnsureRigs();
            if (_left == null || _right == null)
            {
                HideAll();
                return;
            }
            _input.Refresh();
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            Pose(_left, dt);
            Pose(_right, dt);
        }

        private void EnsureRigs()
        {
            if (Time.unscaledTime < _nextSearch) return;
            _nextSearch = Time.unscaledTime + 1f;
            GameObject source = FindSource();
            if (source == null || source == _source) return;
            DestroyRigs();
            _source = source;
            if (_root == null)
            {
                _root = new GameObject("InscryptionVR Game Hands");
                DontDestroyOnLoad(_root);
                _holder = new GameObject("InscryptionVR Game Hand Holder");
                _holder.SetActive(false);
                _holder.transform.SetParent(_root.transform, false);
            }
            _left = Build(source, _input.Left);
            _right = Build(source, _input.Right);
            if (!_reported)
            {
                _reported = true;
                _logger.LogInfo(_right.Shape.Report);
            }
            _logger.LogInfo("Game hands ready: cloned '" + source.name + "' for both hands (" + _right.Chains.Count + " finger chains; the source looks like a " +
                (_right.Shape.RightHanded ? "right" : "left") + " hand" + (_flip ? ", flipped by FlipGameHands" : "") + ").");
        }

        private static GameObject FindSource()
        {
            PlayerHand3D hand = Singleton<PlayerHand>.Instance as PlayerHand3D;
            if (hand == null || ChooserField == null) return null;
            HandModelChooser chooser = ChooserField.GetValue(hand) as HandModelChooser;
            if (chooser == null || chooser.Anim == null) return null;
            return chooser.Anim.gameObject;
        }

        private Rig Build(GameObject source, VrHand hand)
        {
            var rig = new Rig();
            rig.Hand = hand;
            rig.Grip = new GameObject("InscryptionVR Game Hand " + hand.Name);
            rig.Grip.transform.SetParent(_root.transform, false);
            rig.Align = new GameObject("Align").transform;
            rig.Align.SetParent(rig.Grip.transform, false);
            rig.Mirror = new GameObject("Mirror").transform;
            rig.Mirror.SetParent(rig.Align, false);

            GameObject clone = Instantiate(source, _holder.transform, false);
            clone.name = "Game Hand " + hand.Name;
            Strip(clone);
            CopyPropertyBlocks(source, clone);
            clone.transform.SetParent(rig.Grip.transform, false);
            Identity(clone.transform);
            rig.Grip.SetActive(true);
            clone.SetActive(true);
            rig.Clone = clone;
            SetRenderers(clone, true);

            Animator animator = clone.GetComponent<Animator>();
            if (animator != null) animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var open = new Dictionary<Transform, Quaternion>();
            var closed = new Dictionary<Transform, Quaternion>();
            string parameters = Sample(animator, clone.transform, open, closed);
            if (animator != null) animator.enabled = false;
            Apply(open);
            FindChains(clone.transform, open, closed, rig.Chains);
            rig.Shape = Analyze(clone.transform, rig.Chains, open, closed, parameters, source);
            CollapseArm(clone.transform, rig.Chains);

            clone.transform.SetParent(rig.Mirror, false);
            Identity(clone.transform);
            SetRenderers(clone, true);
            Align(rig);
            rig.Grip.SetActive(false);
            return rig;
        }

        private static void Identity(Transform t)
        {
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
        }

        private static void Strip(GameObject clone)
        {
            Card[] cards = clone.GetComponentsInChildren<Card>(true);
            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null && cards[i].gameObject != clone) DestroyImmediate(cards[i].gameObject);
            MonoBehaviour[] behaviours = clone.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == null) continue;
                try { DestroyImmediate(behaviours[i]); } catch (Exception) { }
            }
            Collider[] colliders = clone.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null) DestroyImmediate(colliders[i]);
            Rigidbody[] bodies = clone.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
                if (bodies[i] != null) DestroyImmediate(bodies[i]);
        }

        private static void CopyPropertyBlocks(GameObject source, GameObject clone)
        {
            var byPath = new Dictionary<string, Renderer>();
            Renderer[] from = source.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < from.Length; i++)
                if (from[i] != null) byPath[PathOf(from[i].transform, source.transform)] = from[i];
            Renderer[] to = clone.GetComponentsInChildren<Renderer>(true);
            var block = new MaterialPropertyBlock();
            for (int i = 0; i < to.Length; i++)
            {
                Renderer original;
                if (to[i] == null || !byPath.TryGetValue(PathOf(to[i].transform, clone.transform), out original)) continue;
                block.Clear();
                original.GetPropertyBlock(block);
                to[i].SetPropertyBlock(block);
            }
        }

        private static string PathOf(Transform t, Transform root)
        {
            var path = new StringBuilder();
            for (Transform walk = t; walk != null && walk != root; walk = walk.parent) path.Insert(0, "/" + walk.name);
            return path.ToString();
        }

        private static void SetRenderers(GameObject clone, bool enabled)
        {
            Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                renderer.enabled = enabled;
                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null) skinned.updateWhenOffscreen = true;
            }
        }

        private static string Sample(Animator animator, Transform root, Dictionary<Transform, Quaternion> open, Dictionary<Transform, Quaternion> closed)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            var names = new StringBuilder();
            bool hasClosed = false;
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                animator.Rebind();
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    names.Append(names.Length == 0 ? "" : ", ").Append(parameters[i].name).Append(" (").Append(parameters[i].type).Append(")");
                    if (parameters[i].name == "closed" && parameters[i].type == AnimatorControllerParameterType.Bool) hasClosed = true;
                }
                if (hasClosed) animator.SetBool("closed", false);
                Settle(animator);
            }
            Record(all, open);
            if (animator != null && hasClosed)
            {
                animator.SetBool("closed", true);
                Settle(animator);
            }
            Record(all, closed);
            return names.Length == 0 ? "none" : names.ToString();
        }

        private static void Settle(Animator animator)
        {
            animator.Update(0f);
            for (int i = 0; i < 6; i++) animator.Update(0.5f);
        }

        private static void Record(Transform[] all, Dictionary<Transform, Quaternion> pose)
        {
            for (int i = 0; i < all.Length; i++) pose[all[i]] = all[i].localRotation;
        }

        private static void Apply(Dictionary<Transform, Quaternion> pose)
        {
            foreach (KeyValuePair<Transform, Quaternion> entry in pose)
                if (entry.Key != null) entry.Key.localRotation = entry.Value;
        }

        private static int FingerOf(string name)
        {
            string lower = name.ToLowerInvariant();
            for (int f = 0; f < FingerWords.Length; f++)
                for (int w = 0; w < FingerWords[f].Length; w++)
                    if (lower.Contains(FingerWords[f][w])) return f;
            return -1;
        }

        private static void FindChains(Transform root, Dictionary<Transform, Quaternion> open, Dictionary<Transform, Quaternion> closed, List<Chain> chains)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int f = 0; f < FingerWords.Length; f++)
            {
                Transform start = null;
                int startDepth = int.MaxValue;
                for (int i = 0; i < all.Length; i++)
                {
                    Transform t = all[i];
                    if (t == root || FingerOf(t.name) != f || (t.parent != null && FingerOf(t.parent.name) == f)) continue;
                    int depth = Depth(t, root);
                    if (depth >= startDepth) continue;
                    start = t;
                    startDepth = depth;
                }
                if (start == null) continue;
                var chain = new Chain();
                chain.Finger = f;
                for (Transform walk = start; walk != null; walk = NextInChain(walk, f))
                    AddBone(chain, walk, open, closed);
                chains.Add(chain);
            }
            if (chains.Count > 0) return;
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == root || !Moves(t, open, closed) || (t.parent != null && Moves(t.parent, open, closed))) continue;
                var chain = new Chain();
                chain.Finger = Unnamed;
                for (Transform walk = t; walk != null; walk = NextMoving(walk, open, closed))
                    AddBone(chain, walk, open, closed);
                chains.Add(chain);
            }
        }

        private static void AddBone(Chain chain, Transform bone, Dictionary<Transform, Quaternion> open, Dictionary<Transform, Quaternion> closed)
        {
            chain.Bones.Add(bone);
            Quaternion a, b;
            if (!open.TryGetValue(bone, out a)) a = bone.localRotation;
            if (!closed.TryGetValue(bone, out b)) b = a;
            chain.Open.Add(a);
            chain.Closed.Add(b);
        }

        private static Transform NextInChain(Transform bone, int finger)
        {
            for (int i = 0; i < bone.childCount; i++)
                if (FingerOf(bone.GetChild(i).name) == finger) return bone.GetChild(i);
            return null;
        }

        private static Transform NextMoving(Transform bone, Dictionary<Transform, Quaternion> open, Dictionary<Transform, Quaternion> closed)
        {
            for (int i = 0; i < bone.childCount; i++)
                if (Moves(bone.GetChild(i), open, closed)) return bone.GetChild(i);
            return null;
        }

        private static bool Moves(Transform t, Dictionary<Transform, Quaternion> open, Dictionary<Transform, Quaternion> closed)
        {
            Quaternion a, b;
            return open.TryGetValue(t, out a) && closed.TryGetValue(t, out b) && Quaternion.Angle(a, b) > 5f;
        }

        private static int Depth(Transform t, Transform root)
        {
            int depth = 0;
            for (Transform walk = t; walk != null && walk != root; walk = walk.parent) depth++;
            return depth;
        }

        private static Transform FindWrist(Transform root, List<Chain> chains)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            Transform named = null;
            for (int i = 0; i < all.Length; i++)
            {
                string lower = all[i].name.ToLowerInvariant();
                if (FingerOf(lower) >= 0) continue;
                if (lower.Contains("wrist")) return all[i];
                if (named == null && (lower.Contains("hand") || lower.Contains("palm")) && all[i] != root && !(all[i].GetComponent<Renderer>() != null)) named = all[i];
            }
            if (named != null && Encloses(named, chains)) return named;
            if (chains.Count == 0) return root;
            Transform common = chains[0].Bones[0].parent;
            while (common != null && common != root && !Encloses(common, chains)) common = common.parent;
            return common != null ? common : root;
        }

        private static bool Encloses(Transform t, List<Chain> chains)
        {
            for (int i = 0; i < chains.Count; i++)
                if (chains[i].Bones.Count > 0 && !chains[i].Bones[0].IsChildOf(t)) return false;
            return true;
        }

        private static Vector3 Tip(Transform root, Chain chain)
        {
            int n = chain.Bones.Count;
            Vector3 last = root.InverseTransformPoint(chain.Bones[n - 1].position);
            if (n < 2) return last;
            Vector3 previous = root.InverseTransformPoint(chain.Bones[n - 2].position);
            return last + (last - previous) * 0.6f;
        }

        private static Chain Named(List<Chain> chains, int finger)
        {
            for (int i = 0; i < chains.Count; i++) if (chains[i].Finger == finger) return chains[i];
            return null;
        }

        private static Shape Analyze(Transform root, List<Chain> chains, Dictionary<Transform, Quaternion> open, Dictionary<Transform, Quaternion> closed,
            string parameters, GameObject source)
        {
            var shape = new Shape();
            Transform wrist = FindWrist(root, chains);
            Vector3 w = root.InverseTransformPoint(wrist.position);

            Vector3 knuckleSum = Vector3.zero;
            int knuckles = 0;
            for (int i = 0; i < chains.Count; i++)
            {
                if (chains[i].Finger == Thumb) continue;
                knuckleSum += root.InverseTransformPoint(chains[i].Bones[0].position);
                knuckles++;
            }
            bool fallback = knuckles == 0;
            Vector3 knuckle = fallback ? MeshCenter(root, w) : knuckleSum / knuckles;
            Vector3 forward = knuckle - w;
            if (forward.sqrMagnitude < 1e-10f) forward = Vector3.forward;
            forward.Normalize();

            Chain index = Named(chains, Index), pinky = Named(chains, Pinky);
            if (pinky == null) pinky = Named(chains, Ring);
            Vector3 across = Vector3.zero;
            if (index != null && pinky != null)
                across = root.InverseTransformPoint(pinky.Bones[0].position) - root.InverseTransformPoint(index.Bones[0].position);
            across = Vector3.ProjectOnPlane(across, forward);

            Vector3 palm = Vector3.zero;
            Apply(closed);
            var closedTips = new List<Vector3>();
            for (int i = 0; i < chains.Count; i++) closedTips.Add(Tip(root, chains[i]));
            Apply(open);
            float openReach = 0f, closedReach = 0f;
            for (int i = 0; i < chains.Count; i++)
            {
                if (chains[i].Finger == Thumb) continue;
                Vector3 openTip = Tip(root, chains[i]);
                palm += closedTips[i] - openTip;
                openReach += (openTip - w).magnitude;
                closedReach += (closedTips[i] - w).magnitude;
            }
            palm = Vector3.ProjectOnPlane(palm, forward);
            shape.Swapped = closedReach > openReach + 1e-5f;
            if (shape.Swapped)
            {
                palm = -palm;
                for (int i = 0; i < chains.Count; i++)
                {
                    var keep = new List<Quaternion>(chains[i].Open);
                    chains[i].Open.Clear();
                    chains[i].Open.AddRange(chains[i].Closed);
                    chains[i].Closed.Clear();
                    chains[i].Closed.AddRange(keep);
                }
            }

            bool palmKnown = palm.sqrMagnitude > 1e-10f;
            bool acrossKnown = across.sqrMagnitude > 1e-10f;
            Vector3 back = palmKnown ? -palm.normalized : acrossKnown ? Vector3.Cross(forward, across).normalized : Vector3.ProjectOnPlane(Vector3.up, forward).normalized;
            if (back.sqrMagnitude < 0.5f) back = Vector3.ProjectOnPlane(Vector3.right, forward).normalized;
            shape.RightHanded = !(palmKnown && acrossKnown) || Vector3.Dot(Vector3.Cross(forward, across), back) > 0f;

            Chain middle = Named(chains, Middle);
            float length = 0f;
            if (shape.Swapped) Apply(closed);
            if (middle != null) length = (Tip(root, middle) - w).magnitude;
            else
                for (int i = 0; i < chains.Count; i++) length = Mathf.Max(length, (Tip(root, chains[i]) - w).magnitude);
            if (shape.Swapped) Apply(open);
            if (length < 1e-5f) length = (knuckle - w).magnitude * 1.9f;
            if (length < 1e-5f) length = 1f;

            shape.Knuckle = knuckle;
            shape.Forward = forward;
            shape.Back = back;
            shape.Length = length;
            shape.Report = Describe(root, source, chains, wrist, parameters, shape, palmKnown, acrossKnown, fallback);
            return shape;
        }

        private static Vector3 MeshCenter(Transform root, Vector3 wrist)
        {
            Bounds bounds;
            if (!HandReach.RendererBounds(root, out bounds)) return wrist + Vector3.forward;
            return root.InverseTransformPoint(bounds.center);
        }

        private static string Describe(Transform root, GameObject source, List<Chain> chains, Transform wrist, string parameters, Shape shape,
            bool palmKnown, bool acrossKnown, bool fallback)
        {
            var text = new StringBuilder();
            text.Append("Game hand rig probe for '").Append(source.name).Append("': animator parameters ").Append(parameters).Append("; wrist '").Append(wrist.name).Append("'; ");
            for (int i = 0; i < chains.Count; i++)
                text.Append(FingerNames[chains[i].Finger]).Append('(').Append(chains[i].Bones.Count).Append(") ");
            text.Append("; palm side ").Append(palmKnown ? "from the closed pose" : "guessed").Append(", across ").Append(acrossKnown ? "from index/pinky" : "unknown")
                .Append(fallback ? ", knuckles from mesh bounds" : "").Append(shape.Swapped ? "; the 'closed' pose is the open one, so they were swapped" : "").Append("; length ").Append(shape.Length.ToString("0.000")).Append(" units.");
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                SkinnedMeshRenderer skinned = renderers[i] as SkinnedMeshRenderer;
                text.Append("\n  renderer '").Append(renderers[i].name).Append("' ").Append(renderers[i].GetType().Name).Append(" layer ").Append(renderers[i].gameObject.layer);
                if (skinned != null) text.Append(", ").Append(skinned.bones.Length).Append(" bones, root '").Append(skinned.rootBone != null ? skinned.rootBone.name : "none").Append("'");
                if (renderers[i].sharedMaterial != null) text.Append(", shader ").Append(renderers[i].sharedMaterial.shader.name);
            }
            int lines = 0;
            AppendTree(text, root, 0, ref lines);
            return text.ToString();
        }

        private static void AppendTree(StringBuilder text, Transform t, int depth, ref int lines)
        {
            if (lines++ > 120) return;
            text.Append("\n  ").Append(new string(' ', depth * 2)).Append(t.name);
            for (int i = 0; i < t.childCount; i++) AppendTree(text, t.GetChild(i), depth + 1, ref lines);
        }

        private void CollapseArm(Transform root, List<Chain> chains)
        {
            Transform wrist = FindWrist(root, chains);
            if (wrist == root || wrist.parent == root) return;
            Transform top = wrist;
            while (top.parent != null && top.parent != root) top = top.parent;
            wrist.SetParent(root, true);
            top.position = wrist.position;
            top.localScale = Vector3.one * 0.001f;
        }

        private void Align(Rig rig)
        {
            VrHand hand = rig.Hand;
            Shape shape = rig.Shape;
            bool targetRight = !hand.IsLeft;
            bool mirror = (shape.RightHanded != targetRight) != _flip;
            rig.Mirror.localScale = mirror ? new Vector3(-1f, 1f, 1f) : Vector3.one;
            Vector3 m = mirror ? new Vector3(-1f, 1f, 1f) : Vector3.one;
            Vector3 gameForward = Vector3.Scale(shape.Forward, m);
            Vector3 gameBack = Vector3.Scale(shape.Back, m);
            Vector3 gameKnuckle = Vector3.Scale(shape.Knuckle, m);
            Quaternion rotation = Quaternion.LookRotation(hand.LocalForward, hand.LocalBack) * Quaternion.Inverse(Quaternion.LookRotation(gameForward, gameBack));
            float scale = hand.HandLength / shape.Length;
            rig.Align.localRotation = rotation;
            rig.Align.localScale = Vector3.one * scale;
            rig.Align.localPosition = hand.LocalKnuckle - rotation * (gameKnuckle * scale);
        }

        private void Pose(Rig rig, float dt)
        {
            VrHand hand = rig.Hand;
            bool show = hand.Tracked && hand.HasFrame && rig.Clone != null;
            hand.GameHandShown = show;
            if (!show) return;
            float follow = 1f - Mathf.Exp(-FollowRate * dt);
            float grip = hand.GripValue;
            float index = Mathf.Max(hand.TriggerValue, hand.IndexTouch ? 0.25f : 0f);
            float thumb = Mathf.Max(hand.ThumbTouch ? 0.55f : 0f, grip * 0.35f);
            float[] target = { thumb, index, grip, grip, grip, grip };
            HandHold hold = hand.HoldPose;
            if (hold != HandHold.None) rig.Hold = hold;
            rig.HoldWeight = Mathf.MoveTowards(rig.HoldWeight, hold != HandHold.None ? 1f : 0f, dt * HoldBlendRate);
            if (rig.HoldWeight > 0f)
            {
                float[] held = HoldCurl(rig.Hold, grip);
                for (int f = 0; f < target.Length; f++) target[f] = Mathf.Lerp(target[f], held[f], rig.HoldWeight);
            }
            for (int f = 0; f < target.Length; f++) rig.Curl[f] = Mathf.Lerp(rig.Curl[f], target[f], follow);
            for (int c = 0; c < rig.Chains.Count; c++)
            {
                Chain chain = rig.Chains[c];
                float t = rig.Curl[chain.Finger];
                for (int b = 0; b < chain.Bones.Count; b++)
                    if (chain.Bones[b] != null) chain.Bones[b].localRotation = Quaternion.Slerp(chain.Open[b], chain.Closed[b], t);
            }
        }

        private static float[] HoldCurl(HandHold hold, float grip)
        {
            switch (hold)
            {
                case HandHold.Pinch: return new[] { 0.7f, 0.7f, Mathf.Max(grip, 0.6f), Mathf.Max(grip, 0.6f), Mathf.Max(grip, 0.6f), Mathf.Max(grip, 0.6f) };
                case HandHold.Fist: return new[] { 1f, 1f, 1f, 1f, 1f, 1f };
                case HandHold.Wrap: return new[] { 0.75f, 0.75f, 0.75f, 0.75f, 0.75f, 0.75f };
                default: return new[] { 0f, 0f, 0f, 0f, 0f, 0f };
            }
        }

        private void Place()
        {
            if (_logger == null || _failed) return;
            try
            {
                PlaceRig(_left);
                PlaceRig(_right);
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void PlaceRig(Rig rig)
        {
            if (rig == null || rig.Grip == null) return;
            VrHand hand = rig.Hand;
            if (!_wanted || !hand.GameHandShown || !VrScenes.InCabin)
            {
                if (rig.Grip.activeSelf) rig.Grip.SetActive(false);
                return;
            }
            Align(rig);
            Transform grip = rig.Grip.transform;
            grip.SetPositionAndRotation(hand.Displayed(hand.GripPosition), hand.Displayed(hand.GripRotation));
            grip.localScale = Vector3.one * _input.Scale;
            if (!rig.Grip.activeSelf) rig.Grip.SetActive(true);
        }

        private void HideAll()
        {
            Hide(_left);
            Hide(_right);
        }

        private static void Hide(Rig rig)
        {
            if (rig == null) return;
            rig.Hand.GameHandShown = false;
            if (rig.Grip != null && rig.Grip.activeSelf) rig.Grip.SetActive(false);
        }

        private void DestroyRigs()
        {
            HideAll();
            if (_left != null && _left.Grip != null) Destroy(_left.Grip);
            if (_right != null && _right.Grip != null) Destroy(_right.Grip);
            _left = null;
            _right = null;
            _source = null;
        }

        private void Fail(Exception exception)
        {
            _failed = true;
            try { DestroyRigs(); } catch (Exception) { }
            if (_input != null) _input.Left.GameHandShown = _input.Right.GameHandShown = false;
            if (_logger != null) _logger.LogError("Game hands disabled after an error; the hologram hands are back: " + exception);
        }

        private void OnDestroy()
        {
            if (_renderer != null) _renderer.BeforeEyes -= Place;
            DestroyRigs();
            if (_root != null) Destroy(_root);
        }
    }
}
