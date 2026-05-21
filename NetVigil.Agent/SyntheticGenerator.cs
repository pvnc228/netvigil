using System.Reflection;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using NetVigil.Shared.Protos;

namespace NetVigil.Agent
{
    public sealed class SyntheticGenerator : BackgroundService
    {
        private readonly ILogger<SyntheticGenerator> _log;
        private readonly IConfiguration _config;
        private NetworkScanner.NetworkScannerClient? _client;
        private GrpcChannel? _channel;

        private readonly List<FakeDevice> _devices = new();
        private readonly Random _rng;

        private readonly string _agentId = Guid.NewGuid().ToString("N");
        private readonly DateTime _startedAtUtc = DateTime.UtcNow;
        private readonly string _agentVersion =
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        private string _grpcTarget = string.Empty;
        private string _subnetCidr = "10.42.0.0/24";

        public SyntheticGenerator(ILogger<SyntheticGenerator> log, IConfiguration config)
        {
            _log = log;
            _config = config;
            var seed = config.GetValue("Synthetic:Seed", 42);
            _rng = new Random(seed);
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            _grpcTarget = _config["Server:GrpcAddress"] ?? "http://localhost:5002";
            _channel = GrpcChannel.ForAddress(_grpcTarget);
            _client = new NetworkScanner.NetworkScannerClient(_channel);

            BuildFleet();
            _log.LogInformation(
                "Synthetic agent started: {Count} devices on {Cidr}, target={Target}",
                _devices.Count, _subnetCidr, _grpcTarget);

            return base.StartAsync(cancellationToken);
        }

        private void BuildFleet()
        {
            int count = _config.GetValue("Synthetic:DeviceCount", 50);
            count = Math.Clamp(count, 1, 240); 

            _subnetCidr = _config["Synthetic:Subnet"] ?? "10.42.0.0/24";
            var (baseIp, _) = ParseCidr(_subnetCidr);

            var profileBag = BuildProfileBag(count);

            for (int i = 0; i < count; i++)
            {
                var mac = GenerateMac(i);
                var ipBytes = (byte[])baseIp.GetAddressBytes().Clone();
                ipBytes[3] = (byte)(10 + i); 
                var profile = profileBag[i];

                _devices.Add(new FakeDevice
                {
                    Mac = mac,
                    Ip = string.Join('.', ipBytes),
                    Hostname = $"synth-{mac.Replace(":", "")[^6..].ToLower()}",
                    Vendor = "Private MAC",
                    Profile = profile,
                    BaselineMbps = profile == Profile.Leak ? 0.1 : 0,
                    PhasePoint = _rng.NextDouble() * Math.PI * 2,
                });
            }
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }

            await SendAgentInfoAsync(ct);
            foreach (var d in _devices) await ReportDeviceAsync(d, ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_client is null) { await Task.Delay(1000, ct); continue; }

                    using var call = _client.StreamMetrics(cancellationToken: ct);
                    var lastHeartbeat = DateTime.UtcNow;

