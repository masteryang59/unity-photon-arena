using System;
using System.Collections.Generic;
using System.Net;
using PhotonArena.Core.Net;
using PhotonArena.Core.Net.Client;
using PhotonArena.Core.Net.Server;
using PhotonArena.Core.Net.Transport;

namespace PhotonArena
{
    /// <summary>Settings the lobby screen collects before connecting.</summary>
    [Serializable]
    public sealed class BackendSettings
    {
        public string LanHost = "127.0.0.1";
        public int LanPort = RelayServer.DefaultPort;
        /// <summary>LAN: run the relay inside this game instance so others can join it.</summary>
        public bool HostLan;
        /// <summary>Offline: simulated one-way latency, so the netcode is visible even on one machine.</summary>
        public int OfflineLatencyMs = 40;
    }

    /// <summary>
    /// A connected networking backend: the player's session, plus (where the backend allows it) a way to add
    /// bots, which are extra network clients in this process.
    /// </summary>
    public sealed class Backend : IDisposable
    {
        readonly Action _tick;
        readonly Func<INetSession> _botSession;
        readonly IDisposable _owned;

        public Backend(string id, INetSession session, Action tick = null, Func<INetSession> botSession = null, IDisposable owned = null, string note = null)
        {
            Id = id;
            Session = session;
            _tick = tick;
            _botSession = botSession;
            _owned = owned;
            Note = note;
        }

        public string Id { get; }
        public INetSession Session { get; }
        /// <summary>Extra status for the HUD, e.g. "hosting on 192.168.1.20:7777".</summary>
        public string Note { get; }
        public bool SupportsBots => _botSession != null;

        /// <summary>Runs in-process servers; call once per frame before servicing sessions.</summary>
        public void Tick() => _tick?.Invoke();

        public INetSession CreateBotSession() => _botSession?.Invoke();

        public void Dispose()
        {
            Session.Dispose();
            _owned?.Dispose();
        }
    }

    /// <summary>
    /// The backends the lobby can pick from. Offline and LAN are built in; the Photon assembly (compiled only when
    /// PUN 2 is installed) registers "photon" at startup.
    /// </summary>
    public static class NetBackends
    {
        public const string Offline = "offline";
        public const string Lan = "lan";
        public const string Photon = "photon";

        public sealed class Entry
        {
            public string Id;
            public string DisplayName;
            public Func<BackendSettings, Backend> Create;
            /// <summary>Null when usable; otherwise why not (shown greyed out in the lobby).</summary>
            public Func<string> Unavailable;
        }

        static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        static NetBackends()
        {
            Register(new Entry { Id = Offline, DisplayName = "Offline + bots", Create = CreateOffline });
            Register(new Entry { Id = Lan, DisplayName = "LAN relay", Create = CreateLan });
            Register(new Entry
            {
                Id = Photon,
                DisplayName = "Photon Cloud",
                Create = _ => throw new InvalidOperationException("PUN 2 is not installed."),
                Unavailable = () => "Import PUN 2 from the Asset Store to enable Photon (see README).",
            });
        }

        public static void Register(Entry entry) => Entries[entry.Id] = entry;

        public static Entry Get(string id) => id != null && Entries.TryGetValue(id, out Entry e) ? e : Entries[Offline];

        public static IEnumerable<Entry> All => new[] { Get(Offline), Get(Lan), Get(Photon) };

        static Backend CreateOffline(BackendSettings settings)
        {
            var clock = new StopwatchClock();
            var server = new MatchServer(clock, new MatchServerOptions { LobbyUpdateIntervalMs = 250 });
            var conditions = NetConditions.Of(settings.OfflineLatencyMs, settings.OfflineLatencyMs / 4.0, settings.OfflineLatencyMs > 0 ? 0.01 : 0);
            int seed = 1;
            INetSession NewSession() => new FrameSession(new LoopbackConnector(server, clock, conditions, seed++), clock, Offline);
            return new Backend(Offline, NewSession(), server.Tick, NewSession,
                note: settings.OfflineLatencyMs > 0 ? $"simulated {settings.OfflineLatencyMs * 2} ms round trip" : "no simulated latency");
        }

        static Backend CreateLan(BackendSettings settings)
        {
            var clock = new StopwatchClock();
            if (settings.HostLan)
            {
                var relay = new RelayServer(settings.LanPort, IPAddress.Any, null, msg => UnityEngine.Debug.Log("[relay] " + msg));
                INetSession Local() => new FrameSession(new TcpConnector("127.0.0.1", relay.Port), clock, Lan);
                return new Backend(Lan, Local(), null, Local, relay, $"hosting on {LocalAddress()}:{relay.Port}");
            }
            INetSession Remote() => new FrameSession(new TcpConnector(settings.LanHost, settings.LanPort), clock, Lan);
            return new Backend(Lan, Remote(), null, Remote, note: $"relay {settings.LanHost}:{settings.LanPort}");
        }

        /// <summary>A LAN address others can type in, best effort.</summary>
        public static string LocalAddress()
        {
            try
            {
                foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)) return address.ToString();
                }
            }
            catch (System.Net.Sockets.SocketException)
            {
                // No resolvable host name; fall through.
            }
            return "127.0.0.1";
        }
    }
}


    public static object ExtractText(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object ExtractText(object data = null)
    {
        if (data == null) return null;
        return data;
    }