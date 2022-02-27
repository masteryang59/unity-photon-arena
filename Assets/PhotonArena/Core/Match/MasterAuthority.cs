using System;
using System.Collections.Generic;
using System.Linq;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Sim;

namespace PhotonArena.Core.Match
{
    /// <summary>
    /// The game rules that only the master client runs: the match phase machine, hit detection with lag
    /// compensation, damage, kills, respawns and the winner. Everything it decides is written to room and player
    /// properties, which the server stores, so a new master (after the old one leaves) continues from them.
    ///
    /// Every client records validated position history all the time, not only the master, so a client that
    /// becomes master mid-match can resolve shots straight away.
    /// </summary>
    public sealed class MasterAuthority
    {
        sealed class AuthState
        {
            public int Hp = ArenaRules.MaxHp;
            public bool Alive = true;
            public int Kills;
            public int Deaths;
            public int Life;
            public int RespawnAt;
            public bool RespawnPending;
            // Server times wrap, so a time field can't use 0 as "unset": each has a flag beside it.
            public bool Protected;
            public int ProtectedUntil;
            public int LastFireTime;
            public bool HasFired;
            public int Violations;
            public readonly SnapshotBuffer History = new SnapshotBuffer(64);
        }

        sealed class PendingBolt
        {
            public Bolt Bolt;
            public int CheckedUntil;
        }

        const int SubstepMs = 10;
        const int TransitionRetryMs = 1000;
        const int HistoryMs = 1000;

        readonly INetSession _session;
        readonly Dictionary<int, AuthState> _states = new Dictionary<int, AuthState>();
        readonly List<PendingBolt> _bolts = new List<PendingBolt>();
        bool _wasMaster;
        int _requestedPhase = -1;
        int _requestedAt;

        public MasterAuthority(INetSession session)
        {
            _session = session;
        }

        public bool IsActive => _session.IsMaster && _session.Room != null;
        /// <summary>Speed/teleport violations seen per actor (for logs and the headless report).</summary>
        public int ViolationsOf(int actor) => _states.TryGetValue(actor, out AuthState s) ? s.Violations : 0;
        public int PendingBolts => _bolts.Count;

        /// <summary>Raised on the master for every confirmed hit (the Hit event goes to everyone else).</summary>
        public event Action<GameMessages.HitMsg> HitResolved;

        AuthState StateOf(int actor)
        {
            if (!_states.TryGetValue(actor, out AuthState s))
            {
                s = new AuthState();
                LoadFromProps(actor, s);
                _states.Add(actor, s);
            }
            return s;
        }

        void LoadFromProps(int actor, AuthState s)
        {
            NetPlayer p = _session.Room?.GetPlayer(actor);
            if (p == null) return;
            s.Hp = p.Props.GetInt(PlayerKeys.Hp, ArenaRules.MaxHp);
            s.Alive = p.Props.GetBool(PlayerKeys.Alive, true);
            s.Kills = p.Props.GetInt(PlayerKeys.Kills);
            s.Deaths = p.Props.GetInt(PlayerKeys.Deaths);
            s.Life = p.Props.GetInt(PlayerKeys.Life);
            s.RespawnPending = p.Props.ContainsKey(PlayerKeys.RespawnAt);
            s.RespawnAt = p.Props.GetInt(PlayerKeys.RespawnAt);
        }

        public void Reset()
        {
            _states.Clear();
            _bolts.Clear();
            _wasMaster = false;
            _requestedPhase = -1;
        }

        public void ForgetPlayer(int actor)
        {
            _states.Remove(actor);
            _bolts.RemoveAll(b => b.Bolt.Shooter == actor);
        }

