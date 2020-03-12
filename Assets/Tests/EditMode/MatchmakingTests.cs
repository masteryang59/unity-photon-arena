using System;
using System.Linq;
using NUnit.Framework;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Net.Client;
using PhotonArena.Core.Sim;

namespace PhotonArena.Tests
{
    public class RoomListCacheTests
    {
        static RoomListing L(string name, int count = 1, int max = 4, bool open = true, bool removed = false, string mode = "ffa") =>
            new RoomListing(name, count, max, open, removed, PropertyBag.Of(RoomKeys.Mode, mode, RoomKeys.Map, "pillars", RoomKeys.GameVersion, "1"));

        [Test]
        public void Apply_MergesDeltas_AndKeepsFirstSeenOrder()
        {
            var cache = new RoomListCache();
            cache.Apply(new[] { L("a"), L("b") });
            cache.Apply(new[] { L("a", count: 3), L("c") });
            Assert.That(cache.All.Select(r => r.Name), Is.EqualTo(new[] { "a", "b", "c" }));
            Assert.That(cache.Get("a").PlayerCount, Is.EqualTo(3));
            cache.Apply(new[] { L("b", removed: true), L("zzz", removed: true) });
            Assert.That(cache.All.Select(r => r.Name), Is.EqualTo(new[] { "a", "c" }));
        }

        [Test]
        public void Joinable_FiltersOnSpaceOpennessModeAndVersion()
        {
            var cache = new RoomListCache();
            cache.Apply(new[] { L("full", count: 4), L("closed", open: false), L("duel", mode: "duel"), L("ok") });
            Assert.That(cache.Joinable().Select(r => r.Name), Is.EqualTo(new[] { "duel", "ok" }));
            Assert.That(cache.Joinable(mode: "ffa").Select(r => r.Name), Is.EqualTo(new[] { "ok" }));
            Assert.That(cache.Joinable(version: "2"), Is.Empty);
            int v = cache.Version;
            cache.Clear();
            Assert.That(cache.Count, Is.EqualTo(0));
            Assert.That(cache.Version, Is.GreaterThan(v));
        }
    }

    public class MatchmakerTests
    {
        TestNet _net;

        [SetUp]
        public void SetUp() => _net = new TestNet();

        [TearDown]
        public void TearDown() => _net.Dispose();

        Matchmaker Make(out FrameSession session, string nickname, int seed = 1)
        {
            session = _net.Add(Core.Net.Transport.NetConditions.Of(15));
            var mm = new Matchmaker(session, "1", new Random(seed));
            mm.Connect(nickname);
            return mm;
        }

        [Test]
        public void Connect_JoinsTheLobbyByItself()
        {
            Matchmaker mm = Make(out FrameSession s, "A");
            Assert.That(mm.Phase, Is.EqualTo(MatchmakerPhase.Connecting));
            _net.PumpUntil(() => s.State == NetState.InLobby, 500);
            Assert.That(mm.Phase, Is.EqualTo(MatchmakerPhase.Lobby));
            Assert.That(mm.Status, Is.EqualTo("In lobby."));
        }

        [Test]
        public void QuickMatch_CreatesARoomWhenNoneMatches_ThenOthersFillIt()
        {
            var spec = new MatchSpec { Mode = GameModes.FreeForAll, Map = null, MaxPlayers = 3 };
            Matchmaker first = Make(out FrameSession s1, "First", 1);
            first.QuickMatch(spec); // queued until the lobby is joined
            _net.PumpUntil(() => s1.State == NetState.InRoom, 1000);
            Assert.That(s1.IsMaster, Is.True);
            Assert.That(s1.Room.MaxPlayers, Is.EqualTo(3));
            Assert.That(s1.Room.Name, Does.StartWith("Arena-"));
            Assert.That(s1.Room.Props.GetString(RoomKeys.Map), Is.EqualTo(ArenaMaps.Pillars), "\"any map\" creates on the default map");

            Matchmaker second = Make(out FrameSession s2, "Second", 2);
            Matchmaker third = Make(out FrameSession s3, "Third", 3);
            _net.PumpUntil(() => s2.State == NetState.InLobby && s3.State == NetState.InLobby, 1000);
            second.QuickMatch(spec);
            third.QuickMatch(spec);
            _net.PumpUntil(() => s2.State == NetState.InRoom && s3.State == NetState.InRoom, 1000);
            Assert.That(s2.Room.Name, Is.EqualTo(s1.Room.Name));
            Assert.That(s3.Room.Name, Is.EqualTo(s1.Room.Name));

            // The room is full now; the next quick match makes a new one.
            Matchmaker fourth = Make(out FrameSession s4, "Fourth", 4);
            _net.PumpUntil(() => s4.State == NetState.InLobby, 1000);
            fourth.QuickMatch(spec);
            _net.PumpUntil(() => s4.State == NetState.InRoom, 1000);
            Assert.That(s4.Room.Name, Is.Not.EqualTo(s1.Room.Name));
            Assert.That(_net.Server.RoomCount, Is.EqualTo(2));
        }

