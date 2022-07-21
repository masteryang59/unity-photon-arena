using System;
using System.Collections.Generic;
using PhotonArena.Core.Match;
using PhotonArena.Core.Net;
using PhotonArena.Core.Sim;

namespace PhotonArena.Core.Matchmaking
{
    /// <summary>What a player asks for when creating or quick-matching.</summary>
    [Serializable]
    public sealed class MatchSpec
    {
        public string Mode = GameModes.FreeForAll;
        /// <summary>Map to create with. For quick match, null means "any map".</summary>
        public string Map = ArenaMaps.Pillars;
        public int MaxPlayers = 4;
        public int DurationSec = ArenaRules.DefaultDurationSec;

        public MatchSpec Clone() => (MatchSpec)MemberwiseClone();
    }

    public enum MatchmakerPhase
    {
        Offline,
        Connecting,
        /// <summary>Connected and browsing rooms.</summary>
        Lobby,
        /// <summary>Quick match: looking for a room, or creating one because none matched.</summary>
        Searching,
        Joining,
        InRoom,
        Leaving,
    }

    /// <summary>
    /// The lobby and matchmaking flow on top of any <see cref="INetSession"/>:
    /// connect → join lobby → quick match (join random with mode/map/version filters, create a room when
    /// nothing matches, retry when a listed room filled up in the meantime) or create/join by name.
    /// </summary>
    public sealed class Matchmaker : NetCallbacks, IDisposable
    {
        public const int MaxJoinRetries = 3;
        public const int MaxCreateRetries = 3;

        enum Pending { None, QuickMatch, Create, Join }

        readonly INetSession _session;
        readonly string _gameVersion;
        readonly Random _random;
        Pending _pending;
        MatchSpec _spec;
        string _roomName;
        int _joinRetries;
        int _createRetries;

        public Matchmaker(INetSession session, string gameVersion, Random random = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _gameVersion = gameVersion ?? "1";
            _random = random ?? new Random();
            _session.AddCallbacks(this);
        }

        public RoomListCache Rooms { get; } = new RoomListCache();
        public MatchmakerPhase Phase { get; private set; } = MatchmakerPhase.Offline;
        public string Status { get; private set; } = "Not connected.";
        /// <summary>The last failure the player should see, or null.</summary>
        public string LastError { get; private set; }
        public INetSession Session => _session;

        /// <summary>Raised whenever <see cref="Phase"/>, <see cref="Status"/> or the room list changes.</summary>
        public event Action Changed;

        public void Connect(string nickname)
        {
            if (_session.State != NetState.Disconnected) return;
            LastError = null;
            SetPhase(MatchmakerPhase.Connecting, $"Connecting to {_session.Backend}...");
            _session.Connect(nickname);
        }

        public void Disconnect()
        {
            _pending = Pending.None;
            _session.Disconnect();
        }

        /// <summary>Join any open room with the same mode (and map, if set); create one if there is none.</summary>
        public void QuickMatch(MatchSpec spec)
        {
            Start(Pending.QuickMatch, spec, null);
        }

        public void CreateRoom(MatchSpec spec, string name = null)
        {
            Start(Pending.Create, spec, name);
        }

        public void JoinRoom(string name)
        {
            Start(Pending.Join, _spec ?? new MatchSpec(), name);
        }

        public void LeaveRoom()
        {
            _pending = Pending.None;
            if (_session.State != NetState.InRoom) return;
            SetPhase(MatchmakerPhase.Leaving, "Leaving room...");
            _session.LeaveRoom();
        }

        void Start(Pending what, MatchSpec spec, string name)
        {
            if (_session.State == NetState.InRoom || _session.State == NetState.Joining) return;
            _pending = what;
            _spec = (spec ?? new MatchSpec()).Clone();
            _spec.MaxPlayers = GameModes.ClampMaxPlayers(_spec.Mode, _spec.MaxPlayers);
            _roomName = name;
            _joinRetries = 0;
            _createRetries = 0;
            LastError = null;
            if (_session.State == NetState.InLobby || _session.State == NetState.Connected) RunPending();
            else if (_session.State == NetState.Disconnected) LastError = "Connect first.";
        }

        void RunPending()
        {
            switch (_pending)
            {
                case Pending.QuickMatch:
                    SetPhase(MatchmakerPhase.Searching, $"Looking for a {GameModes.DisplayName(_spec.Mode)} room...");
                    _session.JoinRandom(QuickMatchFilter(_spec), 0);
                    break;
                case Pending.Create:
                    SetPhase(MatchmakerPhase.Joining, "Creating room...");
                    _session.CreateRoom(string.IsNullOrWhiteSpace(_roomName) ? RoomNames.Generate(_random) : _roomName.Trim(), BuildRoomOptions(_spec, _gameVersion));
                    break;
                case Pending.Join:
                    SetPhase(MatchmakerPhase.Joining, $"Joining {_roomName}...");
                    _session.JoinRoom(_roomName);
                    break;
            }
        }

        public PropertyBag QuickMatchFilter(MatchSpec spec)
        {
            var filter = PropertyBag.Of(RoomKeys.Mode, spec.Mode, RoomKeys.GameVersion, _gameVersion);
            if (!string.IsNullOrEmpty(spec.Map)) filter.Set(RoomKeys.Map, spec.Map);
            return filter;
        }

