using System.Linq;
using NUnit.Framework;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Net.Client;
using PhotonArena.Core.Net.Transport;
using PhotonArena.Core.Net.Wire;

namespace PhotonArena.Tests
{
    public class MatchServerTests
    {
        TestNet _net;

        [SetUp]
        public void SetUp() => _net = new TestNet();

        [TearDown]
        public void TearDown() => _net.Dispose();

        static RoomCreateOptions Options(int max = 4, string mode = "ffa", string map = "pillars") =>
            Matchmaker.BuildRoomOptions(new MatchSpec { Mode = mode, Map = map, MaxPlayers = max }, "1");

        [Test]
        public void Connect_RaisesConnected_AndSyncsTheServerClock()
        {
            var net = new TestNet(serverTimeBase: 123_456);
            FrameSession s = net.Add(NetConditions.Of(30));
            s.Connect("Ada");
            Assert.That(s.State, Is.EqualTo(NetState.Connecting));
            net.PumpUntil(() => s.State == NetState.Connected, 500);
            net.Pump(1000);
            Assert.That(net.Rec(s).Log.First(), Is.EqualTo("connected"));
            Assert.That(ServerClock.Diff(s.ServerTime, net.Server.ServerTime), Is.InRange(-6, 6));
            Assert.That(s.RoundTripMs, Is.InRange(55, 70));
            net.Dispose();
        }

        [Test]
        public void JoinLobby_SendsTheFullList_ThenDeltas()
        {
            FrameSession host = _net.Connected("Host");
            host.CreateRoom("R1", Options());
            _net.PumpUntil(() => host.State == NetState.InRoom, 500);

            FrameSession browser = _net.Connected("Browser");
            _net.Pump(50);
            Assert.That(_net.Rec(browser).RoomUpdates.Select(l => l.Name).Distinct(), Is.EqualTo(new[] { "R1" }));
            RoomListing listed = _net.Rec(browser).RoomUpdates.Last();
            Assert.That(listed.Name, Is.EqualTo("R1"));
            Assert.That(listed.PlayerCount, Is.EqualTo(1));
            Assert.That(listed.Props.GetString(RoomKeys.Mode), Is.EqualTo("ffa"));
            Assert.That(listed.Props.ContainsKey(RoomKeys.ScoreLimit), Is.False, "only lobby-visible properties are listed");

            FrameSession second = _net.Connected("Second");
            second.JoinRoom("R1");
            _net.Pump(300);
            Assert.That(_net.Rec(browser).RoomUpdates.Last().PlayerCount, Is.EqualTo(2), "delta after a join");
        }

        [Test]
        public void RoomsThatCloseFillOrEmpty_AreRemovedFromTheList()
        {
            FrameSession browser = _net.Connected("Browser");
            var cache = new RoomListCache();
            FrameSession a = _net.Connected("A");
            FrameSession b = _net.Connected("B");
            a.CreateRoom("Small", Options(max: 2));
            _net.PumpUntil(() => a.State == NetState.InRoom, 500);
            _net.Pump(300);
            cache.Apply(_net.Rec(browser).RoomUpdates);
            Assert.That(cache.Get("Small"), Is.Not.Null);

            _net.Rec(browser).RoomUpdates.Clear();
            b.JoinRoom("Small");
            _net.Pump(300);
            cache.Apply(_net.Rec(browser).RoomUpdates);
            Assert.That(cache.Get("Small"), Is.Null, "full rooms leave the list");

            _net.Rec(browser).RoomUpdates.Clear();
            b.LeaveRoom();
            _net.Pump(300);
            cache.Apply(_net.Rec(browser).RoomUpdates);
            Assert.That(cache.Get("Small"), Is.Not.Null, "back once a slot frees up");

            _net.Rec(browser).RoomUpdates.Clear();
            a.SetRoomOpen(false);
            _net.Pump(300);
            cache.Apply(_net.Rec(browser).RoomUpdates);
            Assert.That(cache.Get("Small"), Is.Null, "closed rooms leave the list");

            a.SetRoomOpen(true);
            _net.Pump(300);
            cache.Apply(_net.Rec(browser).RoomUpdates);
            _net.Rec(browser).RoomUpdates.Clear();
            a.LeaveRoom();
            _net.Pump(300);
            cache.Apply(_net.Rec(browser).RoomUpdates);
            Assert.That(cache.Get("Small"), Is.Null, "empty rooms are deleted");
            Assert.That(_net.Server.RoomCount, Is.EqualTo(0));
        }