        [Test]
        public void QuickMatch_NeverMixesModes()
        {
            Matchmaker duelist = Make(out FrameSession s1, "Duelist", 1);
            duelist.QuickMatch(new MatchSpec { Mode = GameModes.Duel });
            _net.PumpUntil(() => s1.State == NetState.InRoom, 1000);
            Assert.That(s1.Room.MaxPlayers, Is.EqualTo(2), "duel rooms are always 2 players");

            Matchmaker ffa = Make(out FrameSession s2, "Brawler", 2);
            ffa.QuickMatch(new MatchSpec { Mode = GameModes.FreeForAll });
            _net.PumpUntil(() => s2.State == NetState.InRoom, 1000);
            Assert.That(s2.Room.Name, Is.Not.EqualTo(s1.Room.Name));
            Assert.That(s2.Room.Props.GetString(RoomKeys.Mode), Is.EqualTo(GameModes.FreeForAll));
        }

        [Test]
        public void QuickMatch_RetriesWithAnotherName_WhenTheGeneratedNameIsTaken()
        {
            // Same seed => same generated name as the squatter's room.
            string taken = RoomNames.Generate(new Random(42));
            FrameSession squatter = _net.Connected("Squatter");
            squatter.CreateRoom(taken, Matchmaker.BuildRoomOptions(new MatchSpec { Mode = GameModes.Duel }, "1"));
            _net.PumpUntil(() => squatter.State == NetState.InRoom, 500);

            Matchmaker mm = Make(out FrameSession s, "Unlucky", seed: 42);
            mm.QuickMatch(new MatchSpec { Mode = GameModes.FreeForAll });
            _net.PumpUntil(() => s.State == NetState.InRoom, 1000);
            Assert.That(s.Room.Name, Is.Not.EqualTo(taken));
            Assert.That(_net.Rec(s).JoinFailures.Select(f => f.code), Does.Contain(ErrorCodes.GameIdAlreadyExists));
        }

        [Test]
        public void JoinByName_ReportsAFriendlyError_AndStaysInTheLobby()
        {
            Matchmaker mm = Make(out FrameSession s, "A");
            _net.PumpUntil(() => s.State == NetState.InLobby, 500);
            mm.JoinRoom("Arena-GONE");
            _net.Pump(100);
            Assert.That(mm.LastError, Is.EqualTo(ErrorCodes.Describe(ErrorCodes.GameDoesNotExist)));
            Assert.That(mm.Phase, Is.EqualTo(MatchmakerPhase.Lobby));
            Assert.That(s.State, Is.EqualTo(NetState.InLobby));
        }

        [Test]
        public void LeavingARoom_ReturnsToTheLobby_WithAFreshRoomList()
        {
            Matchmaker host = Make(out FrameSession h, "Host", 1);
            host.CreateRoom(new MatchSpec(), "Visible");
            _net.PumpUntil(() => h.State == NetState.InRoom, 1000);

            Matchmaker mm = Make(out FrameSession s, "A", 2);
            _net.PumpUntil(() => s.State == NetState.InLobby, 500);
            _net.Pump(200);
            Assert.That(mm.Rooms.Get("Visible"), Is.Not.Null);
            mm.JoinRoom("Visible");
            _net.PumpUntil(() => s.State == NetState.InRoom, 500);
            Assert.That(mm.Rooms.Count, Is.EqualTo(0), "the list is stale while in a room");
            mm.LeaveRoom();
            _net.PumpUntil(() => s.State == NetState.InLobby, 500);
            _net.Pump(200);
            Assert.That(mm.Phase, Is.EqualTo(MatchmakerPhase.Lobby));
            Assert.That(mm.Rooms.Get("Visible").PlayerCount, Is.EqualTo(1));
        }

        [Test]
        public void ServerGoingAway_MovesToOfflineWithTheCause()
        {
            Matchmaker mm = Make(out FrameSession s, "A");
            _net.PumpUntil(() => s.State == NetState.InLobby, 500);
            s.Disconnect();
            _net.Pump(20);
            Assert.That(mm.Phase, Is.EqualTo(MatchmakerPhase.Offline));
            Assert.That(mm.Status, Does.Contain("disconnected by client"));
        }

        [Test]
        public void BuildRoomOptions_ClampsSettings()
        {
            RoomCreateOptions o = Matchmaker.BuildRoomOptions(new MatchSpec { Mode = GameModes.FreeForAll, Map = "nonsense", MaxPlayers = 9, DurationSec = 5 }, "3");
            Assert.That(o.MaxPlayers, Is.EqualTo(4));
            Assert.That(o.Props.GetString(RoomKeys.Map), Is.EqualTo(ArenaMaps.Pillars));
            Assert.That(o.Props.GetInt(RoomKeys.DurationSec), Is.EqualTo(30));
            Assert.That(o.Props.GetString(RoomKeys.GameVersion), Is.EqualTo("3"));
            Assert.That(o.LobbyProps, Is.EquivalentTo(RoomKeys.Lobby));
            Assert.That(Matchmaker.BuildRoomOptions(new MatchSpec { Mode = GameModes.Duel, MaxPlayers = 4 }, "1").MaxPlayers, Is.EqualTo(2));
        }

        [Test]
        public void RoomNames_AvoidAmbiguousCharacters()
        {
            var random = new Random(5);
            for (int i = 0; i < 200; i++)
            {
                string name = RoomNames.Generate(random);
                Assert.That(name, Does.Match("^Arena-[A-HJ-NP-Z2-9]{4}$"));
            }
        }
    }
}
