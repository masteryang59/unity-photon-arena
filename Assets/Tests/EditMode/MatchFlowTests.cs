using System;
using System.Linq;
using NUnit.Framework;
using PhotonArena.Core.Match;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Net.Client;
using PhotonArena.Core.Net.Transport;
using PhotonArena.Core.Sim;

namespace PhotonArena.Tests
{
    /// <summary>
    /// The master-client rules end to end: real server, real sessions, scripted players. Every test runs three
    /// times: with the server clock near zero, wrapping around int.MaxValue a few seconds in, and negative
    /// throughout, because "time 0 means unset" bugs only show up in the last two.
    /// </summary>
    [TestFixture(0)]
    [TestFixture(int.MaxValue - 15_000)]
    [TestFixture(int.MinValue + 1_000)]
    public class MatchFlowTests
    {
        readonly int _timeBase;
        TestNet _net;
        FrameSession _m, _s, _t;
        ArenaClient _mc, _sc, _tc;

        public MatchFlowTests(int timeBase) => _timeBase = timeBase;

        [SetUp]
        public void SetUp() => _net = new TestNet(serverTimeBase: _timeBase);

        [TearDown]
        public void TearDown() => _net.Dispose();

        static readonly MatchSpec Spec = new MatchSpec { Mode = GameModes.FreeForAll, Map = ArenaMaps.Pillars, MaxPlayers = 4, DurationSec = 60 };

        void ThreeInRoom(NetConditions conditions = null)
        {
            _m = _net.Connected("Master", conditions);
            _s = _net.Connected("Shooter", conditions);
            _t = _net.Connected("Target", conditions);
            _net.IntoRoom("R", Spec, _m, _s, _t);
            _mc = _net.Arena(_m);
            _sc = _net.Arena(_s);
            _tc = _net.Arena(_t);
            _net.Pump(100);
        }

        MatchPhase PhaseOf(FrameSession s) => (MatchPhase)s.Room.Props.GetInt(RoomKeys.Phase);

        void AllReady()
        {
            foreach (ArenaClient c in _net.Clients) c.SetReady(true);
        }

        void StartPlaying()
        {
            AllReady();
            _net.PumpUntil(() => PhaseOf(_m) == MatchPhase.Countdown, 500);
            _net.PumpUntil(() => _net.Sessions.All(s => s.Room == null || PhaseOf(s) == MatchPhase.Playing), ArenaRules.CountdownMs + 500);
            _net.Pump(50);
        }

        NetPlayer P(INetSession viewer, FrameSession who) => viewer.Room.GetPlayer(who.LocalActor);

        [Test]
        public void Countdown_StartsWhenEveryoneIsReady_AndIsCancelledByAnUnready()
        {
            ThreeInRoom();
            _mc.SetReady(true);
            _sc.SetReady(true);
            _net.Pump(200);
            Assert.That(PhaseOf(_s), Is.EqualTo(MatchPhase.Waiting), "one player isn't ready");
            _tc.SetReady(true);
            _net.PumpUntil(() => PhaseOf(_s) == MatchPhase.Countdown, 200);
            _tc.SetReady(false);
            _net.PumpUntil(() => PhaseOf(_s) == MatchPhase.Waiting, 200);
            _tc.SetReady(true);
            _net.PumpUntil(() => PhaseOf(_s) == MatchPhase.Countdown, 200);
            _net.PumpUntil(() => PhaseOf(_s) == MatchPhase.Playing, ArenaRules.CountdownMs + 200);
            Assert.That(_s.Room.IsOpen, Is.False, "matches in progress are closed to joiners");
            Assert.That(_s.Room.Props.GetInt(RoomKeys.Round), Is.EqualTo(1));
        }

        [Test]
        public void ASoloPlayer_CannotStartAMatch()
        {
            FrameSession a = _net.Connected("Alone");
            _net.IntoRoom("Solo", Spec, a);
            ArenaClient c = _net.Arena(a);
            c.SetReady(true);
            _net.Pump(1000);
            Assert.That(PhaseOf(a), Is.EqualTo(MatchPhase.Waiting));
        }

