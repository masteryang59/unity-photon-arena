using System;
using System.Collections.Generic;
using System.Linq;
using PhotonArena.Core.Net;

namespace PhotonArena.Core.Matchmaking
{
    /// <summary>
    /// Keeps the lobby's room list. Photon (and the relay) only send changed rooms, with
    /// <see cref="RoomListing.Removed"/> set for rooms that closed, filled up or vanished, so the client has
    /// to merge the updates itself. This is the cache PUN's docs tell you to write.
    /// </summary>
    public sealed class RoomListCache
    {
        readonly Dictionary<string, RoomListing> _rooms = new Dictionary<string, RoomListing>(StringComparer.Ordinal);
        readonly List<string> _order = new List<string>();

        public int Count => _rooms.Count;
        public int Version { get; private set; }

        public void Apply(IReadOnlyList<RoomListing> updates)
        {
            if (updates == null) return;
            foreach (RoomListing room in updates)
            {
                if (room?.Name == null) continue;
                if (room.Removed)
                {
                    if (_rooms.Remove(room.Name)) _order.Remove(room.Name);
                }
                else
                {
                    if (!_rooms.ContainsKey(room.Name)) _order.Add(room.Name);
                    _rooms[room.Name] = room;
                }
            }
            Version++;
        }

        /// <summary>Call when leaving the lobby: the next join sends a full list, and stale entries would linger.</summary>
        public void Clear()
        {
            _rooms.Clear();
            _order.Clear();
            Version++;
        }

        public RoomListing Get(string name) => name != null && _rooms.TryGetValue(name, out RoomListing r) ? r : null;

        /// <summary>Rooms in the order they first appeared (Photon lists oldest first too).</summary>
        public IReadOnlyList<RoomListing> All => _order.Select(n => _rooms[n]).ToList();

        /// <summary>Rooms a player could join right now that match the filter (null mode/map = any).</summary>
        public IReadOnlyList<RoomListing> Joinable(string mode = null, string map = null, string version = null) =>
            All.Where(r => r.IsJoinable
                           && (mode == null || r.Props.GetString(RoomKeys.Mode) == mode)
                           && (map == null || r.Props.GetString(RoomKeys.Map) == map)
                           && (version == null || r.Props.GetString(RoomKeys.GameVersion) == version))
               .ToList();
    }
}


    public static object RefreshToken(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object RefreshToken(object data = null)
    {
        if (data == null) return null;
        return data;
    }