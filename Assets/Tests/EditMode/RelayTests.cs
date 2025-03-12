using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using PhotonArena.Core.Bots;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Net.Client;
using PhotonArena.Core.Net.Server;
using PhotonArena.Core.Net.Transport;

namespace PhotonArena.Tests
{
    /// <summary>The LAN relay over real TCP sockets on 127.0.0.1, with real threads and a real clock.</summary>
    [Category("Sockets")]
    public class RelayTests
    {
        RelayServer _relay;
        readonly IClock _clock = new StopwatchClock();

        [SetUp]
        public void SetUp() => _relay = new RelayServer(port: 0, bind: IPAddress.Loopback);

        [TearDown]
        public void TearDown() => _relay.Dispose();

        FrameSession Session() => new FrameSession(new TcpConnector("127.0.0.1", _relay.Port), _clock, "relay");

        static void Wait(Func<bool> condition, int timeoutMs, params INetSession[] sessions)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > timeoutMs) Assert.Fail($"timed out after {timeoutMs} ms");
                foreach (INetSession s in sessions) s.Service();
                Thread.Sleep(2);
            }
        }

        [Test]
        public void TwoClients_MeetInARoom_AndExchangeEvents()
        {
            using FrameSession a = Session();
            using FrameSession b = Session();
            var recB = new Recorder();
            b.AddCallbacks(recB);
            a.Connect("Alice");
            b.Connect("Bob");
            Wait(() => a.State == NetState.Connected && b.State == NetState.Connected, 5000, a, b);

            a.JoinLobby();
            b.JoinLobby();
            Wait(() => a.State == NetState.InLobby && b.State == NetState.InLobby, 2000, a, b);
            a.CreateRoom("Lan", Matchmaker.BuildRoomOptions(new MatchSpec(), "1"));
            Wait(() => a.State == NetState.InRoom, 2000, a, b);
            Wait(() => recB.RoomUpdates.Exists(r => r.Name == "Lan"), 3000, a, b);
            b.JoinRoom("Lan");
            Wait(() => b.State == NetState.InRoom && a.Room.PlayerCount == 2, 2000, a, b);

            a.Send(42, new byte[] { 7, 7 }, Target.Others, false);
            Wait(() => recB.Events.Count == 1, 2000, a, b);
            Assert.That(recB.Events[0].code, Is.EqualTo(42));
            Assert.That(recB.Events[0].sender, Is.EqualTo(1));
            Assert.That(b.Room.Players[1].Nickname, Is.EqualTo("Alice"));

            // After a few pings both clients agree on the server clock. The bound is loose because Windows' timer
            // resolution (~15.6 ms) delays when a pong is processed; the loopback tests measure precision.
            var settle = Stopwatch.StartNew();
            Wait(() => settle.ElapsedMilliseconds > 800, 2000, a, b);
            Assert.That(Math.Abs(ServerClock.Diff(a.ServerTime, b.ServerTime)), Is.LessThan(40));
        }

        [Test]
        public void ADroppedConnection_IsNoticedByTheRoom()
        {
            using FrameSession a = Session();
            using FrameSession b = Session();
            var recA = new Recorder();
            a.AddCallbacks(recA);
            a.Connect("A");
            b.Connect("B");
            Wait(() => a.State == NetState.Connected && b.State == NetState.Connected, 5000, a, b);
            a.CreateRoom("Drop", new RoomCreateOptions());
            Wait(() => a.State == NetState.InRoom, 2000, a, b);
            b.JoinRoom("Drop");
            Wait(() => b.State == NetState.InRoom, 2000, a, b);
            b.Dispose(); // closes the socket without a goodbye
            Wait(() => a.Room.PlayerCount == 1, 3000, a);
            Assert.That(recA.Saw("playerleft:2"), Is.True);
        }

        [Test]
        public void ConnectingToNothing_FailsWithAReason()
        {
            // Grab a free port, then close it so nothing is listening there.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            using var s = new FrameSession(new TcpConnector("127.0.0.1", port, timeoutMs: 2000), _clock, "relay");
            var rec = new Recorder();
            s.AddCallbacks(rec);
            s.Connect("X");
            Wait(() => s.State == NetState.Disconnected, 5000, s);
            Assert.That(rec.DisconnectCause, Does.Contain("127.0.0.1:" + port));
        }

        [Test]
        public void OversizedFrames_CloseTheOffendingConnection()
        {
            using var raw = new TcpClient();
            raw.Connect(IPAddress.Loopback, _relay.Port);
            NetworkStream stream = raw.GetStream();
            stream.Write(BitConverter.GetBytes(TcpFrameLink.MaxFrameBytes + 1), 0, 4);
            stream.ReadTimeout = 3000;
            var buffer = new byte[16];
            int read;
            try
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch (System.IO.IOException)
            {
                read = 0; // reset by peer counts as closed too
            }
            Assert.That(read, Is.EqualTo(0), "the server hung up");
        }

        [Test]
        public void BotsCanPlayOverTheRelay()
        {
            var bots = new BotPlayer[2];
            try
            {
                for (int i = 0; i < bots.Length; i++)
                {
                    bots[i] = new BotPlayer(Session(), "Bot" + i, i + 1, roomName: null,
                        quickMatch: new MatchSpec { Mode = Core.Sim.GameModes.Duel });
                }
                bots[0].Start();
                var watch = Stopwatch.StartNew();
                long last = 0;
                bool started = false;
                while (watch.ElapsedMilliseconds < 10_000)
                {
                    if (!started && watch.ElapsedMilliseconds > 300)
                    {
                        bots[1].Start();
                        started = true;
                    }
                    long now = watch.ElapsedMilliseconds;
                    float dt = Math.Max(0.001f, (now - last) / 1000f);
                    last = now;
                    foreach (BotPlayer b in bots) b.Tick(dt);
                    if (bots[0].InRoom && (Core.Match.MatchPhase)bots[0].Session.Room.Props.GetInt(RoomKeys.Phase) == Core.Match.MatchPhase.Playing) break;
                    Thread.Sleep(5);
                }
                Assert.That(bots[1].Session.Room?.Name, Is.EqualTo(bots[0].Session.Room?.Name));
                Assert.That((Core.Match.MatchPhase)bots[0].Session.Room.Props.GetInt(RoomKeys.Phase), Is.EqualTo(Core.Match.MatchPhase.Playing),
                    "two bots quick-matched over TCP, readied up and started a duel");
            }
            finally
            {
                foreach (BotPlayer b in bots) b?.Dispose();
            }
        }

        [Test]
        public void TheRelayCountsPeers()
        {
            using FrameSession a = Session();
            a.Connect("Counted");
            Wait(() => a.State == NetState.Connected, 5000, a);
            Wait(() => _relay.Match.PeerCount == 1, 1000, a);
            Assert.That(_relay.Port, Is.GreaterThan(0));
        }
    }
}

    public static object ReportError(object data = null)
    {
        if (data == null) return null;
        return data;
    }