        public static RoomCreateOptions BuildRoomOptions(MatchSpec spec, string gameVersion)
        {
            string map = ArenaMaps.Exists(spec.Map) ? spec.Map : ArenaMaps.Pillars;
            return new RoomCreateOptions
            {
                MaxPlayers = GameModes.ClampMaxPlayers(spec.Mode, spec.MaxPlayers),
                IsOpen = true,
                IsVisible = true,
                Props = PropertyBag.Of(
                    RoomKeys.Mode, spec.Mode,
                    RoomKeys.Map, map,
                    RoomKeys.GameVersion, gameVersion,
                    RoomKeys.Phase, (int)MatchPhase.Waiting,
                    RoomKeys.ScoreLimit, GameModes.DefaultScoreLimit(spec.Mode),
                    RoomKeys.DurationSec, Math.Clamp(spec.DurationSec, 30, 600),
                    RoomKeys.Round, 0),
                LobbyProps = RoomKeys.Lobby,
            };
        }

        public override void OnConnected()
        {
            // Also raised after leaving a room (back on the master server): rejoin the lobby either way.
            SetPhase(MatchmakerPhase.Lobby, "Connected. Joining lobby...");
            _session.JoinLobby();
        }

        public override void OnJoinedLobby()
        {
            Rooms.Clear();
            SetPhase(MatchmakerPhase.Lobby, "In lobby.");
            if (_pending != Pending.None && Phase == MatchmakerPhase.Lobby) RunPending();
        }

        public override void OnRoomListUpdate(IReadOnlyList<RoomListing> changed)
        {
            Rooms.Apply(changed);
            Changed?.Invoke();
        }

        public override void OnJoinedRoom()
        {
            _pending = Pending.None;
            Rooms.Clear();
            SetPhase(MatchmakerPhase.InRoom, $"In room {_session.Room?.Name}.");
        }

        public override void OnLeftRoom()
        {
            SetPhase(MatchmakerPhase.Lobby, "Left the room.");
        }

        public override void OnJoinFailed(JoinKind kind, short code, string message)
        {
            if (_pending == Pending.QuickMatch)
            {
                if (kind == JoinKind.JoinRandom && code == ErrorCodes.NoRandomMatchFound)
                {
                    // Nothing open: become the host of a new room that others will quick-match into.
                    SetPhase(MatchmakerPhase.Searching, "No open room found. Creating one...");
                    _session.CreateRoom(RoomNames.Generate(_random), BuildRoomOptions(QuickMatchCreateSpec(), _gameVersion));
                    return;
                }
                if (kind == JoinKind.JoinRandom && (code == ErrorCodes.GameFull || code == ErrorCodes.GameClosed) && ++_joinRetries <= MaxJoinRetries)
                {
                    _session.JoinRandom(QuickMatchFilter(_spec), 0);
                    return;
                }
                if (kind == JoinKind.Create && code == ErrorCodes.GameIdAlreadyExists && ++_createRetries <= MaxCreateRetries)
                {
                    _session.CreateRoom(RoomNames.Generate(_random), BuildRoomOptions(QuickMatchCreateSpec(), _gameVersion));
                    return;
                }
            }
            else if (_pending == Pending.Create && code == ErrorCodes.GameIdAlreadyExists && string.IsNullOrWhiteSpace(_roomName) && ++_createRetries <= MaxCreateRetries)
            {
                _session.CreateRoom(RoomNames.Generate(_random), BuildRoomOptions(_spec, _gameVersion));
                return;
            }

            _pending = Pending.None;
            LastError = string.IsNullOrEmpty(message) ? ErrorCodes.Describe(code) : message;
            SetPhase(_session.State == NetState.Disconnected ? MatchmakerPhase.Offline : MatchmakerPhase.Lobby, LastError);
        }

        MatchSpec QuickMatchCreateSpec()
        {
            MatchSpec spec = _spec.Clone();
            if (string.IsNullOrEmpty(spec.Map)) spec.Map = ArenaMaps.Pillars;
            return spec;
        }

        public override void OnDisconnected(string cause)
        {
            _pending = Pending.None;
            Rooms.Clear();
            if (Phase != MatchmakerPhase.Offline || LastError == null) LastError = cause;
            SetPhase(MatchmakerPhase.Offline, "Disconnected: " + cause);
        }

        void SetPhase(MatchmakerPhase phase, string status)
        {
            Phase = phase;
            Status = status;
            Changed?.Invoke();
        }

        public void Dispose() => _session.RemoveCallbacks(this);
    }

    public static class RoomNames
    {
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        /// <summary>Short, readable, unambiguous names like "Arena-K7QF" (no 0/O or 1/I).</summary>
        public static string Generate(Random random)
        {
            var chars = new char[4];
            for (int i = 0; i < chars.Length; i++) chars[i] = Alphabet[random.Next(Alphabet.Length)];
            return "Arena-" + new string(chars);
        }
    }
}


    public static object CancelJob(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object CancelJob(object data = null)
    {
        if (data == null) return null;
        return data;
    }