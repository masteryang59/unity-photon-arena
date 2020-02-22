using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PhotonArena.Core.Bots;
using PhotonArena.Core.Match;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Net.Client;
using PhotonArena.Core.Net.Transport;
using PhotonArena.Core.Sim;

namespace PhotonArena.Tests
{
    public class BotTests
    {
        TestNet _net;
        readonly List<BotPlayer> _bots = new List<BotPlayer>();

        [SetUp]
        public void SetUp() => _net = new TestNet();

        [TearDown]
        public void TearDown()
        {
            foreach (BotPlayer b in _bots) b.Dispose();
            _bots.Clear();
            _net.Dispose();
        }

        BotPlayer Bot(string room, bool waitForHumans, int seed)
        {
            var session = new FrameSession(new LoopbackConnector(_net.Server, _net.Clock, NetConditions.Of(10), seed), _net.Clock, "loopback");
            var bot = new BotPlayer(session, "Bot" + seed, seed, room, new MatchSpec { Mode = GameModes.FreeForAll }) { WaitForHumans = waitForHumans };
            _bots.Add(bot);
            bot.Start();
            return bot;
        }

        void Run(double ms)
        {
            for (double t = 0; t < ms; t += 10)
            {
                foreach (BotPlayer b in _bots) b.Tick(0.01f);
                _net.Step(10);
            }
        }

        static MatchPhase Phase(INetSession s) => (MatchPhase)s.Room.Props.GetInt(RoomKeys.Phase);

        [Test]
        public void BotsJoinARoomByName_FlagThemselves_AndReadyUp()
        {
            FrameSession host = _net.Connected("Host");
            _net.IntoRoom("Hosted", new MatchSpec(), host);
            Bot("Hosted", waitForHumans: false, seed: 1);
            Bot("Hosted", waitForHumans: false, seed: 2);
            Run(500);
            Assert.That(host.Room.PlayerCount, Is.EqualTo(3));
            NetPlayer[] bots = host.Room.Players.Values.Where(p => p.Actor != host.LocalActor).ToArray();
            Assert.That(bots.All(p => p.Props.GetBool(PlayerKeys.IsBot) && p.Props.GetBool(PlayerKeys.Ready)), Is.True);
            Assert.That(Phase(host), Is.EqualTo(MatchPhase.Waiting), "the human isn't ready yet");
        }

        [Test]
        public void WaitingBots_HoldAnOpenRoom_UntilAHumanJoinsAndReadies()
        {
            BotPlayer first = Bot(null, waitForHumans: true, seed: 1);
            Run(300);
            Bot(null, waitForHumans: true, seed: 2);
            Run(2000);
            Assert.That(first.InRoom, Is.True);
            string room = first.Session.Room.Name;
            Assert.That(first.Session.Room.PlayerCount, Is.EqualTo(2), "both quick-matched into one room");
            Assert.That(Phase(first.Session), Is.EqualTo(MatchPhase.Waiting), "two bots alone don't start a match");
            Assert.That(first.Session.Room.IsOpen, Is.True);

            FrameSession human = _net.Connected("Human");
            human.JoinRoom(room);
            _net.PumpUntil(() => human.State == NetState.InRoom, 500);
            ArenaClient client = _net.Arena(human);
            Run(1000);
            Assert.That(human.Room.Players.Values.Count(p => p.Props.GetBool(PlayerKeys.Ready)), Is.EqualTo(2), "bots ready up once a human is there");
            client.SetReady(true);
            Run(ArenaRules.CountdownMs + 500);
            Assert.That(Phase(human), Is.EqualTo(MatchPhase.Playing));

            // When the human walks out mid-match the bots finish it, then wait again.
            human.LeaveRoom();
            Run(500);
            _net.Clients.Remove(client);
            Run(130_000);
            Assert.That(Phase(first.Session), Is.EqualTo(MatchPhase.Waiting));
            Assert.That(first.Session.Room.Players.Values.Any(p => p.Props.GetBool(PlayerKeys.Ready)), Is.False);
        }
    }
}