        /// <summary>
        /// Records a reported state into the history used for hit checks. A jump faster than
        /// <see cref="ArenaRules.MaxReportedSpeed"/> is clamped (and counted) unless the life counter changed,
        /// which marks a legitimate respawn teleport.
        /// </summary>
        public void RecordState(int actor, SnapshotBuffer.Snapshot snapshot)
        {
            AuthState s = StateOf(actor);
            if (!s.History.IsEmpty)
            {
                SnapshotBuffer.Snapshot prev = s.History.Newest;
                int dt = ServerClock.Diff(snapshot.Time, prev.Time);
                if (dt <= 0) return;
                if (prev.Alive && snapshot.Alive && prev.Life == snapshot.Life)
                {
                    float maxDistance = ArenaRules.MaxReportedSpeed * dt / 1000f + 0.25f;
                    Vec2 delta = snapshot.Position - prev.Position;
                    if (delta.Length > maxDistance)
                    {
                        s.Violations++;
                        snapshot.Position = prev.Position + delta.ClampLength(maxDistance);
                    }
                }
            }
            s.History.Add(snapshot);
            s.History.TrimBefore(ServerClock.Add(snapshot.Time, -HistoryMs));
        }

        // ---- Phase machine -------------------------------------------------------------------------------

        public void Tick()
        {
            if (!IsActive)
            {
                _wasMaster = false;
                return;
            }
            NetRoom room = _session.Room;
            int now = _session.ServerTime;
            if (!_wasMaster)
            {
                // Just became master (created the room, or the previous master left): trust the stored props.
                _wasMaster = true;
                foreach (var kv in _states) LoadFromProps(kv.Key, kv.Value);
                _requestedPhase = -1;
            }

            AssignColors(room);

            var phase = (MatchPhase)room.Props.GetInt(RoomKeys.Phase);
            int elapsed = ServerClock.Diff(now, room.Props.GetInt(RoomKeys.PhaseStart, now));
            int players = room.PlayerCount;
            bool allReady = room.Players.Values.All(p => p.Props.GetBool(PlayerKeys.Ready));

            switch (phase)
            {
                case MatchPhase.Waiting:
                    if (players >= ArenaRules.MinPlayersToStart && allReady) RequestPhase(phase, MatchPhase.Countdown, now, null);
                    break;
                case MatchPhase.Countdown:
                    if (players < ArenaRules.MinPlayersToStart || !allReady) RequestPhase(phase, MatchPhase.Waiting, now, null);
                    else if (elapsed >= ArenaRules.CountdownMs) StartMatch(room, now);
                    break;
                case MatchPhase.Playing:
                    ResolveBolts(now);
                    ProcessRespawns(room, now);
                    int durationMs = room.Props.GetInt(RoomKeys.DurationSec, ArenaRules.DefaultDurationSec) * 1000;
                    if (players < ArenaRules.MinPlayersToStart || elapsed >= durationMs) EndMatch(room, now);
                    break;
                case MatchPhase.Results:
                    if (elapsed >= ArenaRules.ResultsMs) BackToWaiting(room, now);
                    break;
            }
        }

        void RequestPhase(MatchPhase from, MatchPhase to, int now, PropertyBag extra)
        {
            // Changes only apply when the server echoes them, so don't re-send while one is in flight.
            if (_requestedPhase == (int)to && ServerClock.Diff(now, _requestedAt) < TransitionRetryMs) return;
            _requestedPhase = (int)to;
            _requestedAt = now;
            PropertyBag changes = extra ?? new PropertyBag();
            changes.Set(RoomKeys.Phase, (int)to).Set(RoomKeys.PhaseStart, now);
            NetRoom room = _session.Room;
            // Compare-and-swap on phase and round, so a stale or duplicate transition can't apply twice.
            var expected = PropertyBag.Of(RoomKeys.Phase, (int)from, RoomKeys.Round, room.Props.GetInt(RoomKeys.Round));
            _session.SetRoomProperties(changes, expected);
        }

        bool TransitionInFlight(MatchPhase to, int now) =>
            _requestedPhase == (int)to && ServerClock.Diff(now, _requestedAt) < TransitionRetryMs;

