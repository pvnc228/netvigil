using NetVigil.Shared;

namespace NetVigil.Server.Services.Anomaly
{
    public interface IAnomalyDetector
    {
        AnomalyResult Score(string deviceMac, double mbps, DateTime timestamp);
        void Observe(string deviceMac, double mbps, DateTime timestamp);
        string Name { get; }

        DetectorStats GetStats();
    }

    public readonly record struct AnomalyResult(double Score, bool IsAnomalous, RiskLevel Severity, string Reason);
}
