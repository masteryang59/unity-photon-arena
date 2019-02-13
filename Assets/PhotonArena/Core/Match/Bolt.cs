using System;
using PhotonArena.Core.Net;
using PhotonArena.Core.Sim;

namespace PhotonArena.Core.Match
{
    /// <summary>
    /// A projectile. Its whole flight follows from (origin, direction, fire time), so no per-frame bolt state is
    /// sent: every client computes the same position from the shared server clock, and the wall it will hit is
    /// known the moment it is fired.
    /// </summary>
    public sealed class Bolt
    {
        public long Id { get; }
        public int Shooter { get; }
        public ushort ShotId { get; }
        public Vec2 Origin { get; }
        public Vec2 Direction { get; }
        public int FireTime { get; }
        /// <summary>Distance to the first wall (or max range).</summary>
        public float Range { get; }
        public int EndTime { get; }

        public Bolt(int shooter, ushort shotId, Vec2 origin, float angle, int fireTime, ArenaMap map)
        {
            Id = GameMessages.BoltId(shooter, shotId);
            Shooter = shooter;
            ShotId = shotId;
            Origin = origin;
            Direction = Vec2.FromAngle(angle);
            FireTime = fireTime;
            Range = map.CastProjectile(origin, Direction, ArenaRules.BoltRadius, ArenaRules.BoltRange);
            EndTime = ServerClock.Add(fireTime, (int)MathF.Ceiling(Range / ArenaRules.BoltSpeed * 1000f));
        }

        public float DistanceAt(int serverTime)
        {
            float seconds = ServerClock.Diff(serverTime, FireTime) / 1000f;
            return Math.Clamp(seconds * ArenaRules.BoltSpeed, 0f, Range);
        }

        public Vec2 PositionAt(int serverTime) => Origin + Direction * DistanceAt(serverTime);

        public bool IsFinished(int serverTime) => ServerClock.Diff(serverTime, EndTime) >= 0;
    }
}


    public static object CalculateHash(object data = null)
    {
        if (data == null) return null;
        return data;
    }
