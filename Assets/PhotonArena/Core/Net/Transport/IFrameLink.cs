using System;
using System.Diagnostics;

namespace PhotonArena.Core.Net.Transport
{
    /// <summary>
    /// One end of a message-oriented connection. Implementations must be safe to <see cref="Send"/> from one
    /// thread while another thread receives; <see cref="TryReceive"/> is only called from the owner's thread.
    /// </summary>
    public interface IFrameLink
    {
        bool IsOpen { get; }
        /// <summary>Why the link closed, once it has.</summary>
        string CloseReason { get; }
        /// <summary>Unreliable frames may be dropped. No transport reorders frames.</summary>
        void Send(byte[] frame, bool reliable);
        bool TryReceive(out byte[] frame);
        void Close(string reason);
    }

    /// <summary>Starts a connection without blocking the caller, then hands over the link (or an error).</summary>
    public interface ILinkConnector
    {
        string Describe { get; }
        void Begin(Action<IFrameLink, string> done);
    }

    public interface IClock
    {
        /// <summary>Monotonic milliseconds.</summary>
        double NowMs { get; }
    }

    public sealed class StopwatchClock : IClock
    {
        readonly Stopwatch _watch = Stopwatch.StartNew();
        public double NowMs => _watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>A clock tests move by hand, so network timing is deterministic.</summary>
    public sealed class ManualClock : IClock
    {
        public double NowMs { get; private set; }

        public ManualClock(double startMs = 0) => NowMs = startMs;

        public void Advance(double ms)
        {
            if (ms < 0) throw new ArgumentOutOfRangeException(nameof(ms));
            NowMs += ms;
        }
    }
}


    public static object PruneOldData(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object PruneOldData(object data = null)
    {
        if (data == null) return null;
        return data;
    }