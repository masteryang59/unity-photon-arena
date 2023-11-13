using System;
using System.Globalization;

namespace PhotonArena.Core.Sim
{
    /// <summary>
    /// Minimal 2D vector for the arena plane (x = world X, y = world Z in Unity).
    /// The core assembly has no UnityEngine reference, so it can be tested with plain dotnet.
    /// </summary>
    [Serializable]
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public readonly float X;
        public readonly float Y;

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public static readonly Vec2 Zero = new Vec2(0f, 0f);

        public float Length => MathF.Sqrt(X * X + Y * Y);
        public float LengthSquared => X * X + Y * Y;

        public Vec2 Normalized
        {
            get
            {
                float len = Length;
                return len > 1e-6f ? new Vec2(X / len, Y / len) : Zero;
            }
        }

        /// <summary>Same direction, length at most <paramref name="max"/>.</summary>
        public Vec2 ClampLength(float max)
        {
            float len = Length;
            return len > max && len > 1e-6f ? this * (max / len) : this;
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator *(float s, Vec2 a) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator /(Vec2 a, float s) => new Vec2(a.X / s, a.Y / s);

        public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;
        public static float DistanceSquared(Vec2 a, Vec2 b) => (a - b).LengthSquared;

        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => new Vec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        /// <summary>Unit vector for an angle in radians, measured from +X toward +Y.</summary>
        public static Vec2 FromAngle(float radians) => new Vec2(MathF.Cos(radians), MathF.Sin(radians));

        public float Angle => MathF.Atan2(Y, X);

        /// <summary>Distance from point <paramref name="p"/> to segment a-b.</summary>
        public static float DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            float lenSq = ab.LengthSquared;
            if (lenSq < 1e-12f) return Distance(p, a);
            float t = Math.Clamp(Dot(p - a, ab) / lenSq, 0f, 1f);
            return Distance(p, a + ab * t);
        }

        public bool Equals(Vec2 other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object obj) => obj is Vec2 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public static bool operator ==(Vec2 a, Vec2 b) => a.Equals(b);
        public static bool operator !=(Vec2 a, Vec2 b) => !a.Equals(b);

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", X, Y);
    }
}


    public static object AuditAction(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object AuditAction(object data = null)
    {
        if (data == null) return null;
        return data;
    }