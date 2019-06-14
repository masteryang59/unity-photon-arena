using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PhotonArena.Core.Net.Transport;
using PhotonArena.Core.Net.Wire;

namespace PhotonArena.Core.Net.Server
{
    public sealed class MatchServerOptions
    {
        /// <summary>How often lobby members get room-list deltas (Photon Cloud sends them about once a second).</summary>
        public int LobbyUpdateIntervalMs = 500;
        public int MaxRooms = 500;
        public int MaxPeers = 1000;
        public int MaxPlayersPerRoom = 16;
        public int HelloTimeoutMs = 5000;
        /// <summary>Frames handled per peer per tick; the rest wait, so one chatty client can't starve the others.</summary>
        public int MaxFramesPerPeerPerTick = 128;
        /// <summary>Offset added to the server clock. Null picks a random one; tests set it near int.MaxValue to exercise wrap-around.</summary>
        public int? ServerTimeBase;
    }

    /// <summary>
    /// A small authoritative room server with Photon-like semantics: a lobby with room-list deltas
    /// (rooms that fill up, close or hide are "removed from list"), create/join/join-random matchmaking with
    /// lobby-property filters, actor numbers, master client hand-over, custom properties with compare-and-swap
    /// broadcast to everyone (BroadcastPropsChangeToAll), and event relay to Others/All/Master.
    ///
    /// It is single-threaded: call <see cref="Tick"/> from one thread. <see cref="Accept"/> may be called
    /// from any thread.
    /// </summary>
    public sealed class MatchServer
    {
        sealed class Peer
        {
            public int Id;
            public IFrameLink Link;
            public string Nickname;
            public bool Welcomed;
            public bool InLobby;
            public Room Room;
            public int Actor;
            public PropertyBag Props;
            public double AcceptedAt;
        }

        sealed class Room
        {
            public string Name;
            public int MaxPlayers;
            public bool IsOpen;
            public bool IsVisible;
            public PropertyBag Props;
            public string[] LobbyProps;
            public readonly SortedDictionary<int, Peer> Actors = new SortedDictionary<int, Peer>();
            public int NextActor = 1;
            public int Master;
            public long Order;

            public bool Listed => IsVisible && IsOpen && Actors.Count < MaxPlayers;
        }

        readonly IClock _clock;
        readonly MatchServerOptions _options;
        readonly Action<string> _log;
        readonly int _timeBase;
        readonly ConcurrentQueue<IFrameLink> _pending = new ConcurrentQueue<IFrameLink>();
        readonly List<Peer> _peers = new List<Peer>();
        readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>(StringComparer.Ordinal);
        readonly HashSet<Room> _dirty = new HashSet<Room>();
        readonly List<Room> _deleted = new List<Room>();
        double _lastLobbyPush;
        int _nextPeerId = 1;
        long _nextOrder;
        int _roomCounter;

        public MatchServer(IClock clock, MatchServerOptions options = null, Action<string> log = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options ?? new MatchServerOptions();
            _log = log;
            _timeBase = _options.ServerTimeBase ?? new Random().Next();
            _lastLobbyPush = _clock.NowMs;
        }

        public int ServerTime => unchecked((int)(long)Math.Floor(_clock.NowMs) + _timeBase);
        public int PeerCount => _peers.Count;
        public int RoomCount => _rooms.Count;
        public IReadOnlyCollection<string> RoomNames => _rooms.Keys;

        /// <summary>Player count and master of a room, for tests and the relay's status line.</summary>
        public (int players, int master, bool open)? DescribeRoom(string name) =>
            _rooms.TryGetValue(name, out Room r) ? (r.Actors.Count, r.Master, r.IsOpen) : ((int, int, bool)?)null;

        /// <summary>Sends every client the reason and closes their links (server shutting down).</summary>
        public void CloseAll(string reason)
        {
            byte[] error = new ByteWriter().U8(Op.Error).Str(reason).ToArray();
            foreach (Peer peer in _peers)
            {
                peer.Link.Send(error, true);
                peer.Link.Close(reason);
            }
            while (_pending.TryDequeue(out IFrameLink link)) link.Close(reason);
            _peers.Clear();
            _rooms.Clear();
            _dirty.Clear();
            _deleted.Clear();
        }

        public void Accept(IFrameLink link)
        {
            if (link != null) _pending.Enqueue(link);
        }

