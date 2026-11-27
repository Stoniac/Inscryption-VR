using System;
using System.Collections.Generic;

namespace InscryptionVR
{
    internal struct HandVec
    {
        internal float X, Y, Z;

        internal HandVec(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static HandVec operator +(HandVec a, HandVec b) { return new HandVec(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static HandVec operator -(HandVec a, HandVec b) { return new HandVec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static HandVec operator -(HandVec a) { return new HandVec(-a.X, -a.Y, -a.Z); }
        public static HandVec operator *(HandVec a, float s) { return new HandVec(a.X * s, a.Y * s, a.Z * s); }

        internal static float Dot(HandVec a, HandVec b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        internal static HandVec Cross(HandVec a, HandVec b)
        {
            return new HandVec(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        internal float Length { get { return (float)Math.Sqrt(X * X + Y * Y + Z * Z); } }

        internal HandVec Normalized
        {
            get
            {
                float length = Length;
                return length > 1e-9f ? this * (1f / length) : new HandVec(0f, 0f, 1f);
            }
        }

        internal static HandVec Lerp(HandVec a, HandVec b, float t) { return a + (b - a) * t; }
    }

    internal struct HandQuat
    {
        internal float X, Y, Z, W;

        internal HandQuat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        internal static readonly HandQuat Identity = new HandQuat(0f, 0f, 0f, 1f);

        public static HandQuat operator *(HandQuat a, HandQuat b)
        {
            return new HandQuat(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }

        internal HandQuat Conjugate { get { return new HandQuat(-X, -Y, -Z, W); } }

        internal static HandQuat AngleAxis(float degrees, HandVec axis)
        {
            HandVec n = axis.Normalized;
            double half = degrees * Math.PI / 360.0;
            float s = (float)Math.Sin(half);
            return new HandQuat(n.X * s, n.Y * s, n.Z * s, (float)Math.Cos(half));
        }

        internal HandVec Rotate(HandVec v)
        {
            HandVec u = new HandVec(X, Y, Z);
            HandVec t = HandVec.Cross(u, v) * 2f;
            return v + t * W + HandVec.Cross(u, t);
        }

        internal static HandQuat LookRotation(HandVec forward, HandVec up)
        {
            HandVec z = forward.Normalized;
            HandVec x = HandVec.Cross(up, z).Normalized;
            HandVec y = HandVec.Cross(z, x);
            return FromBasis(x, y, z);
        }

        internal static HandQuat FromBasis(HandVec x, HandVec y, HandVec z)
        {
            float trace = x.X + y.Y + z.Z;
            HandQuat q;
            if (trace > 0f)
            {
                float s = (float)Math.Sqrt(trace + 1f) * 2f;
                q = new HandQuat((y.Z - z.Y) / s, (z.X - x.Z) / s, (x.Y - y.X) / s, 0.25f * s);
            }
            else if (x.X > y.Y && x.X > z.Z)
            {
                float s = (float)Math.Sqrt(1f + x.X - y.Y - z.Z) * 2f;
                q = new HandQuat(0.25f * s, (y.X + x.Y) / s, (z.X + x.Z) / s, (y.Z - z.Y) / s);
            }
            else if (y.Y > z.Z)
            {
                float s = (float)Math.Sqrt(1f + y.Y - x.X - z.Z) * 2f;
                q = new HandQuat((y.X + x.Y) / s, 0.25f * s, (z.Y + y.Z) / s, (z.X - x.Z) / s);
            }
            else
            {
                float s = (float)Math.Sqrt(1f + z.Z - x.X - y.Y) * 2f;
                q = new HandQuat((z.X + x.Z) / s, (z.Y + y.Z) / s, 0.25f * s, (x.Y - y.X) / s);
            }
            return q.Normalized;
        }

        internal HandQuat Normalized
        {
            get
            {
                float length = (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
                return length > 1e-9f ? new HandQuat(X / length, Y / length, Z / length, W / length) : Identity;
            }
        }

        internal static HandQuat Nlerp(HandQuat a, HandQuat b, float t)
        {
            float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            float sign = dot < 0f ? -1f : 1f;
            return new HandQuat(
                a.X + (b.X * sign - a.X) * t,
                a.Y + (b.Y * sign - a.Y) * t,
                a.Z + (b.Z * sign - a.Z) * t,
                a.W + (b.W * sign - a.W) * t).Normalized;
        }
    }

    internal struct HandShade
    {
        internal float BodyR, BodyG, BodyB;
        internal float RimR, RimG, RimB;
        internal float Opacity;
        internal float RimAlpha;
        internal float Glow;
        internal float Intensity;
        internal float Highlight;
    }

    internal enum HandHold { None, Pinch, Fist, Wrap }

    internal sealed class HandModel
    {
        internal const int BoneCount = 16;
        internal const int Thumb = 0, Index = 1, Middle = 2, Ring = 3, Pinky = 4;

        private const int FingerSides = 10;
        private const int PalmSides = 22;
        private const int PalmRings = 15;
        private const int WristSides = 16;

        private static readonly HandVec AxisX = new HandVec(1f, 0f, 0f);
        private static readonly HandVec AxisY = new HandVec(0f, 1f, 0f);
        private static readonly HandVec AxisZ = new HandVec(0f, 0f, 1f);

        private static readonly HandVec PalmAnchor = new HandVec(-0.002f, -0.013f, 0.075f);
        private static readonly HandVec GripAnchor = new HandVec(0.018f, 0f, 0f);
        private const float BaseTiltDegrees = 30f;

        private static readonly HandVec ThumbRestForward = new HandVec(-0.22f, -0.16f, 0.96f);
        private static readonly HandVec ThumbRestUp = new HandVec(-0.45f, 0.89f, 0f);
        private static readonly HandVec ThumbUpForward = new HandVec(-0.90f, 0.15f, 0.40f);
        private static readonly HandVec ThumbUpUp = new HandVec(0f, 0.55f, -0.84f);

        private sealed class Finger
        {
            internal HandVec Base;
            internal float[] Lengths;
            internal float[] Radii;
            internal float BindYaw;
        }

        private static readonly Finger[] Fingers =
        {
            new Finger { Base = new HandVec(-0.0190f, -0.0090f, 0.0240f), Lengths = new[] { 0.0400f, 0.0310f, 0.0260f }, Radii = new[] { 0.0128f, 0.0110f, 0.0098f, 0.0088f } },
            new Finger { Base = new HandVec(-0.0265f, 0.0005f, 0.0880f), Lengths = new[] { 0.0400f, 0.0245f, 0.0205f }, Radii = new[] { 0.0092f, 0.0084f, 0.0076f, 0.0068f }, BindYaw = -5f },
            new Finger { Base = new HandVec(-0.0085f, 0.0010f, 0.0915f), Lengths = new[] { 0.0445f, 0.0280f, 0.0215f }, Radii = new[] { 0.0095f, 0.0087f, 0.0078f, 0.0070f } },
            new Finger { Base = new HandVec(0.0090f, 0.0000f, 0.0890f), Lengths = new[] { 0.0415f, 0.0265f, 0.0210f }, Radii = new[] { 0.0089f, 0.0081f, 0.0073f, 0.0066f }, BindYaw = 5f },
            new Finger { Base = new HandVec(0.0250f, -0.0020f, 0.0815f), Lengths = new[] { 0.0330f, 0.0205f, 0.0190f }, Radii = new[] { 0.0079f, 0.0072f, 0.0065f, 0.0059f }, BindYaw = 11f }
        };

        private static readonly float[,] OpenFlex = { { 0f, 0f, 0f }, { 3f, 5f, 3f }, { 8f, 12f, 8f }, { 9f, 12f, 8f }, { 10f, 12f, 8f } };
        private static readonly float[,] GripFlex = { { 0f, 0f, 0f }, { 3f, 5f, 3f }, { 70f, 75f, 40f }, { 70f, 70f, 35f }, { 60f, 60f, 30f } };
        private static readonly float[] OpenYaw = { 0f, -5f, 0f, 5f, 11f };
        private static readonly float[] GripYaw = { 0f, -3f, 0f, 3f, 7f };
        private static readonly float[] IndexRestFlex = { 2f, 48f, 32f };
        private static readonly float[] IndexPressFlex = { 6f, 62f, 40f };
        private const float IndexTriggerYaw = 20f;
        private static readonly float[] ThumbRestFlex = { 0f, 10f, 16f };
        private static readonly float[] ThumbUpFlex = { 0f, -4f, -6f };

        private static readonly float[,] FistFlex = { { 0f, 22f, 30f }, { 68f, 74f, 40f }, { 72f, 78f, 42f }, { 72f, 74f, 38f }, { 64f, 66f, 34f } };
        private static readonly float[,] WrapFlex = { { 0f, 16f, 22f }, { 38f, 44f, 24f }, { 42f, 48f, 26f }, { 44f, 46f, 24f }, { 40f, 42f, 22f } };
        private static readonly float[,] PinchFlex = { { 33.8f, 0f, 0f }, { 25.5f, 38.2f, 43.9f }, { 52f, 62f, 34f }, { 58f, 62f, 32f }, { 56f, 58f, 30f } };
        private static readonly HandVec PinchThumbForward = new HandVec(-0.4033f, 0.0317f, 0.9145f);
        private static readonly HandVec PinchThumbUp = new HandVec(-0.5f, 0.8f, 0.1f);
        private const float PinchIndexYaw = -6.8f;

        private readonly bool _left;
        private readonly List<HandVec> _bindPositions = new List<HandVec>();
        private readonly List<HandVec> _bindNormals = new List<HandVec>();
        private readonly List<int> _boneA = new List<int>();
        private readonly List<int> _boneB = new List<int>();
        private readonly List<float> _weightB = new List<float>();
        private readonly List<float> _fadeList = new List<float>();
        private readonly List<int> _triangles = new List<int>();

        private HandVec[] _bindPos;
        private HandVec[] _bindNrm;
        private int[] _a, _b;
        private float[] _w, _fade;

        private readonly HandQuat[] _bindRotation = new HandQuat[BoneCount];
        private readonly HandVec[] _bindOrigin = new HandVec[BoneCount];
        private readonly HandQuat[] _rotation = new HandQuat[BoneCount];
        private readonly HandVec[] _origin = new HandVec[BoneCount];
        private readonly float[] _matrix = new float[BoneCount * 9];
        private readonly HandVec[] _offset = new HandVec[BoneCount];

        private float _grip, _trigger, _indexRest = 1f, _thumbRest = 1f;
        private HandHold _hold;
        private float _holdWeight;
        private float _size = 1f;
        private float _tilt = BaseTiltDegrees;

        internal readonly float[] Positions;
        internal readonly float[] Normals;
        internal readonly int[] Triangles;
        internal readonly int VertexCount;

        internal HandVec IndexTip { get; private set; }
        internal HandVec IndexDirection { get; private set; }
        internal HandVec ThumbTip { get; private set; }
        internal HandVec FistCenter { get; private set; }
        internal HandVec FistForward { get; private set; }
        internal HandVec FistUp { get; private set; }

        private readonly HandVec _fistCenterHand;

        internal HandModel(bool left)
        {
            _left = left;
            BuildBindSkeleton();
            _fistCenterHand = ComputeFistCenter();
            BuildPalm();
            BuildWrist();
            for (int f = 0; f < Fingers.Length; f++) BuildFinger(f);
            _bindPos = _bindPositions.ToArray();
            _bindNrm = _bindNormals.ToArray();
            _a = _boneA.ToArray();
            _b = _boneB.ToArray();
            _w = _weightB.ToArray();
            _fade = _fadeList.ToArray();
            VertexCount = _bindPos.Length;
            Positions = new float[VertexCount * 3];
            Normals = new float[VertexCount * 3];
            Triangles = _triangles.ToArray();
            if (_left)
                for (int i = 0; i < Triangles.Length; i += 3)
                {
                    int swap = Triangles[i + 1];
                    Triangles[i + 1] = Triangles[i + 2];
                    Triangles[i + 2] = swap;
                }
            UpdatePinchCenter();
            Skin();
        }

        internal bool IsLeft { get { return _left; } }

        internal void SetShape(float size, float tiltAdjustDegrees)
        {
            _size = Clamp(size, 0.6f, 1.5f);
            _tilt = BaseTiltDegrees + Clamp(tiltAdjustDegrees, -60f, 60f);
            UpdatePinchCenter();
        }

        internal HandVec PinchCenter { get; private set; }

        private void UpdatePinchCenter()
        {
            var rotations = new HandQuat[BoneCount];
            var origins = new HandVec[BoneCount];
            HandHold hold = _hold;
            _hold = HandHold.Pinch;
            HandVec sum = new HandVec(0f, 0f, 0f);
            foreach (int f in new[] { Thumb, Index })
            {
                HandQuat baseRotation;
                float f0, f1, f2;
                HoldTarget(f, out baseRotation, out f0, out f1, out f2);
                Chain(f, baseRotation, f0, f1, f2, rotations, origins);
                Finger finger = Fingers[f];
                int b = BoneIndex(f, 2);
                sum = sum + origins[b] + rotations[b].Rotate(AxisZ * (finger.Lengths[2] + finger.Radii[3] * 0.8f));
            }
            _hold = hold;
            PinchCenter = ToGrip(sum * 0.5f, AlignRotation(), _left ? -1f : 1f);
        }

        internal void SetPose(float grip, float trigger, float indexRest, float thumbRest)
        {
            _grip = Clamp01(grip);
            _trigger = Clamp01(trigger);
            _indexRest = Clamp01(indexRest);
            _thumbRest = Clamp01(thumbRest);
        }

        internal void SetHold(HandHold hold, float weight)
        {
            _hold = hold;
            _holdWeight = hold == HandHold.None ? 0f : Clamp01(weight);
        }

        internal void Skin()
        {
            Pose();
            HandQuat align = AlignRotation();
            HandVec anchorPalm = PalmAnchor;
            for (int b = 0; b < BoneCount; b++)
            {
                HandQuat skin = (align * _rotation[b] * _bindRotation[b].Conjugate).Normalized;
                WriteMatrix(skin, b);
                HandVec moved = _origin[b] - (_rotation[b] * _bindRotation[b].Conjugate).Rotate(_bindOrigin[b]);
                _offset[b] = align.Rotate((moved - anchorPalm) * _size) + GripAnchor;
            }
            float mirror = _left ? -1f : 1f;
            for (int i = 0; i < VertexCount; i++)
            {
                int a = _a[i], b = _b[i];
                float w = _w[i];
                HandVec p = _bindPos[i], n = _bindNrm[i];
                HandVec pa = Transform(a, p) * _size + _offset[a];
                HandVec na = Transform(a, n);
                if (w > 0f)
                {
                    HandVec pb = Transform(b, p) * _size + _offset[b];
                    HandVec nb = Transform(b, n);
                    pa = HandVec.Lerp(pa, pb, w);
                    na = HandVec.Lerp(na, nb, w);
                }
                na = na.Normalized;
                int k = i * 3;
                Positions[k] = pa.X * mirror;
                Positions[k + 1] = pa.Y;
                Positions[k + 2] = pa.Z;
                Normals[k] = na.X * mirror;
                Normals[k + 1] = na.Y;
                Normals[k + 2] = na.Z;
            }
            IndexTip = ToGrip(FingerTip(Index, 0.8f), align, mirror);
            HandVec direction = align.Rotate(_rotation[BoneIndex(Index, 2)].Rotate(AxisZ));
            IndexDirection = new HandVec(direction.X * mirror, direction.Y, direction.Z);
            ThumbTip = ToGrip(FingerTip(Thumb, 0.8f), align, mirror);
            FistCenter = ToGrip(_fistCenterHand, align, mirror);
            FistForward = (ToGrip(_fistCenterHand + AxisX * 0.01f, align, mirror) - FistCenter).Normalized;
            FistUp = (FistCenter - ToGrip(_fistCenterHand + AxisZ * 0.01f, align, mirror)).Normalized;
        }

        private static HandVec ComputeFistCenter()
        {
            var rotations = new HandQuat[BoneCount];
            var origins = new HandVec[BoneCount];
            Chain(Middle, HandQuat.AngleAxis(GripYaw[Middle], AxisY), GripFlex[Middle, 0], GripFlex[Middle, 1], GripFlex[Middle, 2], rotations, origins);
            Finger finger = Fingers[Middle];
            int b = BoneIndex(Middle, 0);
            HandVec a = origins[b];
            HandVec p = origins[b + 1];
            HandVec c = origins[b + 2] + rotations[b + 2].Rotate(AxisZ * (finger.Lengths[2] + finger.Radii[3]));
            float ax = a.Y, ay = a.Z, bx = p.Y, by = p.Z, cx = c.Y, cy = c.Z;
            float d = 2f * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(d) < 1e-9f) return new HandVec(0f, -0.028f, 0.076f);
            float a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            float y = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
            float z = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
            float x = (Fingers[Index].Base.X + Fingers[Pinky].Base.X) * 0.5f;
            return new HandVec(x, y, z);
        }

        internal void Shade(HandVec eye, HandShade shade, byte[] rgba)
        {
            float intensity = Clamp01(shade.Intensity);
            float highlight = Clamp01(shade.Highlight);
            float bodyAlpha = Clamp01(shade.Opacity * (1f + 0.25f * highlight));
            float rimAlpha = shade.RimAlpha * (1f - bodyAlpha);
            float glow = shade.Glow * (1f + 0.8f * highlight);
            for (int i = 0; i < VertexCount; i++)
            {
                int k = i * 3;
                float px = Positions[k], py = Positions[k + 1], pz = Positions[k + 2];
                float vx = eye.X - px, vy = eye.Y - py, vz = eye.Z - pz;
                float length = (float)Math.Sqrt(vx * vx + vy * vy + vz * vz);
                float facing = length > 1e-6f ? (Normals[k] * vx + Normals[k + 1] * vy + Normals[k + 2] * vz) / length : 1f;
                float edge = 1f - Clamp01(facing);
                float rim = (float)Math.Pow(edge, 2.5);
                float alpha = (bodyAlpha + rimAlpha * rim) * _fade[i] * intensity;
                if (alpha > 1f) alpha = 1f;
                float add = glow * rim * _fade[i] * intensity;
                float r = shade.BodyR * alpha + shade.RimR * add;
                float g = shade.BodyG * alpha + shade.RimG * add;
                float bl = shade.BodyB * alpha + shade.RimB * add;
                int c = i * 4;
                rgba[c] = ToByte(r);
                rgba[c + 1] = ToByte(g);
                rgba[c + 2] = ToByte(bl);
                rgba[c + 3] = ToByte(alpha);
            }
        }

        private void Pose()
        {
            _rotation[0] = HandQuat.Identity;
            _origin[0] = new HandVec(0f, 0f, 0f);
            float grip = Smooth(_grip);
            for (int f = 0; f < Fingers.Length; f++)
            {
                float yaw;
                float f0, f1, f2;
                HandQuat baseRotation;
                if (f == Thumb)
                {
                    float up = (1f - _thumbRest) * (0.7f + 0.3f * grip);
                    baseRotation = HandQuat.Nlerp(ThumbRestBase(), ThumbUpBase(), Smooth(up));
                    f0 = Lerp(ThumbRestFlex[0], ThumbUpFlex[0], up);
                    f1 = Lerp(ThumbRestFlex[1], ThumbUpFlex[1], up);
                    f2 = Lerp(ThumbRestFlex[2], ThumbUpFlex[2], up);
                }
                else if (f == Index)
                {
                    float rest = Smooth(_indexRest);
                    float press = Smooth(_trigger);
                    f0 = Lerp(OpenFlex[f, 0], Lerp(IndexRestFlex[0], IndexPressFlex[0], press), rest);
                    f1 = Lerp(OpenFlex[f, 1], Lerp(IndexRestFlex[1], IndexPressFlex[1], press), rest);
                    f2 = Lerp(OpenFlex[f, 2], Lerp(IndexRestFlex[2], IndexPressFlex[2], press), rest);
                    yaw = Lerp(Lerp(OpenYaw[f], GripYaw[f], grip), IndexTriggerYaw, rest);
                    baseRotation = HandQuat.AngleAxis(yaw, AxisY);
                }
                else
                {
                    f0 = Lerp(OpenFlex[f, 0], GripFlex[f, 0], grip);
                    f1 = Lerp(OpenFlex[f, 1], GripFlex[f, 1], grip);
                    f2 = Lerp(OpenFlex[f, 2], GripFlex[f, 2], grip);
                    yaw = Lerp(OpenYaw[f], GripYaw[f], grip);
                    baseRotation = HandQuat.AngleAxis(yaw, AxisY);
                }
                if (_holdWeight > 0f)
                {
                    float w = Smooth(_holdWeight);
                    HandQuat holdBase;
                    float h0, h1, h2;
                    HoldTarget(f, out holdBase, out h0, out h1, out h2);
                    baseRotation = HandQuat.Nlerp(baseRotation, holdBase, w);
                    f0 = Lerp(f0, h0, w);
                    f1 = Lerp(f1, h1, w);
                    f2 = Lerp(f2, h2, w);
                }
                Chain(f, baseRotation, f0, f1, f2, _rotation, _origin);
            }
        }

        private void HoldTarget(int f, out HandQuat baseRotation, out float f0, out float f1, out float f2)
        {
            float[,] flex = _hold == HandHold.Pinch ? PinchFlex : _hold == HandHold.Fist ? FistFlex : WrapFlex;
            f0 = flex[f, 0];
            f1 = flex[f, 1];
            f2 = flex[f, 2];
            if (f == Thumb) baseRotation = _hold == HandHold.Pinch ? HandQuat.LookRotation(PinchThumbForward, PinchThumbUp) : ThumbRestBase();
            else baseRotation = HandQuat.AngleAxis(_hold == HandHold.Pinch && f == Index ? PinchIndexYaw : GripYaw[f], AxisY);
        }

        private static void Chain(int f, HandQuat baseRotation, float f0, float f1, float f2, HandQuat[] rotations, HandVec[] origins)
        {
            Finger finger = Fingers[f];
            HandQuat r0 = baseRotation * HandQuat.AngleAxis(f0, AxisX);
            HandQuat r1 = r0 * HandQuat.AngleAxis(f1, AxisX);
            HandQuat r2 = r1 * HandQuat.AngleAxis(f2, AxisX);
            int b0 = BoneIndex(f, 0);
            rotations[b0] = r0;
            origins[b0] = finger.Base;
            rotations[b0 + 1] = r1;
            origins[b0 + 1] = finger.Base + r0.Rotate(AxisZ * finger.Lengths[0]);
            rotations[b0 + 2] = r2;
            origins[b0 + 2] = origins[b0 + 1] + r1.Rotate(AxisZ * finger.Lengths[1]);
        }

        private HandVec FingerTip(int f, float radiusFraction)
        {
            Finger finger = Fingers[f];
            int b = BoneIndex(f, 2);
            return _origin[b] + _rotation[b].Rotate(AxisZ * (finger.Lengths[2] + finger.Radii[3] * radiusFraction));
        }

        private HandVec ToGrip(HandVec handPoint, HandQuat align, float mirror)
        {
            HandVec g = align.Rotate((handPoint - PalmAnchor) * _size) + GripAnchor;
            return new HandVec(g.X * mirror, g.Y, g.Z);
        }

        internal void GetFrame(out HandVec knuckle, out HandVec forward, out HandVec back, out float length)
        {
            HandQuat align = AlignRotation();
            float mirror = _left ? -1f : 1f;
            HandVec k = (Fingers[Index].Base + Fingers[Pinky].Base) * 0.5f;
            knuckle = ToGrip(k, align, mirror);
            forward = (ToGrip(k + AxisZ * 0.01f, align, mirror) - knuckle).Normalized;
            back = (ToGrip(k + AxisY * 0.01f, align, mirror) - knuckle).Normalized;
            Finger middle = Fingers[Middle];
            HandVec tip = middle.Base + AxisZ * (middle.Lengths[0] + middle.Lengths[1] + middle.Lengths[2]);
            length = (ToGrip(tip, align, mirror) - ToGrip(new HandVec(0f, 0f, 0f), align, mirror)).Length;
        }

        private HandQuat AlignRotation()
        {
            HandQuat toGrip = HandQuat.LookRotation(new HandVec(0f, -1f, 0f), new HandVec(1f, 0f, 0f));
            return HandQuat.AngleAxis(-_tilt, AxisX) * toGrip;
        }

        private static HandQuat ThumbRestBase() { return HandQuat.LookRotation(ThumbRestForward, ThumbRestUp); }
        private static HandQuat ThumbUpBase() { return HandQuat.LookRotation(ThumbUpForward, ThumbUpUp); }

        private static int BoneIndex(int finger, int joint) { return 1 + finger * 3 + joint; }

        private void BuildBindSkeleton()
        {
            _bindRotation[0] = HandQuat.Identity;
            _bindOrigin[0] = new HandVec(0f, 0f, 0f);
            for (int f = 0; f < Fingers.Length; f++)
            {
                HandQuat baseRotation = f == Thumb ? ThumbRestBase() : HandQuat.AngleAxis(Fingers[f].BindYaw, AxisY);
                Chain(f, baseRotation, 0f, 0f, 0f, _bindRotation, _bindOrigin);
            }
        }

        private void BuildPalm()
        {
            int start = _bindPositions.Count;
            const float zMin = -0.004f, zMax = 0.098f;
            for (int ring = 0; ring < PalmRings; ring++)
            {
                float u = ring / (float)(PalmRings - 1);
                float z = Lerp(zMin, zMax, u);
                float cap = (float)Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(Math.Abs(2.0 * u - 1.0), 5.0)), 1.0 / 5.0);
                float halfWidth = Lerp(0.031f, 0.0425f, Smooth(Clamp01(u * 1.15f))) * cap;
                float halfThick = Lerp(0.0122f, 0.0108f, u) * (0.35f + 0.65f * cap);
                float centerX = Lerp(0.0015f, -0.0012f, u);
                if (ring == 0 || ring == PalmRings - 1)
                {
                    AddVertex(new HandVec(centerX, 0f, z), new HandVec(0f, 0f, ring == 0 ? -1f : 1f), 0, 0, 0f, 1f);
                    continue;
                }
                for (int side = 0; side < PalmSides; side++)
                {
                    double t = side * 2.0 * Math.PI / PalmSides;
                    float c = (float)Math.Cos(t), s = (float)Math.Sin(t);
                    float x = centerX + halfWidth * Superellipse(c, 3.2f);
                    float y = halfThick * Superellipse(s, 2.4f);
                    if (y < 0f) y *= 1.05f;
                    AddVertex(new HandVec(x, y, z), new HandVec(0f, 0f, 1f), 0, 0, 0f, 1f);
                }
            }
            int pole0 = start;
            int pole1 = start + 1 + (PalmRings - 2) * PalmSides;
            for (int side = 0; side < PalmSides; side++)
            {
                int next = (side + 1) % PalmSides;
                _triangles.Add(pole0); _triangles.Add(start + 1 + next); _triangles.Add(start + 1 + side);
            }
            for (int ring = 0; ring < PalmRings - 3; ring++)
                Grid(start + 1 + ring * PalmSides, start + 1 + (ring + 1) * PalmSides, PalmSides);
            int last = start + 1 + (PalmRings - 3) * PalmSides;
            for (int side = 0; side < PalmSides; side++)
            {
                int next = (side + 1) % PalmSides;
                _triangles.Add(pole1); _triangles.Add(last + side); _triangles.Add(last + next);
            }
            RecalculateNormals(start, _bindPositions.Count);
        }

        private void BuildWrist()
        {
            int start = _bindPositions.Count;
            float[] zs = { 0.016f, 0.004f, -0.010f, -0.022f, -0.032f };
            float[] fades = { 1f, 1f, 0.75f, 0.32f, 0f };
            for (int ring = 0; ring < zs.Length; ring++)
            {
                float z = zs[ring];
                float halfWidth = Lerp(0.0285f, 0.0262f, ring / (float)(zs.Length - 1));
                float halfThick = Lerp(0.0165f, 0.0158f, ring / (float)(zs.Length - 1));
                for (int side = 0; side < WristSides; side++)
                {
                    double t = side * 2.0 * Math.PI / WristSides;
                    float c = (float)Math.Cos(t), s = (float)Math.Sin(t);
                    HandVec p = new HandVec(0.0015f + halfWidth * Superellipse(c, 2.3f), -0.001f + halfThick * Superellipse(s, 2.3f), z);
                    HandVec n = new HandVec(c / halfWidth, s / halfThick, 0f).Normalized;
                    AddVertex(p, n, 0, 0, 0f, fades[ring]);
                }
            }
            for (int ring = zs.Length - 1; ring > 0; ring--)
                Grid(start + ring * WristSides, start + (ring - 1) * WristSides, WristSides);
        }

        private void BuildFinger(int f)
        {
            Finger finger = Fingers[f];
            float l0 = finger.Lengths[0], l1 = finger.Lengths[1], l2 = finger.Lengths[2];
            float s1 = l0, s2 = l0 + l1, s3 = l0 + l1 + l2;
            float r0 = finger.Radii[0], rTip = finger.Radii[3];
            var rings = new List<float[]>();
            AddCapRings(rings, 0f, r0, -1f, 0);
            AddRing(rings, 0.00f, 0, 0, 0f);
            AddRing(rings, l0 * 0.35f, 0, 0, 0f);
            AddRing(rings, l0 * 0.72f, 0, 1, 0.12f);
            AddRing(rings, s1, 0, 1, 0.5f);
            AddRing(rings, s1 + l1 * 0.28f, 1, 0, 0.12f);
            AddRing(rings, s1 + l1 * 0.70f, 1, 2, 0.12f);
            AddRing(rings, s2, 1, 2, 0.5f);
            AddRing(rings, s2 + l2 * 0.30f, 2, 1, 0.1f);
            AddRing(rings, s2 + l2 * 0.70f, 2, 2, 0f);
            AddRing(rings, s3, 2, 2, 0f);
            AddCapRings(rings, s3, rTip, 1f, 2);

            HandQuat frame = _bindRotation[BoneIndex(f, 0)];
            HandVec origin = _bindOrigin[BoneIndex(f, 0)];
            int start = _bindPositions.Count;
            float squash = f == Thumb ? 0.86f : 0.9f;
            for (int i = 0; i < rings.Count; i++)
            {
                float[] ring = rings[i];
                float s = ring[0];
                float radius = ring[4] >= 0f ? ring[4] : FingerRadius(finger, s);
                int boneA = BoneIndex(f, (int)ring[1]);
                int boneB = BoneIndex(f, (int)ring[2]);
                float weight = ring[3];
                if (radius <= 0f)
                {
                    HandVec axisNormal = new HandVec(0f, 0f, ring[5]);
                    AddVertex(origin + frame.Rotate(AxisZ * s), frame.Rotate(axisNormal), boneA, boneB, weight, 1f);
                    continue;
                }
                float capNormalZ = ring[5];
                for (int side = 0; side < FingerSides; side++)
                {
                    double t = side * 2.0 * Math.PI / FingerSides;
                    float c = (float)Math.Cos(t), sn = (float)Math.Sin(t);
                    HandVec local = new HandVec(c * radius, sn * radius * squash, s);
                    HandVec radial = new HandVec(c, sn / squash, 0f).Normalized;
                    float radialScale = (float)Math.Sqrt(Math.Max(0f, 1f - capNormalZ * capNormalZ));
                    HandVec normal = new HandVec(radial.X * radialScale, radial.Y * radialScale, capNormalZ).Normalized;
                    AddVertex(origin + frame.Rotate(local), frame.Rotate(normal), boneA, boneB, weight, 1f);
                }
            }
            int cursor = start;
            for (int i = 0; i < rings.Count - 1; i++)
            {
                bool currentPole = rings[i][4] == 0f;
                bool nextPole = rings[i + 1][4] == 0f;
                int currentCount = currentPole ? 1 : FingerSides;
                int next = cursor + currentCount;
                if (currentPole)
                {
                    for (int side = 0; side < FingerSides; side++)
                    {
                        int n = (side + 1) % FingerSides;
                        _triangles.Add(cursor); _triangles.Add(next + n); _triangles.Add(next + side);
                    }
                }
                else if (nextPole)
                {
                    for (int side = 0; side < FingerSides; side++)
                    {
                        int n = (side + 1) % FingerSides;
                        _triangles.Add(next); _triangles.Add(cursor + side); _triangles.Add(cursor + n);
                    }
                }
                else Grid(cursor, next, FingerSides);
                cursor = next;
            }
        }

        private static void AddRing(List<float[]> rings, float s, int boneA, int boneB, float weight)
        {
            rings.Add(new[] { s, boneA, boneB, weight, -1f, 0f });
        }

        private static void AddCapRings(List<float[]> rings, float s, float radius, float direction, int bone)
        {
            float[] angles = direction < 0f ? new[] { 0f, 35f, 62f, 82f } : new[] { 82f, 62f, 35f, 0f };
            for (int i = 0; i < angles.Length; i++)
            {
                double a = angles[i] * Math.PI / 180.0;
                float ringRadius = (float)Math.Sin(a) * radius;
                float offset = (float)Math.Cos(a) * radius * direction;
                float normalZ = (float)Math.Cos(a) * direction;
                if (angles[i] == 0f) ringRadius = 0f;
                rings.Add(new[] { s + offset, bone, bone, 0f, ringRadius, normalZ });
            }
        }

        private static float FingerRadius(Finger finger, float s)
        {
            float l0 = finger.Lengths[0], l1 = finger.Lengths[1], l2 = finger.Lengths[2];
            if (s <= l0) return Lerp(finger.Radii[0], finger.Radii[1], Clamp01(s / l0));
            if (s <= l0 + l1) return Lerp(finger.Radii[1], finger.Radii[2], (s - l0) / l1);
            return Lerp(finger.Radii[2], finger.Radii[3], Clamp01((s - l0 - l1) / l2));
        }

        private void AddVertex(HandVec position, HandVec normal, int boneA, int boneB, float weightB, float fade)
        {
            _bindPositions.Add(position);
            _bindNormals.Add(normal.Normalized);
            _boneA.Add(boneA);
            _boneB.Add(boneB);
            _weightB.Add(weightB);
            _fadeList.Add(fade);
        }

        private void Grid(int ringA, int ringB, int sides)
        {
            for (int side = 0; side < sides; side++)
            {
                int next = (side + 1) % sides;
                int a = ringA + side, b = ringA + next, c = ringB + side, d = ringB + next;
                _triangles.Add(a); _triangles.Add(b); _triangles.Add(c);
                _triangles.Add(b); _triangles.Add(d); _triangles.Add(c);
            }
        }

        private void RecalculateNormals(int start, int end)
        {
            var sums = new HandVec[end - start];
            for (int i = 0; i < _triangles.Count; i += 3)
            {
                int a = _triangles[i], b = _triangles[i + 1], c = _triangles[i + 2];
                if (a < start || a >= end) continue;
                HandVec n = HandVec.Cross(_bindPositions[b] - _bindPositions[a], _bindPositions[c] - _bindPositions[a]);
                sums[a - start] = sums[a - start] + n;
                sums[b - start] = sums[b - start] + n;
                sums[c - start] = sums[c - start] + n;
            }
            for (int i = start; i < end; i++) _bindNormals[i] = sums[i - start].Normalized;
        }

        private void WriteMatrix(HandQuat q, int bone)
        {
            float x = q.X, y = q.Y, z = q.Z, w = q.W;
            int m = bone * 9;
            _matrix[m] = 1f - 2f * (y * y + z * z);
            _matrix[m + 1] = 2f * (x * y - z * w);
            _matrix[m + 2] = 2f * (x * z + y * w);
            _matrix[m + 3] = 2f * (x * y + z * w);
            _matrix[m + 4] = 1f - 2f * (x * x + z * z);
            _matrix[m + 5] = 2f * (y * z - x * w);
            _matrix[m + 6] = 2f * (x * z - y * w);
            _matrix[m + 7] = 2f * (y * z + x * w);
            _matrix[m + 8] = 1f - 2f * (x * x + y * y);
        }

        private HandVec Transform(int bone, HandVec v)
        {
            int m = bone * 9;
            return new HandVec(
                _matrix[m] * v.X + _matrix[m + 1] * v.Y + _matrix[m + 2] * v.Z,
                _matrix[m + 3] * v.X + _matrix[m + 4] * v.Y + _matrix[m + 5] * v.Z,
                _matrix[m + 6] * v.X + _matrix[m + 7] * v.Y + _matrix[m + 8] * v.Z);
        }

        private static float Superellipse(float value, float exponent)
        {
            float magnitude = (float)Math.Pow(Math.Abs(value), 2.0 / exponent);
            return value < 0f ? -magnitude : magnitude;
        }

        private static byte ToByte(float value)
        {
            if (value <= 0f) return 0;
            if (value >= 1f) return 255;
            return (byte)(value * 255f + 0.5f);
        }

        private static float Smooth(float t) { t = Clamp01(t); return t * t * (3f - 2f * t); }
        private static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
        private static float Clamp01(float value) { return value < 0f ? 0f : value > 1f ? 1f : value; }
        private static float Clamp(float value, float min, float max) { return value < min ? min : value > max ? max : value; }
    }
}
