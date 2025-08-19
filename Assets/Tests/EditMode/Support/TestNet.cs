using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PhotonArena.Core.Match;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Net.Client;
using PhotonArena.Core.Net.Server;
using PhotonArena.Core.Net.Transport;

namespace PhotonArena.Tests
{
    /// <summary>Records every callback a session raises, in order.</summary>
    public sealed class Recorder : NetCallbacks
    {
        public readonly List<string> Log = new List<string>();
        public readonly List<RoomListing> RoomUpdates = new List<RoomListing>();
        public readonly List<(byte code, int sender, byte[] payload)> Events = new List<(byte, int, byte[])>();
        public readonly List<(JoinKind kind, short code)> JoinFailures = new List<(JoinKind, short)>();
        public readonly List<string> Rejections = new List<string>();
        public string DisconnectCause;

        public override void OnConnected() => Log.Add("connected");
        public override void OnDisconnected(string cause) { DisconnectCause = cause; Log.Add("disconnected"); }
        public override void OnJoinedLobby() => Log.Add("lobby");
        public override void OnRoomListUpdate(IReadOnlyList<RoomListing> changed) { RoomUpdates.AddRange(changed); Log.Add("roomlist"); }
        public override void OnJoinedRoom() => Log.Add("joined");
        public override void OnJoinFailed(JoinKind kind, short code, string message) { JoinFailures.Add((kind, code)); Log.Add($"joinfailed:{code}"); }
        public override void OnLeftRoom() => Log.Add("left");
        public override void OnPlayerEntered(NetPlayer player) => Log.Add($"entered:{player.Actor}");
        public override void OnPlayerLeft(NetPlayer player) => Log.Add($"playerleft:{player.Actor}");
        public override void OnRoomPropertiesChanged(PropertyBag changed) => Log.Add("roomprops");
        public override void OnPlayerPropertiesChanged(NetPlayer player, PropertyBag changed) => Log.Add($"playerprops:{player.Actor}");
        public override void OnMasterChanged(int newMasterActor) => Log.Add($"master:{newMasterActor}");
        public override void OnPropertiesRejected(string reason) { Rejections.Add(reason); Log.Add("rejected"); }
        public override void OnEvent(byte code, int senderActor, byte[] payload) => Events.Add((code, senderActor, payload));

        public bool Saw(string entry) => Log.Contains(entry);
    }

    /// <summary>An in-process server and clients on a manual clock: deterministic multiplayer in a unit test.</summary>
    public sealed class TestNet : IDisposable
    {
        public readonly ManualClock Clock = new ManualClock(10_000);
        public readonly MatchServer Server;
        public readonly List<FrameSession> Sessions = new List<FrameSession>();
        public readonly Dictionary<FrameSession, Recorder> Recorders = new Dictionary<FrameSession, Recorder>();
        public readonly List<ArenaClient> Clients = new List<ArenaClient>();
        readonly Dictionary<ArenaClient, Func<ArenaInput>> _inputs = new Dictionary<ArenaClient, Func<ArenaInput>>();
        int _seed = 1;

        public TestNet(int? serverTimeBase = 0, int lobbyIntervalMs = 100)
        {
            Server = new MatchServer(Clock, new MatchServerOptions { ServerTimeBase = serverTimeBase, LobbyUpdateIntervalMs = lobbyIntervalMs });
        }

        public FrameSession Add(NetConditions conditions = null)
        {
            var session = new FrameSession(new LoopbackConnector(Server, Clock, conditions ?? NetConditions.Perfect, _seed++), Clock, "loopback");
            var recorder = new Recorder();
            session.AddCallbacks(recorder);
            Sessions.Add(session);
            Recorders[session] = recorder;
            return session;
        }

        public Recorder Rec(FrameSession s) => Recorders[s];

        /// <summary>A connected session, already in the lobby.</summary>
        public FrameSession Connected(string nickname, NetConditions conditions = null)
        {
            FrameSession s = Add(conditions);
            s.Connect(nickname);
            PumpUntil(() => s.State == NetState.Connected, 2000);
            s.JoinLobby();
            PumpUntil(() => s.State == NetState.InLobby, 2000);
            return s;
        }

        public ArenaClient Arena(FrameSession s, Func<ArenaInput> input = null)
        {
            var client = new ArenaClient(s);
            Clients.Add(client);
            _inputs[client] = input ?? (() => ArenaInput.None);
            return client;
        }

        public void SetInput(ArenaClient client, Func<ArenaInput> input) => _inputs[client] = input;

        /// <summary>Advances time in small steps, ticking the server, every session and every arena client.</summary>
        public void Pump(double ms, double stepMs = 5)
        {
            for (double t = 0; t < ms; t += stepMs)
            {
                Step(stepMs);
            }
        }

        public void Step(double stepMs = 5)
        {
            Server.Tick();
            foreach (FrameSession s in Sessions) s.Service();
            foreach (ArenaClient c in Clients)
            {
                if (c.Session.State == NetState.InRoom) c.Tick((float)(stepMs / 1000.0), _inputs[c]());
            }
            Clock.Advance(stepMs);
        }

        public void PumpUntil(Func<bool> condition, double maxMs, double stepMs = 5)
        {
            for (double t = 0; t <= maxMs; t += stepMs)
            {
                if (condition()) return;
                Step(stepMs);
            }
            Assert.That(condition(), Is.True, $"condition not met within {maxMs} ms");
        }

        /// <summary>Creates a room with the first session and joins the rest into it.</summary>
        public void IntoRoom(string name, MatchSpec spec, params FrameSession[] sessions)
        {
            sessions[0].CreateRoom(name, Matchmaker.BuildRoomOptions(spec, "1"));
            PumpUntil(() => sessions[0].State == NetState.InRoom, 2000);
            foreach (FrameSession s in sessions.Skip(1))
            {
                s.JoinRoom(name);
                PumpUntil(() => s.State == NetState.InRoom, 2000);
            }
            PumpUntil(() => sessions.All(s => s.Room.PlayerCount == sessions.Length), 2000);
        }

        public void Dispose()
        {
            foreach (ArenaClient c in Clients) c.Dispose();
            foreach (FrameSession s in Sessions) s.Dispose();
        }
    }
}

    public static object DestroySession(object data = null)
    {
        if (data == null) return null;
        return data;
    }