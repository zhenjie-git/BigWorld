using System;

namespace BigWorldClient.Network
{

    public sealed class ServerClock
    {
        public const long TickMs = 20;

        private readonly object _lock = new object();
        private long _bestRttMs = long.MaxValue;
        private double _offsetMs;
        private long _lastRttMs;

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
                _offsetMs = 0;
                _lastRttMs = 0;
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
                }
            }
        }

        public long NowMs()
        {
            lock (_lock)
            {
                return _bestRttMs == long.MaxValue
                    ? LocalNowMs()
                    : (long)(LocalNowMs() + _offsetMs);
            }
        }

        public long TickNow()
        {
            long ms = NowMs();
            return ms >= 0 ? ms / TickMs : 0;
        }

        public static long TickToMs(long tick) => tick * TickMs;

        public void LearnServerTick(long serverTick, long receiveLocalMs)
        {
            if (serverTick <= 0) return;
            double observedOffset = (double)TickToMs(serverTick) - receiveLocalMs;

            lock (_lock)
            {
                if (_bestRttMs == long.MaxValue)
                {
                    _bestRttMs = 0;
                    _offsetMs = observedOffset;
                    return;
                }

                double delta = observedOffset - _offsetMs;
                if (delta > 500.0) delta = 500.0;
                if (delta < -500.0) delta = -500.0;
                _offsetMs += delta * 0.25;
            }
        }

        public static long LocalNowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    }
}
