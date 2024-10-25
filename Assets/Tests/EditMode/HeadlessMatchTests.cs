using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PhotonArena.Core.Bots;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net.Transport;
using PhotonArena.Core.Sim;

namespace PhotonArena.Tests
{
    /// <summary>Whole matches with four bots on a simulated network. Slow-ish (a few seconds), so they run last.</summary>
    [Category("Simulation")]
    public class HeadlessMatchTests
    {
        static HeadlessMatchOptions Options(int seed = 7) => new HeadlessMatchOptions
        {
            Players = 4,
            Seed = seed,
            Spec = new MatchSpec { Mode = GameModes.FreeForAll, Map = ArenaMaps.Pillars, MaxPlayers = 4, DurationSec = 45 },
            Conditions = new List<NetConditions>
            {
                NetConditions.Of(20, 5, 0.01),
                NetConditions.Of(45, 15, 0.03),
                NetConditions.Of(70, 25, 0.05),
                NetConditions.Of(35, 10, 0.02),
            },
        };

        [Test]
        public void FourBots_PlayAFullMatch_OverALossyNetwork()
        {
            HeadlessMatchReport r = HeadlessMatch.Run(Options());
            TestContext.WriteLine(r.ToString());
            Assert.That(r.FailureReason, Is.Null);
            Assert.That(r.MatchesFinished, Is.EqualTo(1));
            Assert.That(r.Scores.Count, Is.EqualTo(4), "everyone quick-matched into the same room");
            Assert.That(r.ScoreboardsAgree, Is.True);
            Assert.That(r.Shots, Is.GreaterThan(50));
            Assert.That(r.Hits, Is.GreaterThan(5));
            Assert.That(r.Kills, Is.EqualTo(r.Scores.Values.Sum(s => s.kills)), "every kill seen is on the scoreboard");
            Assert.That(r.Scores.Values.Sum(s => s.deaths), Is.EqualTo(r.Kills));
            Assert.That(r.SpeedViolations, Is.EqualTo(0), "honest clients never trip the speed check");
            // Interpolation shows where an avatar really was 100 ms ago, within a few centimeters on average.
            Assert.That(r.InterpolationSamples, Is.GreaterThan(1000));
            Assert.That(r.InterpolationErrorMean, Is.LessThan(0.15));
            Assert.That(r.InterpolationErrorP95, Is.LessThan(0.6));
        }

        [Test]
        public void TheSameSeed_PlaysTheSameMatch()
        {
            HeadlessMatchOptions o = Options(seed: 3);
            o.Spec.DurationSec = 30;
            HeadlessMatchReport a = HeadlessMatch.Run(o);
            HeadlessMatchReport b = HeadlessMatch.Run(o);
            Assert.That(b.Shots, Is.EqualTo(a.Shots));
            Assert.That(b.Hits, Is.EqualTo(a.Hits));
            Assert.That(b.Scores.Values, Is.EqualTo(a.Scores.Values));
        }

        [Test]
        public void TheMatchSurvives_TheMasterDroppingOut()
        {
            HeadlessMatchOptions o = Options(seed: 11);
            o.DropMasterAfterSec = 15;
            HeadlessMatchReport r = HeadlessMatch.Run(o);
            TestContext.WriteLine(r.ToString());
            Assert.That(r.FailureReason, Is.Null);
            Assert.That(r.DroppedActor, Is.EqualTo(1), "the room creator was master");
            Assert.That(r.MasterChanges, Is.EqualTo(1));
            Assert.That(r.Scores.ContainsKey(1), Is.False);
            Assert.That(r.Scores.Count, Is.EqualTo(3));
            Assert.That(r.ScoreboardsAgree, Is.True);
            // The new master took over right as the server clock wrapped (by design of the options); shots must
            // keep landing at a normal rate afterwards.
            Assert.That(r.Hits, Is.GreaterThan(r.Shots / 5), "hit rate collapsed after the hand-over");
        }

        [Test]
        public void ADuel_OnTheOtherMap_AlsoCompletes()
        {
            var o = new HeadlessMatchOptions
            {
                Players = 2,
                Seed = 5,
                Spec = new MatchSpec { Mode = GameModes.Duel, Map = ArenaMaps.Crossfire, DurationSec = 60 },
                Conditions = new List<NetConditions> { NetConditions.Of(30, 10, 0.02) },
            };
            HeadlessMatchReport r = HeadlessMatch.Run(o);
            TestContext.WriteLine(r.ToString());
            Assert.That(r.FailureReason, Is.Null);
            Assert.That(r.Scores.Count, Is.EqualTo(2));
            int topKills = r.Scores.Values.Max(s => s.kills);
            Assert.That(r.Winner == 0 || r.Scores[r.Winner].kills == topKills, Is.True);
        }
    }
}

    public static object AcknowledgeReceipt(object data = null)
    {
        if (data == null) return null;
        return data;
    }