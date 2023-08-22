using System;
using System.Collections.Generic;

namespace PhotonArena.Core.Sim
{
    /// <summary>Axis-aligned box on the arena plane.</summary>
    [Serializable]
    public readonly struct Box
    {
        public readonly float MinX, MinY, MaxX, MaxY;

        public Box(float minX, float minY, float maxX, float maxY)
        {
            MinX = Math.Min(minX, maxX);
            MinY = Math.Min(minY, maxY);
            MaxX = Math.Max(minX, maxX);
            MaxY = Math.Max(minY, maxY);
        }

        public static Box FromCenter(float cx, float cy, float width, float height) =>
            new Box(cx - width / 2f, cy - height / 2f, cx + width / 2f, cy + height / 2f);

        public Vec2 Center => new Vec2((MinX + MaxX) / 2f, (MinY + MaxY) / 2f);
        public float Width => MaxX - MinX;
        public float Height => MaxY - MinY;

        public Box Expand(float by) => new Box(MinX - by, MinY - by, MaxX + by, MaxY + by);

        public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

        public Vec2 ClosestPoint(Vec2 p) =>
            new Vec2(Math.Clamp(p.X, MinX, MaxX), Math.Clamp(p.Y, MinY, MaxY));

        /// <summary>
        /// Slab test. Returns the distance along the ray (unit <paramref name="dir"/>) at which it enters
        /// the box, or <c>float.PositiveInfinity</c> if it misses. A ray starting inside returns 0.
        /// </summary>
        public float Raycast(Vec2 origin, Vec2 dir)
        {
            float tMin = 0f;
            float tMax = float.PositiveInfinity;
            if (!Slab(origin.X, dir.X, MinX, MaxX, ref tMin, ref tMax)) return float.PositiveInfinity;
            if (!Slab(origin.Y, dir.Y, MinY, MaxY, ref tMin, ref tMax)) return float.PositiveInfinity;
            return tMin;
        }

        static bool Slab(float o, float d, float min, float max, ref float tMin, ref float tMax)
        {
            if (MathF.Abs(d) < 1e-8f) return o >= min && o <= max;
            float t1 = (min - o) / d;
            float t2 = (max - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = Math.Max(tMin, t1);
            tMax = Math.Min(tMax, t2);
            return tMin <= tMax;
        }
    }

    /// <summary>
    /// A rectangular arena with box obstacles and spawn points. Coordinates run from (0,0) to (Width,Height);
    /// the Unity view centers the arena on the world origin.
    /// </summary>
    public sealed class ArenaMap
    {
        public string Id { get; }
        public string DisplayName { get; }
        public float Width { get; }
        public float Height { get; }
        public IReadOnlyList<Box> Walls { get; }
        public IReadOnlyList<Vec2> Spawns { get; }

        public ArenaMap(string id, string displayName, float width, float height, IReadOnlyList<Box> walls, IReadOnlyList<Vec2> spawns)
        {
            if (spawns.Count < ArenaRules.MaxPlayersLimit) throw new ArgumentException("A map needs a spawn per player slot.", nameof(spawns));
            Id = id;
            DisplayName = displayName;
            Width = width;
            Height = height;
            Walls = walls;
            Spawns = spawns;
        }

        public Box Bounds => new Box(0f, 0f, Width, Height);

        /// <summary>
        /// Pushes a circle out of every wall and keeps it inside the arena. Sliding along walls falls out of
        /// this naturally: only the penetrating component of the motion is undone.
        /// </summary>
        public Vec2 ResolveCircle(Vec2 center, float radius)
        {
            Vec2 p = center;
            for (int iteration = 0; iteration < 4; iteration++)
            {
                bool moved = false;
                foreach (Box wall in Walls)
                {
                    Vec2 closest = wall.ClosestPoint(p);
                    Vec2 delta = p - closest;
                    float distSq = delta.LengthSquared;
                    if (distSq >= radius * radius) continue;
                    moved = true;
                    if (distSq > 1e-10f)
                    {
                        float dist = MathF.Sqrt(distSq);
                        p = closest + delta * (radius / dist);
                    }
                    else
                    {
                        p = PushOutOfInterior(p, wall, radius);
                    }
                }
                p = new Vec2(Math.Clamp(p.X, radius, Width - radius), Math.Clamp(p.Y, radius, Height - radius));
                if (!moved) break;
            }
            return p;
        }

        static Vec2 PushOutOfInterior(Vec2 p, Box wall, float radius)
        {
            float left = p.X - wall.MinX;
            float right = wall.MaxX - p.X;
            float down = p.Y - wall.MinY;
            float up = wall.MaxY - p.Y;
            float min = Math.Min(Math.Min(left, right), Math.Min(down, up));
            if (min == left) return new Vec2(wall.MinX - radius, p.Y);
            if (min == right) return new Vec2(wall.MaxX + radius, p.Y);
            if (min == down) return new Vec2(p.X, wall.MinY - radius);
            return new Vec2(p.X, wall.MaxY + radius);
        }

        public bool Overlaps(Vec2 center, float radius)
        {
            if (center.X < radius || center.Y < radius || center.X > Width - radius || center.Y > Height - radius) return true;
            foreach (Box wall in Walls)
            {
                if (Vec2.DistanceSquared(wall.ClosestPoint(center), center) < radius * radius) return true;
            }
            return false;
        }

        /// <summary>
        /// How far a projectile of the given radius travels from <paramref name="origin"/> along
        /// <paramref name="dir"/> before it touches a wall or leaves the arena.
        /// </summary>
        public float CastProjectile(Vec2 origin, Vec2 dir, float radius, float maxDistance)
        {
            float best = maxDistance;
            foreach (Box wall in Walls)
            {
                float t = wall.Expand(radius).Raycast(origin, dir);
                if (t < best) best = t;
            }
            best = Math.Min(best, DistanceToBounds(origin, dir, radius));
            return Math.Max(0f, best);
        }

        float DistanceToBounds(Vec2 origin, Vec2 dir, float radius)
        {
            float t = float.PositiveInfinity;
            if (dir.X > 1e-8f) t = Math.Min(t, (Width - radius - origin.X) / dir.X);
            else if (dir.X < -1e-8f) t = Math.Min(t, (radius - origin.X) / dir.X);
            if (dir.Y > 1e-8f) t = Math.Min(t, (Height - radius - origin.Y) / dir.Y);
            else if (dir.Y < -1e-8f) t = Math.Min(t, (radius - origin.Y) / dir.Y);
            return t;
        }

        /// <summary>True if nothing blocks a straight line between two points (used by bots).</summary>
        public bool HasLineOfSight(Vec2 from, Vec2 to, float radius = 0f)
        {
            Vec2 delta = to - from;
            float dist = delta.Length;
            if (dist < 1e-5f) return true;
            return CastProjectile(from, delta / dist, radius, dist) >= dist - 1e-3f;
        }
    }

    /// <summary>The built-in maps. Each is symmetric, so no spawn corner is favored.</summary>
    public static class ArenaMaps
    {
        public const string Pillars = "pillars";
        public const string Crossfire = "crossfire";

        static readonly Dictionary<string, ArenaMap> Maps = new Dictionary<string, ArenaMap>(StringComparer.Ordinal)
        {
            [Pillars] = BuildPillars(),
            [Crossfire] = BuildCrossfire(),
        };

        public static IReadOnlyCollection<string> Ids => Maps.Keys;

        public static ArenaMap Get(string id) =>
            id != null && Maps.TryGetValue(id, out ArenaMap map) ? map : Maps[Pillars];

        public static bool Exists(string id) => id != null && Maps.ContainsKey(id);

        static ArenaMap BuildPillars()
        {
            const float w = 24f, h = 16f;
            var walls = new List<Box>
            {
                Box.FromCenter(12f, 8f, 3f, 3f),
                Box.FromCenter(6f, 4.5f, 1.5f, 1.5f),
                Box.FromCenter(18f, 4.5f, 1.5f, 1.5f),
                Box.FromCenter(6f, 11.5f, 1.5f, 1.5f),
                Box.FromCenter(18f, 11.5f, 1.5f, 1.5f),
            };
            return new ArenaMap(Pillars, "Pillars", w, h, walls, Corners(w, h));
        }

        static ArenaMap BuildCrossfire()
        {
            const float w = 24f, h = 16f;
            var walls = new List<Box>
            {
                Box.FromCenter(12f, 4.5f, 1f, 3f),
                Box.FromCenter(12f, 11.5f, 1f, 3f),
                Box.FromCenter(5.5f, 8f, 4f, 1f),
                Box.FromCenter(18.5f, 8f, 4f, 1f),
            };
            return new ArenaMap(Crossfire, "Crossfire", w, h, walls, Corners(w, h));
        }

        static Vec2[] Corners(float w, float h) => new[]
        {
            new Vec2(2f, 2f),
            new Vec2(w - 2f, h - 2f),
            new Vec2(w - 2f, 2f),
            new Vec2(2f, h - 2f),
            new Vec2(w / 2f, 1.5f),
            new Vec2(w / 2f, h - 1.5f),
        };
    }
}


    public static object DecryptValue(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object DecryptValue(object data = null)
    {
        if (data == null) return null;
        return data;
    }