        void StartMatch(NetRoom room, int now)
        {
            if (TransitionInFlight(MatchPhase.Playing, now)) return;
            _session.SetRoomOpen(false);
            _bolts.Clear();
            int slot = 0;
            ArenaMap map = ArenaMaps.Get(room.Props.GetString(RoomKeys.Map));
            foreach (NetPlayer p in room.Players.Values)
            {
                AuthState s = StateOf(p.Actor);
                s.Hp = ArenaRules.MaxHp;
                s.Alive = true;
                s.Kills = 0;
                s.Deaths = 0;
                s.Life++;
                s.RespawnPending = false;
                s.Protected = true;
                s.ProtectedUntil = ServerClock.Add(now, ArenaRules.CountdownMs / 3);
                s.HasFired = false;
                _session.SetPlayerProperties(p.Actor, PropertyBag.Of(
                    PlayerKeys.Hp, s.Hp, PlayerKeys.Alive, true, PlayerKeys.Kills, 0, PlayerKeys.Deaths, 0,
                    PlayerKeys.Spawn, slot++ % map.Spawns.Count, PlayerKeys.Life, s.Life, PlayerKeys.RespawnAt, null));
            }
            int round = room.Props.GetInt(RoomKeys.Round) + 1;
            RequestPhase(MatchPhase.Countdown, MatchPhase.Playing, now, PropertyBag.Of(RoomKeys.Round, round, RoomKeys.Winner, null));
        }

        void EndMatch(NetRoom room, int now)
        {
            if (TransitionInFlight(MatchPhase.Results, now)) return;
            RequestPhase(MatchPhase.Playing, MatchPhase.Results, now, PropertyBag.Of(RoomKeys.Winner, PickWinner(room)));
            _bolts.Clear();
        }

        /// <summary>Most kills; ties go to fewer deaths; a remaining tie is a draw (0).</summary>
        public int PickWinner(NetRoom room)
        {
            var ranked = room.Players.Values
                .Select(p => (actor: p.Actor, s: StateOf(p.Actor)))
                .OrderByDescending(x => x.s.Kills).ThenBy(x => x.s.Deaths)
                .ToList();
            if (ranked.Count == 0) return 0;
            if (ranked.Count == 1) return ranked[0].actor;
            bool tie = ranked[0].s.Kills == ranked[1].s.Kills && ranked[0].s.Deaths == ranked[1].s.Deaths;
            return tie ? 0 : ranked[0].actor;
        }

        void BackToWaiting(NetRoom room, int now)
        {
            if (TransitionInFlight(MatchPhase.Waiting, now)) return;
            // Everyone readies up again for the next round. (Bots that should play re-ready themselves; leaving
            // them ready would let the next countdown start before a waiting bot could opt out.)
            foreach (NetPlayer p in room.Players.Values)
            {
                if (p.Props.GetBool(PlayerKeys.Ready)) _session.SetPlayerProperties(p.Actor, PropertyBag.Of(PlayerKeys.Ready, false));
            }
            _session.SetRoomOpen(true);
            RequestPhase(MatchPhase.Results, MatchPhase.Waiting, now, null);
        }

        void AssignColors(NetRoom room)
        {
            var used = new HashSet<int>();
            foreach (NetPlayer p in room.Players.Values)
            {
                if (p.Props.ContainsKey(PlayerKeys.Color)) used.Add(p.Props.GetInt(PlayerKeys.Color));
            }
            foreach (NetPlayer p in room.Players.Values)
            {
                if (p.Props.ContainsKey(PlayerKeys.Color)) continue;
                int color = 0;
                while (used.Contains(color)) color++;
                used.Add(color);
                // Expected "absent", so two masters (or a duplicate tick) can't hand out conflicting colors.
                _session.SetPlayerProperties(p.Actor, PropertyBag.Of(PlayerKeys.Color, color), PropertyBag.Of(PlayerKeys.Color, null));
            }
        }

        // ---- Shots -------------------------------------------------------------------------------------