        [Test]
        public void CreateRoom_WithATakenName_Fails()
        {
            FrameSession a = _net.Connected("A");
            FrameSession b = _net.Connected("B");
            a.CreateRoom("Same", Options());
            _net.PumpUntil(() => a.State == NetState.InRoom, 500);
            b.CreateRoom("Same", Options());
            _net.Pump(50);
            Assert.That(_net.Rec(b).JoinFailures.Single(), Is.EqualTo((JoinKind.Create, ErrorCodes.GameIdAlreadyExists)));
            Assert.That(b.State, Is.EqualTo(NetState.InLobby), "a failed join goes back to where it came from");
        }

        [Test]
        public void JoinRoom_ReportsMissingFullAndClosedRooms()
        {
            FrameSession a = _net.Connected("A");
            FrameSession b = _net.Connected("B");
            FrameSession c = _net.Connected("C");
            b.JoinRoom("Nope");
            _net.Pump(50);
            Assert.That(_net.Rec(b).JoinFailures.Last().code, Is.EqualTo(ErrorCodes.GameDoesNotExist));

            a.CreateRoom("Duo", Options(max: 2));
            _net.PumpUntil(() => a.State == NetState.InRoom, 500);
            a.SetRoomOpen(false);
            _net.Pump(50);
            b.JoinRoom("Duo");
            _net.Pump(50);
            Assert.That(_net.Rec(b).JoinFailures.Last().code, Is.EqualTo(ErrorCodes.GameClosed));

            a.SetRoomOpen(true);
            _net.Pump(50);
            b.JoinRoom("Duo");
            _net.PumpUntil(() => b.State == NetState.InRoom, 500);
            c.JoinRoom("Duo");
            _net.Pump(50);
            Assert.That(_net.Rec(c).JoinFailures.Last().code, Is.EqualTo(ErrorCodes.GameFull));
        }

        [Test]
        public void JoinRandom_FiltersOnLobbyProps_AndFillsTheOldestRoomFirst()
        {
            FrameSession h1 = _net.Connected("H1");
            FrameSession h2 = _net.Connected("H2");
            FrameSession h3 = _net.Connected("H3");
            h1.CreateRoom("Duel", Options(max: 2, mode: "duel"));
            _net.Pump(20);
            h2.CreateRoom("OldFfa", Options(mode: "ffa"));
            _net.Pump(20);
            h3.CreateRoom("NewFfa", Options(mode: "ffa", map: "crossfire"));
            _net.Pump(20);

            FrameSession p = _net.Connected("P");
            p.JoinRandom(PropertyBag.Of(RoomKeys.Mode, "ffa"), 0);
            _net.PumpUntil(() => p.State == NetState.InRoom, 500);
            Assert.That(p.Room.Name, Is.EqualTo("OldFfa"));

            FrameSession q = _net.Connected("Q");
            q.JoinRandom(PropertyBag.Of(RoomKeys.Mode, "ffa", RoomKeys.Map, "crossfire"), 0);
            _net.PumpUntil(() => q.State == NetState.InRoom, 500);
            Assert.That(q.Room.Name, Is.EqualTo("NewFfa"));

            FrameSession r = _net.Connected("R");
            r.JoinRandom(PropertyBag.Of(RoomKeys.Mode, "ffa"), 2);
            _net.Pump(50);
            Assert.That(_net.Rec(r).JoinFailures.Single().code, Is.EqualTo(ErrorCodes.NoRandomMatchFound), "no 2-player ffa room");

            r.JoinRandom(PropertyBag.Of(RoomKeys.Mode, "ffa", RoomKeys.GameVersion, "2"), 0);
            _net.Pump(50);
            Assert.That(_net.Rec(r).JoinFailures.Last().code, Is.EqualTo(ErrorCodes.NoRandomMatchFound), "different game versions never meet");
        }

        [Test]
        public void JoinRandom_CannotFilterOnPropertiesHiddenFromTheLobby()
        {
            FrameSession h = _net.Connected("H");
            h.CreateRoom("R", Options());
            _net.Pump(20);
            FrameSession p = _net.Connected("P");
            // ScoreLimit is a room property, but not a lobby property: like Photon, it can't be matched on.
            p.JoinRandom(PropertyBag.Of(RoomKeys.ScoreLimit, 10), 0);
            _net.Pump(50);
            Assert.That(_net.Rec(p).JoinFailures.Single().code, Is.EqualTo(ErrorCodes.NoRandomMatchFound));
        }

