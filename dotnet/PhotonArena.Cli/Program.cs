using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using PhotonArena.Core.Bots;
using PhotonArena.Core.Match;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Net.Client;
using PhotonArena.Core.Net.Server;
using PhotonArena.Core.Net.Transport;
using PhotonArena.Core.Sim;

namespace PhotonArena.Cli
{
    /// <summary>
    /// Headless tools built on the same core as the game:
    ///   relay     dedicated LAN relay server
    ///   bots      headless bots that join a relay (fill a room for testing)
    ///   simulate  a whole match with bots on a simulated network, with a netcode report (and optional SVG)
    /// </summary>
    public static class Program
    {
        const string Usage = @"Photon Arena headless tools

  relay    [--port 7777]
  bots     [--host 127.0.0.1] [--port 7777] [--count 3] [--room NAME] [--mode ffa|duel] [--ready-alone]
  simulate [--players 4] [--mode ffa|duel] [--map pillars|crossfire] [--duration 60]
           [--latency 40] [--jitter 10] [--loss 0.02] [--seed 7] [--drop-master SECONDS] [--svg FILE]";

        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }
            var options = ParseOptions(args.Skip(1).ToArray());
            try
            {
                switch (args[0])
                {
                    case "relay": return Relay(options);
                    case "bots": return Bots(options);
                    case "simulate": return Simulate(options);
                    default:
                        Console.Error.WriteLine($"Unknown command '{args[0]}'.\n\n{Usage}");
                        return 1;
                }
            }
            catch (FormatException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }

        static Dictionary<string, string> ParseOptions(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new FormatException($"Unexpected argument '{args[i]}'.");
                string key = args[i].Substring(2);
                result[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            }
            return result;
        }

        static string Str(Dictionary<string, string> o, string key, string fallback) => o.TryGetValue(key, out string v) ? v : fallback;

