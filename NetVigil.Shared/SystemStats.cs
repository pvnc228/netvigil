using System;
using System.Collections.Generic;

namespace NetVigil.Shared
{
    public class SystemStats
    {
        public int TotalDevices { get; set; }
        public int OnlineDevices { get; set; }
        public double TotalTrafficIn { get; set; }
        public double TotalTrafficOut { get; set; }
        public int AlertsCount { get; set; }
        public int AnomaliesLast24h { get; set; }
        public int CriticalDevices { get; set; }
        public DateTime SnapshotAt { get; set; } = DateTime.UtcNow;
    }

    public class TrafficPoint
    {
        public DateTime Timestamp { get; set; }
        public double Mbps { get; set; }
        public bool IsAnomalous { get; set; }
    }

    public class TrafficSample
    {
        public long Id { get; set; }
        public string DeviceMac { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public double Mbps { get; set; }
        public double AnomalyScore { get; set; }
        public bool IsAnomalous { get; set; }
    }

    public class AnomalyEvent
    {
        public long Id { get; set; }
        public string DeviceMac { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public double Mbps { get; set; }
        public double Score { get; set; }
        public RiskLevel Severity { get; set; }
        public string Description { get; set; } = string.Empty;
        public bool Acknowledged { get; set; }
    }

    public class DetectorStats
    {
        public string DetectorKind { get; set; } = "";

        public bool IsTrained { get; set; }

        public long SamplesSeen { get; set; }

        public int ReservoirFilled { get; set; }
        public int ReservoirCapacity { get; set; }

        public int? Trees { get; set; }
        public int? SubSampleSize { get; set; }
        public int? MaxDepth { get; set; }

        public DateTime? LastTrainedAt { get; set; }
        public long? LastTrainDurationMs { get; set; }
        public int RetrainIntervalSeconds { get; set; }

        public double? MbpsMin { get; set; }
        public double? MbpsMax { get; set; }

        public double SuspiciousThreshold { get; set; }
        public double AnomalousThreshold { get; set; }
        public double CriticalThreshold { get; set; }

        public int DevicesNormal { get; set; }
        public int DevicesSuspicious { get; set; }
        public int DevicesAnomalous { get; set; }
        public int DevicesCritical { get; set; }

        public DateTime SnapshotAt { get; set; } = DateTime.UtcNow;
    }
}
