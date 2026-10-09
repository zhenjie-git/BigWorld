using System;

namespace BigWorldClient.Network
{

    public sealed class ServerClock
    {
        public const long TickMs = 20;

        private readonly object _lock = new object();
        private long _bestRttMs = long.MaxValue;
        private long _lastRttMs;
        private bool _offsetSeeded;
        private double _offsetMs;

        public bool IsSynced
        {
            get { lock (_lock) return _bestRttMs != long.MaxValue; }
        }

        public long LastRttMs
        {
            get { lock (_lock) return _lastRttMs; }
        }

        public long BestRttMs
        {
            get { lock (_lock) return _bestRttMs == long.MaxValue ? 0 : _bestRttMs; }
        }

        public double OffsetMs
        {
            get { lock (_lock) return _offsetMs; }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _bestRttMs = long.MaxValue;
                _lastRttMs = 0;
                _offsetSeeded = false;
                _offsetMs = 0;
            }
        }

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
                    _offsetSeeded = true;
                }
            }
        }

        public long NowMs()
        {
            lock (_lock)
                return _offsetSeeded ? (long)(LocalNowMs() + _offsetMs) : LocalNowMs();
        }

        public long TickNow()
        {
            long ms = NowMs();
            return ms >= 0 ? ms / TickMs : 0;
        }

        public static long MsToTick(long ms) => ms >= 0 ? ms / TickMs : 0;

        public static long TickToMs(long tick) => tick * TickMs;

        public static long LocalNowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    }
}
