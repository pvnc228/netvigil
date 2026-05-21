using System.Collections.Concurrent;
using NetVigil.Shared;

namespace NetVigil.Server.Services.Anomaly
{
    public class RollingZScoreDetector : IAnomalyDetector
    {
        public string Name => "rolling-zscore";

        private readonly int _window;
        private readonly double _suspiciousZ;
        private readonly double _anomalousZ;
        private readonly double _criticalZ;
        private readonly double _absoluteFloorMbps;

        private readonly ConcurrentDictionary<string, DeviceWindow> _windows = new();
        private long _samplesSeen;

        public RollingZScoreDetector(
            int window = 60,
            double suspiciousZ = 2.0,
            double anomalousZ = 3.0,
            double criticalZ = 4.5,
            double absoluteFloorMbps = 1.0)
        {
            _window = window;
            _suspiciousZ = suspiciousZ;
            _anomalousZ = anomalousZ;
            _criticalZ = criticalZ;
            _absoluteFloorMbps = absoluteFloorMbps;
        }

        public void Observe(string deviceMac, double mbps, DateTime timestamp)
        {
            var w = _windows.GetOrAdd(deviceMac, _ => new DeviceWindow(_window));
            w.Add(mbps);
            Interlocked.Increment(ref _samplesSeen);
        }

        public DetectorStats GetStats()
        {
            int trained = 0;
            int totalDevices = 0;
            foreach (var kv in _windows)
            {
                totalDevices++;
                if (kv.Value.Count >= 10) trained++;
            }

            return new DetectorStats
            {
                DetectorKind          = Name,
                IsTrained             = trained > 0,
                SamplesSeen           = Interlocked.Read(ref _samplesSeen),
                ReservoirFilled       = trained,
                ReservoirCapacity     = totalDevices,
                Trees                 = null,
                SubSampleSize         = null,
                MaxDepth              = null,
                LastTrainedAt         = null,
                LastTrainDurationMs   = null,
                RetrainIntervalSeconds = 0,
                MbpsMin               = null,
                MbpsMax               = null,
                SuspiciousThreshold   = _suspiciousZ,
                AnomalousThreshold    = _anomalousZ,
                CriticalThreshold     = _criticalZ
            };
        }

        public AnomalyResult Score(string deviceMac, double mbps, DateTime timestamp)
        {
            if (!_windows.TryGetValue(deviceMac, out var w) || w.Count < 10)
            {
                return new AnomalyResult(0, false, RiskLevel.Normal, "warmup");
            }

            var (mean, std) = w.MeanStd();
            if (std < _absoluteFloorMbps) std = _absoluteFloorMbps;

            var z = Math.Abs((mbps - mean) / std);

            var severity = z switch
            {
                var v when v >= _criticalZ   => RiskLevel.Critical,
                var v when v >= _anomalousZ  => RiskLevel.Anomalous,
                var v when v >= _suspiciousZ => RiskLevel.Suspicious,
                _                            => RiskLevel.Normal
            };

            var isAnomalous = severity >= RiskLevel.Anomalous;
            var reason = isAnomalous
                ? $"z={z:F2} (mean={mean:F1} Mbps, std={std:F1})"
                : "ok";

            return new AnomalyResult(Math.Round(z, 3), isAnomalous, severity, reason);
        }

        private sealed class DeviceWindow
        {
            private readonly Queue<double> _q;
            private readonly int _capacity;
            private double _sum;
            private double _sumSq;

            public DeviceWindow(int capacity)
            {
                _capacity = capacity;
                _q = new Queue<double>(capacity);
            }

            public int Count => _q.Count;

            public void Add(double v)
            {
                _q.Enqueue(v);
                _sum += v;
                _sumSq += v * v;

                if (_q.Count > _capacity)
                {
                    var old = _q.Dequeue();
                    _sum -= old;
                    _sumSq -= old * old;
                }
            }

            public (double mean, double std) MeanStd()
            {
                int n = _q.Count;
                if (n == 0) return (0, 0);
                var mean = _sum / n;
                var variance = Math.Max(0, (_sumSq / n) - mean * mean);
                return (mean, Math.Sqrt(variance));
            }
        }
    }
}