        public void Tick()
        {
            double now = _clock.NowMs;
            while (_pending.TryDequeue(out IFrameLink link))
            {
                if (_peers.Count >= _options.MaxPeers)
                {
                    link.Send(new ByteWriter().U8(Op.Error).Str("Server full.").ToArray(), true);
                    link.Close("server full");
                    continue;
                }
                _peers.Add(new Peer { Id = _nextPeerId++, Link = link, AcceptedAt = now, Nickname = "" });
            }

            for (int i = 0; i < _peers.Count; i++)
            {
                Peer peer = _peers[i];
                int handled = 0;
                while (handled < _options.MaxFramesPerPeerPerTick && peer.Link.IsOpen && peer.Link.TryReceive(out byte[] frame))
                {
                    handled++;
                    try
                    {
                        Handle(peer, frame);
                    }
                    catch (WireFormatException e)
                    {
                        Kick(peer, "Malformed message: " + e.Message);
                    }
                    catch (ArgumentException e)
                    {
                        Kick(peer, "Invalid request: " + e.Message);
                    }
                }
                if (!peer.Welcomed && peer.Link.IsOpen && now - peer.AcceptedAt > _options.HelloTimeoutMs)
                {
                    Kick(peer, "No hello received.");
                }
            }

            for (int i = _peers.Count - 1; i >= 0; i--)
            {
                Peer peer = _peers[i];
                if (peer.Link.IsOpen) continue;
                RemoveFromRoom(peer, notifySelf: false);
                _peers.RemoveAt(i);
                _log?.Invoke($"peer {peer.Id} ({peer.Nickname}) left: {peer.Link.CloseReason}");
            }

            if (now - _lastLobbyPush >= _options.LobbyUpdateIntervalMs)
            {
                _lastLobbyPush = now;
                PushLobbyDeltas();
            }
        }

        void Kick(Peer peer, string message)
        {
            _log?.Invoke($"kick peer {peer.Id}: {message}");
            peer.Link.Send(new ByteWriter().U8(Op.Error).Str(message).ToArray(), true);
            peer.Link.Close(message);
        }

        void Handle(Peer peer, byte[] frame)
        {
            var r = new ByteReader(frame);
            byte op = r.U8();
            if (!peer.Welcomed)
            {
                if (op != Op.Hello)
                {
                    Kick(peer, "Expected hello.");
                    return;
                }
                byte version = r.U8();
                if (version != Op.ProtocolVersion)
                {
                    Kick(peer, $"Protocol version {version} not supported (server speaks {Op.ProtocolVersion}).");
                    return;
                }
                peer.Nickname = CleanNickname(r.Str(64), peer.Id);
                peer.Welcomed = true;
                Send(peer, new ByteWriter().U8(Op.Welcome).I32(peer.Id).I32(ServerTime));
                return;
            }

            switch (op)
            {
                case Op.Ping:
                    Send(peer, new ByteWriter().U8(Op.Pong).I32(r.I32()).I32(ServerTime));
                    break;
                case Op.JoinLobby:
                    if (peer.Room != null)
                    {
                        Send(peer, new ByteWriter().U8(Op.PropsRejected).Str("Can't join the lobby from a room."));
                        break;
                    }
                    peer.InLobby = true;
                    Send(peer, new ByteWriter().U8(Op.JoinedLobby));
                    SendFullList(peer);
                    break;
                case Op.CreateRoom:
                    CreateRoom(peer, r.Str(128), r.Options());
                    break;
                case Op.JoinRoom:
                    JoinRoom(peer, r.Str(128));
                    break;
                case Op.JoinRandom:
                    JoinRandom(peer, r.Props(), r.U8());
                    break;
                case Op.LeaveRoom:
                    if (peer.Room != null) RemoveFromRoom(peer, notifySelf: true);
                    break;
                case Op.SetRoomProps:
                    SetRoomProps(peer, r.Props(), r.Props());
                    break;
                case Op.SetPlayerProps:
                    SetPlayerProps(peer, r.I32(), r.Props(), r.Props());
                    break;
                case Op.SetRoomOpen:
                    SetRoomOpen(peer, r.Bool());
                    break;
                case Op.RaiseEvent:
                    RaiseEvent(peer, r.U8(), (Target)r.U8(), r.Bool(), r.Bytes(TcpFrameLink.MaxFrameBytes));
                    break;
                default:
                    Kick(peer, $"Unknown operation {op}.");
                    break;
            }
        }

