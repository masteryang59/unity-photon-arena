using System;
using System.Collections.Generic;
using System.Linq;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Sim;

namespace PhotonArena.Core.Match
{
    public struct ArenaInput
    {
        /// <summary>Desired movement, length 0..1.</summary>
        public Vec2 Move;
        /// <summary>Aim direction (any length; zero keeps the previous aim).</summary>
        public Vec2 Aim;
        public bool Fire;

        public static ArenaInput None => new ArenaInput();
    }

    public sealed class AvatarView
    {
        public int Actor;
        public string Nickname;
        public bool IsLocal;
        public bool IsBot;
        public bool IsMaster;
        public bool Ready;
        public int Color;
        public Vec2 Position;
        public float Aim;
        public bool Alive;
        public bool Visible;
        public int Hp;
        public int Kills;
        public int Deaths;
        public int RespawnInMs;
    }

    public sealed class BoltView
    {
        public long Id;
        public int Shooter;
        public int Color;
        public Vec2 Position;
        public Vec2 Direction;
    }

    /// <summary>Everything the view needs to draw one frame. Engine-agnostic: Unity renders it, tests assert on it.</summary>
    public sealed class ArenaFrame
    {
        public int ServerTime;
        public int LocalActor;
        public bool LocalIsMaster;
        public string RoomName;
        public string Mode;
        public ArenaMap Map;
        public MatchPhase Phase;
        public int Round;
        public int ScoreLimit;
        /// <summary>Countdown: until the match starts. Playing: until time runs out. Results: until the next lobby.</summary>
        public int PhaseRemainingMs;
        public int Winner;
        public readonly List<AvatarView> Avatars = new List<AvatarView>();
        public readonly List<BoltView> Bolts = new List<BoltView>();

        public AvatarView Local => Avatars.FirstOrDefault(a => a.IsLocal);
        public AvatarView Get(int actor) => Avatars.FirstOrDefault(a => a.Actor == actor);
    }

    /// <summary>
    /// One player's view of the match, independent of Unity. It moves the local avatar from input (client-side
    /// authority over movement, like a PUN PhotonView owned by the player), sends state at 20 Hz, interpolates
    /// remote avatars 100 ms in the past, flies bolts from the shared server clock, and, while this client is the
    /// master, runs <see cref="MasterAuthority"/>.
    /// </summary>
    public sealed class ArenaClient : NetCallbacks, IDisposable
    {
        readonly INetSession _session;
        readonly Dictionary<int, SnapshotBuffer> _remote = new Dictionary<int, SnapshotBuffer>();
        readonly Dictionary<long, Bolt> _bolts = new Dictionary<long, Bolt>();

        ArenaMap _map = ArenaMaps.Get(null);
        Vec2 _position;
        Vec2 _velocity;
        float _aim;
        int _life = -1;
        ushort _shotCounter;
        ushort _stateSeq;
        int _nextFireAt;
        bool _firedOnce;
        int _nextSendAt;
        bool _sendScheduled; // server times wrap: 0 is a valid time, not "unset"
        bool _placed;

        public ArenaClient(INetSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            Authority = new MasterAuthority(session);
            Authority.HitResolved += OnHitConfirmed;
            _session.AddCallbacks(this);
            if (_session.Room != null) OnJoinedRoom();
        }

        public MasterAuthority Authority { get; }
        public INetSession Session => _session;
        public Vec2 LocalPosition => _position;
        public ArenaMap Map => _map;

        /// <summary>Raised for every confirmed hit (on the master directly, elsewhere via the Hit event).</summary>
        public event Action<GameMessages.HitMsg> Hit;
        /// <summary>Raised when any bolt is spawned locally (own shot or a remote one).</summary>
        public event Action<Bolt> BoltFired;

        MatchPhase Phase => (MatchPhase)(_session.Room?.Props.GetInt(RoomKeys.Phase) ?? 0);

