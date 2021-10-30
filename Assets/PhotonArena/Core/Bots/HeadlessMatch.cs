using System;
using System.Collections.Generic;
using System.Linq;
using PhotonArena.Core.Match;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Net.Client;
using PhotonArena.Core.Net.Server;
using PhotonArena.Core.Net.Transport;
using PhotonArena.Core.Sim;

namespace PhotonArena.Core.Bots
{
    public sealed class HeadlessMatchOptions
    {
        public int Players = 4;
        public MatchSpec Spec = new MatchSpec { Mode = GameModes.FreeForAll, Map = ArenaMaps.Pillars, MaxPlayers = 4, DurationSec = 60 };
        /// <summary>Per-player conditions; the list wraps if shorter than <see cref="Players"/>.</summary>
        public List<NetConditions> Conditions = new List<NetConditions> { NetConditions.Of(40, 10, 0.02) };
        public double TickMs = 1000.0 / 60.0;
        /// <summary>Stop after this many matches finish (reach Results).</summary>
        public int Matches = 1;
        public double MaxSimulatedSeconds = 600;
        /// <summary>Disconnect whoever is master this many seconds into the first match (0 = never).</summary>
        public double DropMasterAfterSec;
        public int Seed = 7;
        public int ServerTimeBase = int.MaxValue - 20_000; // wraps 20 s in, on purpose
    }

    public sealed class HeadlessMatchReport
    {
        public double SimulatedSeconds;
        public int MatchesFinished;
        public int Shots;
        public int Hits;
        public int Kills;
        public int SpeedViolations;
        public int MasterChanges;
        public int DroppedActor;
        public bool ScoreboardsAgree;
        public string FailureReason;
        public Dictionary<int, (string name, int kills, int deaths)> Scores = new Dictionary<int, (string, int, int)>();
        public int Winner;
        /// <summary>Distance between where an observer drew a remote avatar and where its owner really was at that time (m).</summary>
        public double InterpolationErrorMean;
        public double InterpolationErrorP95;
        public int InterpolationSamples;
        /// <summary>A frame from player 1's point of view, mid-match, for the screenshot.</summary>
        public ArenaFrame SnapshotFrame;

