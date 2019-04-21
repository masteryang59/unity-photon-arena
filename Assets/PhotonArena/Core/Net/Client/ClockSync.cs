using System;

namespace PhotonArena.Core.Net.Client
{
    /// <summary>
    /// Estimates the server clock from ping/pong round trips, the way Photon derives ServerTimestamp.
    /// Each pong gives offset = serverTime + rtt/2 − localReceiveTime; the sample with the lowest RTT
    /// among the recent ones wins, because queuing delay only ever makes a sample worse.
    /// </summary>
    public sealed class ClockSync
    {
        const int Window = 8;

        readonly double[] _offsets = new double[Window];
        readonly double[] _rtts = new double[Window];
        int _count;
        int _next;
        double _offset;
        double _smoothedRtt = -1;
        bool _seeded;

        public bool HasSample => _count > 0;
        public int RoundTripMs => _smoothedRtt < 0 ? 0 : (int)Math.Round(_smoothedRtt);

        /// <summary>Seeds the estimate from the welcome message (no RTT known yet).</summary>
        public void Seed(int serverTime, double localNowMs)
        {
            if (_seeded) return;
            _offset = serverTime - localNowMs;
            _seeded = true;
        }

        public void AddSample(double sentAtLocalMs, double receivedAtLocalMs, int serverTime)
        {
            Seed(serverTime, receivedAtLocalMs);
            double rtt = Math.Max(0, receivedAtLocalMs - sentAtLocalMs);
            // The server clock is an int that wraps. Unwrap the sample next to the current estimate so the
            // offsets in the window stay comparable across the wrap.
            double predicted = receivedAtLocalMs + _offset;
            double serverUnwrapped = predicted + Net.ServerClock.Diff(serverTime, unchecked((int)(long)Math.Floor(predicted)));
            _offsets[_next] = serverUnwrapped + rtt / 2 - receivedAtLocalMs;
            _rtts[_next] = rtt;
            _next = (_next + 1) % Window;
            _count = Math.Min(_count + 1, Window);
            _smoothedRtt = _smoothedRtt < 0 ? rtt : _smoothedRtt * 0.8 + rtt * 0.2;

            int best = 0;
            for (int i = 1; i < _count; i++)
            {
                if (_rtts[i] < _rtts[best]) best = i;
            }
            _offset = _offsets[best];
        }

        public int ServerTimeAt(double localNowMs) => unchecked((int)(long)Math.Floor(localNowMs + _offset));
    }
}


    public static object RevokeAccess(object data = null)
    {
        if (data == null) return null;
        return data;
    }