        public override void OnJoinedRoom()
        {
            _remote.Clear();
            _bolts.Clear();
            Authority.Reset();
            _map = ArenaMaps.Get(_session.Room.Props.GetString(RoomKeys.Map));
            _life = -1;
            _placed = false;
            _firedOnce = false;
            _sendScheduled = false;
            _velocity = Vec2.Zero;
        }

        public override void OnLeftRoom()
        {
            _remote.Clear();
            _bolts.Clear();
            Authority.Reset();
        }

        public override void OnPlayerLeft(NetPlayer player)
        {
            _remote.Remove(player.Actor);
            Authority.ForgetPlayer(player.Actor);
        }

        public override void OnEvent(byte code, int senderActor, byte[] payload)
        {
            try
            {
                switch (code)
                {
                    case EventCodes.State:
                        OnState(senderActor, GameMessages.ReadState(payload));
                        break;
                    case EventCodes.Fire:
                        OnRemoteFire(senderActor, GameMessages.ReadFire(payload));
                        break;
                    case EventCodes.Hit:
                        // Only the master may confirm hits; ignore forgeries from anyone else.
                        if (_session.Room != null && senderActor == _session.Room.MasterActor) OnHitConfirmed(GameMessages.ReadHit(payload));
                        break;
                }
            }
            catch (Net.Wire.WireFormatException)
            {
                // A malformed game event from one player must not break everyone else's frame.
            }
        }

        void OnState(int actor, GameMessages.StateMsg m)
        {
            if (_session.Room?.GetPlayer(actor) == null) return;
            if (!_remote.TryGetValue(actor, out SnapshotBuffer buffer))
            {
                buffer = new SnapshotBuffer(32);
                _remote.Add(actor, buffer);
            }
            var snapshot = new SnapshotBuffer.Snapshot { Time = m.Time, Position = m.Position, Velocity = m.Velocity, Aim = m.Aim, Alive = m.Alive, Life = m.Life };
            // A new life means the owner teleported to a spawn point: don't blend across the map.
            if (!buffer.IsEmpty && buffer.Newest.Life != m.Life) buffer.Reset(snapshot);
            else buffer.Add(snapshot);
            Authority.RecordState(actor, snapshot);
        }

        void OnRemoteFire(int shooter, GameMessages.FireMsg m)
        {
            if (_session.Room?.GetPlayer(shooter) == null) return;
            var bolt = new Bolt(shooter, m.ShotId, m.Origin, m.Angle, m.Time, _map);
            if (!_bolts.ContainsKey(bolt.Id))
            {
                _bolts.Add(bolt.Id, bolt);
                BoltFired?.Invoke(bolt);
            }
            Authority.OnFire(shooter, m, _map);
        }

        void OnHitConfirmed(GameMessages.HitMsg hit)
        {
            _bolts.Remove(GameMessages.BoltId(hit.Shooter, hit.ShotId));
            Hit?.Invoke(hit);
        }