                    while (!ct.IsCancellationRequested)
                    {
                        var now = DateTime.UtcNow;

                        if ((now - lastHeartbeat) > TimeSpan.FromSeconds(25))
                        {
                            await SendAgentInfoAsync(ct);
                            foreach (var d in _devices) await ReportDeviceAsync(d, ct);
                            lastHeartbeat = now;
                        }

                        foreach (var d in _devices)
                        {
                            var mbps = GenerateMbps(d, now);
                            var inMbps  = Math.Round(mbps * 0.6, 3);
                            var outMbps = Math.Round(mbps * 0.4, 3);
                            await call.RequestStream.WriteAsync(new MetricSample
                            {
                                MacAddress = d.Mac,
                                IpAddress = d.Ip,
                                Timestamp = Timestamp.FromDateTime(now),
                                MbpsIn = inMbps,
                                MbpsOut = outMbps,
                                PacketsPerSec = (uint)Math.Max(1, mbps * 100)
                            }, ct);
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    }

                    await call.RequestStream.CompleteAsync();
                    var resp = await call.ResponseAsync;
                    _log.LogInformation("Synthetic stream closed: received={R}, anomalies={A}",
                                         resp.SamplesReceived, resp.AnomaliesDetected);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.LogWarning("Synthetic stream error: {Msg}. Reconnecting in 5s...", ex.Message);
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task SendAgentInfoAsync(CancellationToken ct)
        {
            if (_client is null) return;
            try
            {
                await _client.ReportAgentInfoAsync(new AgentInfo
                {
                    AgentId = _agentId,
                    Hostname = Environment.MachineName,
                    InterfaceName = "synthetic0",
                    InterfaceDesc = $"Synthetic generator ({_devices.Count} devices)",
                    LocalIp = "10.42.0.1",
                    SubnetCidr = _subnetCidr,
                    Mode = "Synthetic",
                    Version = _agentVersion,
                    GrpcTarget = _grpcTarget,
                    StartedAt = Timestamp.FromDateTime(_startedAtUtc)
                }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _log.LogDebug("Synthetic ReportAgentInfo failed: {Msg}", ex.Message);
            }
        }

        private async Task ReportDeviceAsync(FakeDevice d, CancellationToken ct)
        {
            if (_client is null) return;
            try
            {
                await _client.ReportDeviceAsync(new DeviceData
                {
                    MacAddress = d.Mac,
                    IpAddress = d.Ip,
                    Hostname = d.Hostname,
                    Vendor = d.Vendor,
                    CurrentTrafficMbps = 0
                }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _log.LogDebug("Synthetic ReportDevice {Mac} failed: {Msg}", d.Mac, ex.Message);
            }
        }


        private double GenerateMbps(FakeDevice d, DateTime nowUtc)
        {
            double hour = nowUtc.ToLocalTime().TimeOfDay.TotalHours;
            double diurnal = 0.5 + 0.5 * Math.Sin((hour - 14) / 24.0 * 2 * Math.PI);

            switch (d.Profile)
            {
                case Profile.Idle:
                    return Math.Max(0, Gauss(0.1, 0.04));

                case Profile.Light:
                    return Math.Max(0, Gauss(2.0 * (0.6 + 0.4 * diurnal), 1.0));

                case Profile.Streaming:
                    return Math.Max(0, Gauss(15.0 * (0.4 + 0.6 * diurnal), 4.0));

                case Profile.Burst:
                    if (_rng.NextDouble() < 0.05)
                    {
                        return 50 + _rng.NextDouble() * 150;
                    }
                    return Math.Max(0, Gauss(0.5, 0.2));

                case Profile.Leak:
                    var ageMin = (nowUtc - _startedAtUtc).TotalMinutes;
                    d.BaselineMbps = Math.Min(30, 0.1 + ageMin);
                    return Math.Max(0, Gauss(d.BaselineMbps, 0.5));

                default:
                    return 0;
            }
        }

        private double Gauss(double mu, double sigma)
        {
            double u1 = 1 - _rng.NextDouble();
            double u2 = 1 - _rng.NextDouble();
            double std = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return mu + sigma * std;
        }


        private static (System.Net.IPAddress Net, int Prefix) ParseCidr(string cidr)
        {
            var parts = cidr.Split('/');
            var ip = System.Net.IPAddress.Parse(parts[0]);
            int prefix = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 24;
            return (ip, prefix);
        }

        private static string GenerateMac(int index)
        {
            return $"02:42:{(byte)(index >> 24):X2}:{(byte)(index >> 16):X2}:{(byte)(index >> 8):X2}:{(byte)index:X2}";
        }

        private static List<Profile> BuildProfileBag(int count)
        {
            var bag = new List<Profile>(count);
            int idle      = (int)Math.Round(count * 0.60);
            int light     = (int)Math.Round(count * 0.25);
            int streaming = (int)Math.Round(count * 0.10);
            int burst     = (int)Math.Round(count * 0.04);
            int leak      = count - idle - light - streaming - burst;
            if (leak < 0) { idle += leak; leak = 0; }

            for (int i = 0; i < idle;      i++) bag.Add(Profile.Idle);
            for (int i = 0; i < light;     i++) bag.Add(Profile.Light);
            for (int i = 0; i < streaming; i++) bag.Add(Profile.Streaming);
            for (int i = 0; i < burst;     i++) bag.Add(Profile.Burst);
            for (int i = 0; i < leak;      i++) bag.Add(Profile.Leak);
            return bag;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            _channel?.Dispose();
        }

        private enum Profile { Idle, Light, Streaming, Burst, Leak }

        private sealed class FakeDevice
        {
            public string Mac = string.Empty;
            public string Ip = string.Empty;
            public string Hostname = string.Empty;
            public string Vendor = string.Empty;
            public Profile Profile;
            public double BaselineMbps;
            public double PhasePoint;
        }
    }
}