        /// <summary>
        /// A shot reported by <paramref name="shooter"/> (or fired by the master itself). Returns false when the
        /// shot is rejected: not playing, shooter dead, firing faster than the cooldown, or fired from a place the
        /// shooter wasn't. Rejected shots still fly on screen, they just can't hit.
        /// </summary>
        public bool OnFire(int shooter, GameMessages.FireMsg msg, ArenaMap map)
        {
            if (!IsActive) return false;
            NetRoom room = _session.Room;
            if ((MatchPhase)room.Props.GetInt(RoomKeys.Phase) != MatchPhase.Playing) return false;
            int now = _session.ServerTime;
            AuthState s = StateOf(shooter);
            if (!s.Alive) return false;

            int fireTime = msg.Time;
            if (ServerClock.Diff(fireTime, now) > 50) fireTime = now; // claims to be from the future
            if (s.HasFired && ServerClock.Diff(fireTime, s.LastFireTime) < ArenaRules.FireCooldownMs - 40) return false;

            if (!s.History.IsEmpty)
            {
                int checkTime = ServerClock.Diff(now, fireTime) > ArenaRules.MaxRewindMs ? ServerClock.Add(now, -ArenaRules.MaxRewindMs) : fireTime;
                Vec2 there = s.History.SampleAt(checkTime).Position;
                if (Vec2.Distance(there, msg.Origin) > ArenaRules.MaxOriginError + ArenaRules.MuzzleOffset) return false;
            }

            s.LastFireTime = fireTime;
            s.HasFired = true;
            var bolt = new Bolt(shooter, msg.ShotId, msg.Origin, msg.Angle, fireTime, map);
            // Lag compensation, capped: check the bolt's path from when it was fired, but never further back
            // than MaxRewindMs. The part of a very late shot's path before that is not checked.
            int earliest = ServerClock.Add(now, -ArenaRules.MaxRewindMs);
            _bolts.Add(new PendingBolt { Bolt = bolt, CheckedUntil = ServerClock.Diff(fireTime, earliest) < 0 ? earliest : fireTime });
            return true;
        }

        void ResolveBolts(int now)
        {
            NetRoom room = _session.Room;
            for (int i = _bolts.Count - 1; i >= 0; i--)
            {
                PendingBolt pb = _bolts[i];
                Bolt bolt = pb.Bolt;
                int until = ServerClock.Diff(now, bolt.EndTime) < 0 ? now : bolt.EndTime;
                bool done = ServerClock.Diff(until, bolt.EndTime) >= 0;
                int hitActor = 0;

                while (ServerClock.Diff(until, pb.CheckedUntil) > 0 && hitActor == 0)
                {
                    int ta = pb.CheckedUntil;
                    int tb = ServerClock.Diff(until, ta) > SubstepMs ? ServerClock.Add(ta, SubstepMs) : until;
                    Vec2 pa = bolt.PositionAt(ta);
                    Vec2 pbPos = bolt.PositionAt(tb);
                    int tm = ServerClock.Add(ta, ServerClock.Diff(tb, ta) / 2);
                    float best = float.MaxValue;
                    foreach (NetPlayer target in room.Players.Values)
                    {
                        if (target.Actor == bolt.Shooter) continue;
                        AuthState ts = StateOf(target.Actor);
                        if (!ts.Alive || (ts.Protected && ServerClock.Diff(tm, ts.ProtectedUntil) < 0) || ts.History.IsEmpty) continue;
                        Vec2 tp = ts.History.SampleAt(tm).Position;
                        float d = Vec2.DistanceToSegment(tp, pa, pbPos);
                        if (d <= ArenaRules.PlayerRadius + ArenaRules.BoltRadius && d < best)
                        {
                            best = d;
                            hitActor = target.Actor;
                        }
                    }
                    pb.CheckedUntil = tb;
                }

                if (hitActor != 0)
                {
                    _bolts.RemoveAt(i);
                    ApplyHit(room, bolt, hitActor, now);
                    // A kill can end the match, which clears the remaining bolts.
                    if (i > _bolts.Count) i = _bolts.Count;
                }
                else if (done)
                {
                    _bolts.RemoveAt(i);
                }
            }
        }

