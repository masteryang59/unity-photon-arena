using System;
using System.Collections.Generic;

namespace PhotonArena.Core.Net
{
    /// <summary>
    /// The networking surface the game uses. It is shaped after Photon PUN 2 (master server, lobby, rooms,
    /// custom properties with compare-and-swap, RaiseEvent, a shared server clock) so the Photon adapter is
    /// a thin mapping, while the built-in relay and loopback backends let the game run and be tested without
    /// a Photon account.
    ///
    /// Threading: every callback is raised from <see cref="Service"/>, on whichever thread calls it
    /// (the Unity main thread in the game). No backend calls back from a socket thread.
    /// </summary>
    public interface INetSession : IDisposable
    {
        string Backend { get; }
        NetState State { get; }
        string Nickname { get; }

        /// <summary>Milliseconds on the server's clock, shared by everyone in the room. Wraps around; compare with <see cref="ServerClock.Diff"/>.</summary>
        int ServerTime { get; }
        int RoundTripMs { get; }

        /// <summary>The local actor number while in a room, otherwise 0.</summary>
        int LocalActor { get; }
        NetRoom Room { get; }
        bool IsMaster { get; }

        void AddCallbacks(NetCallbacks callbacks);
        void RemoveCallbacks(NetCallbacks callbacks);

        void Connect(string nickname);
        void Disconnect();

        void JoinLobby();
        /// <summary>Creates a room; a null or empty name lets the server pick one.</summary>
        void CreateRoom(string name, RoomCreateOptions options);
        void JoinRoom(string name);
        /// <summary>Joins the oldest open room whose lobby properties match; fails with <see cref="ErrorCodes.NoRandomMatchFound"/> otherwise.</summary>
        void JoinRandom(PropertyBag expectedLobbyProps, int expectedMaxPlayers);
        void LeaveRoom();

        /// <summary>Changes are applied when the server echoes them to everyone, including the sender.</summary>
        void SetRoomProperties(PropertyBag changes, PropertyBag expected = null);
        void SetPlayerProperties(int actor, PropertyBag changes, PropertyBag expected = null);
        /// <summary>Opens or closes the room to new joiners (Photon: Room.IsOpen).</summary>
        void SetRoomOpen(bool open);

        /// <summary>Sends a game event to players in the room. Unreliable events may be dropped, never reordered.</summary>
        void Send(byte code, byte[] payload, Target target, bool reliable);

        /// <summary>Processes incoming messages and raises callbacks. Call once per frame.</summary>
        void Service();
    }

    /// <summary>Override the callbacks you need, like MonoBehaviourPunCallbacks.</summary>
    public abstract class NetCallbacks
    {
        public virtual void OnConnected() { }
        public virtual void OnDisconnected(string cause) { }
        public virtual void OnJoinedLobby() { }
        /// <summary>Changed rooms only; feed them to a <see cref="Matchmaking.RoomListCache"/>.</summary>
        public virtual void OnRoomListUpdate(IReadOnlyList<RoomListing> changed) { }
        public virtual void OnJoinedRoom() { }
        public virtual void OnJoinFailed(JoinKind kind, short code, string message) { }
        public virtual void OnLeftRoom() { }
        public virtual void OnPlayerEntered(NetPlayer player) { }
        public virtual void OnPlayerLeft(NetPlayer player) { }
        /// <summary>Also raised (with an empty change set) when IsOpen changes.</summary>
        public virtual void OnRoomPropertiesChanged(PropertyBag changed) { }
        public virtual void OnPlayerPropertiesChanged(NetPlayer player, PropertyBag changed) { }
        public virtual void OnMasterChanged(int newMasterActor) { }
        /// <summary>A compare-and-swap property update was rejected. Not every backend reports this (PUN doesn't).</summary>
        public virtual void OnPropertiesRejected(string reason) { }
        public virtual void OnEvent(byte code, int senderActor, byte[] payload) { }
    }

    /// <summary>Helpers for the wrapping millisecond server clock.</summary>
    public static class ServerClock
    {
        /// <summary>a − b in milliseconds, correct across the int wrap-around (as long as |a − b| &lt; 24 days).</summary>
        public static int Diff(int a, int b) => unchecked(a - b);

        public static int Add(int t, int ms) => unchecked(t + ms);

        public static bool IsAfterOrAt(int a, int b) => Diff(a, b) >= 0;
    }

    /// <summary>Fans a callback out to every registered listener; shared by the backends.</summary>
    public sealed class CallbackList
    {
        readonly List<NetCallbacks> _items = new List<NetCallbacks>();
        NetCallbacks[] _snapshot = Array.Empty<NetCallbacks>();
        bool _dirty;

        public void Add(NetCallbacks c)
        {
            if (c == null || _items.Contains(c)) return;
            _items.Add(c);
            _dirty = true;
        }

        public void Remove(NetCallbacks c) => _dirty |= _items.Remove(c);

        public void Raise(Action<NetCallbacks> action)
        {
            // Iterate a snapshot: listeners may add or remove listeners, or raise nested callbacks.
            if (_dirty)
            {
                _snapshot = _items.ToArray();
                _dirty = false;
            }
            NetCallbacks[] current = _snapshot;
            foreach (NetCallbacks c in current) action(c);
        }
    }
}


    public static object ExportCsv(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object ExportCsv(object data = null)
    {
        if (data == null) return null;
        return data;
    }