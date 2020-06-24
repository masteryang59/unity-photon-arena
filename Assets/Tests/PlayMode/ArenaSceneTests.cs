using System.Collections;
using NUnit.Framework;
using PhotonArena.Core.Match;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PhotonArena.Tests.PlayMode
{
    /// <summary>
    /// Loads the real scene and plays through the offline backend: connect, create a room, add bots, ready up,
    /// and check that a match starts with everyone rendered. The core rules are covered by the EditMode tests.
    /// </summary>
    public class ArenaSceneTests
    {
        [UnityTest]
        public IEnumerator Offline_RoomWithBots_StartsAMatch()
        {
            yield return SceneManager.LoadSceneAsync("Arena");
            var app = Object.FindAnyObjectByType<ArenaApp>();
            Assert.That(app, Is.Not.Null);

            app.BackendId = NetBackends.Offline;
            app.Settings.OfflineLatencyMs = 30;
            app.Nickname = "Tester";
            app.Connect();
            yield return WaitFor(() => app.Session.State == NetState.InLobby, 5f);

            app.Spec = new MatchSpec { Mode = Core.Sim.GameModes.FreeForAll, Map = Core.Sim.ArenaMaps.Pillars, MaxPlayers = 4 };
            app.CreateRoom();
            yield return WaitFor(() => app.InRoom, 5f);
            Assert.That(app.Session.IsMaster, Is.True);

            app.AddBot();
            app.AddBot();
            yield return WaitFor(() => app.Session.Room.PlayerCount == 3, 5f);
            app.ToggleReady();
            yield return WaitFor(() => app.Frame.Phase == MatchPhase.Playing, 10f);

            Assert.That(app.Frame.Avatars.Count, Is.EqualTo(3));
            Assert.That(GameObject.Find("Avatar 2"), Is.Not.Null, "bots are drawn");
            Assert.That(app.Session.Room.IsOpen, Is.False);

            app.LeaveRoom();
            yield return WaitFor(() => app.Session.State == NetState.InLobby, 5f);
            Assert.That(app.Bots.Count, Is.EqualTo(0));
            app.Disconnect();
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > end) Assert.Fail($"timed out after {seconds} s");
                yield return null;
            }
        }
    }
}
