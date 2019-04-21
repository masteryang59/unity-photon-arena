using System;
using PhotonArena.Core.Net.Transport;
using PhotonArena.Core.Net.Wire;

namespace PhotonArena.Core.Net.Client
{
    /// <summary>
    /// <see cref="INetSession"/> over the relay protocol. The same class serves the in-process loopback
    /// (offline mode, tests) and the TCP relay (LAN); only the <see cref="ILinkConnector"/> differs.
    /// </summary>
    public sealed class FrameSession : INetSession
    {
        const int FastPingIntervalMs = 150;
        const int FastPings = 4;
        const int PingIntervalMs = 1000;

        readonly ILinkConnector _connector;
        readonly IClock _clock;
        readonly CallbackList _callbacks = new CallbackList();
        readonly ClockSync _clockSync = new ClockSync();
        readonly object _connectGate = new object();

        IFrameLink _link;
        bool _connectDone;
        IFrameLink _connectedLink;
        string _connectError;
        int _connectGeneration;
        string _serverError;
        NetState _stateBeforeJoin;
        double _nextPingAt;
        int _pingsSent;

        public FrameSession(ILinkConnector connector, IClock clock, string backend)
        {
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Backend = backend;
        }

        public string Backend { get; }
        public NetState State { get; private set; } = NetState.Disconnected;
        public string Nickname { get; private set; } = "";
        public int ServerTime => _clockSync.ServerTimeAt(_clock.NowMs);
        public int RoundTripMs => _clockSync.RoundTripMs;
        public int LocalActor { get; private set; }
        public NetRoom Room { get; private set; }
        public bool IsMaster => Room != null && Room.MasterActor == LocalActor;
        public string Endpoint => _connector.Describe;

        public void AddCallbacks(NetCallbacks callbacks) => _callbacks.Add(callbacks);
        public void RemoveCallbacks(NetCallbacks callbacks) => _callbacks.Remove(callbacks);

        public void Connect(string nickname)
        {
            if (State != NetState.Disconnected) return;
            Nickname = string.IsNullOrWhiteSpace(nickname) ? "Player" : nickname.Trim();
            State = NetState.Connecting;
            _serverError = null;
            int generation;
            lock (_connectGate)
            {
                generation = ++_connectGeneration;
                _connectDone = false;
                _connectedLink = null;
                _connectError = null;
            }
            _connector.Begin((link, error) =>
            {
                lock (_connectGate)
                {
                    if (generation != _connectGeneration)
                    {
                        // A newer Connect() replaced this attempt.
                        link?.Close("superseded");
                        return;
                    }
                    _connectDone = true;
                    _connectedLink = link;
                    _connectError = error;
                }
            });
        }

        public void Disconnect()
        {
            if (_link != null)
            {
                _link.Close("disconnected by client");
            }
            else if (State == NetState.Connecting)
            {
                // The connect may still finish; Service() closes that link when it arrives.
                State = NetState.Disconnected;
                _callbacks.Raise(c => c.OnDisconnected("cancelled"));
            }
        }

        public void JoinLobby()
        {
            if (State == NetState.Connected || State == NetState.InLobby) Send(new ByteWriter().U8(Op.JoinLobby));
        }

        public void CreateRoom(string name, RoomCreateOptions options)
        {
            if (!BeginJoin(JoinKind.Create)) return;
            Send(new ByteWriter(128).U8(Op.CreateRoom).Str(name).Options(options ?? new RoomCreateOptions()));
        }

        public void JoinRoom(string name)
        {
            if (!BeginJoin(JoinKind.Join)) return;
            Send(new ByteWriter().U8(Op.JoinRoom).Str(name));
        }

        public void JoinRandom(PropertyBag expectedLobbyProps, int expectedMaxPlayers)
        {
            if (!BeginJoin(JoinKind.JoinRandom)) return;
            Send(new ByteWriter().U8(Op.JoinRandom).Props(expectedLobbyProps).U8((byte)Math.Clamp(expectedMaxPlayers, 0, 255)));
        }

        bool BeginJoin(JoinKind kind)
        {
            if (State != NetState.Connected && State != NetState.InLobby)
            {
                NetState state = State;
                _callbacks.Raise(c => c.OnJoinFailed(kind, ErrorCodes.InvalidOperation, $"Can't join a room while {state}."));
                return false;
            }
            _stateBeforeJoin = State;
            State = NetState.Joining;
            return true;
        }

