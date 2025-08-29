using System;
using System.Collections.Generic;
using NUnit.Framework;
using PhotonArena.Core.Net;
using PhotonArena.Core.Net.Client;
using PhotonArena.Core.Net.Transport;

namespace PhotonArena.Tests
{
    public class ServerClockTests
    {
        [Test]
        public void Diff_IsCorrectAcrossTheIntWrap()
        {
            int before = int.MaxValue - 10;
            int after = ServerClock.Add(before, 25);
            Assert.That(after, Is.LessThan(0), "wrapped");
            Assert.That(ServerClock.Diff(after, before), Is.EqualTo(25));
            Assert.That(ServerClock.Diff(before, after), Is.EqualTo(-25));
            Assert.That(ServerClock.IsAfterOrAt(after, before), Is.True);
        }

        [Test]
        public void ClockSync_UsesTheLowestRoundTripSample()
        {
            var sync = new ClockSync();
            const double trueOffset = 5000; // server = local + 5000
            sync.Seed((int)(100 + trueOffset), 100);
            // Symmetric 40 ms RTT: the server stamped its time halfway through.
            sync.AddSample(1000, 1040, (int)(1020 + trueOffset));
            // A sample delayed by queuing on the way back (RTT 200, stamped early) must not win.
            sync.AddSample(2000, 2200, (int)(2020 + trueOffset));
            Assert.That(sync.ServerTimeAt(3000), Is.EqualTo((int)(3000 + trueOffset)).Within(1));
            Assert.That(sync.RoundTripMs, Is.InRange(40, 200));
        }

        [Test]
        public void ClockSync_StaysContinuousWhenTheServerClockWraps()
        {
            var sync = new ClockSync();
            int baseServer = int.MaxValue - 500;
            sync.Seed(baseServer, 0);
            for (int i = 1; i <= 10; i++)
            {
                double sent = i * 100.0, received = sent + 20;
                sync.AddSample(sent, received, ServerClock.Add(baseServer, (int)(sent + 10)));
            }
            // 1100 ms after the seed the server clock has wrapped; the estimate must have wrapped with it.
            int estimate = sync.ServerTimeAt(1100);
            Assert.That(ServerClock.Diff(estimate, ServerClock.Add(baseServer, 1100)), Is.InRange(-1, 1));
        }
    }

    public class LoopbackLinkTests
    {
        [Test]
        public void Frames_ArriveAfterLatency_InOrder()
        {
            var clock = new ManualClock();
            var (a, b) = LoopbackLink.CreatePair(clock, NetConditions.Of(50, 30), NetConditions.Perfect, seed: 3);
            for (byte i = 0; i < 20; i++) a.Send(new[] { i }, true);
            Assert.That(b.TryReceive(out _), Is.False, "nothing before the latency");
            var got = new List<byte>();
            for (int ms = 0; ms < 200; ms++)
            {
                clock.Advance(1);
                while (b.TryReceive(out byte[] f)) got.Add(f[0]);
            }
            Assert.That(got.Count, Is.EqualTo(20));
            for (int i = 0; i < 20; i++) Assert.That(got[i], Is.EqualTo(i), "jitter must never reorder");
        }

        [Test]
        public void Loss_OnlyDropsUnreliableFrames()
        {
            var clock = new ManualClock();
            var (a, b) = LoopbackLink.CreatePair(clock, NetConditions.Of(0, 0, 0.5), NetConditions.Perfect, seed: 11);
            for (int i = 0; i < 400; i++) a.Send(new byte[] { 1 }, reliable: i % 2 == 0);
            int received = 0;
            while (b.TryReceive(out _)) received++;
            Assert.That(a.Dropped, Is.InRange(70, 130), "about half of the 200 unreliable frames");
            Assert.That(received, Is.EqualTo(400 - a.Dropped));
        }

        [Test]
        public void Close_StillDeliversFramesInFlight_ThenReportsClosed()
        {
            var clock = new ManualClock();
            var (a, b) = LoopbackLink.CreatePair(clock, NetConditions.Of(20), NetConditions.Perfect);
            a.Send(new byte[] { 9 }, true);
            a.Close("bye");
            Assert.That(b.IsOpen, Is.True, "a frame is still on its way");
            clock.Advance(25);
            Assert.That(b.TryReceive(out byte[] f), Is.True);
            Assert.That(f[0], Is.EqualTo(9));
            Assert.That(b.IsOpen, Is.False);
            Assert.That(b.CloseReason, Does.Contain("bye"));
            b.Send(new byte[] { 1 }, true); // no throw after close
        }
    }
}

    public static object ValidateRequest(object data = null)
    {
        if (data == null) return null;
        return data;
    }