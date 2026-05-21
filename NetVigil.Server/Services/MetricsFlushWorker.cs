namespace NetVigil.Server.Services
{
    public class MetricsFlushWorker : BackgroundService
    {
        private readonly MetricsService _metrics;
        private readonly ILogger<MetricsFlushWorker> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);
        private readonly TimeSpan _offlineAfter = TimeSpan.FromSeconds(30);

        private readonly TimeSpan _purgeEvery = TimeSpan.FromSeconds(30);
        private DateTime _lastPurgeAt = DateTime.MinValue;

        public MetricsFlushWorker(MetricsService metrics, ILogger<MetricsFlushWorker> logger)
        {
            _metrics = metrics;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _metrics.LoadFromDatabaseAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _metrics.FlushAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Metrics flush failed");
                }

                try
                {
                    var n = _metrics.SweepOffline(_offlineAfter);
                    if (n > 0) _logger.LogInformation("Marked {Count} devices offline (stale > {Sec}s)",
                        n, (int)_offlineAfter.TotalSeconds);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Offline sweep failed");
                }

                if (DateTime.UtcNow - _lastPurgeAt > _purgeEvery)
                {
                    try
                    {
                        var purged = await _metrics.PurgeSyntheticAsync(stoppingToken);
                        if (purged > 0)
                            _logger.LogInformation("Purged {Count} stale synthetic devices", purged);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Synthetic-device purge failed");
                    }
                    _lastPurgeAt = DateTime.UtcNow;
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
    }
}
