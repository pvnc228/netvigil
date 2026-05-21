using System;

namespace NetVigil.Shared
{
    public class NetworkDevice
    {
        public string MacAddress { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Hostname { get; set; } = "Unknown";
        public string Vendor { get; set; } = "Generic";
        public bool IsOnline { get; set; }
        public DateTime LastSeen { get; set; }
        public DateTime FirstSeen { get; set; }
        public double CurrentTrafficMbps { get; set; }
        public string Type { get; set; } = "Device";

        public double AnomalyScore { get; set; }
        public RiskLevel RiskLevel { get; set; } = RiskLevel.Normal;
        public DateTime? LastAnomalyAt { get; set; }
        public int AnomalyCount24h { get; set; }

        public bool IsFlagged { get; set; }
        public DateTime? FlaggedAt { get; set; }
        public string? FlaggedBy { get; set; }
    }

    public enum RiskLevel
    {
        Normal = 0,
        Suspicious = 1,
        Anomalous = 2,
        Critical = 3
    }
}
