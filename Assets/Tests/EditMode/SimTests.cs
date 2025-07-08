using System;
using NUnit.Framework;
using PhotonArena.Core.Match;
using PhotonArena.Core.Net;
using PhotonArena.Core.Sim;

namespace PhotonArena.Tests
{
    public class ArenaMapTests
    {
        [Test]
        public void EveryMap_HasFreeSpawns_InsideTheArena()
        {
            foreach (string id in ArenaMaps.Ids)
            {
                ArenaMap map = ArenaMaps.Get(id);
                foreach (Vec2 spawn in map.Spawns)
                {
                    Assert.That(map.Overlaps(spawn, ArenaRules.PlayerRadius), Is.False, $"{id} spawn {spawn}");
                }
            }
        }

        [Test]
        public void UnknownMapIds_FallBackToPillars()
        {
            Assert.That(ArenaMaps.Get("nope").Id, Is.EqualTo(ArenaMaps.Pillars));
            Assert.That(ArenaMaps.Get(null).Id, Is.EqualTo(ArenaMaps.Pillars));
            Assert.That(ArenaMaps.Exists("crossfire"), Is.True);
        }

        [Test]
        public void ResolveCircle_PushesOutOfWalls_AndSlidesAlongThem()
        {
            ArenaMap map = ArenaMaps.Get(ArenaMaps.Pillars);
            // Center block spans x 10.5..13.5, y 6.5..9.5. Walk diagonally into its left face.
            Vec2 p = new Vec2(9.8f, 7.5f);
            Vec2 next = map.ResolveCircle(p + new Vec2(0.3f, 0.3f), ArenaRules.PlayerRadius);
            Assert.That(next.X, Is.EqualTo(10.5f - ArenaRules.PlayerRadius).Within(1e-4f), "pushed back out along x");
            Assert.That(next.Y, Is.EqualTo(7.8f).Within(1e-4f), "kept the motion along the wall");
            Assert.That(map.Overlaps(next, ArenaRules.PlayerRadius - 1e-3f), Is.False);
        }

        [Test]
        public void ResolveCircle_HandlesACenterDeepInsideAWall()
        {
            ArenaMap map = ArenaMaps.Get(ArenaMaps.Pillars);
            Vec2 resolved = map.ResolveCircle(new Vec2(12.2f, 8f), ArenaRules.PlayerRadius);
            Assert.That(map.Overlaps(resolved, ArenaRules.PlayerRadius - 1e-3f), Is.False);
            Assert.That(resolved.X, Is.EqualTo(13.5f + ArenaRules.PlayerRadius).Within(1e-4f), "nearest face");
        }

        [Test]
        public void ResolveCircle_KeepsPlayersInsideTheBounds()
        {
            ArenaMap map = ArenaMaps.Get(ArenaMaps.Pillars);
            Vec2 p = map.ResolveCircle(new Vec2(-5f, 100f), ArenaRules.PlayerRadius);
            Assert.That(p, Is.EqualTo(new Vec2(ArenaRules.PlayerRadius, map.Height - ArenaRules.PlayerRadius)));
        }

        [Test]
        public void CastProjectile_StopsAtTheFirstWallOrTheBounds()
        {
            ArenaMap map = ArenaMaps.Get(ArenaMaps.Pillars);
            float r = ArenaRules.BoltRadius;
            // From the left, straight at the center block (left face at x = 10.5).
            Assert.That(map.CastProjectile(new Vec2(2f, 8f), new Vec2(1f, 0f), r, 50f), Is.EqualTo(10.5f - r - 2f).Within(1e-4f));
            // Along the bottom edge, nothing in the way until the right bound.
            Assert.That(map.CastProjectile(new Vec2(1f, 1f), new Vec2(1f, 0f), r, 50f), Is.EqualTo(24f - r - 1f).Within(1e-4f));
            // Max range wins when nothing is closer.
            Assert.That(map.CastProjectile(new Vec2(1f, 1f), new Vec2(1f, 0f), r, 5f), Is.EqualTo(5f));
            // Starting inside a wall: zero.
            Assert.That(map.CastProjectile(new Vec2(12f, 8f), new Vec2(1f, 0f), r, 50f), Is.EqualTo(0f));
        }

        [Test]
        public void LineOfSight_IsBlockedByWalls()
        {
            ArenaMap map = ArenaMaps.Get(ArenaMaps.Pillars);
            Assert.That(map.HasLineOfSight(new Vec2(2f, 8f), new Vec2(22f, 8f)), Is.False);
            Assert.That(map.HasLineOfSight(new Vec2(2f, 1f), new Vec2(22f, 1f)), Is.True);
        }