        void ApplyHit(NetRoom room, Bolt bolt, int targetActor, int now)
        {
            AuthState target = StateOf(targetActor);
            target.Hp = Math.Max(0, target.Hp - 1);
            bool killed = target.Hp == 0;
            var targetChanges = PropertyBag.Of(PlayerKeys.Hp, target.Hp);
            if (killed)
            {
                target.Alive = false;
                target.Deaths++;
                target.RespawnAt = ServerClock.Add(now, ArenaRules.RespawnMs);
                target.RespawnPending = true;
                targetChanges.Set(PlayerKeys.Alive, false).Set(PlayerKeys.Deaths, target.Deaths).Set(PlayerKeys.RespawnAt, target.RespawnAt);
            }
            _session.SetPlayerProperties(targetActor, targetChanges);

            if (killed && room.GetPlayer(bolt.Shooter) != null)
            {
                AuthState shooter = StateOf(bolt.Shooter);
                shooter.Kills++;
                _session.SetPlayerProperties(bolt.Shooter, PropertyBag.Of(PlayerKeys.Kills, shooter.Kills));
            }

            var hit = new GameMessages.HitMsg { Shooter = bolt.Shooter, ShotId = bolt.ShotId, Target = targetActor, HpAfter = (byte)target.Hp, Killed = killed };
            _session.Send(EventCodes.Hit, GameMessages.Write(hit), Target.Others, true);
            HitResolved?.Invoke(hit);

            int limit = room.Props.GetInt(RoomKeys.ScoreLimit, GameModes.DefaultScoreLimit(room.Props.GetString(RoomKeys.Mode)));
            if (killed && room.GetPlayer(bolt.Shooter) != null && StateOf(bolt.Shooter).Kills >= limit) EndMatch(room, now);
        }

        void ProcessRespawns(NetRoom room, int now)
        {
            ArenaMap map = ArenaMaps.Get(room.Props.GetString(RoomKeys.Map));
            foreach (NetPlayer p in room.Players.Values)
            {
                AuthState s = StateOf(p.Actor);
                if (s.Alive || !s.RespawnPending || ServerClock.Diff(now, s.RespawnAt) < 0) continue;
                int spawn = PickSpawn(room, map, p.Actor);
                s.Alive = true;
                s.Hp = ArenaRules.MaxHp;
                s.Life++;
                s.RespawnPending = false;
                s.Protected = true;
                s.ProtectedUntil = ServerClock.Add(now, ArenaRules.SpawnProtectionMs);
                _session.SetPlayerProperties(p.Actor, PropertyBag.Of(
                    PlayerKeys.Alive, true, PlayerKeys.Hp, s.Hp, PlayerKeys.Spawn, spawn, PlayerKeys.Life, s.Life, PlayerKeys.RespawnAt, null));
            }
        }

        /// <summary>The spawn point farthest from the nearest living enemy.</summary>
        public int PickSpawn(NetRoom room, ArenaMap map, int actor)
        {
            int now = _session.ServerTime;
            int best = 0;
            float bestScore = float.MinValue;
            for (int i = 0; i < map.Spawns.Count; i++)
            {
                float nearest = float.MaxValue;
                foreach (NetPlayer other in room.Players.Values)
                {
                    if (other.Actor == actor) continue;
                    AuthState os = StateOf(other.Actor);
                    if (!os.Alive || os.History.IsEmpty) continue;
                    nearest = Math.Min(nearest, Vec2.Distance(map.Spawns[i], os.History.SampleAt(now).Position));
                }
                if (nearest > bestScore)
                {
                    bestScore = nearest;
                    best = i;
                }
            }
            return best;
        }
    }
}


    public static object GenerateReport(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object GenerateReport(object data = null)
    {
        if (data == null) return null;
        return data;
    }