        public override string ToString()
        {
            var lines = new List<string>
            {
                $"simulated {SimulatedSeconds:0.0}s, matches finished {MatchesFinished}, master changes {MasterChanges}",
                $"shots {Shots}, hits {Hits} ({(Shots == 0 ? 0 : 100.0 * Hits / Shots):0.0}%), kills {Kills}, speed violations {SpeedViolations}",
                $"interpolation error mean {InterpolationErrorMean:0.000} m, p95 {InterpolationErrorP95:0.000} m over {InterpolationSamples} samples",
                $"scoreboards agree on every client: {ScoreboardsAgree}",
            };
            foreach (var kv in Scores.OrderByDescending(s => s.Value.kills))
            {
                lines.Add($"  #{kv.Key} {kv.Value.name,-10} {kv.Value.kills,2} kills {kv.Value.deaths,2} deaths{(kv.Key == Winner ? "  WINNER" : "")}");
            }
            if (FailureReason != null) lines.Add("FAILED: " + FailureReason);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Runs a whole match with bots on a simulated network and a virtual clock: real server, real sessions,
    /// real netcode, nothing mocked except time and the wire. Deterministic for a given seed.
    /// </summary>
    public static class HeadlessMatch
    {
        public static HeadlessMatchReport Run(HeadlessMatchOptions o, Action<string> log = null)
        {
            var clock = new ManualClock(1000);
            var server = new MatchServer(clock, new MatchServerOptions { ServerTimeBase = o.ServerTimeBase, LobbyUpdateIntervalMs = 250 });
            var bots = new List<BotPlayer>();
            var report = new HeadlessMatchReport();
            var truth = new Dictionary<int, SnapshotBuffer>();
            var hitIds = new HashSet<long>();
            var errors = new List<double>();
            var lifeChangedAt = new Dictionary<int, (int life, double at)>();
            double stopAt = -1;
            int startedBots = 0;
            int lastMaster = 0;
            bool dropped = false;
            MatchPhase lastPhase = MatchPhase.Waiting;
            double playingSince = -1;
            float dt = (float)(o.TickMs / 1000.0);
            BotPlayer watcher = null;

            for (int i = 0; i < o.Players; i++)
            {
                NetConditions conditions = o.Conditions[i % o.Conditions.Count];
                var session = new FrameSession(new LoopbackConnector(server, clock, conditions, o.Seed * 100 + i), clock, "loopback");
                var bot = new BotPlayer(session, "Bot" + (i + 1), o.Seed * 31 + i, null, o.Spec);
                bot.Client.BoltFired += b => { if (b.Shooter == bot.Session.LocalActor) report.Shots++; };
                bot.Client.Hit += h =>
                {
                    if (hitIds.Add(GameMessages.BoltId(h.Shooter, h.ShotId)))
                    {
                        report.Hits++;
                        if (h.Killed) report.Kills++;
                    }
                };
                bots.Add(bot);
            }
            watcher = bots[0];

            double elapsedMs = 0;
            while (elapsedMs < o.MaxSimulatedSeconds * 1000)
            {
                // Stagger connects so the first bot creates the room and the rest quick-match into it.
                if (startedBots < bots.Count && elapsedMs >= startedBots * 300)
                {
                    bots[startedBots++].Start();
                }

                server.Tick();
                foreach (BotPlayer bot in bots)
                {
                    bot.Tick(dt);
                    if (bot.InRoom)
                    {
                        // Ground truth: where each avatar really was, on the server clock.
                        int actor = bot.Session.LocalActor;
                        int life = bot.Session.Room.GetPlayer(actor)?.Props.GetInt(PlayerKeys.Life) ?? 0;
                        if (!truth.TryGetValue(actor, out SnapshotBuffer buffer)) truth[actor] = buffer = new SnapshotBuffer(256);
                        var snapshot = new SnapshotBuffer.Snapshot { Time = bot.Session.ServerTime, Position = bot.Client.LocalPosition, Alive = true };
                        if (!lifeChangedAt.TryGetValue(actor, out var lc) || lc.life != life)
                        {
                            lifeChangedAt[actor] = (life, elapsedMs);
                            buffer.Reset(snapshot);
                        }
                        else buffer.Add(snapshot);
                    }
                }

                BotPlayer observer = bots.FirstOrDefault(b => b.InRoom);
                if (observer != null)
                {
                    NetRoom room = observer.Session.Room;
                    if (room.MasterActor != lastMaster)
                    {
                        if (lastMaster != 0) report.MasterChanges++;
                        lastMaster = room.MasterActor;
                    }
                    var phase = (MatchPhase)room.Props.GetInt(RoomKeys.Phase);
                    if (phase == MatchPhase.Playing && lastPhase != MatchPhase.Playing) playingSince = elapsedMs;
                    if (phase == MatchPhase.Results && lastPhase == MatchPhase.Playing)
                    {
                        report.MatchesFinished++;
                        // Let the last property updates reach every client before comparing scoreboards.
                        if (report.MatchesFinished >= o.Matches) stopAt = elapsedMs + 1500;
                    }
                    lastPhase = phase;
                    if (stopAt >= 0 && elapsedMs >= stopAt)
                    {
                        CaptureScores(observer, report);
                        report.ScoreboardsAgree = ScoreboardsAgree(bots.Where(b => b.InRoom).Select(b => b.Session));
                        break;
                    }

                    if (phase == MatchPhase.Playing)
                    {
                        MeasureInterpolation(bots, truth, lifeChangedAt, elapsedMs, errors);
                        if (report.SnapshotFrame == null && playingSince >= 0 && elapsedMs - playingSince > 20_000 && watcher.InRoom)
                        {
                            // A representative moment: everyone on screen and something in the air.
                            ArenaFrame candidate = watcher.Client.BuildFrame();
                            if (candidate.Avatars.All(a => a.Visible) && candidate.Bolts.Count >= 2) report.SnapshotFrame = candidate;
                        }
                    }

                    if (!dropped && o.DropMasterAfterSec > 0 && phase == MatchPhase.Playing && elapsedMs - playingSince >= o.DropMasterAfterSec * 1000)
                    {
                        BotPlayer master = bots.FirstOrDefault(b => b.InRoom && b.Session.IsMaster);
                        if (master != null)
                        {
                            report.DroppedActor = master.Session.LocalActor;
                            log?.Invoke($"dropping master #{report.DroppedActor} at {elapsedMs / 1000:0.0}s");
                            master.Leave();
                            if (watcher == master) watcher = bots.First(b => b != master && b.InRoom);
                        }
                        dropped = true;
                    }
                }

                clock.Advance(o.TickMs);
                elapsedMs += o.TickMs;
            }

            report.SimulatedSeconds = elapsedMs / 1000;
            // Every client validates everyone's movement; report the worst count any one of them saw.
            report.SpeedViolations = bots.Where(b => b.InRoom)
                .Select(b => b.Session.Room.Players.Keys.Sum(actor => b.Client.Authority.ViolationsOf(actor)))
                .DefaultIfEmpty(0).Max();
            if (errors.Count > 0)
            {
                errors.Sort();
                report.InterpolationErrorMean = errors.Average();
                report.InterpolationErrorP95 = errors[(int)(errors.Count * 0.95)];
                report.InterpolationSamples = errors.Count;
            }
            if (report.MatchesFinished < o.Matches) report.FailureReason = $"only {report.MatchesFinished} of {o.Matches} matches finished in {o.MaxSimulatedSeconds}s";
            foreach (BotPlayer bot in bots) bot.Dispose();
            return report;
        }

        static void CaptureScores(BotPlayer observer, HeadlessMatchReport report)
        {
            NetRoom room = observer.Session.Room;
            report.Winner = room.Props.GetInt(RoomKeys.Winner);
            report.Scores.Clear();
            foreach (NetPlayer p in room.Players.Values)
            {
                report.Scores[p.Actor] = (p.Nickname, p.Props.GetInt(PlayerKeys.Kills), p.Props.GetInt(PlayerKeys.Deaths));
            }
        }

        static void MeasureInterpolation(List<BotPlayer> bots, Dictionary<int, SnapshotBuffer> truth,
            Dictionary<int, (int life, double at)> lifeChangedAt, double nowMs, List<double> errors)
        {
            foreach (BotPlayer observer in bots)
            {
                if (!observer.InRoom) continue;
                ArenaFrame frame = observer.Client.BuildFrame();
                int renderTime = ServerClock.Add(frame.ServerTime, -ArenaRules.InterpolationDelayMs);
                foreach (AvatarView a in frame.Avatars)
                {
                    if (a.IsLocal || !a.Visible || !truth.TryGetValue(a.Actor, out SnapshotBuffer real) || real.IsEmpty) continue;
                    // Skip half a second after a respawn teleport: observers learn about it a latency later.
                    if (lifeChangedAt.TryGetValue(a.Actor, out var lc) && nowMs - lc.at < 500) continue;
                    errors.Add(Vec2.Distance(real.SampleAt(renderTime).Position, a.Position));
                }
            }
        }

        /// <summary>Every client's copy of the scoreboard must match the first one's (all of it comes from the server's props).</summary>
        public static bool ScoreboardsAgree(IEnumerable<INetSession> sessions)
        {
            string reference = null;
            foreach (INetSession s in sessions)
            {
                if (s.Room == null) continue;
                string board = string.Join(";", s.Room.Players.Values.Select(p => $"{p.Actor}:{p.Props.GetInt(PlayerKeys.Kills)}/{p.Props.GetInt(PlayerKeys.Deaths)}"));
                if (reference == null) reference = board;
                else if (board != reference) return false;
            }
            return true;
        }
    }
}


    public static object TransformData(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object TransformData(object data = null)
    {
        if (data == null) return null;
        return data;
    }