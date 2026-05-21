using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetVigil.Server.Data;
using NetVigil.Server.Services.Anomaly;
using NetVigil.Shared;

namespace NetVigil.Server.Services
{
    public class MetricsService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAnomalyDetector _detector;
        private readonly NotificationService _notifier;
        private readonly ILogger<MetricsService> _logger;

        private readonly ConcurrentDictionary<string, NetworkDevice> _devices = new();
        private readonly ConcurrentQueue<TrafficSample> _pendingSamples = new();
        private readonly ConcurrentQueue<AnomalyEvent> _pendingAnomalies = new();

        // Set of MAC addresses whose in-memory state changed since the last
        // flush. FlushAsync iterates this instead of the entire device map,
        // so a 500-device load test no longer issues 500 UPDATEs every 2s
        // when most rows haven't changed.
        private readonly ConcurrentDictionary<string, byte> _dirtyMacs = new();
        private void MarkDirty(string mac) => _dirtyMacs[mac] = 0;

        public MetricsService(
            IServiceScopeFactory scopeFactory,
            IAnomalyDetector detector,
            NotificationService notifier,
            ILogger<MetricsService> logger)
        {
            _scopeFactory = scopeFactory;
            _detector = detector;
            _notifier = notifier;
            _logger = logger;
        }

        public async Task LoadFromDatabaseAsync(CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();

            var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
            foreach (var d in devices)
            {
                _devices[d.MacAddress] = d;
            }

            var since = DateTime.UtcNow.AddMinutes(-10);
            var recent = await db.TrafficSamples
                .Where(s => s.Timestamp >= since)
                .OrderBy(s => s.Timestamp)
                .AsNoTracking()
                .ToListAsync(ct);

            foreach (var s in recent)
            {
                _detector.Observe(s.DeviceMac, s.Mbps, s.Timestamp);
            }

            _logger.LogInformation(
                "Loaded {Devices} devices and warmed detector with {Samples} recent samples.",
                devices.Count, recent.Count);
        }

        public List<NetworkDevice> GetAllDevices()
        {
            return _devices.Values.OrderBy(d => d.IpAddress).ToList();
        }

        public bool SetFlag(string mac, bool flagged, string? changedBy = null, string? reason = null)
        {
            if (!_devices.TryGetValue(mac, out var device)) return false;
            var actor = string.IsNullOrWhiteSpace(changedBy) ? "system" : changedBy;
            var now = DateTime.UtcNow;

            device.IsFlagged = flagged;
            device.FlaggedAt = flagged ? now : null;
            device.FlaggedBy = flagged ? actor : null;

            // Persist immediately + write audit row. Fire-and-forget: the
            // operator UI calls /flag rarely (manual click), so an extra DB
            // round-trip per call is fine and avoids losing the audit if the
            // process is killed before the next FlushAsync tick.
            _ = PersistFlagAsync(device, flagged, actor, reason, now);
            return true;
        }

        private async Task PersistFlagAsync(NetworkDevice device, bool flagged, string actor, string? reason, DateTime when)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
                var existing = await db.Devices.FindAsync(device.MacAddress);
                if (existing is not null)
                {
                    existing.IsFlagged = device.IsFlagged;
                    existing.FlaggedAt = device.FlaggedAt;
                    existing.FlaggedBy = device.FlaggedBy;
                }

                db.DeviceFlagAudits.Add(new DeviceFlagAudit
                {
                    DeviceMac = device.MacAddress,
                    IsFlagged = flagged,
                    ChangedAt = when,
                    ChangedBy = actor,
                    Reason = reason
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist flag change for {Mac}", device.MacAddress);
            }
        }

        // Synthetic devices come from NetVigil.LoadTest (MAC prefix 02:99:)
        // and the agent's `--synthetic` mode (hostname pattern "loadtest-*").
        // After a stress run finishes they sit forever in the device map and
        // every flush still iterates them — so 1 min after the agent stops
        // reporting we drop them from memory and the DB. Real devices are
        // never matched by either rule.
        private static readonly TimeSpan SyntheticPurgeAfter = TimeSpan.FromMinutes(1);

        private static bool IsSynthetic(NetworkDevice d) =>
            d.MacAddress.StartsWith("02:99:", StringComparison.OrdinalIgnoreCase) ||
            (d.Hostname?.Contains("loadtest", StringComparison.OrdinalIgnoreCase) ?? false);

