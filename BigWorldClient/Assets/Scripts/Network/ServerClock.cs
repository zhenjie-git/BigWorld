using System;

namespace BigWorldClient.Network
{
    /// <summary>
    /// Estimates the server's wall-clock time (Unix ms) from heartbeat samples,
    /// without any system-level clock sync (NTP). Each heartbeat round trip yields
    ///     offset = server_time_ms + rtt/2 - receive_ms
    /// and the sample with the lowest RTT is kept — low RTT means the packet spent
    /// the least time queued in the network, so its midpoint estimate is the least
    /// corrupted by one-way latency asymmetry.
    ///
    /// Thread-safe: fed on the session poll thread (GameSession.Loop), read from the
    /// Unity main thread via NowMs(). Use the estimated server time for anything that
    /// must be server-authoritative (cooldowns, buff expiry, activity windows); the
    /// server still validates with its own clock — never trust the client's estimate.
    /// </summary>
    public sealed class ServerClock
    {
        private readonly object _lock = new object();
        private long _bestRttMs = long.MaxValue; // sentinel: not synced yet
        private double _offsetMs;                // server - local, in ms
        private long _lastRttMs;

        /// <summary>True once at least one heartbeat reply has been processed.</summary>
        public bool IsSynced
        {
            get { lock (_lock) return _bestRttMs != long.MaxValue; }
        }

        /// <summary>Most recent round-trip time, in ms (0 before the first sync).</summary>
        public long LastRttMs
        {
            get { lock (_lock) return _lastRttMs; }
        }

        /// <summary>Lowest RTT observed, in ms — the sample that set the current offset.</summary>
        public long BestRttMs
        {
            get { lock (_lock) return _bestRttMs == long.MaxValue ? 0 : _bestRttMs; }
        }

        /// <summary>Estimated clock offset (server - local) in ms.</summary>
        public double OffsetMs
        {
            get { lock (_lock) return _offsetMs; }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _bestRttMs = long.MaxValue;
                _offsetMs = 0;
                _lastRttMs = 0;
            }
        }

        /// <summary>Feed one heartbeat sample. sendMs/receiveMs are local time (Unix ms),
        /// serverTimeMs is the timestamp the gateway stamped into the reply.</summary>
        public void Sync(long serverTimeMs, long sendMs, long receiveMs)
        {
            long rtt = receiveMs - sendMs;
            if (rtt < 0) rtt = 0;
            double offset = serverTimeMs + rtt / 2.0 - receiveMs;

            lock (_lock)
            {
                _lastRttMs = rtt;
                if (rtt < _bestRttMs)
                {
                    _bestRttMs = rtt;
                    _offsetMs = offset;
                }
            }
        }

        /// <summary>Estimated server epoch time in ms. Falls back to local time if the
        /// clock is not yet synced (first reply still pending).</summary>
        public long NowMs()
        {
            lock (_lock)
            {
                return _bestRttMs == long.MaxValue
                    ? LocalNowMs()
                    : (long)(LocalNowMs() + _offsetMs);
            }
        }

        public static long LocalNowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    }
}