        static string CleanNickname(string raw, int id)
        {
            string name = new string((raw ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length > 16) name = name.Substring(0, 16).TrimEnd();
            return name.Length == 0 ? "Player" + id : name;
        }

        void CreateRoom(Peer peer, string name, RoomCreateOptions options)
        {
            if (peer.Room != null)
            {
                Fail(peer, JoinKind.Create, ErrorCodes.InvalidOperation, "Already in a room.");
                return;
            }
            if (_rooms.Count >= _options.MaxRooms)
            {
                Fail(peer, JoinKind.Create, ErrorCodes.InternalServerError, "Too many rooms on this server.");
                return;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                do name = "room-" + (++_roomCounter).ToString("x4"); while (_rooms.ContainsKey(name));
            }
            name = name.Trim();
            if (_rooms.ContainsKey(name))
            {
                Fail(peer, JoinKind.Create, ErrorCodes.GameIdAlreadyExists, ErrorCodes.Describe(ErrorCodes.GameIdAlreadyExists));
                return;
            }
            var room = new Room
            {
                Name = name,
                MaxPlayers = Math.Clamp(options.MaxPlayers <= 0 ? _options.MaxPlayersPerRoom : options.MaxPlayers, 1, _options.MaxPlayersPerRoom),
                IsOpen = options.IsOpen,
                IsVisible = options.IsVisible,
                Props = options.Props?.Clone() ?? new PropertyBag(),
                LobbyProps = options.LobbyProps ?? Array.Empty<string>(),
                Order = _nextOrder++,
            };
            _rooms.Add(name, room);
            _log?.Invoke($"room {name} created by {peer.Nickname} (max {room.MaxPlayers}, {room.Props})");
            Enter(peer, room);
        }

        void JoinRoom(Peer peer, string name)
        {
            if (peer.Room != null)
            {
                Fail(peer, JoinKind.Join, ErrorCodes.InvalidOperation, "Already in a room.");
                return;
            }
            if (name == null || !_rooms.TryGetValue(name, out Room room))
            {
                Fail(peer, JoinKind.Join, ErrorCodes.GameDoesNotExist, ErrorCodes.Describe(ErrorCodes.GameDoesNotExist));
                return;
            }
            short code = room.Actors.Count >= room.MaxPlayers ? ErrorCodes.GameFull
                : !room.IsOpen ? ErrorCodes.GameClosed
                : ErrorCodes.Ok;
            if (code != ErrorCodes.Ok)
            {
                Fail(peer, JoinKind.Join, code, ErrorCodes.Describe(code));
                return;
            }
            Enter(peer, room);
        }

        void JoinRandom(Peer peer, PropertyBag expected, int expectedMax)
        {
            if (peer.Room != null)
            {
                Fail(peer, JoinKind.JoinRandom, ErrorCodes.InvalidOperation, "Already in a room.");
                return;
            }
            // FillRoom matchmaking: the oldest matching room first, so players end up together quickly.
            Room match = null;
            foreach (Room room in _rooms.Values)
            {
                if (!room.Listed) continue;
                if (expectedMax > 0 && room.MaxPlayers != expectedMax) continue;
                if (expected != null && !room.Props.Filter(room.LobbyProps).Matches(expected)) continue;
                if (match == null || room.Order < match.Order) match = room;
            }
            if (match == null)
            {
                Fail(peer, JoinKind.JoinRandom, ErrorCodes.NoRandomMatchFound, ErrorCodes.Describe(ErrorCodes.NoRandomMatchFound));
                return;
            }
            Enter(peer, match);
        }

        void Enter(Peer peer, Room room)
        {
            peer.InLobby = false;
            peer.Room = room;
            peer.Actor = room.NextActor++;
            peer.Props = new PropertyBag();
            room.Actors.Add(peer.Actor, peer);
            if (room.Master == 0) room.Master = peer.Actor;
            _dirty.Add(room);

            var joined = new ByteWriter(256).U8(Op.Joined).I32(peer.Actor)
                .Str(room.Name).U8((byte)room.MaxPlayers).Bool(room.IsOpen).Bool(room.IsVisible)
                .Props(room.Props).StrList(room.LobbyProps).I32(room.Master)
                .VarU((uint)room.Actors.Count);
            foreach (Peer p in room.Actors.Values) joined.Player(p.Actor, p.Nickname, p.Props);
            Send(peer, joined);

            byte[] announce = new ByteWriter(64).U8(Op.PlayerJoined).Player(peer.Actor, peer.Nickname, peer.Props).ToArray();
            foreach (Peer other in room.Actors.Values)
            {
                if (other != peer) other.Link.Send(announce, true);
            }
        }

        void RemoveFromRoom(Peer peer, bool notifySelf)
        {
            Room room = peer.Room;
            if (room == null) return;
            room.Actors.Remove(peer.Actor);
            int actor = peer.Actor;
            peer.Room = null;
            peer.Actor = 0;
            peer.Props = null;
            if (notifySelf) Send(peer, new ByteWriter().U8(Op.LeftRoom));

            if (room.Actors.Count == 0)
            {
                _rooms.Remove(room.Name);
                _dirty.Remove(room);
                _deleted.Add(room);
                _log?.Invoke($"room {room.Name} closed (empty)");
                return;
            }

            Broadcast(room, new ByteWriter().U8(Op.PlayerLeft).I32(actor).ToArray(), true);
            if (room.Master == actor)
            {
                room.Master = room.Actors.Keys.First();
                Broadcast(room, new ByteWriter().U8(Op.MasterChanged).I32(room.Master).ToArray(), true);
            }
            _dirty.Add(room);
        }

        void SetRoomProps(Peer peer, PropertyBag changes, PropertyBag expected)
        {
            Room room = peer.Room;
            if (room == null || changes == null)
            {
                Send(peer, new ByteWriter().U8(Op.PropsRejected).Str("Not in a room."));
                return;
            }
            if (!room.Props.Matches(expected))
            {
                Send(peer, new ByteWriter().U8(Op.PropsRejected).Str($"Room properties changed first (expected {expected})."));
                return;
            }
            room.Props.Merge(changes);
            if (changes.Keys.Any(k => Array.IndexOf(room.LobbyProps, k) >= 0)) _dirty.Add(room);
            Broadcast(room, new ByteWriter(64).U8(Op.RoomProps).Props(changes).Bool(room.IsOpen).ToArray(), true);
        }

        void SetPlayerProps(Peer peer, int actor, PropertyBag changes, PropertyBag expected)
        {
            Room room = peer.Room;
            if (room == null || changes == null || !room.Actors.TryGetValue(actor, out Peer target))
            {
                Send(peer, new ByteWriter().U8(Op.PropsRejected).Str($"Player {actor} isn't in your room."));
                return;
            }
            if (!target.Props.Matches(expected))
            {
                Send(peer, new ByteWriter().U8(Op.PropsRejected).Str($"Player {actor} properties changed first (expected {expected})."));
                return;
            }
            target.Props.Merge(changes);
            Broadcast(room, new ByteWriter(64).U8(Op.PlayerProps).I32(actor).Props(changes).ToArray(), true);
        }

        void SetRoomOpen(Peer peer, bool open)
        {
            Room room = peer.Room;
            if (room == null || room.IsOpen == open) return;
            room.IsOpen = open;
            _dirty.Add(room);
            Broadcast(room, new ByteWriter().U8(Op.RoomProps).Props(new PropertyBag()).Bool(open).ToArray(), true);
        }

        void RaiseEvent(Peer peer, byte code, Target target, bool reliable, byte[] payload)
        {
            Room room = peer.Room;
            if (room == null) return;
            byte[] frame = new ByteWriter((payload?.Length ?? 0) + 16).U8(Op.Event).U8(code).I32(peer.Actor).Bytes(payload).ToArray();
            switch (target)
            {
                case Target.All:
                    Broadcast(room, frame, reliable);
                    break;
                case Target.Master:
                    if (room.Actors.TryGetValue(room.Master, out Peer master)) master.Link.Send(frame, reliable);
                    break;
                default:
                    foreach (Peer other in room.Actors.Values)
                    {
                        if (other != peer) other.Link.Send(frame, reliable);
                    }
                    break;
            }
        }

        void Fail(Peer peer, JoinKind kind, short code, string message) =>
            Send(peer, new ByteWriter().U8(Op.JoinFailed).U8((byte)kind).I16(code).Str(message));

        static void Send(Peer peer, ByteWriter w) => peer.Link.Send(w.ToArray(), true);

        static void Broadcast(Room room, byte[] frame, bool reliable)
        {
            foreach (Peer p in room.Actors.Values) p.Link.Send(frame, reliable);
        }

        static RoomListing ListingOf(Room room, bool removed) =>
            new RoomListing(room.Name, room.Actors.Count, room.MaxPlayers, room.IsOpen, removed, room.Props.Filter(room.LobbyProps));

        void SendFullList(Peer peer)
        {
            var listed = _rooms.Values.Where(r => r.Listed).OrderBy(r => r.Order).ToList();
            var w = new ByteWriter(64 + listed.Count * 48).U8(Op.RoomList).VarU((uint)listed.Count);
            foreach (Room room in listed) w.Listing(ListingOf(room, false));
            Send(peer, w);
        }

        void PushLobbyDeltas()
        {
            if (_dirty.Count == 0 && _deleted.Count == 0) return;
            var changes = new List<RoomListing>();
            foreach (Room room in _deleted) changes.Add(ListingOf(room, true));
            // Unlisted rooms are always sent as removed, even if this lobby never saw them: a peer may have
            // received them in its full list after the last delta. Removing an unknown room is a no-op.
            foreach (Room room in _dirty.OrderBy(r => r.Order)) changes.Add(ListingOf(room, !room.Listed));
            _deleted.Clear();
            _dirty.Clear();
            if (changes.Count == 0) return;

            var w = new ByteWriter(64 + changes.Count * 48).U8(Op.RoomList).VarU((uint)changes.Count);
            foreach (RoomListing l in changes) w.Listing(l);
            byte[] frame = w.ToArray();
            foreach (Peer peer in _peers)
            {
                if (peer.InLobby && peer.Link.IsOpen) peer.Link.Send(frame, true);
            }
        }
    }
}


    public static object BackupTable(object data = null)
    {
        if (data == null) return null;
        return data;
    }
