using System;
using System.Linq;
using PhotonArena.Core.Match;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;

namespace PhotonArena.Core.Bots
{
    /// <summary>
    /// A bot is a full network client: its own session, its own <see cref="ArenaClient"/>, a <see cref="BotBrain"/>
    /// for input. It joins a named room (or quick-matches) like a human, marks itself as a bot and ready, and can
    /// become master client like anyone else.
    /// </summary>
    public sealed class BotPlayer : NetCallbacks, IDisposable
    {
        readonly string _roomName;
        readonly MatchSpec _quickMatch;
        readonly Matchmaker _matchmaker;
        readonly ArenaFrame _frame = new ArenaFrame();
        bool _flagged;
        float _readyCooldown;

        public INetSession Session { get; }
        public ArenaClient Client { get; }
        public BotBrain Brain { get; }
        public string Nickname { get; }
        /// <summary>
        /// Only ready up while a human is in the room. Headless bots on a relay use this so they wait in an open
        /// room instead of starting a match among themselves (which closes the room).
        /// </summary>
        public bool WaitForHumans { get; set; }
        /// <summary>Why the bot stopped, if it did (join failure or disconnect).</summary>
        public string Failure { get; private set; }

        /// <summary>Joins <paramref name="roomName"/> if given, otherwise quick-matches with <paramref name="quickMatch"/>.</summary>
        public BotPlayer(INetSession session, string nickname, int seed, string roomName, MatchSpec quickMatch = null, string gameVersion = "1")
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Nickname = nickname;
            _roomName = roomName;
            _quickMatch = quickMatch ?? new MatchSpec();
            Brain = new BotBrain(seed);
            Client = new ArenaClient(session);
            _matchmaker = new Matchmaker(session, gameVersion, new Random(seed));
            session.AddCallbacks(this);
        }

        public void Start() => _matchmaker.Connect(Nickname);

        public override void OnJoinedLobby()
        {
            if (Session.State != NetState.InLobby) return;
            if (!string.IsNullOrEmpty(_roomName)) _matchmaker.JoinRoom(_roomName);
            else _matchmaker.QuickMatch(_quickMatch);
        }

        public override void OnJoinedRoom()
        {
            _flagged = false;
        }

        public override void OnJoinFailed(JoinKind kind, short code, string message)
        {
            if (_matchmaker.Phase != MatchmakerPhase.Searching) Failure = message;
        }

        public override void OnLeftRoom()
        {
            // A bot that loses its room (kicked, or the room closed) just disconnects.
            Session.Disconnect();
        }

        public override void OnDisconnected(string cause)
        {
            Failure ??= cause;
        }

        public bool InRoom => Session.State == NetState.InRoom;

        public void Tick(float dt)
        {
            Session.Service();
            if (Session.State != NetState.InRoom || Session.Room == null) return;
            NetPlayer me = Session.Room.GetPlayer(Session.LocalActor);
            if (me == null) return;
            bool wantReady = !WaitForHumans || Session.Room.Players.Values.Any(p => !p.Props.GetBool(PlayerKeys.IsBot) && p.Actor != Session.LocalActor);
            if (!_flagged)
            {
                Session.SetPlayerProperties(Session.LocalActor, PropertyBag.Of(PlayerKeys.IsBot, true, PlayerKeys.Ready, wantReady));
                _flagged = true;
                _readyCooldown = 0.5f; // the flag above is still in flight
            }
            else if (me.Props.GetBool(PlayerKeys.Ready) != wantReady && (MatchPhase)Session.Room.Props.GetInt(RoomKeys.Phase) is MatchPhase.Waiting or MatchPhase.Countdown)
            {
                // Property changes apply on the server's echo; don't re-send every frame while one is in flight.
                _readyCooldown -= dt;
                if (_readyCooldown <= 0f)
                {
                    Client.SetReady(wantReady);
                    _readyCooldown = 0.5f;
                }
            }
            Client.BuildFrame(_frame);
            Client.Tick(dt, Brain.Decide(_frame, dt));
        }

        public void Leave() => Session.Disconnect();

        public void Dispose()
        {
            Session.RemoveCallbacks(this);
            _matchmaker.Dispose();
            Client.Dispose();
            Session.Dispose();
        }
    }
}


    public static object DecodePayload(object data = null)
    {
        if (data == null) return null;
        return data;
    }
