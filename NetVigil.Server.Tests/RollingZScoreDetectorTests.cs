using NetVigil.Server.Services.Anomaly;
using NetVigil.Shared;
using Xunit;

namespace NetVigil.Server.Tests;

public class RollingZScoreDetectorTests
{
    private const string Mac = "AA:BB:CC:11:22:33";

    private static RollingZScoreDetector NewDetector()
        => new(window: 60, suspiciousZ: 2.0, anomalousZ: 3.0, criticalZ: 4.5, absoluteFloorMbps: 1.0);

    [Fact]
    public void Returns_warmup_until_ten_samples_observed()
    {
        var det = NewDetector();
        var t = DateTime.UtcNow;

        for (int i = 0; i < 9; i++) det.Observe(Mac, 1.0, t);

        var r = det.Score(Mac, 999.0, t);
        Assert.Equal(0, r.Score);
        Assert.False(r.IsAnomalous);
        Assert.Equal(RiskLevel.Normal, r.Severity);
        Assert.Equal("warmup", r.Reason);
    }

    [Fact]
    public void Stable_baseline_keeps_severity_normal()
    {
        var det = NewDetector();
        var t = DateTime.UtcNow;
        for (int i = 0; i < 30; i++) det.Observe(Mac, 1.0, t);

        // Same value as the baseline → z ≈ 0.
        var r = det.Score(Mac, 1.0, t);
        Assert.False(r.IsAnomalous);
        Assert.Equal(RiskLevel.Normal, r.Severity);
    }

    [Theory]
    [InlineData(3.0, RiskLevel.Suspicious)]   // z = 2.0 → suspicious threshold
    [InlineData(4.0, RiskLevel.Anomalous)]    // z = 3.0 → anomalous threshold
    [InlineData(5.5, RiskLevel.Critical)]     // z = 4.5 → critical threshold
    public void Crosses_thresholds_at_expected_z_values(double mbps, RiskLevel expected)
    {
        var det = NewDetector();
        var t = DateTime.UtcNow;
        for (int i = 0; i < 20; i++) det.Observe(Mac, 1.0, t);

        var r = det.Score(Mac, mbps, t);
        Assert.Equal(expected, r.Severity);
    }

    [Fact]
    public void IsAnomalous_only_at_or_above_anomalous_severity()
    {
        var det = NewDetector();
        var t = DateTime.UtcNow;
        for (int i = 0; i < 20; i++) det.Observe(Mac, 1.0, t);

        Assert.False(det.Score(Mac, 3.0, t).IsAnomalous);
        Assert.True (det.Score(Mac, 4.0, t).IsAnomalous);
        Assert.True (det.Score(Mac, 5.5, t).IsAnomalous);
    }
}