        [Test]
        public void Box_RaycastMissesWhenParallelAndOutside()
        {
            var box = new Box(0, 0, 1, 1);
            Assert.That(box.Raycast(new Vec2(-1f, 2f), new Vec2(1f, 0f)), Is.EqualTo(float.PositiveInfinity));
            Assert.That(box.Raycast(new Vec2(-1f, 0.5f), new Vec2(1f, 0f)), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void Vec2_SegmentDistance()
        {
            Assert.That(Vec2.DistanceToSegment(new Vec2(0, 1), new Vec2(-1, 0), new Vec2(1, 0)), Is.EqualTo(1f).Within(1e-6f));
            Assert.That(Vec2.DistanceToSegment(new Vec2(3, 0), new Vec2(-1, 0), new Vec2(1, 0)), Is.EqualTo(2f).Within(1e-6f));
            Assert.That(Vec2.DistanceToSegment(new Vec2(3, 4), Vec2.Zero, Vec2.Zero), Is.EqualTo(5f).Within(1e-6f));
            Assert.That(new Vec2(3, 4).ClampLength(1f).Length, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(Vec2.Zero.Normalized, Is.EqualTo(Vec2.Zero));
        }
    }

    public class SnapshotBufferTests
    {
        static SnapshotBuffer.Snapshot S(int t, float x, float vx = 0, bool alive = true) =>
            new SnapshotBuffer.Snapshot { Time = t, Position = new Vec2(x, 0), Velocity = new Vec2(vx, 0), Alive = alive };

        [Test]
        public void Interpolates_BetweenTheSurroundingSnapshots()
        {
            var b = new SnapshotBuffer();
            b.Add(S(1000, 0));
            b.Add(S(1050, 5));
            b.Add(S(1100, 10));
            Assert.That(b.SampleAt(1025).Position.X, Is.EqualTo(2.5f).Within(1e-5f));
            Assert.That(b.SampleAt(1075).Position.X, Is.EqualTo(7.5f).Within(1e-5f));
            Assert.That(b.SampleAt(900).Position.X, Is.EqualTo(0f), "before the oldest: hold the oldest");
            Assert.That(b.SampleAt(1100).Extrapolated, Is.False);
        }

        [Test]
        public void Extrapolates_ForAShortGap_ThenHolds()
        {
            var b = new SnapshotBuffer();
            b.Add(S(0, 0, vx: 4));
            SnapshotBuffer.Sample s = b.SampleAt(100);
            Assert.That(s.Extrapolated, Is.True);
            Assert.That(s.Position.X, Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(b.SampleAt(10_000).Position.X, Is.EqualTo(4f * ArenaRules.MaxExtrapolationMs / 1000f).Within(1e-5f), "capped");
        }

        [Test]
        public void DropsLateAndDuplicateSnapshots()
        {
            var b = new SnapshotBuffer();
            Assert.That(b.Add(S(100, 1)), Is.True);
            Assert.That(b.Add(S(100, 2)), Is.False);
            Assert.That(b.Add(S(50, 3)), Is.False);
            Assert.That(b.Count, Is.EqualTo(1));
        }

        [Test]
        public void WorksAcrossTheServerClockWrap()
        {
            var b = new SnapshotBuffer();
            int t0 = int.MaxValue - 20;
            b.Add(S(t0, 0));
            b.Add(S(ServerClock.Add(t0, 40), 4));
            Assert.That(b.SampleAt(ServerClock.Add(t0, 30)).Position.X, Is.EqualTo(3f).Within(1e-4f));
        }

        [Test]
        public void ADeathIsNotBlendedOver()
        {
            var b = new SnapshotBuffer();
            b.Add(S(0, 0));
            b.Add(S(100, 10, alive: false));
            SnapshotBuffer.Sample mid = b.SampleAt(50);
            Assert.That(mid.Alive, Is.True);
            Assert.That(mid.Position.X, Is.EqualTo(0f), "holds the last alive position instead of sliding");
        }

        [Test]
        public void CapacityAndTrim_KeepTheBufferSmall()
        {
            var b = new SnapshotBuffer(4);
            for (int i = 0; i < 10; i++) b.Add(S(i * 10, i));
            Assert.That(b.Count, Is.EqualTo(4));
            Assert.That(b.Oldest.Time, Is.EqualTo(60));
            b.TrimBefore(85);
            Assert.That(b.Oldest.Time, Is.EqualTo(80), "keeps one snapshot before the trim time to interpolate from");
        }

        [Test]
        public void AngleLerp_TakesTheShortWayRound()
        {
            float a = 3.0f, c = -3.0f; // 0.28 rad apart across ±π
            float mid = SnapshotBuffer.LerpAngle(a, c, 0.5f);
            Assert.That(Math.Abs(Math.Abs(mid) - Math.PI), Is.LessThan(0.01));
        }
    }

    public class BoltTests
    {
        [Test]
        public void FliesFromTheSharedClock_AndEndsAtTheWall()
        {
            ArenaMap map = ArenaMaps.Get(ArenaMaps.Pillars);
            var bolt = new Bolt(2, 7, new Vec2(2f, 8f), 0f, int.MaxValue - 100, map);
            Assert.That(bolt.Id, Is.EqualTo(GameMessages.BoltId(2, 7)));
            Assert.That(bolt.Range, Is.EqualTo(10.5f - ArenaRules.BoltRadius - 2f).Within(1e-4f));
            int halfway = ServerClock.Add(bolt.FireTime, (int)(bolt.Range / 2 / ArenaRules.BoltSpeed * 1000));
            Assert.That(bolt.PositionAt(halfway).X, Is.EqualTo(2f + bolt.Range / 2).Within(0.02f));
            Assert.That(bolt.IsFinished(halfway), Is.False);
            Assert.That(bolt.IsFinished(bolt.EndTime), Is.True);
            Assert.That(bolt.PositionAt(ServerClock.Add(bolt.EndTime, 5000)).X, Is.EqualTo(2f + bolt.Range).Within(1e-4f), "never passes through");
            Assert.That(bolt.PositionAt(ServerClock.Add(bolt.FireTime, -500)), Is.EqualTo(bolt.Origin));
        }
    }
}

    public static object CreateSession(object data = null)
    {
        if (data == null) return null;
        return data;
    }