        [Test]
        public void TheMaster_HandsOutDistinctColors()
        {
            ThreeInRoom();
            int[] colors = _s.Room.Players.Values.Select(p => p.Props.GetInt(PlayerKeys.Color, -1)).ToArray();
            Assert.That(colors, Is.EquivalentTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void MatchStart_ResetsStatsAndTeleportsEveryoneToASpawn()
        {
            ThreeInRoom();
            _net.SetInput(_tc, () => new ArenaInput { Move = new Vec2(1, 1) });
            _net.Pump(500);
            Vec2 wandered = _tc.LocalPosition;
            _net.SetInput(_tc, () => ArenaInput.None);
            StartPlaying();
            NetPlayer t = P(_s, _t);
            Assert.That(t.Props.GetInt(PlayerKeys.Hp), Is.EqualTo(ArenaRules.MaxHp));
            Assert.That(t.Props.GetBool(PlayerKeys.Alive), Is.True);
            Assert.That(t.Props.GetInt(PlayerKeys.Kills), Is.EqualTo(0));
            Vec2 spawn = ArenaMaps.Get(ArenaMaps.Pillars).Spawns[t.Props.GetInt(PlayerKeys.Spawn)];
            Assert.That(_tc.LocalPosition, Is.EqualTo(spawn));
            Assert.That(wandered, Is.Not.EqualTo(spawn));
            var spawns = _s.Room.Players.Values.Select(p => p.Props.GetInt(PlayerKeys.Spawn)).ToArray();
            Assert.That(spawns.Distinct().Count(), Is.EqualTo(3), "nobody spawns on top of anybody");
        }

        [Test]
        public void ARealShot_AcrossTheNetwork_HitsAndIsConfirmedToEveryone()
        {
            ThreeInRoom(NetConditions.Of(40, 10));
            StartPlaying();
            // Shooter and target face each other along the top edge: move them into the same row first.
            Vec2 shooterSpot = new Vec2(20f, 14.5f), targetSpot = new Vec2(13f, 14.5f);
            WalkTo(_sc, shooterSpot);
            WalkTo(_tc, targetSpot);
            _net.Pump(300);
            int hits = 0;
            _tc.Hit += h => { if (h.Target == _t.LocalActor) hits++; };
            _net.SetInput(_sc, () => new ArenaInput { Aim = targetSpot - _sc.LocalPosition, Fire = true });
            _net.Pump(80);
            _net.SetInput(_sc, () => ArenaInput.None);
            _net.Pump(800);
            Assert.That(hits, Is.EqualTo(1));
            Assert.That(P(_m, _t).Props.GetInt(PlayerKeys.Hp), Is.EqualTo(ArenaRules.MaxHp - 1));
            Assert.That(P(_s, _t).Props.GetInt(PlayerKeys.Hp), Is.EqualTo(ArenaRules.MaxHp - 1));
        }

        void WalkTo(ArenaClient c, Vec2 spot)
        {
            _net.SetInput(c, () =>
            {
                Vec2 d = spot - c.LocalPosition;
                return new ArenaInput { Move = d.Length > 0.05f ? d.Normalized * Math.Min(1f, d.Length * 4) : Vec2.Zero };
            });
            _net.PumpUntil(() => Vec2.Distance(c.LocalPosition, spot) < 0.1f, 5000);
            _net.SetInput(c, () => ArenaInput.None);
        }

        // --- Lag compensation, driven directly so the timing is exact ---

        /// <summary>
        /// Feeds the master a position history for <paramref name="who"/>, standing at <paramref name="at"/> from
        /// t0 − 300 ms to t0 + 700 ms. It is stamped with a new life number, so the master treats the jump from the
        /// real position as a respawn teleport rather than a speed hack.
        /// </summary>
        void Inject(FrameSession who, int t0, Vec2 at)
        {
            int life = P(_m, who).Props.GetInt(PlayerKeys.Life) + 1;
            for (int dt = -300; dt <= 700; dt += 10)
            {
                _mc.Authority.RecordState(who.LocalActor, new SnapshotBuffer.Snapshot { Time = ServerClock.Add(t0, dt), Position = at, Alive = true, Life = (byte)life });
            }
        }

        /// <summary>
        /// The shooter at (20, 14) fires west along y = 14 at time t0, at a target standing at (16, 14). The bolt
        /// passes x = 16 about 183 ms after t0. If the master only checked the bolt from the moment it heard about
        /// the shot (later than that), the bolt would already be past the target.
        /// </summary>
        int LateShot(int reportDelayMs)
        {
            ThreeInRoom();
            StartPlaying();
            _net.Pump(ArenaRules.CountdownMs); // spawn protection is over
            // Freeze the real clients' own reports so only the injected history counts.
            _net.Clients.Remove(_sc);
            _net.Clients.Remove(_tc);
            int t0 = ServerClock.Add(_m.ServerTime, 400);
            Vec2 shooter = new Vec2(20f, 14f);
            Inject(_s, t0, shooter);
            Inject(_t, t0, new Vec2(16f, 14f));
            // Advance the master's clock to the moment the report arrives.
            _net.PumpUntil(() => ServerClock.Diff(_m.ServerTime, ServerClock.Add(t0, reportDelayMs)) >= 0, 2000, stepMs: 1);
            var shot = new GameMessages.FireMsg { ShotId = 1, Time = t0, Origin = GameMessages.Quantize(shooter + new Vec2(-ArenaRules.MuzzleOffset, 0)), Angle = (float)Math.PI };
            Assert.That(_mc.Authority.OnFire(_s.LocalActor, shot, ArenaMaps.Get(ArenaMaps.Pillars)), Is.True, "the shot is accepted");
            _net.Pump(600);
            return P(_m, _t).Props.GetInt(PlayerKeys.Hp);
        }

        [Test]
        public void ALateShot_IsJudgedAtTheTimeItWasFired()
        {
            Assert.That(LateShot(reportDelayMs: 240), Is.EqualTo(ArenaRules.MaxHp - 1));
        }

        [Test]
        public void Rewind_IsCapped_SoVeryOldShotsCantHitThePast()
        {
            Assert.That(LateShot(reportDelayMs: 600), Is.EqualTo(ArenaRules.MaxHp));
        }

        [Test]
        public void ShotsFromWhereTheShooterWasnt_OrTooFast_AreRejected()
        {
            ThreeInRoom();
            StartPlaying();
            _net.Clients.Remove(_sc);
            int now = _m.ServerTime;
            Inject(_s, now, new Vec2(20f, 14f));
            MasterAuthority auth = _mc.Authority;
            ArenaMap map = ArenaMaps.Get(ArenaMaps.Pillars);
            var farAway = new GameMessages.FireMsg { ShotId = 1, Time = now, Origin = new Vec2(5f, 14f), Angle = 0f };
            Assert.That(auth.OnFire(_s.LocalActor, farAway, map), Is.False, "origin 15 m from the shooter");

            var ok = new GameMessages.FireMsg { ShotId = 2, Time = now, Origin = new Vec2(19.3f, 14f), Angle = (float)Math.PI };
            Assert.That(auth.OnFire(_s.LocalActor, ok, map), Is.True);
            var spam = new GameMessages.FireMsg { ShotId = 3, Time = ServerClock.Add(now, 50), Origin = new Vec2(19.3f, 14f), Angle = (float)Math.PI };
            Assert.That(auth.OnFire(_s.LocalActor, spam, map), Is.False, "faster than the fire cooldown");
            _net.Pump(ArenaRules.FireCooldownMs);
            var later = new GameMessages.FireMsg { ShotId = 4, Time = _m.ServerTime, Origin = new Vec2(19.3f, 14f), Angle = (float)Math.PI };
            Assert.That(auth.OnFire(_s.LocalActor, later, map), Is.True);

            _net.Pump(ArenaRules.FireCooldownMs);
            var future = new GameMessages.FireMsg { ShotId = 5, Time = ServerClock.Add(_m.ServerTime, 5000), Origin = new Vec2(19.3f, 14f), Angle = (float)Math.PI };
            Assert.That(auth.OnFire(_s.LocalActor, future, map), Is.True, "accepted, but judged as fired now");
            var next = new GameMessages.FireMsg { ShotId = 6, Time = ServerClock.Add(_m.ServerTime, 100), Origin = new Vec2(19.3f, 14f), Angle = (float)Math.PI };
            Assert.That(auth.OnFire(_s.LocalActor, next, map), Is.False, "a future timestamp doesn't buy a cooldown head start");
        }

        [Test]
        public void ShotsOutsideAMatch_NeverCount()
        {
            ThreeInRoom();
            var msg = new GameMessages.FireMsg { ShotId = 1, Time = _m.ServerTime, Origin = new Vec2(2f, 2f), Angle = 0 };
            Assert.That(_mc.Authority.OnFire(_s.LocalActor, msg, ArenaMaps.Get(null)), Is.False);
            Assert.That(_sc.Authority.OnFire(_s.LocalActor, msg, ArenaMaps.Get(null)), Is.False, "only the master judges shots");
        }

        [Test]
        public void TeleportingFasterThanPossible_IsClampedAndCounted()
        {
            ThreeInRoom();
            MasterAuthority auth = _mc.Authority;
            int t = _m.ServerTime;
            auth.RecordState(9, new SnapshotBuffer.Snapshot { Time = t, Position = new Vec2(2, 2), Alive = true, Life = 1 });
            auth.RecordState(9, new SnapshotBuffer.Snapshot { Time = ServerClock.Add(t, 50), Position = new Vec2(20, 2), Alive = true, Life = 1 });
            Assert.That(auth.ViolationsOf(9), Is.EqualTo(1));
            // The same jump with a new life is a respawn, which is allowed.
            auth.RecordState(9, new SnapshotBuffer.Snapshot { Time = ServerClock.Add(t, 100), Position = new Vec2(2, 14), Alive = true, Life = 2 });
            Assert.That(auth.ViolationsOf(9), Is.EqualTo(1));
        }

        [Test]
        public void Kills_RespawnAfterTheDelay_AtTheSafestSpawn()
        {
            ThreeInRoom();
            StartPlaying();
            _net.Pump(ArenaRules.CountdownMs);
            for (int i = 0; i < ArenaRules.MaxHp; i++) ForceHit(_s, _t);
            NetPlayer t = P(_m, _t);
            Assert.That(t.Props.GetBool(PlayerKeys.Alive), Is.False);
            Assert.That(t.Props.GetInt(PlayerKeys.Deaths), Is.EqualTo(1));
            Assert.That(P(_m, _s).Props.GetInt(PlayerKeys.Kills), Is.EqualTo(1));
            int life = t.Props.GetInt(PlayerKeys.Life);
            ArenaFrame frame = _sc.BuildFrame();
            Assert.That(frame.Get(_t.LocalActor).Visible, Is.False, "dead players disappear");
            Assert.That(frame.Get(_t.LocalActor).RespawnInMs, Is.InRange(1, ArenaRules.RespawnMs));

            _net.Pump(ArenaRules.RespawnMs + 200);
            Assert.That(t.Props.GetBool(PlayerKeys.Alive), Is.True);
            Assert.That(t.Props.GetInt(PlayerKeys.Hp), Is.EqualTo(ArenaRules.MaxHp));
            Assert.That(t.Props.GetInt(PlayerKeys.Life), Is.EqualTo(life + 1));
            Vec2 spawn = ArenaMaps.Get(ArenaMaps.Pillars).Spawns[t.Props.GetInt(PlayerKeys.Spawn)];
            Assert.That(_tc.LocalPosition, Is.EqualTo(spawn), "the owner teleported");
        }

        /// <summary>
        /// Has the shooter hit the target with a bolt that can't miss: the master is told the shooter stood 1.2 m
        /// to the target's right and fired left. The shooter's own client is paused meanwhile so its real
        /// position reports don't interleave with the injected one.
        /// </summary>
        void ForceHit(FrameSession shooter, FrameSession target) => ForceHitBy(_mc, shooter, target);

        void ForceHitBy(ArenaClient master, FrameSession shooter, FrameSession target)
        {
            ArenaClient shooterClient = _net.Clients.First(c => c.Session == shooter);
            ArenaClient targetClient = _net.Clients.First(c => c.Session == target);
            _net.Clients.Remove(shooterClient);
            int fireTime = ServerClock.Add(master.Session.ServerTime, 1);
            Vec2 from = targetClient.LocalPosition + new Vec2(1.2f, 0);
            int life = P(master.Session, shooter).Props.GetInt(PlayerKeys.Life) + 1;
            master.Authority.RecordState(shooter.LocalActor, new SnapshotBuffer.Snapshot { Time = fireTime, Position = from, Alive = true, Life = (byte)life });
            var msg = new GameMessages.FireMsg { ShotId = (ushort)(fireTime & 0x7FFF), Time = fireTime, Origin = from + new Vec2(-ArenaRules.MuzzleOffset, 0), Angle = (float)Math.PI };
            Assert.That(master.Authority.OnFire(shooter.LocalActor, msg, ArenaMaps.Get(ArenaMaps.Pillars)), Is.True);
            int hp = P(master.Session, target).Props.GetInt(PlayerKeys.Hp);
            _net.PumpUntil(() => P(master.Session, target).Props.GetInt(PlayerKeys.Hp) < hp, 500);
            _net.Pump(ArenaRules.FireCooldownMs);
            _net.Clients.Add(shooterClient);
        }

        [Test]
        public void ReachingTheScoreLimit_EndsTheMatch_ThenTheRoomReopens()
        {
            ThreeInRoom();
            _m.SetRoomProperties(PropertyBag.Of(RoomKeys.ScoreLimit, 1));
            StartPlaying();
            _net.Pump(ArenaRules.CountdownMs);
            for (int i = 0; i < ArenaRules.MaxHp; i++) ForceHit(_s, _t);
            _net.PumpUntil(() => PhaseOf(_t) == MatchPhase.Results, 500);
            Assert.That(_t.Room.Props.GetInt(RoomKeys.Winner), Is.EqualTo(_s.LocalActor));
            Assert.That(_tc.BuildFrame().Winner, Is.EqualTo(_s.LocalActor));

            _net.PumpUntil(() => PhaseOf(_t) == MatchPhase.Waiting, ArenaRules.ResultsMs + 500);
            Assert.That(_t.Room.IsOpen, Is.True);
            Assert.That(_t.Room.Players.Values.All(p => !p.Props.GetBool(PlayerKeys.Ready)), Is.True, "everyone readies up again for the next round");
        }

        [Test]
        public void TimeRunningOut_EndsTheMatch_AndATieIsADraw()
        {
            ThreeInRoom();
            _m.SetRoomProperties(PropertyBag.Of(RoomKeys.DurationSec, 1));
            StartPlaying();
            _net.PumpUntil(() => PhaseOf(_s) == MatchPhase.Results, 1500);
            Assert.That(_s.Room.Props.GetInt(RoomKeys.Winner), Is.EqualTo(0), "nobody scored");
        }

        [Test]
        public void WhenTheMasterLeavesMidMatch_TheNextOneCarriesOn()
        {
            ThreeInRoom();
            StartPlaying();
            _net.Pump(ArenaRules.CountdownMs);
            ForceHit(_s, _t);
            _net.Clients.Remove(_mc);
            _m.Disconnect();
            _net.PumpUntil(() => _s.IsMaster, 500);
            Assert.That(PhaseOf(_s), Is.EqualTo(MatchPhase.Playing), "the match survives the hand-over");
            // The new master starts from the stored props, not from scratch.
            Assert.That(P(_s, _t).Props.GetInt(PlayerKeys.Hp), Is.EqualTo(ArenaRules.MaxHp - 1));
            Assert.That(_sc.Authority.IsActive, Is.True);
            Assert.That(_tc.Authority.IsActive, Is.False);
            // ...and can judge hits straight away (regression: fresh state used to read "protected until time 0").
            _net.Pump(ArenaRules.SpawnProtectionMs);
            ForceHitBy(_sc, _t, _s);
            Assert.That(P(_t, _s).Props.GetInt(PlayerKeys.Hp), Is.EqualTo(ArenaRules.MaxHp - 1));
            // Two players are still enough to keep playing; dropping to one ends the match.
            _t.Disconnect();
            _net.PumpUntil(() => PhaseOf(_s) == MatchPhase.Results, 500);
            Assert.That(_s.Room.Props.GetInt(RoomKeys.Winner), Is.EqualTo(_s.LocalActor), "last one standing");
        }

        [Test]
        public void HitEventsFromAnyoneButTheMaster_AreIgnored()
        {
            ThreeInRoom();
            StartPlaying();
            int hits = 0;
            _tc.Hit += _ => hits++;
            var forged = new GameMessages.HitMsg { Shooter = _s.LocalActor, ShotId = 1, Target = _t.LocalActor, HpAfter = 0, Killed = true };
            _s.Send(EventCodes.Hit, GameMessages.Write(forged), Target.Others, true);
            _net.Pump(50);
            Assert.That(hits, Is.EqualTo(0));
            Assert.That(P(_t, _t).Props.GetBool(PlayerKeys.Alive), Is.True);
        }

        [Test]
        public void GarbageGameEvents_DontBreakTheClient()
        {
            ThreeInRoom();
            _s.Send(EventCodes.State, new byte[] { 1, 2 }, Target.Others, false);
            _s.Send(EventCodes.Fire, new byte[0], Target.Others, true);
            Assert.DoesNotThrow(() => _net.Pump(50));
        }

        [Test]
        public void RemoteAvatars_AreDrawnInterpolated_CloseToTheTruth()
        {
            ThreeInRoom(NetConditions.Of(50, 20, 0.1));
            float t = 0;
            _net.SetInput(_tc, () =>
            {
                t += 0.005f;
                return new ArenaInput { Move = new Vec2((float)Math.Cos(t), (float)Math.Sin(t * 1.3f)) };
            });
            double worst = 0;
            for (int i = 0; i < 400; i++)
            {
                _net.Step();
                if (i < 100) continue;
                ArenaFrame frame = _sc.BuildFrame();
                AvatarView remote = frame.Get(_t.LocalActor);
                // Compare with where the target really was 100 ms ago: that's what interpolation shows.
                worst = Math.Max(worst, Vec2.Distance(remote.Position, _tc.LocalPosition));
            }
            // Drawn 100 ms behind at up to 6 m/s, plus latency jitter: within about a meter of "now".
            Assert.That(worst, Is.LessThan(ArenaRules.MoveSpeed * 0.2));
        }
    }
}
