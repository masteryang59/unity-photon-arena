using System;
using PhotonArena.Core.Net;
using PhotonArena.Core.Sim;

namespace PhotonArena.Core.Match
{
    /// <summary>
    /// Timestamped states of one remote avatar. Rendering samples it <see cref="ArenaRules.InterpolationDelayMs"/>
    /// in the past and blends the two snapshots around that time; the master samples it at a shot's time for lag
    /// compensation. Times are server-clock milliseconds and may wrap.
    /// </summary>
    public sealed class SnapshotBuffer
    {
        public struct Snapshot
        {
            public int Time;
            public Vec2 Position;
            public Vec2 Velocity;
            public float Aim;
            public bool Alive;
            public byte Life;
        }

        public struct Sample
        {
            public Vec2 Position;
            public float Aim;
            public bool Alive;
            /// <summary>True when the time was past the newest snapshot and the position was extrapolated or held.</summary>
            public bool Extrapolated;
        }

        readonly Snapshot[] _items;
        int _start;
        int _count;

        public SnapshotBuffer(int capacity = 64)
        {
            _items = new Snapshot[Math.Max(4, capacity)];
        }

        public int Count => _count;
        public bool IsEmpty => _count == 0;

        Snapshot At(int i) => _items[(_start + i) % _items.Length];

        public Snapshot Newest => At(_count - 1);
        public Snapshot Oldest => At(0);

        /// <summary>Adds a snapshot. Ones older than the newest (late or duplicated packets) are dropped.</summary>
        public bool Add(Snapshot s)
        {
            if (_count > 0 && ServerClock.Diff(s.Time, Newest.Time) <= 0) return false;
            if (_count == _items.Length)
            {
                _start = (_start + 1) % _items.Length;
                _count--;
            }
            _items[(_start + _count) % _items.Length] = s;
            _count++;
            return true;
        }

        public void Clear()
        {
            _start = 0;
            _count = 0;
        }

        /// <summary>Forget history, e.g. after a respawn teleport, so nothing blends across it.</summary>
        public void Reset(Snapshot s)
        {
            Clear();
            Add(s);
        }

        public Sample SampleAt(int time)
        {
            if (_count == 0) return new Sample();
            Snapshot oldest = Oldest;
            if (ServerClock.Diff(time, oldest.Time) <= 0)
            {
                return new Sample { Position = oldest.Position, Aim = oldest.Aim, Alive = oldest.Alive };
            }
            Snapshot newest = Newest;
            int pastNewest = ServerClock.Diff(time, newest.Time);
            if (pastNewest >= 0)
            {
                // Dead reckoning for a short gap, then hold.
                float seconds = Math.Min(pastNewest, ArenaRules.MaxExtrapolationMs) / 1000f;
                Vec2 p = newest.Alive ? newest.Position + newest.Velocity * seconds : newest.Position;
                return new Sample { Position = p, Aim = newest.Aim, Alive = newest.Alive, Extrapolated = pastNewest > 0 };
            }

            // Binary search for the last snapshot at or before `time`.
            int lo = 0, hi = _count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (ServerClock.Diff(At(mid).Time, time) <= 0) lo = mid;
                else hi = mid;
            }
            Snapshot a = At(lo), b = At(hi);
            int span = ServerClock.Diff(b.Time, a.Time);
            float t = span <= 0 ? 1f : Math.Clamp(ServerClock.Diff(time, a.Time) / (float)span, 0f, 1f);
            // Don't slide a dead avatar back to life; switch at the later snapshot.
            if (a.Alive != b.Alive) return t < 1f ? new Sample { Position = a.Position, Aim = a.Aim, Alive = a.Alive } : new Sample { Position = b.Position, Aim = b.Aim, Alive = b.Alive };
            return new Sample
            {
                Position = Vec2.Lerp(a.Position, b.Position, t),
                Aim = LerpAngle(a.Aim, b.Aim, t),
                Alive = a.Alive,
            };
        }

        public static float LerpAngle(float a, float b, float t)
        {
            float delta = b - a;
            const float twoPi = MathF.PI * 2f;
            delta -= MathF.Floor((delta + MathF.PI) / twoPi) * twoPi;
            return a + delta * t;
        }

        /// <summary>Drops snapshots older than <paramref name="time"/>, keeping one before it for interpolation.</summary>
        public void TrimBefore(int time)
        {
            while (_count > 2 && ServerClock.Diff(At(1).Time, time) < 0)
            {
                _start = (_start + 1) % _items.Length;
                _count--;
            }
        }
    }
}


    public static object ScheduleTask(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object ScheduleTask(object data = null)
    {
        if (data == null) return null;
        return data;
    }