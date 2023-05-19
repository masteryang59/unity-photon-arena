using System.Collections.Generic;

namespace PhotonArena.Core.Net.Wire
{
    /// <summary>
    /// Frame types of the relay protocol, shared by the TCP relay and the in-process loopback.
    /// Every frame is one message: [op:u8][fields...]. TCP adds a u32 length prefix.
    /// </summary>
    public static class Op
    {
        public const byte ProtocolVersion = 1;

        // client -> server
        public const byte Hello = 1;            // version:u8 nickname:str
        public const byte Ping = 2;             // clientTime:i32
        public const byte JoinLobby = 3;
        public const byte CreateRoom = 4;       // name:str options
        public const byte JoinRoom = 5;         // name:str
        public const byte JoinRandom = 6;       // expected:props maxPlayers:u8
        public const byte LeaveRoom = 7;
        public const byte SetRoomProps = 8;     // changes:props expected:props?
        public const byte SetPlayerProps = 9;   // actor:i32 changes:props expected:props?
        public const byte SetRoomOpen = 10;     // open:bool
        public const byte RaiseEvent = 11;      // code:u8 target:u8 reliable:bool payload:bytes

        // server -> client
        public const byte Welcome = 64;         // userId:i32 serverTime:i32
        public const byte Pong = 65;            // clientTime:i32 serverTime:i32
        public const byte JoinedLobby = 66;
        public const byte RoomList = 67;        // count:var listing*
        public const byte Joined = 68;          // localActor:i32 room
        public const byte JoinFailed = 69;      // kind:u8 code:i16 message:str
        public const byte LeftRoom = 70;
        public const byte PlayerJoined = 71;    // player
        public const byte PlayerLeft = 72;      // actor:i32
        public const byte RoomProps = 73;       // changes:props isOpen:bool
        public const byte PlayerProps = 74;     // actor:i32 changes:props
        public const byte MasterChanged = 75;   // actor:i32
        public const byte Event = 76;           // code:u8 sender:i32 payload:bytes
        public const byte PropsRejected = 77;   // reason:str
        public const byte Error = 78;           // message:str (server closes the link afterwards)
    }

    public static class WireExtensions
    {
        const byte TNull = 0, TInt = 1, TBool = 2, TFloat = 3, TString = 4;
        public const int MaxProps = 64;

        public static ByteWriter Props(this ByteWriter w, PropertyBag bag)
        {
            if (bag == null)
            {
                w.VarU(0);
                return w;
            }
            w.VarU((uint)bag.Count + 1);
            foreach (var kv in bag)
            {
                w.Str(kv.Key);
                switch (kv.Value)
                {
                    case null: w.U8(TNull); break;
                    case int i: w.U8(TInt).I32(i); break;
                    case bool b: w.U8(TBool).Bool(b); break;
                    case float f: w.U8(TFloat).F32(f); break;
                    case string s: w.U8(TString).Str(s); break;
                }
            }
            return w;
        }

        /// <summary>Returns null when the writer wrote a null bag.</summary>
        public static PropertyBag Props(this ByteReader r)
        {
            uint raw = r.VarU();
            if (raw == 0) return null;
            uint count = raw - 1;
            if (count > MaxProps) throw new WireFormatException("Too many properties.");
            var bag = new PropertyBag();
            for (int i = 0; i < count; i++)
            {
                string key = r.Str(PropertyBag.MaxKeyLength * 4);
                if (string.IsNullOrEmpty(key) || key.Length > PropertyBag.MaxKeyLength) throw new WireFormatException("Bad property key.");
                byte type = r.U8();
                object value = type switch
                {
                    TNull => null,
                    TInt => r.I32(),
                    TBool => r.Bool(),
                    TFloat => r.F32(),
                    TString => r.Str(PropertyBag.MaxStringLength * 4),
                    _ => throw new WireFormatException("Bad property type."),
                };
                if (value is string s && s.Length > PropertyBag.MaxStringLength) throw new WireFormatException("Property string too long.");
                bag.Set(key, value);
            }
            return bag;
        }

        public static ByteWriter StrList(this ByteWriter w, IReadOnlyList<string> items)
        {
            w.VarU((uint)(items?.Count ?? 0));
            if (items != null) foreach (string s in items) w.Str(s);
            return w;
        }

        public static string[] StrList(this ByteReader r, int max = MaxProps)
        {
            uint count = r.VarU();
            if (count > max) throw new WireFormatException("List too long.");
            var items = new string[count];
            for (int i = 0; i < count; i++) items[i] = r.Str(256);
            return items;
        }

        public static ByteWriter Listing(this ByteWriter w, RoomListing l) =>
            w.Str(l.Name).U8((byte)l.PlayerCount).U8((byte)l.MaxPlayers).Bool(l.IsOpen).Bool(l.Removed).Props(l.Props);

        public static RoomListing Listing(this ByteReader r) =>
            new RoomListing(r.Str(128), r.U8(), r.U8(), r.Bool(), r.Bool(), r.Props());

        public static ByteWriter Player(this ByteWriter w, int actor, string nickname, PropertyBag props) =>
            w.I32(actor).Str(nickname).Props(props);

        public static ByteWriter Options(this ByteWriter w, RoomCreateOptions o) =>
            w.U8((byte)o.MaxPlayers).Bool(o.IsOpen).Bool(o.IsVisible).Props(o.Props).StrList(o.LobbyProps);

        public static RoomCreateOptions Options(this ByteReader r) => new RoomCreateOptions
        {
            MaxPlayers = r.U8(),
            IsOpen = r.Bool(),
            IsVisible = r.Bool(),
            Props = r.Props() ?? new PropertyBag(),
            LobbyProps = r.StrList(),
        };
    }
}


    public static object SubscribeTopic(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object SubscribeTopic(object data = null)
    {
        if (data == null) return null;
        return data;
    }