        static int Int(Dictionary<string, string> o, string key, int fallback)
        {
            if (!o.TryGetValue(key, out string v)) return fallback;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) throw new FormatException($"--{key} expects a whole number, got '{v}'.");
            return n;
        }

        static double Dbl(Dictionary<string, string> o, string key, double fallback)
        {
            if (!o.TryGetValue(key, out string v)) return fallback;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) throw new FormatException($"--{key} expects a number, got '{v}'.");
            return n;
        }

        static string Mode(Dictionary<string, string> o)
        {
            string mode = Str(o, "mode", GameModes.FreeForAll);
            if (!GameModes.IsValid(mode)) throw new FormatException("--mode must be ffa or duel.");
            return mode;
        }

        static int Relay(Dictionary<string, string> o)
        {
            int port = Int(o, "port", RelayServer.DefaultPort);
            using var relay = new RelayServer(port, log: m => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}"));
            Console.WriteLine($"Relay on port {relay.Port}. LAN address: {LocalAddress()}. Ctrl+C to stop.");
            var stop = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Set();
            };
            while (!stop.Wait(10_000))
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} status: {relay.Match.PeerCount} connected, {relay.Match.RoomCount} rooms");
            }
            Console.WriteLine("Stopping.");
            return 0;
        }

        static int Bots(Dictionary<string, string> o)
        {
            string host = Str(o, "host", "127.0.0.1");
            int port = Int(o, "port", RelayServer.DefaultPort);
            int count = Math.Clamp(Int(o, "count", 3), 1, ArenaRules.MaxPlayersLimit);
            string room = Str(o, "room", null);
            var spec = new MatchSpec { Mode = Mode(o), Map = null };
            var clock = new StopwatchClock();
            var bots = new List<BotPlayer>();
            for (int i = 0; i < count; i++)
            {
                var session = new FrameSession(new TcpConnector(host, port), clock, "relay");
                // By default bots wait in the room until a human joins; --ready-alone lets them play each other.
                bots.Add(new BotPlayer(session, "Bot" + (i + 1), Environment.TickCount + i, room, spec) { WaitForHumans = !o.ContainsKey("ready-alone") });
            }
            Console.WriteLine(room == null ? $"{count} bots quick-matching on {host}:{port}..." : $"{count} bots joining room {room} on {host}:{port}...");
            var stop = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Set();
            };

            var watch = Stopwatch.StartNew();
            double last = 0, lastReport = 0;
            int started = 0;
            while (!stop.IsSet)
            {
                double now = watch.Elapsed.TotalMilliseconds;
                // Stagger quick matches so the first bot's room exists before the others look for one.
                if (started < bots.Count && now >= started * 400) bots[started++].Start();
                float dt = (float)((now - last) / 1000.0);
                last = now;
                foreach (BotPlayer bot in bots) bot.Tick(dt);
                if (now - lastReport > 5000)
                {
                    lastReport = now;
                    foreach (BotPlayer bot in bots)
                    {
                        NetRoom r = bot.Session.Room;
                        string where = r == null ? bot.Session.State.ToString() : $"{r.Name} {(MatchPhase)r.Props.GetInt(RoomKeys.Phase)} {r.PlayerCount}/{r.MaxPlayers}";
                        Console.WriteLine($"  {bot.Nickname}: {where}{(bot.Failure != null ? " - " + bot.Failure : "")}");
                    }
                }
                if (bots.All(b => b.Failure != null && b.Session.State == NetState.Disconnected))
                {
                    Console.Error.WriteLine("All bots stopped: " + bots[0].Failure);
                    return 1;
                }
                Thread.Sleep(8);
            }
            foreach (BotPlayer bot in bots) bot.Dispose();
            return 0;
        }

        static int Simulate(Dictionary<string, string> o)
        {
            string mode = Mode(o);
            string map = Str(o, "map", ArenaMaps.Pillars);
            if (!ArenaMaps.Exists(map)) throw new FormatException("--map must be pillars or crossfire.");
            int players = mode == GameModes.Duel ? 2 : Math.Clamp(Int(o, "players", 4), 2, ArenaRules.MaxPlayersLimit);
            double latency = Dbl(o, "latency", 40), jitter = Dbl(o, "jitter", 10), loss = Dbl(o, "loss", 0.02);
            var options = new HeadlessMatchOptions
            {
                Players = players,
                Seed = Int(o, "seed", 7),
                Spec = new MatchSpec { Mode = mode, Map = map, MaxPlayers = players, DurationSec = Int(o, "duration", 60) },
                // Spread players over a range of connections around the requested one.
                Conditions = Enumerable.Range(0, players)
                    .Select(i => NetConditions.Of(latency * (0.5 + i * 0.5), jitter, loss))
                    .ToList(),
                DropMasterAfterSec = Dbl(o, "drop-master", 0),
            };
            Console.WriteLine($"Simulating {GameModes.DisplayName(mode)} on {ArenaMaps.Get(map).DisplayName}, {players} bots, one-way latency "
                              + string.Join(" / ", options.Conditions.Select(c => $"{c.LatencyMs:0}")) + $" ms, jitter {jitter:0} ms, loss {loss:P0}");
            var watch = Stopwatch.StartNew();
            HeadlessMatchReport report = HeadlessMatch.Run(options, Console.WriteLine);
            Console.WriteLine(report);
            Console.WriteLine($"(ran in {watch.ElapsedMilliseconds} ms of real time)");

            string svg = Str(o, "svg", null);
            if (svg != null)
            {
                if (report.SnapshotFrame == null)
                {
                    Console.Error.WriteLine("No mid-match frame was captured (match too short?); SVG not written.");
                    return 1;
                }
                string subtitle = $"headless match · {players} bots · simulated {options.Conditions.Min(c => c.LatencyMs) * 2:0}–{options.Conditions.Max(c => c.LatencyMs) * 2:0} ms RTT, {loss:P0} loss";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(svg)));
                File.WriteAllText(svg, SvgFrame.Render(report.SnapshotFrame, report, subtitle));
                Console.WriteLine("Wrote " + svg);
            }
            return report.FailureReason == null ? 0 : 1;
        }

        static string LocalAddress()
        {
            try
            {
                return System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))?.ToString() ?? "127.0.0.1";
            }
            catch (System.Net.Sockets.SocketException)
            {
                return "127.0.0.1";
            }
        }
    }
}


    public static object PingService(object data = null)
    {
        if (data == null) return null;
        return data;
    }