        [Test]
        public void ActorsCountUp_AndTheLowestRemainingActorBecomesMaster()
        {
            FrameSession a = _net.Connected("A");
            FrameSession b = _net.Connected("B");
            FrameSession c = _net.Connected("C");
            _net.IntoRoom("R", new MatchSpec(), a, b, c);
            Assert.That((a.LocalActor, b.LocalActor, c.LocalActor), Is.EqualTo((1, 2, 3)));
            Assert.That(a.IsMaster, Is.True);
            Assert.That(c.Room.Players[2].Nickname, Is.EqualTo("B"));

            a.Disconnect();
            _net.Pump(50);
            Assert.That(b.IsMaster, Is.True);
            Assert.That(c.Room.MasterActor, Is.EqualTo(2));
            Assert.That(_net.Rec(c).Log, Does.Contain("playerleft:1").And.Contain("master:2"));

            b.LeaveRoom();
            _net.Pump(50);
            Assert.That(c.IsMaster, Is.True);
            Assert.That(_net.Rec(b).Log.Skip(_net.Rec(b).Log.IndexOf("left")), Does.Contain("connected"), "back on the master server after leaving");

            FrameSession d = _net.Connected("D");
            d.JoinRoom("R");
            _net.PumpUntil(() => d.State == NetState.InRoom, 500);
            Assert.That(d.LocalActor, Is.EqualTo(4), "actor numbers are never reused within a room");
        }

        [Test]
        public void PropertyChanges_ApplyOnEcho_ToEveryone()
        {
            FrameSession a = _net.Connected("A");
            FrameSession b = _net.Connected("B");
            _net.IntoRoom("R", new MatchSpec(), a, b);
            a.SetRoomProperties(PropertyBag.Of("x", 5));
            Assert.That(a.Room.Props.ContainsKey("x"), Is.False, "not applied locally before the server confirms");
            _net.Pump(20);
            Assert.That(a.Room.Props.GetInt("x"), Is.EqualTo(5));
            Assert.That(b.Room.Props.GetInt("x"), Is.EqualTo(5));

            b.SetPlayerProperties(a.LocalActor, PropertyBag.Of("tag", "it"));
            _net.Pump(20);
            Assert.That(a.Room.Players[1].Props.GetString("tag"), Is.EqualTo("it"));
            Assert.That(_net.Rec(a).Saw("playerprops:1"), Is.True);
        }

        [Test]
        public void CompareAndSwap_RejectsStaleWrites()
        {
            FrameSession a = _net.Connected("A");
            FrameSession b = _net.Connected("B");
            _net.IntoRoom("R", new MatchSpec(), a, b);
            // Both think the phase is 0 and race to move it.
            a.SetRoomProperties(PropertyBag.Of(RoomKeys.Phase, 1), PropertyBag.Of(RoomKeys.Phase, 0));
            b.SetRoomProperties(PropertyBag.Of(RoomKeys.Phase, 2), PropertyBag.Of(RoomKeys.Phase, 0));
            _net.Pump(20);
            Assert.That(a.Room.Props.GetInt(RoomKeys.Phase), Is.EqualTo(1));
            Assert.That(b.Room.Props.GetInt(RoomKeys.Phase), Is.EqualTo(1));
            Assert.That(_net.Rec(b).Rejections, Has.Count.EqualTo(1));

            a.SetPlayerProperties(2, PropertyBag.Of("c", 1), PropertyBag.Of("c", null));
            a.SetPlayerProperties(2, PropertyBag.Of("c", 2), PropertyBag.Of("c", null));
            _net.Pump(20);
            Assert.That(b.Room.Players[2].Props.GetInt("c"), Is.EqualTo(1), "'expected absent' lets only the first write in");
        }

        [Test]
        public void Events_AreRoutedToOthersAllOrMaster_WithTheSendersActor()
        {
            FrameSession a = _net.Connected("A");
            FrameSession b = _net.Connected("B");
            FrameSession c = _net.Connected("C");
            _net.IntoRoom("R", new MatchSpec(), a, b, c);
            b.Send(10, new byte[] { 1 }, Target.Others, true);
            b.Send(11, new byte[] { 2 }, Target.All, true);
            c.Send(12, null, Target.Master, false);
            _net.Pump(20);
            Assert.That(_net.Rec(a).Events.Select(e => (e.code, e.sender)), Is.EqualTo(new[] { ((byte)10, 2), ((byte)11, 2), ((byte)12, 3) }));
            Assert.That(_net.Rec(b).Events.Select(e => e.code), Is.EqualTo(new byte[] { 11 }), "Others excludes the sender, All includes it");
            Assert.That(_net.Rec(c).Events.Select(e => e.code), Is.EqualTo(new byte[] { 10, 11 }));
            Assert.That(_net.Rec(c).Events[0].payload, Is.EqualTo(new byte[] { 1 }));
        }

