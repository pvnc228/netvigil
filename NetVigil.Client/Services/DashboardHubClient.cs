using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using NetVigil.Shared;

namespace NetVigil.Client.Services
{
    public class DashboardHubClient : IAsyncDisposable
    {
        private readonly AuthService _auth;
        private readonly IConfiguration _config;
        private readonly ILogger<DashboardHubClient> _logger;
        private readonly HttpClient _http;
        private HubConnection? _connection;
        private DashboardSnapshot? _latest;
        private DateTime _lastSnapshotAt = DateTime.MinValue;
        private System.Threading.Timer? _fallbackTimer;
        private int _fallbackInFlight;
        private int _initialConnectStarted;

        private static readonly TimeSpan SilenceThreshold = TimeSpan.FromSeconds(5);

        public event Action<DashboardSnapshot>? OnSnapshot;
        public event Action? OnConnectionStateChanged;

        public DashboardSnapshot? Latest => _latest;
        public bool IsConnected => _connection?.State == HubConnectionState.Connected;

        public DashboardHubClient(
            AuthService auth,
            IConfiguration config,
            ILogger<DashboardHubClient> logger,
            HttpClient http)
        {
            _auth = auth;
            _config = config;
            _logger = logger;
            _http = http;
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            EnsureFallbackTimer();

            if (Interlocked.Exchange(ref _initialConnectStarted, 1) == 1) return;

            _ = Task.Run(() => ConnectWithRetryAsync(ct), ct);
            await Task.CompletedTask;
        }

        private async Task ConnectWithRetryAsync(CancellationToken ct)
        {
            var delays = new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(20)
            };
            int attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var conn = BuildConnection();
                    _connection = conn;
                    await conn.StartAsync(ct);
                    OnConnectionStateChanged?.Invoke();
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dashboard hub connect attempt {Attempt} failed; will retry", attempt + 1);

                    var failed = _connection;
                    _connection = null;
                    if (failed is not null)
                    {
                        try { await failed.DisposeAsync(); } catch {}
                    }

                    var delay = delays[Math.Min(attempt, delays.Length - 1)];
                    attempt++;
                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private HubConnection BuildConnection()
        {
            var apiBase = _config["Api:BaseUrl"] ?? "";
            var hubUrl  = $"{apiBase.TrimEnd('/')}/hubs/dashboard";

            var conn = new HubConnectionBuilder()
                .WithUrl(hubUrl, opts =>
                {
                    opts.AccessTokenProvider = async () => await _auth.GetTokenAsync();
                })
                .WithAutomaticReconnect()
                .Build();

            conn.On<DashboardSnapshot>("Snapshot", snap =>
            {
                _latest = snap;
                _lastSnapshotAt = DateTime.UtcNow;
                OnSnapshot?.Invoke(snap);
            });

            conn.Reconnecting += _ => { OnConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
            conn.Reconnected  += _ => { OnConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
            conn.Closed       += _ => { OnConnectionStateChanged?.Invoke(); return Task.CompletedTask; };

            return conn;
        }

        private void EnsureFallbackTimer()
        {
            if (_fallbackTimer is not null) return;
            _fallbackTimer = new System.Threading.Timer(
                _ => _ = FallbackTickAsync(),
                null,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2));
        }

        private async Task FallbackTickAsync()
        {
            if (IsConnected && DateTime.UtcNow - _lastSnapshotAt < SilenceThreshold) return;
            if (Interlocked.Exchange(ref _fallbackInFlight, 1) == 1) return;
            try
            {
                var token = await _auth.GetTokenAsync();
                if (string.IsNullOrEmpty(token)) return;

                var statsT     = _http.GetFromJsonAsync<SystemStats>("api/dashboard/stats");
                var devicesT   = _http.GetFromJsonAsync<List<NetworkDevice>>("api/dashboard/devices");
                var anomaliesT = _http.GetFromJsonAsync<List<AnomalyEvent>>("api/dashboard/anomalies?limit=20");
                var trafficT   = _http.GetFromJsonAsync<List<TrafficPoint>>("api/dashboard/traffic-history?minutes=5");
                var agentsT    = _http.GetFromJsonAsync<List<AgentInfoSnapshot>>("api/dashboard/agent-info");

                await Task.WhenAll(statsT, devicesT, anomaliesT, trafficT, agentsT);

                var snap = new DashboardSnapshot
                {
                    Stats      = statsT.Result ?? new SystemStats(),
                    Devices    = devicesT.Result ?? new List<NetworkDevice>(),
                    Anomalies  = anomaliesT.Result ?? new List<AnomalyEvent>(),
                    Traffic    = trafficT.Result ?? new List<TrafficPoint>(),
                    Agents     = agentsT.Result ?? new List<AgentInfoSnapshot>(),
                    SnapshotAt = DateTime.UtcNow
                };

                _latest = snap;
                _lastSnapshotAt = DateTime.UtcNow;
                OnSnapshot?.Invoke(snap);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dashboard REST fallback fetch failed");
            }
            finally
            {
                Interlocked.Exchange(ref _fallbackInFlight, 0);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _fallbackTimer?.Dispose();
            _fallbackTimer = null;

            if (_connection is not null)
            {
                try { await _connection.DisposeAsync(); }
                catch {}
                _connection = null;
            }
        }
    }
}
