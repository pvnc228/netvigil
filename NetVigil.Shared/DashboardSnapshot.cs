using System;
using System.Collections.Generic;

namespace NetVigil.Shared
{
    public class DashboardSnapshot
    {
        public SystemStats Stats { get; set; } = new();
        public List<NetworkDevice> Devices { get; set; } = new();
        public List<AnomalyEvent> Anomalies { get; set; } = new();
        public List<TrafficPoint> Traffic { get; set; } = new();
        public List<AgentInfoSnapshot> Agents { get; set; } = new();
        public DateTime SnapshotAt { get; set; }
    }
}