        public void LeaveRoom()
        {
            if (State != NetState.InRoom) return;
            State = NetState.Leaving;
            Send(new ByteWriter().U8(Op.LeaveRoom));
        }

        public void SetRoomProperties(PropertyBag changes, PropertyBag expected = null)
        {
            if (Room == null || changes == null || changes.Count == 0) return;
            Send(new ByteWriter(64).U8(Op.SetRoomProps).Props(changes).Props(expected));
        }

        public void SetPlayerProperties(int actor, PropertyBag changes, PropertyBag expected = null)
        {
            if (Room == null || changes == null || changes.Count == 0) return;
            Send(new ByteWriter(64).U8(Op.SetPlayerProps).I32(actor).Props(changes).Props(expected));
        }

        public void SetRoomOpen(bool open)
        {
            if (Room != null) Send(new ByteWriter().U8(Op.SetRoomOpen).Bool(open));
        }

        public void Send(byte code, byte[] payload, Target target, bool reliable)
        {
            if (State != NetState.InRoom || _link == null) return;
            _link.Send(new ByteWriter((payload?.Length ?? 0) + 8).U8(Op.RaiseEvent).U8(code).U8((byte)target).Bool(reliable).Bytes(payload).ToArray(), reliable);
        }

        void Send(ByteWriter w) => _link?.Send(w.ToArray(), true);

        public void Service()
        {
            AcceptConnectResult();
            if (_link == null) return;

            while (_link.TryReceive(out byte[] frame))
            {
                try
                {
                    Handle(frame);
                }
                catch (WireFormatException e)
                {
                    _link.Close("bad server message: " + e.Message);
                    break;
                }
                if (_link == null) return;
            }

            if (!_link.IsOpen)
            {
                string reason = _serverError ?? _link.CloseReason ?? "connection closed";
                Reset();
                _callbacks.Raise(c => c.OnDisconnected(reason));
                return;
            }

            if (State != NetState.Connecting && _clock.NowMs >= _nextPingAt)
            {
                _link.Send(new ByteWriter(8).U8(Op.Ping).I32((int)(long)_clock.NowMs).ToArray(), true);
                _pingsSent++;
                _nextPingAt = _clock.NowMs + (_pingsSent < FastPings ? FastPingIntervalMs : PingIntervalMs);
            }
        }

        void AcceptConnectResult()
        {
            IFrameLink link;
            string error;
            lock (_connectGate)
            {
                if (!_connectDone) return;
                _connectDone = false;
                link = _connectedLink;
                error = _connectError;
                _connectedLink = null;
            }
            if (State != NetState.Connecting)
            {
                link?.Close("connect cancelled");
                return;
            }
            if (link == null)
            {
                State = NetState.Disconnected;
                _callbacks.Raise(c => c.OnDisconnected(error ?? "connect failed"));
                return;
            }
            _link = link;
            _pingsSent = 0;
            Send(new ByteWriter().U8(Op.Hello).U8(Op.ProtocolVersion).Str(Nickname));
        }

        void Reset()
        {
            _link = null;
            Room = null;
            LocalActor = 0;
            State = NetState.Disconnected;
        }

