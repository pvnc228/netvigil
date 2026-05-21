using Microsoft.AspNetCore.SignalR;
using NetVigil.Server.Hubs;
using NetVigil.Shared;

namespace NetVigil.Server.Services
{
    public class DashboardBroadcaster : BackgroundService
    {
        private readonly IHubContext<DashboardHub> _hub;
        private readonly MetricsService _metrics;
        private readonly AgentRegistry _agents;
        private readonly ILogger<DashboardBroadcaster> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan AgentStale = TimeSpan.FromSeconds(60);

        public DashboardBroadcaster(
            IHubContext<DashboardHub> hub,
            MetricsService metrics,
            AgentRegistry agents,
            ILogger<DashboardBroadcaster> logger)
        {
            _hub = hub;
            _metrics = metrics;
            _agents = agents;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var snap = new DashboardSnapshot
                    {
                        Stats     = _metrics.GetStats(),
                        Devices   = _metrics.GetAllDevices(),
                        Anomalies = await _metrics.GetRecentAnomaliesAsync(20, stoppingToken),
                        Traffic   = await _metrics.GetTrafficHistoryAsync(5, stoppingToken),
                        Agents    = _agents.GetActive(AgentStale),
                        SnapshotAt = DateTime.UtcNow
                    };

                    await _hub.Clients.All.SendAsync("Snapshot", snap, stoppingToken);
                }
                catch (OperationCanceledException) {}
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dashboard broadcast failed");
                }

                try { await Task.Delay(_interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
