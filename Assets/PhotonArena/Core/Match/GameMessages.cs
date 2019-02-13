using System;
using PhotonArena.Core.Net.Wire;
using PhotonArena.Core.Sim;

namespace PhotonArena.Core.Match
{
    public enum MatchPhase
    {
        Waiting = 0,
        Countdown = 1,
        Playing = 2,
        Results = 3,
    }

    /// <summary>Game event codes. Photon reserves 200 and above, so these stay small.</summary>
    public static class EventCodes
    {
        /// <summary>Avatar state, 20 Hz, unreliable, to Others.</summary>
        public const byte State = 1;
        /// <summary>A shot, reliable, to Others (the shooter spawns its own bolt immediately).</summary>
        public const byte Fire = 2;
        /// <summary>A confirmed hit, from the master, reliable, to All.</summary>
        public const byte Hit = 3;
    }

    /// <summary>
    /// Fixed-size binary messages. Positions are quantized to centimeters in an int16 (±327 m) and angles to
    /// 1/65536 of a turn, so a state update is 18 bytes. Senders simulate with the quantized values too, so the
    /// shooter and everyone else fly exactly the same bolt.
    /// </summary>
    public static class GameMessages
    {
        public const float PositionScale = 100f;

        public struct StateMsg
        {
            public ushort Seq;
            public int Time;
            public Vec2 Position;
            public Vec2 Velocity;
            public float Aim;
            public bool Alive;
            /// <summary>Low byte of the sender's life counter; a change means a respawn teleport, not a speed hack.</summary>
            public byte Life;
        }

        public struct FireMsg
        {
            public ushort ShotId;
            public int Time;
            public Vec2 Origin;
            public float Angle;
        }

        public struct HitMsg
        {
            public int Shooter;
            public ushort ShotId;
            public int Target;
            public byte HpAfter;
            public bool Killed;
        }

        public static short QuantizeCoord(float v) =>
            (short)Math.Clamp((int)MathF.Round(v * PositionScale), short.MinValue, short.MaxValue);

        public static float DequantizeCoord(short v) => v / PositionScale;

        public static Vec2 Quantize(Vec2 v) =>
            new Vec2(DequantizeCoord(QuantizeCoord(v.X)), DequantizeCoord(QuantizeCoord(v.Y)));

        public static ushort QuantizeAngle(float radians)
        {
            double turns = radians / (2 * Math.PI);
            turns -= Math.Floor(turns);
            return (ushort)((int)Math.Round(turns * 65536.0) & 0xFFFF);
        }

        public static float DequantizeAngle(ushort v) => (float)(v / 65536.0 * 2 * Math.PI);

        public static float QuantizeAngleValue(float radians) => DequantizeAngle(QuantizeAngle(radians));

        public static byte[] Write(in StateMsg m) => new ByteWriter(18)
            .U16(m.Seq).I32(m.Time)
            .I16(QuantizeCoord(m.Position.X)).I16(QuantizeCoord(m.Position.Y))
            .I16(QuantizeCoord(m.Velocity.X)).I16(QuantizeCoord(m.Velocity.Y))
            .U16(QuantizeAngle(m.Aim)).U8(m.Alive ? (byte)1 : (byte)0).U8(m.Life)
            .ToArray();

        public static StateMsg ReadState(byte[] data)
        {
            var r = new ByteReader(data);
            var m = new StateMsg
            {
                Seq = r.U16(),
                Time = r.I32(),
                Position = new Vec2(DequantizeCoord(r.I16()), DequantizeCoord(r.I16())),
                Velocity = new Vec2(DequantizeCoord(r.I16()), DequantizeCoord(r.I16())),
                Aim = DequantizeAngle(r.U16()),
                Alive = (r.U8() & 1) != 0,
                Life = r.U8(),
            };
            return m;
        }

        public static byte[] Write(in FireMsg m) => new ByteWriter(12)
            .U16(m.ShotId).I32(m.Time)
            .I16(QuantizeCoord(m.Origin.X)).I16(QuantizeCoord(m.Origin.Y))
            .U16(QuantizeAngle(m.Angle))
            .ToArray();

        public static FireMsg ReadFire(byte[] data)
        {
            var r = new ByteReader(data);
            return new FireMsg
            {
                ShotId = r.U16(),
                Time = r.I32(),
                Origin = new Vec2(DequantizeCoord(r.I16()), DequantizeCoord(r.I16())),
                Angle = DequantizeAngle(r.U16()),
            };
        }

        public static byte[] Write(in HitMsg m) => new ByteWriter(12)
            .I32(m.Shooter).U16(m.ShotId).I32(m.Target).U8(m.HpAfter).Bool(m.Killed)
            .ToArray();

        public static HitMsg ReadHit(byte[] data)
        {
            var r = new ByteReader(data);
            return new HitMsg
            {
                Shooter = r.I32(),
                ShotId = r.U16(),
                Target = r.I32(),
                HpAfter = r.U8(),
                Killed = r.Bool(),
            };
        }

        /// <summary>Bolt ids are unique per room: the shooter's actor number and its own shot counter.</summary>
        public static long BoltId(int shooter, ushort shotId) => ((long)shooter << 16) | shotId;
    }
}


    public static object RenderTemplate(object data = null)
    {
        if (data == null) return null;
        return data;
    }