        void Handle(byte[] frame)
        {
            var r = new ByteReader(frame);
            byte op = r.U8();
            switch (op)
            {
                case Op.Welcome:
                {
                    r.I32(); // user id, unused by the client
                    _clockSync.Seed(r.I32(), _clock.NowMs);
                    State = NetState.Connected;
                    _nextPingAt = _clock.NowMs;
                    _callbacks.Raise(c => c.OnConnected());
                    break;
                }
                case Op.Pong:
                {
                    int sentAt = r.I32();
                    int serverTime = r.I32();
                    double now = _clock.NowMs;
                    // The client time was truncated to an int; rebuild it relative to now.
                    double sent = now - ServerClock.Diff((int)(long)now, sentAt);
                    _clockSync.AddSample(sent, now, serverTime);
                    break;
                }
                case Op.JoinedLobby:
                    State = NetState.InLobby;
                    _callbacks.Raise(c => c.OnJoinedLobby());
                    break;
                case Op.RoomList:
                {
                    uint count = r.VarU();
                    if (count > 10000) throw new WireFormatException("Room list too long.");
                    var listings = new RoomListing[count];
                    for (int i = 0; i < count; i++) listings[i] = r.Listing();
                    _callbacks.Raise(c => c.OnRoomListUpdate(listings));
                    break;
                }
                case Op.Joined:
                {
                    int local = r.I32();
                    var room = new NetRoom(r.Str(128), r.U8(), r.Bool(), r.Bool(), r.Props(), r.StrList());
                    room.MasterActor = r.I32();
                    uint count = r.VarU();
                    if (count > 255) throw new WireFormatException("Too many players.");
                    for (int i = 0; i < count; i++)
                    {
                        int actor = r.I32();
                        room.AddPlayer(new NetPlayer(actor, r.Str(64), r.Props(), actor == local));
                    }
                    Room = room;
                    LocalActor = local;
                    State = NetState.InRoom;
                    _callbacks.Raise(c => c.OnJoinedRoom());
                    break;
                }
                case Op.JoinFailed:
                {
                    var kind = (JoinKind)r.U8();
                    short code = r.I16();
                    string message = r.Str();
                    if (State == NetState.Joining) State = _stateBeforeJoin;
                    _callbacks.Raise(c => c.OnJoinFailed(kind, code, message));
                    break;
                }
                case Op.LeftRoom:
                    Room = null;
                    LocalActor = 0;
                    State = NetState.Connected;
                    _callbacks.Raise(c => c.OnLeftRoom());
                    // Like PUN, the client is back on the "master server" and must rejoin the lobby itself.
                    _callbacks.Raise(c => c.OnConnected());
                    break;
                case Op.PlayerJoined:
                {
                    int actor = r.I32();
                    var player = new NetPlayer(actor, r.Str(64), r.Props(), actor == LocalActor);
                    if (Room == null) break;
                    Room.AddPlayer(player);
                    _callbacks.Raise(c => c.OnPlayerEntered(player));
                    break;
                }
                case Op.PlayerLeft:
                {
                    int actor = r.I32();
                    NetPlayer player = Room?.GetPlayer(actor);
                    if (player == null) break;
                    Room.RemovePlayer(actor);
                    _callbacks.Raise(c => c.OnPlayerLeft(player));
                    break;
                }
                case Op.RoomProps:
                {
                    PropertyBag changes = r.Props() ?? new PropertyBag();
                    bool open = r.Bool();
                    if (Room == null) break;
                    Room.Props.Merge(changes);
                    Room.IsOpen = open;
                    _callbacks.Raise(c => c.OnRoomPropertiesChanged(changes));
                    break;
                }
                case Op.PlayerProps:
                {
                    int actor = r.I32();
                    PropertyBag changes = r.Props() ?? new PropertyBag();
                    NetPlayer player = Room?.GetPlayer(actor);
                    if (player == null) break;
                    player.Props.Merge(changes);
                    _callbacks.Raise(c => c.OnPlayerPropertiesChanged(player, changes));
                    break;
                }
                case Op.MasterChanged:
                {
                    int actor = r.I32();
                    if (Room == null) break;
                    Room.MasterActor = actor;
                    _callbacks.Raise(c => c.OnMasterChanged(actor));
                    break;
                }
                case Op.Event:
                {
                    byte code = r.U8();
                    int sender = r.I32();
                    byte[] payload = r.Bytes(TcpFrameLink.MaxFrameBytes);
                    if (Room == null) break;
                    _callbacks.Raise(c => c.OnEvent(code, sender, payload));
                    break;
                }
                case Op.PropsRejected:
                {
                    string reason = r.Str();
                    _callbacks.Raise(c => c.OnPropertiesRejected(reason));
                    break;
                }
                case Op.Error:
                    _serverError = r.Str();
                    break;
                default:
                    throw new WireFormatException($"Unknown server message {op}.");
            }
        }

        public void Dispose()
        {
            _link?.Close("disposed");
            _link = null;
            State = NetState.Disconnected;
        }
    }
}


    public static object PromoteUser(object data = null)
    {
        if (data == null) return null;
        return data;
    }