        public async Task<int> PurgeSyntheticAsync(CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow - SyntheticPurgeAfter;
            var toRemove = new List<string>();
            foreach (var d in _devices.Values)
            {
                if (!d.IsOnline && d.LastSeen < cutoff && IsSynthetic(d))
                {
                    toRemove.Add(d.MacAddress);
                }
            }
            if (toRemove.Count == 0) return 0;

            foreach (var mac in toRemove)
            {
                _devices.TryRemove(mac, out _);
                _dirtyMacs.TryRemove(mac, out _);
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
                const int batch = 200;
                for (int i = 0; i < toRemove.Count; i += batch)
                {
                    var slice = toRemove.GetRange(i, Math.Min(batch, toRemove.Count - i));
                    await db.Devices.Where(d => slice.Contains(d.MacAddress)).ExecuteDeleteAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete {Count} synthetic devices from DB", toRemove.Count);
            }

            return toRemove.Count;
        }

        public async Task<List<DeviceFlagAudit>> GetFlagAuditAsync(string? mac = null, int limit = 100, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
            var q = db.DeviceFlagAudits.AsNoTracking().OrderByDescending(a => a.ChangedAt);
            if (!string.IsNullOrWhiteSpace(mac))
            {
                return await q.Where(a => a.DeviceMac == mac).Take(limit).ToListAsync(ct);
            }
            return await q.Take(limit).ToListAsync(ct);
        }

        // Mark devices that haven't reported recently as offline. Returns
        // the number of state transitions, so the caller can log only when
        // something actually changed.
        public int SweepOffline(TimeSpan staleAfter)
        {
            var cutoff = DateTime.UtcNow - staleAfter;
            int transitioned = 0;
            foreach (var d in _devices.Values)
            {
                if (d.IsOnline && d.LastSeen < cutoff)
                {
                    d.IsOnline = false;
                    d.CurrentTrafficMbps = 0;
                    MarkDirty(d.MacAddress);
                    transitioned++;
                }
            }
            return transitioned;
        }

        public async Task<List<TrafficPoint>> GetTrafficHistoryAsync(int minutes = 60, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();

            var since = DateTime.UtcNow.AddMinutes(-minutes);
            var raw = await db.TrafficSamples
                .Where(s => s.Timestamp >= since)
                .GroupBy(s => new { Bucket = s.Timestamp.Date.AddSeconds(((long)(s.Timestamp - s.Timestamp.Date).TotalSeconds / 5) * 5) })
                .Select(g => new TrafficPoint
                {
                    Timestamp = g.Key.Bucket,
                    Mbps = g.Sum(x => x.Mbps),
                    IsAnomalous = g.Any(x => x.IsAnomalous)
                })
                .OrderBy(p => p.Timestamp)
                .AsNoTracking()
                .ToListAsync(ct);

            return raw;
        }

        public async Task<List<AnomalyEvent>> GetRecentAnomaliesAsync(int limit = 50, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();

            return await db.Anomalies
                .OrderByDescending(a => a.Timestamp)
                .Take(limit)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        public SystemStats GetStats()
        {
            var devices = _devices.Values.ToList();
            var since = DateTime.UtcNow.AddHours(-24);

            return new SystemStats
            {
                TotalDevices = devices.Count,
                OnlineDevices = devices.Count(d => d.IsOnline),
                TotalTrafficIn = Math.Round(devices.Where(d => d.IsOnline).Sum(d => d.CurrentTrafficMbps), 1),
                CriticalDevices = devices.Count(d => d.RiskLevel >= RiskLevel.Anomalous),
                AnomaliesLast24h = devices.Sum(d => d.AnomalyCount24h),
                AlertsCount = devices.Sum(d => d.AnomalyCount24h),
                SnapshotAt = DateTime.UtcNow
            };
        }

        public DetectorStats GetDetectorStats()
        {
            var stats = _detector.GetStats();
            // Attach a current-population breakdown by current risk level so
            // the ML page can show "what the model is calling things right now"
            // alongside the training metadata from the detector itself.
            foreach (var d in _devices.Values)
            {
                switch (d.RiskLevel)
                {
                    case RiskLevel.Critical:   stats.DevicesCritical++;   break;
                    case RiskLevel.Anomalous:  stats.DevicesAnomalous++;  break;
                    case RiskLevel.Suspicious: stats.DevicesSuspicious++; break;
                    default:                   stats.DevicesNormal++;     break;
                }
            }
            return stats;
        }

        public void UpdateRealDevice(NetworkDevice incoming)
        {
            bool isNew = !_devices.ContainsKey(incoming.MacAddress);
            var now = DateTime.UtcNow;

            var device = _devices.AddOrUpdate(incoming.MacAddress,
                _ =>
                {
                    incoming.IsOnline = true;
                    incoming.LastSeen = now;
                    incoming.FirstSeen = now;
                    return incoming;
                },
                (_, existing) =>
                {
                    existing.IpAddress = incoming.IpAddress;
                    existing.Hostname = incoming.Hostname;
                    existing.Vendor = incoming.Vendor;
                    existing.IsOnline = true;
                    existing.LastSeen = now;
                    if (incoming.CurrentTrafficMbps > 0)
                        existing.CurrentTrafficMbps = incoming.CurrentTrafficMbps;
                    return existing;
                });

            MarkDirty(device.MacAddress);

            if (isNew)
            {
                _ = _notifier.SendNewDeviceAlertAsync(device.Hostname, device.IpAddress);
                _ = PersistDeviceAsync(device);
            }
        }

        public AnomalyResult IngestSample(string mac, double mbps, DateTime timestamp)
        {
            if (!_devices.TryGetValue(mac, out var device))
            {
                _detector.Observe(mac, mbps, timestamp);
                return new AnomalyResult(0, false, RiskLevel.Normal, "unknown-device");
            }

            var result = _detector.Score(mac, mbps, timestamp);
            _detector.Observe(mac, mbps, timestamp);

            device.CurrentTrafficMbps = Math.Round(mbps, 2);
            device.LastSeen = timestamp;
            device.AnomalyScore = result.Score;
            device.RiskLevel = result.Severity;
            MarkDirty(mac);

            _pendingSamples.Enqueue(new TrafficSample
            {
                DeviceMac = mac,
                Timestamp = timestamp,
                Mbps = mbps,
                AnomalyScore = result.Score,
                IsAnomalous = result.IsAnomalous
            });

            if (result.IsAnomalous)
            {
                device.LastAnomalyAt = timestamp;
                Interlocked.Increment(ref _anomalyCounter);

                var ev = new AnomalyEvent
                {
                    DeviceMac = mac,
                    DeviceName = device.Hostname,
                    Timestamp = timestamp,
                    Mbps = mbps,
                    Score = result.Score,
                    Severity = result.Severity,
                    Description = result.Reason
                };
                _pendingAnomalies.Enqueue(ev);

                if (result.Severity >= RiskLevel.Anomalous)
                {
                    _ = _notifier.SendAnomalyAlertAsync(ev);
                }
            }

            return result;
        }

        private int _anomalyCounter;

        private const int FlushDeviceBatch = 200;
        private const int FlushSampleBatch = 1000;

        public async Task FlushAsync(CancellationToken ct = default)
        {
            var samples = new List<TrafficSample>();
            while (_pendingSamples.TryDequeue(out var s)) samples.Add(s);

            var anomalies = new List<AnomalyEvent>();
            while (_pendingAnomalies.TryDequeue(out var a)) anomalies.Add(a);

            var dirtyMacs = new List<string>(_dirtyMacs.Count);
            foreach (var mac in _dirtyMacs.Keys.ToArray())
            {
                if (_dirtyMacs.TryRemove(mac, out _)) dirtyMacs.Add(mac);
            }

            if (samples.Count == 0 && anomalies.Count == 0 && dirtyMacs.Count == 0) return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            for (int i = 0; i < samples.Count; i += FlushSampleBatch)
            {
                var slice = samples.GetRange(i, Math.Min(FlushSampleBatch, samples.Count - i));
                await db.TrafficSamples.AddRangeAsync(slice, ct);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }

            if (anomalies.Count > 0)
            {
                await db.Anomalies.AddRangeAsync(anomalies, ct);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }

            for (int i = 0; i < dirtyMacs.Count; i += FlushDeviceBatch)
            {
                var slice = dirtyMacs.GetRange(i, Math.Min(FlushDeviceBatch, dirtyMacs.Count - i));

                var existing = await db.Devices
                    .Where(d => slice.Contains(d.MacAddress))
                    .ToDictionaryAsync(d => d.MacAddress, ct);

                foreach (var mac in slice)
                {
                    if (!_devices.TryGetValue(mac, out var inMem)) continue;

                    if (existing.TryGetValue(mac, out var tracked))
                    {
                        db.Entry(tracked).CurrentValues.SetValues(inMem);
                    }
                    else
                    {
                        db.Devices.Add(inMem);
                    }
                }

                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }

            _logger.LogDebug(
                "Flushed {Samples} samples, {Anomalies} anomalies, {Devices} device updates.",
                samples.Count, anomalies.Count, dirtyMacs.Count);
        }

        private async Task PersistDeviceAsync(NetworkDevice device)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
                var existing = await db.Devices.FindAsync(device.MacAddress);
                if (existing is null) db.Devices.Add(device);
                else db.Entry(existing).CurrentValues.SetValues(device);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist new device {Mac}", device.MacAddress);
            }
        }
    }
}
