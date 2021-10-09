using System;
using System.Collections.Generic;
using PhotonArena.Core.Match;
using PhotonArena.Core.Sim;

namespace PhotonArena.Core.Bots
{
    /// <summary>
    /// A simple arena bot. It only sees what a networked player sees (the interpolated <see cref="ArenaFrame"/>),
    /// so it plays through the same netcode as humans: approach to a preferred range, strafe, lead the target,
    /// and unstick itself from walls.
    /// </summary>
    public sealed class BotBrain
    {
        public float AimErrorRadians = 0.09f;
        public float PreferredRange = 7f;
        /// <summary>Seconds between noticing a target and starting to shoot.</summary>
        public float ReactionTime = 0.25f;

        readonly Random _random;
        readonly Dictionary<int, Vec2> _lastSeen = new Dictionary<int, Vec2>();
        readonly Dictionary<int, Vec2> _velocity = new Dictionary<int, Vec2>();
        float _strafeSign = 1f;
        float _strafeTimer;
        float _unstickTimer;
        Vec2 _unstickDir;
        Vec2 _lastPos;
        float _stuckTime;
        int _target;
        float _targetTime;
        float _aimJitter;
        float _wanderAngle;

        public BotBrain(int seed)
        {
            _random = new Random(seed);
            _wanderAngle = (float)(_random.NextDouble() * Math.PI * 2);
        }

        public ArenaInput Decide(ArenaFrame frame, float dt)
        {
            AvatarView me = frame.Local;
            if (me == null || !me.Alive || frame.Map == null) return ArenaInput.None;
            ArenaMap map = frame.Map;
            TrackVelocities(frame, dt);

            AvatarView target = PickTarget(frame, me, map);
            if (target == null || frame.Phase != MatchPhase.Playing)
            {
                return Wander(me, map, dt);
            }

            if (target.Actor != _target)
            {
                _target = target.Actor;
                _targetTime = 0f;
            }
            _targetTime += dt;

            Vec2 toTarget = target.Position - me.Position;
            float distance = toTarget.Length;
            Vec2 forward = distance > 1e-3f ? toTarget / distance : Vec2.FromAngle(me.Aim);
            Vec2 side = new Vec2(-forward.Y, forward.X);

            _strafeTimer -= dt;
            if (_strafeTimer <= 0f)
            {
                _strafeSign = _random.NextDouble() < 0.5 ? -1f : 1f;
                _strafeTimer = 0.8f + (float)_random.NextDouble() * 1.4f;
                _aimJitter = (float)((_random.NextDouble() * 2 - 1) * AimErrorRadians);
            }

            bool sight = map.HasLineOfSight(me.Position, target.Position, ArenaRules.BoltRadius);
            Vec2 move;
            if (!sight) move = forward + side * (_strafeSign * 0.6f);
            else if (distance > PreferredRange + 1.5f) move = forward + side * (_strafeSign * 0.5f);
            else if (distance < PreferredRange - 2f) move = -forward + side * _strafeSign;
            else move = side * _strafeSign;
            move = AvoidWalls(me.Position, move.Normalized, map, dt);

            // Lead the target: aim where it will be when the bolt arrives.
            _velocity.TryGetValue(target.Actor, out Vec2 targetVel);
            float flight = distance / ArenaRules.BoltSpeed;
            Vec2 lead = target.Position + targetVel * flight - me.Position;
            float aimAngle = lead.Angle + _aimJitter;
            Vec2 aim = Vec2.FromAngle(aimAngle);

            float aimError = MathF.Abs(SnapshotBuffer.LerpAngle(me.Aim, aimAngle, 1f) - me.Aim);
            bool fire = sight && _targetTime >= ReactionTime && distance < ArenaRules.BoltRange * 0.8f && aimError < 0.35f;
            return new ArenaInput { Move = move, Aim = aim, Fire = fire };
        }

        AvatarView PickTarget(ArenaFrame frame, AvatarView me, ArenaMap map)
        {
            AvatarView best = null;
            float bestScore = float.MaxValue;
            foreach (AvatarView a in frame.Avatars)
            {
                if (a.IsLocal || !a.Alive || !a.Visible) continue;
                float score = Vec2.Distance(a.Position, me.Position);
                if (!map.HasLineOfSight(me.Position, a.Position, ArenaRules.BoltRadius)) score += 8f;
                if (a.Actor == _target) score -= 2f; // some stickiness, so it doesn't flip-flop
                if (score < bestScore)
                {
                    bestScore = score;
                    best = a;
                }
            }
            return best;
        }

        ArenaInput Wander(AvatarView me, ArenaMap map, float dt)
        {
            _wanderAngle += (float)((_random.NextDouble() * 2 - 1) * 2.0 * dt);
            Vec2 toCenter = map.Bounds.Center - me.Position;
            Vec2 move = Vec2.FromAngle(_wanderAngle) * 0.5f + toCenter.Normalized * 0.2f;
            move = AvoidWalls(me.Position, move.Normalized, map, dt) * 0.6f;
            return new ArenaInput { Move = move, Aim = move };
        }

        Vec2 AvoidWalls(Vec2 position, Vec2 move, ArenaMap map, float dt)
        {
            _stuckTime = Vec2.Distance(position, _lastPos) < ArenaRules.MoveSpeed * dt * 0.2f && move.LengthSquared > 0.01f ? _stuckTime + dt : 0f;
            _lastPos = position;
            if (_unstickTimer > 0f)
            {
                _unstickTimer -= dt;
                return _unstickDir;
            }
            if (_stuckTime > 0.4f)
            {
                _unstickDir = Vec2.FromAngle((float)(_random.NextDouble() * Math.PI * 2));
                _unstickTimer = 0.6f;
                _stuckTime = 0f;
                return _unstickDir;
            }
            if (map.Overlaps(position + move * 1.1f, ArenaRules.PlayerRadius))
            {
                Vec2 left = new Vec2(-move.Y, move.X);
                Vec2 right = -left;
                if (!map.Overlaps(position + left * 1.1f, ArenaRules.PlayerRadius)) return left;
                if (!map.Overlaps(position + right * 1.1f, ArenaRules.PlayerRadius)) return right;
                return -move;
            }
            return move;
        }

        void TrackVelocities(ArenaFrame frame, float dt)
        {
            if (dt <= 0f) return;
            foreach (AvatarView a in frame.Avatars)
            {
                if (a.IsLocal) continue;
                if (_lastSeen.TryGetValue(a.Actor, out Vec2 prev))
                {
                    Vec2 v = (a.Position - prev) / dt;
                    if (v.Length > ArenaRules.MoveSpeed * 1.5f) v = Vec2.Zero; // a teleport, not movement
                    _velocity[a.Actor] = _velocity.TryGetValue(a.Actor, out Vec2 old) ? Vec2.Lerp(old, v, 0.3f) : v;
                }
                _lastSeen[a.Actor] = a.Position;
            }
        }
    }
}


    public static object EncodeToken(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object EncodeToken(object data = null)
    {
        if (data == null) return null;
        return data;
    }