        [Test]
        public void UnreliableEvents_CanBeLost_ReliableOnesNever()
        {
            FrameSession a = _net.Connected("A", NetConditions.Of(10, 5, 0.3));
            FrameSession b = _net.Connected("B");
            _net.IntoRoom("R", new MatchSpec(), a, b);
            for (int i = 0; i < 100; i++)
            {
                a.Send(1, null, Target.Others, false);
                a.Send(2, null, Target.Others, true);
            }
            _net.Pump(200);
            int unreliable = _net.Rec(b).Events.Count(e => e.code == 1);
            int reliable = _net.Rec(b).Events.Count(e => e.code == 2);
            Assert.That(reliable, Is.EqualTo(100));
            Assert.That(unreliable, Is.InRange(50, 90));
        }

        [Test]
        public void Nicknames_AreCleaned()
        {
            FrameSession a = _net.Add();
            a.Connect("  Bob\u0007 the Builder of Things  ");
            _net.PumpUntil(() => a.State == NetState.Connected, 500);
            a.JoinLobby();
            _net.Pump(20);
            a.CreateRoom("R", Options());
            _net.PumpUntil(() => a.State == NetState.InRoom, 500);
            Assert.That(a.Room.Players[1].Nickname, Is.EqualTo("Bob the Builder"));
        }

        [Test]
        public void ClientsThatNeverSayHello_AreDropped()
        {
            var (client, serverEnd) = LoopbackLink.CreatePair(_net.Clock, NetConditions.Perfect, NetConditions.Perfect);
            _net.Server.Accept(serverEnd);
            _net.Pump(100);
            Assert.That(_net.Server.PeerCount, Is.EqualTo(1));
            _net.Pump(5200);
            Assert.That(_net.Server.PeerCount, Is.EqualTo(0));
            Assert.That(client.TryReceive(out byte[] frame), Is.True);
            Assert.That(frame[0], Is.EqualTo(Op.Error));
        }

        [Test]
        public void MalformedFrames_GetTheSenderKicked_NotTheServerKilled()
        {
            FrameSession good = _net.Connected("Good");
            var (client, serverEnd) = LoopbackLink.CreatePair(_net.Clock, NetConditions.Perfect, NetConditions.Perfect);
            _net.Server.Accept(serverEnd);
            client.Send(new ByteWriter().U8(Op.Hello).U8(Op.ProtocolVersion).Str("Evil").ToArray(), true);
            client.Send(new byte[] { Op.CreateRoom, 0xFF }, true);
            _net.Pump(50);
            Assert.That(client.IsOpen, Is.True, "still reading the error");
            byte[] last = null;
            while (client.TryReceive(out byte[] f)) last = f;
            Assert.That(last[0], Is.EqualTo(Op.Error));
            Assert.That(new ByteReader(last, 1, last.Length - 1).Str(), Does.StartWith("Malformed message"));
            _net.Pump(20);
            Assert.That(_net.Server.PeerCount, Is.EqualTo(1));
            Assert.That(good.State, Is.EqualTo(NetState.InLobby));
        }

        [Test]
        public void AWrongProtocolVersion_IsRefusedWithAReason()
        {
            var (client, serverEnd) = LoopbackLink.CreatePair(_net.Clock, NetConditions.Perfect, NetConditions.Perfect);
            _net.Server.Accept(serverEnd);
            client.Send(new ByteWriter().U8(Op.Hello).U8(99).Str("Future").ToArray(), true);
            _net.Pump(20);
            Assert.That(client.TryReceive(out byte[] f), Is.True);
            Assert.That(new ByteReader(f, 1, f.Length - 1).Str(), Does.Contain("version 99"));
        }

        [Test]
        public void ServerClose_IsReportedAsDisconnectWithTheServersReason()
        {
            FrameSession a = _net.Add();
            var server = new Core.Net.Server.MatchServer(_net.Clock, new Core.Net.Server.MatchServerOptions { MaxPeers = 0 });
            var session = new FrameSession(new LoopbackConnector(server, _net.Clock), _net.Clock, "loopback");
            var rec = new Recorder();
            session.AddCallbacks(rec);
            session.Connect("X");
            for (int i = 0; i < 10; i++)
            {
                server.Tick();
                session.Service();
                _net.Clock.Advance(5);
            }
            Assert.That(session.State, Is.EqualTo(NetState.Disconnected));
            Assert.That(rec.DisconnectCause, Is.EqualTo("Server full."));
            a.Dispose();
        }
    }
}

    public static object TraceRequest(object data = null)
    {
        if (data == null) return null;
        return data;
    }