using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace InscryptionVR
{
    public sealed class HologramHands : MonoBehaviour
    {
        internal const int Layer = 29;
        internal static readonly string[] Palettes = { "Cyan", "Blue", "Green", "Amber", "White", "Violet", "Off" };

        private const float FadeSeconds = 0.15f;
        private const float TouchBlendSeconds = 0.09f;
        private const float FollowRate = 28f;
        private static readonly Bounds LocalBounds = new Bounds(Vector3.zero, Vector3.one * 0.6f);

        private sealed class Rig
        {
            internal VrHand Hand;
            internal HandModel Model;
            internal GameObject Root;
            internal Mesh Mesh;
            internal Vector3[] Vertices;
            internal Color32[] Colors;
            internal byte[] Rgba;
            internal float Grip;
            internal float Trigger;
            internal float IndexRest = 1f;
            internal float ThumbRest = 1f;
            internal float Presence;
            internal float Highlight;
            internal HandHold Hold;
            internal float HoldWeight;
            internal bool Posed;
        }

        private const float HoldBlendSeconds = 0.08f;

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private VrInput _input;
        private Material _depthMaterial;
        private Material _colorMaterial;
        private Rig _left;
        private Rig _right;
        private HandShade _shade;
        private float _opacity = 0.5f;
        private bool _visible = true;
        private bool _failed;

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, VrInput input, string palette, float size, float angle, float opacity)
        {
            _logger = logger;
            _renderer = renderer;
            _input = input;
            try
            {
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) throw new InvalidOperationException("the built-in colored shader is missing");
                _depthMaterial = MakeMaterial(shader, "InscryptionVR Hand Depth", BlendMode.Zero, BlendMode.One, true, 3001);
                _colorMaterial = MakeMaterial(shader, "InscryptionVR Hand Color", BlendMode.One, BlendMode.OneMinusSrcAlpha, false, 3002);
                _left = BuildRig(input.Left);
                _right = BuildRig(input.Right);
                SetOpacity(opacity);
                SetPalette(palette);
                SetShape(size, angle);
                _renderer.BeforeEyes += PlaceHands;
                _logger.LogInfo("Hologram hands ready (" + _left.Model.VertexCount + " vertices per hand, palette " + palette + ").");
            }
            catch (Exception exception) { Fail(exception); }
        }

        public void SetPalette(string palette)
        {
            _visible = palette != "Off";
            switch (palette)
            {
                case "Blue": _shade = Shade(0.22f, 0.45f, 1f, 0.5f, 0.7f, 1f); break;
                case "Green": _shade = Shade(0.25f, 1f, 0.55f, 0.6f, 1f, 0.75f); break;
                case "Amber": _shade = Shade(1f, 0.62f, 0.2f, 1f, 0.85f, 0.5f); break;
                case "White": _shade = Shade(0.85f, 0.9f, 0.95f, 1f, 1f, 1f); break;
                case "Violet": _shade = Shade(0.7f, 0.35f, 1f, 0.9f, 0.7f, 1f); break;
                default: _shade = Shade(0.3f, 0.8f, 1f, 0.6f, 0.95f, 1f); break;
            }
        }

        public void SetOpacity(float opacity)
        {
            _opacity = Mathf.Clamp(opacity, 0.01f, 1f);
            ApplyOpacity(ref _shade);
        }

        private void ApplyOpacity(ref HandShade shade)
        {
            float edge = Mathf.Clamp01(_opacity / 0.5f);
            shade.Opacity = _opacity;
            shade.RimAlpha = 0.7f * edge;
            shade.Glow = 0.4f * edge;
        }

        public void SetShape(float size, float angle)
        {
            if (_left != null) _left.Model.SetShape(size, angle);
            if (_right != null) _right.Model.SetShape(size, angle);
        }

        private HandShade Shade(float r, float g, float b, float rimR, float rimG, float rimB)
        {
            var shade = new HandShade();
            shade.BodyR = r; shade.BodyG = g; shade.BodyB = b;
            shade.RimR = rimR; shade.RimG = rimG; shade.RimB = rimB;
            ApplyOpacity(ref shade);
            return shade;
        }

        private Material MakeMaterial(Shader shader, string name, BlendMode source, BlendMode destination, bool depth, int queue)
        {
            var material = new Material(shader);
            material.name = name;
            material.hideFlags = HideFlags.HideAndDontSave;
            material.SetInt("_SrcBlend", (int)source);
            material.SetInt("_DstBlend", (int)destination);
            material.SetInt("_ZWrite", depth ? 1 : 0);
            material.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            material.SetInt("_Cull", (int)CullMode.Back);
            material.renderQueue = queue;
            return material;
        }

        private Rig BuildRig(VrHand hand)
        {
            var rig = new Rig();
            rig.Hand = hand;
            rig.Model = new HandModel(hand.IsLeft);
            rig.Vertices = new Vector3[rig.Model.VertexCount];
            rig.Colors = new Color32[rig.Model.VertexCount];
            rig.Rgba = new byte[rig.Model.VertexCount * 4];
            CopyVertices(rig);
            rig.Mesh = new Mesh();
            rig.Mesh.name = "InscryptionVR " + hand.Name + " hologram hand";
            rig.Mesh.MarkDynamic();
            rig.Mesh.vertices = rig.Vertices;
            rig.Mesh.colors32 = rig.Colors;
            rig.Mesh.triangles = rig.Model.Triangles;
            rig.Mesh.bounds = LocalBounds;

            rig.Root = new GameObject("InscryptionVR " + (hand.IsLeft ? "Left" : "Right") + " Hand");
            rig.Root.layer = Layer;
            rig.Root.transform.SetParent(transform, false);
            rig.Root.AddComponent<MeshFilter>().sharedMesh = rig.Mesh;
            MeshRenderer renderer = rig.Root.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { _depthMaterial, _colorMaterial };
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;
            rig.Root.SetActive(false);
            return rig;
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed) return;
            try
            {
                _input.Refresh();
                float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                PoseRig(_left, dt);
                PoseRig(_right, dt);
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void PlaceHands()
        {
            if (_logger == null || _failed) return;
            try
            {
                Vector3 head;
                Quaternion headRotation;
                float unitsPerMeter;
                bool haveHead = _renderer.TryGetHeadPose(out head, out headRotation, out unitsPerMeter);
                PlaceRig(_left, haveHead, head);
                PlaceRig(_right, haveHead, head);
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void PlaceRig(Rig rig, bool haveHead, Vector3 head)
        {
            if (rig.Presence <= 0f || !_visible || rig.Hand.GameHandShown)
            {
                Hide(rig);
                return;
            }
            if (rig.Posed)
            {
                VrHand hand = rig.Hand;
                Transform root = rig.Root.transform;
                root.SetPositionAndRotation(hand.Displayed(hand.GripPosition), hand.Displayed(hand.GripRotation));
                root.localScale = Vector3.one * _input.Scale;
                CopyVertices(rig);
                rig.Mesh.vertices = rig.Vertices;
                rig.Mesh.bounds = LocalBounds;
            }
            Draw(rig, haveHead, head);
        }

        private void PoseRig(Rig rig, float dt)
        {
            VrHand hand = rig.Hand;
            bool tracked = _input.Active && hand.Tracked;
            rig.Presence = Mathf.MoveTowards(rig.Presence, tracked && _visible ? 1f : 0f, dt / FadeSeconds);
            rig.Posed = tracked;
            if (!tracked)
            {
                hand.HasIndexTip = false;
                hand.HasFist = false;
                return;
            }

            float follow = 1f - Mathf.Exp(-FollowRate * dt);
            rig.Grip = Mathf.Lerp(rig.Grip, hand.GripValue, follow);
            rig.Trigger = Mathf.Lerp(rig.Trigger, hand.TriggerValue, follow);
            rig.IndexRest = Mathf.MoveTowards(rig.IndexRest, hand.IndexTouch ? 1f : 0f, dt / TouchBlendSeconds);
            rig.ThumbRest = Mathf.MoveTowards(rig.ThumbRest, hand.ThumbTouch ? 1f : 0f, dt / TouchBlendSeconds);
            rig.Highlight = Mathf.MoveTowards(rig.Highlight, hand.Highlighted ? 1f : 0f, dt / 0.12f);
            HandHold hold = hand.HoldPose;
            if (hold != HandHold.None && hold != rig.Hold && rig.HoldWeight > 0.05f) rig.HoldWeight = Mathf.MoveTowards(rig.HoldWeight, 0f, dt / HoldBlendSeconds);
            else
            {
                if (hold != HandHold.None) rig.Hold = hold;
                rig.HoldWeight = Mathf.MoveTowards(rig.HoldWeight, hold != HandHold.None ? 1f : 0f, dt / HoldBlendSeconds);
            }
            rig.Model.SetPose(rig.Grip, rig.Trigger, rig.IndexRest, rig.ThumbRest);
            rig.Model.SetHold(rig.Hold, rig.HoldWeight);
            rig.Model.Skin();

            float scale = _input.Scale;
            HandVec pinch = rig.Model.PinchCenter;
            Vector3 pinchWorld = hand.GripPosition + hand.GripRotation * (new Vector3(pinch.X, pinch.Y, pinch.Z) * scale);
            hand.LocalPinch = Quaternion.Inverse(hand.Rotation) * (pinchWorld - hand.Position) / Mathf.Max(scale, 1e-4f);
            HandVec tip = rig.Model.IndexTip;
            hand.LocalIndexTip = new Vector3(tip.X, tip.Y, tip.Z);
            HandVec fist = rig.Model.FistCenter;
            HandVec forward = rig.Model.FistForward;
            HandVec up = rig.Model.FistUp;
            hand.LocalFist = new Vector3(fist.X, fist.Y, fist.Z);
            hand.LocalFistRotation = Quaternion.LookRotation(new Vector3(forward.X, forward.Y, forward.Z), new Vector3(up.X, up.Y, up.Z));
            hand.UpdateModelFrames(scale);
            hand.HasIndexTip = true;
            hand.HasFist = true;
            FindLowest(rig);
            HandVec knuckle, handForward, back;
            float length;
            rig.Model.GetFrame(out knuckle, out handForward, out back, out length);
            hand.LocalKnuckle = new Vector3(knuckle.X, knuckle.Y, knuckle.Z);
            hand.LocalForward = new Vector3(handForward.X, handForward.Y, handForward.Z);
            hand.LocalBack = new Vector3(back.X, back.Y, back.Z);
            hand.HandLength = length;
            hand.HasFrame = true;
        }

        private static void FindLowest(Rig rig)
        {
            VrHand hand = rig.Hand;
            float[] positions = rig.Model.Positions;
            Vector3 down = Quaternion.Inverse(hand.GripRotation) * Vector3.down;
            float best = float.NegativeInfinity;
            int bestIndex = -1;
            for (int k = 0; k + 2 < positions.Length; k += 3)
            {
                float depth = positions[k] * down.x + positions[k + 1] * down.y + positions[k + 2] * down.z;
                if (depth <= best) continue;
                best = depth;
                bestIndex = k;
            }
            hand.HasLowest = bestIndex >= 0;
            if (bestIndex >= 0) hand.LocalLowest = new Vector3(positions[bestIndex], positions[bestIndex + 1], positions[bestIndex + 2]);
        }

        private void Draw(Rig rig, bool haveHead, Vector3 head)
        {
            Transform root = rig.Root.transform;
            Vector3 eye = haveHead ? root.InverseTransformPoint(head) : new Vector3(0f, 0.25f, -0.25f);
            HandShade shade = _shade;
            shade.Intensity = Mathf.SmoothStep(0f, 1f, rig.Presence);
            shade.Highlight = rig.Highlight;
            rig.Model.Shade(new HandVec(eye.x, eye.y, eye.z), shade, rig.Rgba);
            byte[] rgba = rig.Rgba;
            Color32[] colors = rig.Colors;
            for (int i = 0; i < colors.Length; i++)
            {
                int k = i * 4;
                colors[i] = new Color32(rgba[k], rgba[k + 1], rgba[k + 2], rgba[k + 3]);
            }
            rig.Mesh.colors32 = colors;
            if (!rig.Root.activeSelf) rig.Root.SetActive(true);
        }

        private static void Hide(Rig rig)
        {
            if (rig.Root.activeSelf) rig.Root.SetActive(false);
        }

        private static void CopyVertices(Rig rig)
        {
            float[] positions = rig.Model.Positions;
            Vector3[] vertices = rig.Vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                int k = i * 3;
                vertices[i] = new Vector3(positions[k], positions[k + 1], positions[k + 2]);
            }
        }

        private void Fail(Exception exception)
        {
            _failed = true;
            if (_input != null) _input.Left.HasFist = _input.Right.HasFist = false;
            if (_left != null && _left.Root != null) _left.Root.SetActive(false);
            if (_right != null && _right.Root != null) _right.Root.SetActive(false);
            if (_logger != null) _logger.LogError("Hologram hands disabled after an error; everything else keeps working: " + exception);
        }

        private void OnDestroy()
        {
            if (_renderer != null) _renderer.BeforeEyes -= PlaceHands;
            DestroyRig(_left);
            DestroyRig(_right);
            if (_depthMaterial != null) Destroy(_depthMaterial);
            if (_colorMaterial != null) Destroy(_colorMaterial);
        }

        private static void DestroyRig(Rig rig)
        {
            if (rig == null) return;
            if (rig.Root != null) Destroy(rig.Root);
            if (rig.Mesh != null) Destroy(rig.Mesh);
        }
    }
}
