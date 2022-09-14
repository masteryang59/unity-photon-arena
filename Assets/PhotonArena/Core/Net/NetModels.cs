using System;
using System.Collections.Generic;

namespace PhotonArena.Core.Net
{
    public enum NetState
    {
        Disconnected,
        Connecting,
        /// <summary>Connected, but not in the lobby or a room (Photon: on the master server).</summary>
        Connected,
        InLobby,
        Joining,
        InRoom,
        Leaving,
    }

    /// <summary>Who receives a game event. Mirrors Photon's ReceiverGroup.</summary>
    public enum Target : byte
    {
        Others = 0,
        All = 1,
        Master = 2,
    }

    public enum JoinKind : byte
    {
        Create = 0,
        Join = 1,
        JoinRandom = 2,
    }

    /// <summary>Operation error codes. The values match Photon Realtime's ErrorCode class.</summary>
    public static class ErrorCodes
    {
        public const short Ok = 0;
        public const short InvalidOperation = -2;
        public const short InternalServerError = -1;
        public const short GameIdAlreadyExists = 32766;
        public const short GameFull = 32765;
        public const short GameClosed = 32764;
        public const short NoRandomMatchFound = 32760;
        public const short GameDoesNotExist = 32758;

        public static string Describe(short code) => code switch
        {
            GameIdAlreadyExists => "A room with that name already exists.",
            GameFull => "That room is full.",
            GameClosed => "That room has a match in progress.",
            NoRandomMatchFound => "No open room matches those settings.",
            GameDoesNotExist => "That room doesn't exist any more.",
            InvalidOperation => "Not allowed right now.",
            _ => $"Server error {code}.",
        };
    }

    public sealed class NetPlayer
    {
        public int Actor { get; }
        public string Nickname { get; set; }
        public PropertyBag Props { get; }
        public bool IsLocal { get; }

        public NetPlayer(int actor, string nickname, PropertyBag props, bool isLocal)
        {
            Actor = actor;
            Nickname = nickname ?? "";
            Props = props ?? new PropertyBag();
            IsLocal = isLocal;
        }

        public override string ToString() => $"#{Actor} {Nickname}";
    }

    /// <summary>The room the local client is in, kept up to date by the session.</summary>
    public sealed class NetRoom
    {
        public string Name { get; }
        public int MaxPlayers { get; }
        public bool IsOpen { get; set; }
        public bool IsVisible { get; set; }
        public PropertyBag Props { get; }
        public IReadOnlyList<string> LobbyProps { get; }
        public int MasterActor { get; set; }

        readonly SortedDictionary<int, NetPlayer> _players = new SortedDictionary<int, NetPlayer>();
        public IReadOnlyDictionary<int, NetPlayer> Players => _players;

        public NetRoom(string name, int maxPlayers, bool isOpen, bool isVisible, PropertyBag props, IReadOnlyList<string> lobbyProps)
        {
            Name = name;
            MaxPlayers = maxPlayers;
            IsOpen = isOpen;
            IsVisible = isVisible;
            Props = props ?? new PropertyBag();
            LobbyProps = lobbyProps ?? Array.Empty<string>();
        }

        public int PlayerCount => _players.Count;

        public NetPlayer GetPlayer(int actor) => _players.TryGetValue(actor, out NetPlayer p) ? p : null;

        /// <summary>Adds or replaces a player. Backends call this; game code treats the room as read-only.</summary>
        public void AddPlayer(NetPlayer player) => _players[player.Actor] = player;
        public bool RemovePlayer(int actor) => _players.Remove(actor);
    }

    /// <summary>One entry in the lobby's room list.</summary>
    public sealed class RoomListing
    {
        public string Name { get; }
        public int PlayerCount { get; }
        public int MaxPlayers { get; }
        public bool IsOpen { get; }
        /// <summary>Photon's RoomInfo.RemovedFromList: the room closed, filled up, went invisible, or was deleted.</summary>
        public bool Removed { get; }
        /// <summary>Only the lobby-visible properties (Photon's CustomRoomPropertiesForLobby).</summary>
        public PropertyBag Props { get; }

        public RoomListing(string name, int playerCount, int maxPlayers, bool isOpen, bool removed, PropertyBag props)
        {
            Name = name;
            PlayerCount = playerCount;
            MaxPlayers = maxPlayers;
            IsOpen = isOpen;
            Removed = removed;
            Props = props ?? new PropertyBag();
        }

        public bool IsJoinable => !Removed && IsOpen && PlayerCount < MaxPlayers;

        public override string ToString() => $"{Name} {PlayerCount}/{MaxPlayers}{(IsOpen ? "" : " closed")}{(Removed ? " removed" : "")} {Props}";
    }

    public sealed class RoomCreateOptions
    {
        public int MaxPlayers = 4;
        public bool IsOpen = true;
        public bool IsVisible = true;
        public PropertyBag Props = new PropertyBag();
        /// <summary>Keys of <see cref="Props"/> shown in the lobby and usable as JoinRandom filters.</summary>
        public string[] LobbyProps = Array.Empty<string>();
    }
}


    public static object ImportData(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object ImportData(object data = null)
    {
        if (data == null) return null;
        return data;
    }