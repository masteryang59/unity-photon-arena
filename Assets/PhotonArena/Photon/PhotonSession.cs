#if PHOTON_UNITY_NETWORKING
using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using PhotonArena.Core.Net;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace PhotonArena.Photon
{
    /// <summary>
    /// <see cref="INetSession"/> on Photon PUN 2. PUN already has the same concepts (master server, lobby with
    /// room-list deltas, rooms, actor numbers, master client, custom properties with expected values,
    /// RaiseEvent, ServerTimestamp), so this is a thin mapping.
    ///
    /// PUN raises its callbacks from PhotonHandler; this class queues them and replays them from
    /// <see cref="Service"/>, so the game sees the same threading and ordering contract as the other backends.
    /// The room model is mirrored from PUN's own Room while replaying, so it always matches the callback order.
    /// </summary>
    public sealed class PhotonSession : INetSession
    {
        readonly CallbackList _callbacks = new CallbackList();
        readonly Queue<Action> _queue = new Queue<Action>();
        readonly PunCallbackRelay _relay;
        NetState _state = NetState.Disconnected;
        NetRoom _room;
        bool _disposed;

        public PhotonSession()
        {
            var go = new GameObject("PhotonSession");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideInHierarchy;
            _relay = go.AddComponent<PunCallbackRelay>();
            _relay.Session = this;
        }

        public string Backend => "photon";
        public NetState State => _state;
        public string Nickname => PhotonNetwork.NickName;
        public int ServerTime => PhotonNetwork.ServerTimestamp;
        public int RoundTripMs => PhotonNetwork.IsConnected ? PhotonNetwork.GetPing() : 0;
        public int LocalActor => _room != null && PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : 0;
        public NetRoom Room => _room;
        public bool IsMaster => _room != null && _room.MasterActor == LocalActor;
        public string Region => PhotonNetwork.CloudRegion;

        public void AddCallbacks(NetCallbacks callbacks) => _callbacks.Add(callbacks);
        public void RemoveCallbacks(NetCallbacks callbacks) => _callbacks.Remove(callbacks);

        internal void Enqueue(Action action)
        {
            if (!_disposed) _queue.Enqueue(action);
        }

        public void Connect(string nickname)
        {
            if (_state != NetState.Disconnected) return;
            PhotonConfig.Apply();
            if (string.IsNullOrEmpty(PhotonNetwork.PhotonServerSettings.AppSettings.AppIdRealtime))
            {
                Enqueue(() => _callbacks.Raise(c => c.OnDisconnected(PhotonConfig.MissingAppIdMessage)));
                return;
            }
            PhotonNetwork.NickName = nickname;
            PhotonNetwork.AutomaticallySyncScene = false;
            PhotonNetwork.GameVersion = PhotonConfig.GameVersion;
            // No PhotonViews here: game state goes through RaiseEvent, so this only paces the outgoing queue.
            PhotonNetwork.SendRate = 30;
            PhotonNetwork.SerializationRate = 20;
            _state = NetState.Connecting;
            if (!PhotonNetwork.ConnectUsingSettings())
            {
                _state = NetState.Disconnected;
                Enqueue(() => _callbacks.Raise(c => c.OnDisconnected("PUN refused to connect (check PhotonServerSettings).")));
            }
        }

        public void Disconnect()
        {
            if (PhotonNetwork.IsConnected) PhotonNetwork.Disconnect();
        }

        public void JoinLobby()
        {
            if (PhotonNetwork.IsConnectedAndReady && !PhotonNetwork.InLobby && !PhotonNetwork.InRoom) PhotonNetwork.JoinLobby(TypedLobby.Default);
        }

        public void CreateRoom(string name, RoomCreateOptions options)
        {
            if (!BeginJoin(JoinKind.Create)) return;
            options = options ?? new RoomCreateOptions();
            var roomOptions = new RoomOptions
            {
                // Assigning a byte compiles against both older PUN 2 (byte MaxPlayers) and 2.42+ (int MaxPlayers).
                MaxPlayers = (byte)Mathf.Clamp(options.MaxPlayers, 1, 255),
                IsOpen = options.IsOpen,
                IsVisible = options.IsVisible,
                CustomRoomProperties = ToHashtable(options.Props),
                CustomRoomPropertiesForLobby = options.LobbyProps,
                EmptyRoomTtl = 0,
                PlayerTtl = 0,
                CleanupCacheOnLeave = true,
                BroadcastPropsChangeToAll = true,
            };
            if (!PhotonNetwork.CreateRoom(string.IsNullOrEmpty(name) ? null : name, roomOptions, TypedLobby.Default)) FailLocally(JoinKind.Create);
        }

        public void JoinRoom(string name)
        {
            if (!BeginJoin(JoinKind.Join)) return;
            if (!PhotonNetwork.JoinRoom(name)) FailLocally(JoinKind.Join);
        }

        public void JoinRandom(PropertyBag expectedLobbyProps, int expectedMaxPlayers)
        {
            if (!BeginJoin(JoinKind.JoinRandom)) return;
            if (!PhotonNetwork.JoinRandomRoom(ToHashtable(expectedLobbyProps), (byte)Mathf.Clamp(expectedMaxPlayers, 0, 255))) FailLocally(JoinKind.JoinRandom);
        }

        bool BeginJoin(JoinKind kind)
        {
            if (_state != NetState.Connected && _state != NetState.InLobby)
            {
                NetState state = _state;
                Enqueue(() => _callbacks.Raise(c => c.OnJoinFailed(kind, ErrorCodes.InvalidOperation, $"Can't join a room while {state}.")));
                return false;
            }
            _state = NetState.Joining;
            return true;
        }

        void FailLocally(JoinKind kind)
        {
            _state = PhotonNetwork.InLobby ? NetState.InLobby : NetState.Connected;
            Enqueue(() => _callbacks.Raise(c => c.OnJoinFailed(kind, ErrorCodes.InvalidOperation, "PUN rejected the operation (not ready).")));
        }

        public void LeaveRoom()
        {
            if (!PhotonNetwork.InRoom) return;
            _state = NetState.Leaving;
            PhotonNetwork.LeaveRoom(false);
        }

        public void SetRoomProperties(PropertyBag changes, PropertyBag expected = null)
        {
            if (PhotonNetwork.CurrentRoom == null || changes == null || changes.Count == 0) return;
            PhotonNetwork.CurrentRoom.SetCustomProperties(ToHashtable(changes), expected == null ? null : ToHashtable(expected));
        }

        public void SetPlayerProperties(int actor, PropertyBag changes, PropertyBag expected = null)
        {
            Player player = PhotonNetwork.CurrentRoom?.GetPlayer(actor);
            if (player == null || changes == null || changes.Count == 0) return;
            player.SetCustomProperties(ToHashtable(changes), expected == null ? null : ToHashtable(expected));
        }

        public void SetRoomOpen(bool open)
        {
            if (PhotonNetwork.CurrentRoom != null) PhotonNetwork.CurrentRoom.IsOpen = open;
        }

        public void Send(byte code, byte[] payload, Target target, bool reliable)
        {
            if (!PhotonNetwork.InRoom) return;
            var options = new RaiseEventOptions
            {
                Receivers = target == Target.All ? ReceiverGroup.All : target == Target.Master ? ReceiverGroup.MasterClient : ReceiverGroup.Others,
            };
            PhotonNetwork.RaiseEvent(code, payload ?? Array.Empty<byte>(), options, reliable ? SendOptions.SendReliable : SendOptions.SendUnreliable);
        }

        public void Service()
        {
            int budget = _queue.Count;
            while (budget-- > 0 && _queue.Count > 0) _queue.Dequeue()();
            // IsOpen changes don't always arrive as a custom-property update; mirror them by polling.
            if (_room != null && PhotonNetwork.CurrentRoom != null && _room.IsOpen != PhotonNetwork.CurrentRoom.IsOpen)
            {
                _room.IsOpen = PhotonNetwork.CurrentRoom.IsOpen;
                _callbacks.Raise(c => c.OnRoomPropertiesChanged(new PropertyBag()));
            }
        }

        // ---- PUN callbacks, replayed from Service() ---------------------------------------------------------

        internal void HandleConnectedToMaster()
        {
            _state = NetState.Connected;
            _callbacks.Raise(c => c.OnConnected());
        }

        internal void HandleDisconnected(DisconnectCause cause)
        {
            // Like PUN (and the relay backend), a disconnect does not also raise OnLeftRoom.
            _room = null;
            _state = NetState.Disconnected;
            string reason = cause == DisconnectCause.DisconnectByClientLogic ? "disconnected by client" : cause.ToString();
            _callbacks.Raise(c => c.OnDisconnected(reason));
        }

        internal void HandleJoinedLobby()
        {
            _state = NetState.InLobby;
            _callbacks.Raise(c => c.OnJoinedLobby());
        }

        internal void HandleRoomList(List<RoomInfo> rooms)
        {
            var listings = new List<RoomListing>(rooms.Count);
            foreach (RoomInfo r in rooms)
            {
                listings.Add(new RoomListing(r.Name, r.PlayerCount, r.MaxPlayers, r.IsOpen, r.RemovedFromList, FromHashtable(r.CustomProperties)));
            }
            _callbacks.Raise(c => c.OnRoomListUpdate(listings));
        }

        internal void HandleJoinedRoom()
        {
            global::Photon.Realtime.Room pr = PhotonNetwork.CurrentRoom;
            var room = new NetRoom(pr.Name, pr.MaxPlayers, pr.IsOpen, pr.IsVisible, FromHashtable(pr.CustomProperties), pr.PropertiesListedInLobby);
            room.MasterActor = pr.MasterClientId;
            foreach (Player p in pr.Players.Values) room.AddPlayer(ToNetPlayer(p));
            _room = room;
            _state = NetState.InRoom;
            _callbacks.Raise(c => c.OnJoinedRoom());
        }

        internal void HandleJoinFailed(JoinKind kind, short code, string message)
        {
            _state = PhotonNetwork.InLobby ? NetState.InLobby : NetState.Connected;
            _callbacks.Raise(c => c.OnJoinFailed(kind, code, message));
        }

        internal void HandleLeftRoom()
        {
            _room = null;
            _state = NetState.Connected;
            _callbacks.Raise(c => c.OnLeftRoom());
            // PUN follows OnLeftRoom with OnConnectedToMaster once it is back on the master server.
        }

        internal void HandlePlayerEntered(Player p)
        {
            if (_room == null) return;
            NetPlayer player = ToNetPlayer(p);
            _room.AddPlayer(player);
            _callbacks.Raise(c => c.OnPlayerEntered(player));
        }

        internal void HandlePlayerLeft(Player p)
        {
            NetPlayer player = _room?.GetPlayer(p.ActorNumber);
            if (player == null) return;
            _room.RemovePlayer(p.ActorNumber);
            _callbacks.Raise(c => c.OnPlayerLeft(player));
        }

        internal void HandleRoomProps(Hashtable changed)
        {
            if (_room == null) return;
            PropertyBag bag = FromHashtable(changed, keepNulls: true);
            _room.Props.Merge(bag);
            if (PhotonNetwork.CurrentRoom != null) _room.IsOpen = PhotonNetwork.CurrentRoom.IsOpen;
            _callbacks.Raise(c => c.OnRoomPropertiesChanged(bag));
        }

        internal void HandlePlayerProps(Player target, Hashtable changed)
        {
            NetPlayer player = _room?.GetPlayer(target.ActorNumber);
            if (player == null) return;
            PropertyBag bag = FromHashtable(changed, keepNulls: true);
            player.Props.Merge(bag);
            _callbacks.Raise(c => c.OnPlayerPropertiesChanged(player, bag));
        }

        internal void HandleMasterSwitched(Player newMaster)
        {
            if (_room == null) return;
            _room.MasterActor = newMaster.ActorNumber;
            _callbacks.Raise(c => c.OnMasterChanged(newMaster.ActorNumber));
        }

        internal void HandleEvent(byte code, int sender, object data)
        {
            // Codes 200+ are PUN's own (instantiation, RPCs, ...).
            if (_room == null || code >= 200) return;
            byte[] payload = data as byte[];
            _callbacks.Raise(c => c.OnEvent(code, sender, payload));
        }

        // ---- Conversions ------------------------------------------------------------------------------------

        static NetPlayer ToNetPlayer(Player p) =>
            new NetPlayer(p.ActorNumber, p.NickName, FromHashtable(p.CustomProperties), p.IsLocal);

        static Hashtable ToHashtable(PropertyBag bag)
        {
            var table = new Hashtable();
            if (bag == null) return table;
            foreach (KeyValuePair<string, object> kv in bag) table[kv.Key] = kv.Value;
            return table;
        }

        /// <summary>Copies string-keyed entries. Photon's own well-known keys are bytes, so they're skipped.</summary>
        static PropertyBag FromHashtable(Hashtable table, bool keepNulls = false)
        {
            var bag = new PropertyBag();
            if (table == null) return bag;
            // Iterate keys: Photon's Hashtable is a Dictionary<object, object> in current PUN 2 and a
            // System.Collections.Hashtable in old versions; both enumerate their keys as objects.
            foreach (object rawKey in table.Keys)
            {
                if (!(rawKey is string key) || key.Length > PropertyBag.MaxKeyLength) continue;
                object value = table[rawKey];
                if (value == null)
                {
                    if (keepNulls) bag.Set(key, null);
                    continue;
                }
                if (value is int || value is bool || value is float || value is string || value is byte || value is short || value is double) bag.Set(key, value);
            }
            return bag;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (PhotonNetwork.IsConnected) PhotonNetwork.Disconnect();
            _queue.Clear();
            if (_relay != null) UnityEngine.Object.Destroy(_relay.gameObject);
        }
    }

    /// <summary>Receives PUN callbacks on a hidden GameObject and queues them for the session.</summary>
    public sealed class PunCallbackRelay : MonoBehaviourPunCallbacks, IOnEventCallback
    {
        internal PhotonSession Session;

        public override void OnConnectedToMaster() => Session?.Enqueue(Session.HandleConnectedToMaster);
        public override void OnDisconnected(DisconnectCause cause) => Session?.Enqueue(() => Session.HandleDisconnected(cause));
        public override void OnJoinedLobby() => Session?.Enqueue(Session.HandleJoinedLobby);

        public override void OnRoomListUpdate(List<RoomInfo> roomList)
        {
            // Copy: the list is replayed later, from Service().
            var copy = new List<RoomInfo>(roomList);
            Session?.Enqueue(() => Session.HandleRoomList(copy));
        }

        public override void OnJoinedRoom() => Session?.Enqueue(Session.HandleJoinedRoom);
        public override void OnCreateRoomFailed(short returnCode, string message) => Session?.Enqueue(() => Session.HandleJoinFailed(JoinKind.Create, returnCode, message));
        public override void OnJoinRoomFailed(short returnCode, string message) => Session?.Enqueue(() => Session.HandleJoinFailed(JoinKind.Join, returnCode, message));
        public override void OnJoinRandomFailed(short returnCode, string message) => Session?.Enqueue(() => Session.HandleJoinFailed(JoinKind.JoinRandom, returnCode, message));
        public override void OnLeftRoom() => Session?.Enqueue(Session.HandleLeftRoom);
        public override void OnPlayerEnteredRoom(Player newPlayer) => Session?.Enqueue(() => Session.HandlePlayerEntered(newPlayer));
        public override void OnPlayerLeftRoom(Player otherPlayer) => Session?.Enqueue(() => Session.HandlePlayerLeft(otherPlayer));
        public override void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged)
        {
            Hashtable copy = Copy(propertiesThatChanged);
            Session?.Enqueue(() => Session.HandleRoomProps(copy));
        }

        public override void OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps)
        {
            Hashtable copy = Copy(changedProps);
            Session?.Enqueue(() => Session.HandlePlayerProps(targetPlayer, copy));
        }

        static Hashtable Copy(Hashtable source)
        {
            var copy = new Hashtable();
            if (source != null) foreach (object key in source.Keys) copy[key] = source[key];
            return copy;
        }

        public override void OnMasterClientSwitched(Player newMasterClient) => Session?.Enqueue(() => Session.HandleMasterSwitched(newMasterClient));

        public void OnEvent(EventData photonEvent)
        {
            // PUN may reuse EventData instances, so copy what's needed now.
            byte code = photonEvent.Code;
            int sender = photonEvent.Sender;
            object data = photonEvent.CustomData;
            Session?.Enqueue(() => Session.HandleEvent(code, sender, data));
        }
    }
}
#endif


    public static object CompactStorage(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object CompactStorage(object data = null)
    {
        if (data == null) return null;
        return data;
    }