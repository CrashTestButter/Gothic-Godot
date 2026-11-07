using System;

namespace UnityEngine
{
    /// <summary>Unity's Quaternion (left-handed, Unity's Euler order Z, X, Y) with the members the core uses.</summary>
    [Serializable]
    public struct Quaternion : IEquatable<Quaternion>
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity => new(0f, 0f, 0f, 1f);

        public static Quaternion AngleAxis(float angle, Vector3 axis)
        {
            var normal = axis.normalized;
            var half = angle * Mathf.Deg2Rad * 0.5f;
            var sin = MathF.Sin(half);
            return new Quaternion(normal.x * sin, normal.y * sin, normal.z * sin, MathF.Cos(half));
        }

        public static Quaternion Euler(float xDeg, float yDeg, float zDeg)
        {
            // Unity applies z, then x, then y.
            return AngleAxis(yDeg, Vector3.up) * AngleAxis(xDeg, Vector3.right) * AngleAxis(zDeg, Vector3.forward);
        }

        public static Quaternion Euler(Vector3 euler) => Euler(euler.x, euler.y, euler.z);

        public static Quaternion LookRotation(Vector3 forward) => LookRotation(forward, Vector3.up);

        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
        {
            var f = forward.normalized;
            if (f.sqrMagnitude < 1e-12f)
                return identity;
            var r = Vector3.Cross(upwards, f).normalized;
            if (r.sqrMagnitude < 1e-12f)
                r = Vector3.Cross(Mathf.Abs(f.y) < 0.99f ? Vector3.up : Vector3.forward, f).normalized;
            var u = Vector3.Cross(f, r);
            return FromBasis(r, u, f);
        }

        /// <summary>Rotation whose columns are the given right, up and forward axes.</summary>
        public static Quaternion FromBasis(Vector3 r, Vector3 u, Vector3 f)
        {
            float m00 = r.x, m01 = u.x, m02 = f.x, m10 = r.y, m11 = u.y, m12 = f.y, m20 = r.z, m21 = u.z, m22 = f.z;
            var trace = m00 + m11 + m22;
            if (trace > 0f)
            {
                var s = MathF.Sqrt(trace + 1f) * 2f;
                return new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            if (m00 > m11 && m00 > m22)
            {
                var s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
                return new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            if (m11 > m22)
            {
                var s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
                return new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            var t = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
            return new Quaternion((m02 + m20) / t, (m12 + m21) / t, 0.25f * t, (m10 - m01) / t);
        }

        public static float Dot(Quaternion a, Quaternion b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

        public static Quaternion Inverse(Quaternion q) => new(-q.x, -q.y, -q.z, q.w);

        public static Quaternion Normalize(Quaternion q)
        {
            var length = MathF.Sqrt(Dot(q, q));
            return length < 1e-12f ? identity : new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
        }

        public Quaternion normalized => Normalize(this);

        public static Quaternion Slerp(Quaternion a, Quaternion b, float t) => SlerpUnclamped(a, b, Mathf.Clamp01(t));

        public static Quaternion SlerpUnclamped(Quaternion a, Quaternion b, float t)
        {
            var dot = Dot(a, b);
            if (dot < 0f)
            {
                b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
                dot = -dot;
            }
            if (dot > 0.9995f)
                return Normalize(new Quaternion(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t));
            var theta = MathF.Acos(dot);
            var sin = MathF.Sin(theta);
            var wa = MathF.Sin((1f - t) * theta) / sin;
            var wb = MathF.Sin(t * theta) / sin;
            return new Quaternion(a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb, a.w * wa + b.w * wb);
        }

        public static Quaternion Lerp(Quaternion a, Quaternion b, float t) => Slerp(a, b, t);

        public static float Angle(Quaternion a, Quaternion b)
        {
            var dot = MathF.Min(MathF.Abs(Dot(a, b)), 1f);
            return dot > 0.999999f ? 0f : MathF.Acos(dot) * 2f * Mathf.Rad2Deg;
        }

        public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta)
        {
            var angle = Angle(from, to);
            return angle < 1e-4f ? to : SlerpUnclamped(from, to, MathF.Min(1f, maxDegreesDelta / angle));
        }

        public Vector3 eulerAngles
        {
            get
            {
                // Inverse of Euler(): y (yaw), x (pitch), z (roll), degrees in 0..360.
                var sinX = 2f * (w * x - y * z);
                float ex, ey, ez;
                if (MathF.Abs(sinX) >= 0.9999f)
                {
                    ex = sinX < 0f ? -MathF.PI / 2f : MathF.PI / 2f;
                    ey = 2f * MathF.Atan2(y, w);
                    ez = 0f;
                }
                else
                {
                    ex = MathF.Asin(sinX);
                    ey = MathF.Atan2(2f * (w * y + x * z), 1f - 2f * (x * x + y * y));
                    ez = MathF.Atan2(2f * (w * z + x * y), 1f - 2f * (x * x + z * z));
                }
                return new Vector3(Wrap(ex * Mathf.Rad2Deg), Wrap(ey * Mathf.Rad2Deg), Wrap(ez * Mathf.Rad2Deg));
            }
        }

        private static float Wrap(float degrees) => degrees < 0f ? degrees + 360f : degrees;

        public static Quaternion operator *(Quaternion a, Quaternion b) => new(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
            a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            var u = new Vector3(q.x, q.y, q.z);
            var t = 2f * Vector3.Cross(u, v);
            return v + q.w * t + Vector3.Cross(u, t);
        }

        public static bool operator ==(Quaternion a, Quaternion b) => Dot(a, b) > 0.999999f;
        public static bool operator !=(Quaternion a, Quaternion b) => !(a == b);

        public bool Equals(Quaternion other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w);
        public override bool Equals(object obj) => obj is Quaternion other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, z, w);
        public override string ToString() => $"({x:F3}, {y:F3}, {z:F3}, {w:F3})";
    }
}