        /// <summary>Advances the local simulation by <paramref name="dt"/> seconds. Call after <see cref="INetSession.Service"/>.</summary>
        public void Tick(float dt, ArenaInput input)
        {
            NetRoom room = _session.Room;
            if (room == null || _session.State != NetState.InRoom) return;
            int now = _session.ServerTime;
            NetPlayer me = room.GetPlayer(_session.LocalActor);
            if (me == null) return;
            if (_map.Id != room.Props.GetString(RoomKeys.Map, _map.Id)) _map = ArenaMaps.Get(room.Props.GetString(RoomKeys.Map));

            bool alive = me.Props.GetBool(PlayerKeys.Alive, true);
            int life = me.Props.GetInt(PlayerKeys.Life);
            if (!_placed || life != _life)
            {
                // First placement, or the master respawned us: teleport to the assigned spawn.
                int spawn = me.Props.ContainsKey(PlayerKeys.Spawn) ? me.Props.GetInt(PlayerKeys.Spawn) : (_session.LocalActor - 1);
                _position = _map.Spawns[((spawn % _map.Spawns.Count) + _map.Spawns.Count) % _map.Spawns.Count];
                _aim = (_map.Bounds.Center - _position).Angle;
                _velocity = Vec2.Zero;
                _life = life;
                _placed = true;
            }

            if (alive && dt > 0)
            {
                Vec2 desired = input.Move.ClampLength(1f) * ArenaRules.MoveSpeed;
                Vec2 next = _map.ResolveCircle(_position + desired * dt, ArenaRules.PlayerRadius);
                _velocity = (next - _position) / dt;
                _position = next;
            }
            else
            {
                _velocity = Vec2.Zero;
            }
            if (input.Aim.LengthSquared > 1e-6f) _aim = input.Aim.Angle;

            if (alive && input.Fire && Phase == MatchPhase.Playing && (!_firedOnce || ServerClock.Diff(now, _nextFireAt) >= 0))
            {
                FireLocal(now);
            }

            var snapshot = new SnapshotBuffer.Snapshot { Time = now, Position = _position, Velocity = _velocity, Aim = _aim, Alive = alive, Life = (byte)_life };
            Authority.RecordState(_session.LocalActor, snapshot);

            if (!_sendScheduled || ServerClock.Diff(now, _nextSendAt) >= 0)
            {
                _sendScheduled = true;
                _nextSendAt = ServerClock.Add(now, ArenaRules.SendIntervalMs);
                var msg = new GameMessages.StateMsg { Seq = ++_stateSeq, Time = now, Position = _position, Velocity = _velocity, Aim = _aim, Alive = alive, Life = (byte)_life };
                _session.Send(EventCodes.State, GameMessages.Write(msg), Target.Others, false);
            }

            foreach (SnapshotBuffer buffer in _remote.Values) buffer.TrimBefore(ServerClock.Add(now, -1000));

            if (_bolts.Count > 0)
            {
                List<long> finished = null;
                foreach (var kv in _bolts)
                {
                    if (kv.Value.IsFinished(now)) (finished ??= new List<long>()).Add(kv.Key);
                }
                if (finished != null) foreach (long id in finished) _bolts.Remove(id);
            }

            Authority.Tick();
        }

        void FireLocal(int now)
        {
            float angle = GameMessages.QuantizeAngleValue(_aim);
            Vec2 origin = GameMessages.Quantize(_position + Vec2.FromAngle(angle) * ArenaRules.MuzzleOffset);
            var msg = new GameMessages.FireMsg { ShotId = ++_shotCounter, Time = now, Origin = origin, Angle = angle };
            _nextFireAt = ServerClock.Add(now, ArenaRules.FireCooldownMs);
            _firedOnce = true;
            var bolt = new Bolt(_session.LocalActor, msg.ShotId, origin, angle, now, _map);
            _bolts[bolt.Id] = bolt;
            BoltFired?.Invoke(bolt);
            _session.Send(EventCodes.Fire, GameMessages.Write(msg), Target.Others, true);
            Authority.OnFire(_session.LocalActor, msg, _map);
        }

        /// <summary>The ready flag is the player's own property; the master starts the countdown when everyone is ready.</summary>
        public void SetReady(bool ready)
        {
            if (_session.Room == null) return;
            _session.SetPlayerProperties(_session.LocalActor, PropertyBag.Of(PlayerKeys.Ready, ready));
        }

        public bool IsLocalReady => _session.Room?.GetPlayer(_session.LocalActor)?.Props.GetBool(PlayerKeys.Ready) ?? false;

        public ArenaFrame BuildFrame(ArenaFrame reuse = null)
        {
            ArenaFrame frame = reuse ?? new ArenaFrame();
            frame.Avatars.Clear();
            frame.Bolts.Clear();
            NetRoom room = _session.Room;
            frame.Map = _map;
            if (room == null) return frame;

            int now = _session.ServerTime;
            int renderTime = ServerClock.Add(now, -ArenaRules.InterpolationDelayMs);
            frame.ServerTime = now;
            frame.LocalActor = _session.LocalActor;
            frame.LocalIsMaster = _session.IsMaster;
            frame.RoomName = room.Name;
            frame.Mode = room.Props.GetString(RoomKeys.Mode, GameModes.FreeForAll);
            frame.Phase = Phase;
            frame.Round = room.Props.GetInt(RoomKeys.Round);
            frame.ScoreLimit = room.Props.GetInt(RoomKeys.ScoreLimit, GameModes.DefaultScoreLimit(frame.Mode));
            frame.Winner = room.Props.GetInt(RoomKeys.Winner);
            int elapsed = ServerClock.Diff(now, room.Props.GetInt(RoomKeys.PhaseStart, now));
            frame.PhaseRemainingMs = frame.Phase switch
            {
                MatchPhase.Countdown => Math.Max(0, ArenaRules.CountdownMs - elapsed),
                MatchPhase.Playing => Math.Max(0, room.Props.GetInt(RoomKeys.DurationSec, ArenaRules.DefaultDurationSec) * 1000 - elapsed),
                MatchPhase.Results => Math.Max(0, ArenaRules.ResultsMs - elapsed),
                _ => 0,
            };

            foreach (NetPlayer p in room.Players.Values)
            {
                var view = new AvatarView
                {
                    Actor = p.Actor,
                    Nickname = p.Nickname,
                    IsLocal = p.Actor == _session.LocalActor,
                    IsBot = p.Props.GetBool(PlayerKeys.IsBot),
                    IsMaster = p.Actor == room.MasterActor,
                    Ready = p.Props.GetBool(PlayerKeys.Ready),
                    Color = p.Props.GetInt(PlayerKeys.Color, (p.Actor - 1) % 4),
                    Hp = p.Props.GetInt(PlayerKeys.Hp, ArenaRules.MaxHp),
                    Kills = p.Props.GetInt(PlayerKeys.Kills),
                    Deaths = p.Props.GetInt(PlayerKeys.Deaths),
                    Alive = p.Props.GetBool(PlayerKeys.Alive, true),
                };
                if (!view.Alive && p.Props.ContainsKey(PlayerKeys.RespawnAt))
                {
                    view.RespawnInMs = Math.Max(0, ServerClock.Diff(p.Props.GetInt(PlayerKeys.RespawnAt), now));
                }
                if (view.IsLocal)
                {
                    view.Position = _position;
                    view.Aim = _aim;
                    view.Visible = _placed && view.Alive;
                }
                else if (_remote.TryGetValue(p.Actor, out SnapshotBuffer buffer) && !buffer.IsEmpty)
                {
                    SnapshotBuffer.Sample s = buffer.SampleAt(renderTime);
                    view.Position = s.Position;
                    view.Aim = s.Aim;
                    // Hide on the authoritative death right away; show again once the owner reports being alive.
                    view.Visible = view.Alive && s.Alive;
                }
                frame.Avatars.Add(view);
            }

            foreach (Bolt bolt in _bolts.Values)
            {
                // Remote bolts are drawn on the shared clock, so they appear part-way along their path; that is the
                // honest position, rather than starting them at the muzzle late.
                frame.Bolts.Add(new BoltView
                {
                    Id = bolt.Id,
                    Shooter = bolt.Shooter,
                    Color = room.GetPlayer(bolt.Shooter)?.Props.GetInt(PlayerKeys.Color, (bolt.Shooter - 1) % 4) ?? 0,
                    Position = bolt.PositionAt(now),
                    Direction = bolt.Direction,
                });
            }
            return frame;
        }

        public void Dispose()
        {
            Authority.HitResolved -= OnHitConfirmed;
            _session.RemoveCallbacks(this);
        }
    }
}


    public static object NormalizeText(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object NormalizeText(object data = null)
    {
        if (data == null) return null;
        return data;
    }