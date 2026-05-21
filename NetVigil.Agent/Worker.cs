using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using NetVigil.Shared.Protos;

namespace NetVigil.Agent
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _config;
        private NetworkScanner.NetworkScannerClient? _client;
        private GrpcChannel? _channel;

        private string? _localMac;
        private IPAddress? _localIp;
        private NetworkInterface? _localInterface;
        private long _lastBytesIn;
        private long _lastBytesOut;
        private DateTime _lastSampleAt;

        private readonly string _agentId = Guid.NewGuid().ToString("N");
        private readonly DateTime _startedAtUtc = DateTime.UtcNow;
        private readonly string _agentVersion =
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        private string _grpcTarget = string.Empty;

        public Worker(ILogger<Worker> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            var address = _config["Server:GrpcAddress"] ?? "http://localhost:5002";
            _grpcTarget = address;
            _channel = GrpcChannel.ForAddress(address);
            _client = new NetworkScanner.NetworkScannerClient(_channel);
            _logger.LogInformation("Agent connecting to {Address} (agentId={Id})", address, _agentId);
            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var mode = _config["Agent:Mode"]?.Trim();
            bool snifferActive = string.Equals(mode, "GatewaySniffer",
                                               StringComparison.OrdinalIgnoreCase);
            if (!snifferActive)
                _ = Task.Run(() => StreamMetricsLoop(stoppingToken), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DiscoverOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Discovery iteration failed");
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        private async Task DiscoverOnceAsync(CancellationToken ct)
        {
            var (localIp, mask, iface) = NetworkUtilities.GetLocalInfo();
            _localIp = localIp;
            _localInterface = iface;
            _localMac = FormatMac(iface.GetPhysicalAddress());

            _logger.LogInformation("Local IP: {Ip}, MAC: {Mac}. Scanning subnet...", localIp, _localMac);

            await ReportAgentInfoAsync(localIp, mask, iface, ct);

            var bytes = localIp.GetAddressBytes();
            var gatewayIp = new IPAddress(new byte[] { bytes[0], bytes[1], bytes[2], 1 });

            var ipList = NetworkUtilities.GetIpRange(localIp, mask);
            var responsive = new System.Collections.Concurrent.ConcurrentBag<IPAddress>();
            using var pingLimiter = new SemaphoreSlim(32);

            var pingTasks = ipList.Select(async ip =>
            {
                if (ct.IsCancellationRequested) return;
                if (ip.Equals(localIp)) return;
                await pingLimiter.WaitAsync(ct);
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(ip, 1000);
                    if (reply.Status == IPStatus.Success)
                        responsive.Add(ip);
                }
                catch { }
                finally { pingLimiter.Release(); }
            });
            await Task.WhenAll(pingTasks);

            var arp = await NetworkUtilities.GetArpTableAsync();

            await ReportAsync(localIp, $"{Environment.MachineName} (Agent)", iface.Description, _localMac);
            var gatewayMac = arp.TryGetValue(gatewayIp, out var gm) ? gm : null;
            await ReportAsync(gatewayIp, "Gateway", "Network Gateway", gatewayMac);

            var enrichTasks = responsive
                .Where(ip => !ip.Equals(localIp) && !ip.Equals(gatewayIp))
                .Select(async ip =>
            {
                if (ct.IsCancellationRequested) return;
                string? mac = arp.TryGetValue(ip, out var am) ? am : null;

                string? hostname = null;
                try
                {
                    var entry = await Dns.GetHostEntryAsync(ip);
                    var first = entry.HostName.Split('.')[0];
                    if (!string.IsNullOrWhiteSpace(first) && first != ip.ToString())
                        hostname = first;
                }
                catch { }

                hostname ??= await TryGetNbNameAsync(ip);
                hostname ??= await TryGetMdnsNameAsync(ip);

                if (string.IsNullOrWhiteSpace(hostname))
                    hostname = SynthesizeHostname(mac, ip);

                string vendor = mac != null
                    ? NetworkUtilities.GetVendorFromMac(mac)
                    : "Unknown vendor";

                await ReportAsync(ip, hostname, vendor, mac);
            });

            await Task.WhenAll(enrichTasks);
            _logger.LogInformation("Subnet scan complete: {Count} responsive hosts.", responsive.Count);
        }

        private async Task ReportAgentInfoAsync(IPAddress ip, IPAddress mask, NetworkInterface iface, CancellationToken ct)
        {
            if (_client is null) return;
            try
            {
                var info = new AgentInfo
                {
                    AgentId = _agentId,
                    Hostname = Environment.MachineName,
                    InterfaceName = iface.Name ?? string.Empty,
                    InterfaceDesc = iface.Description ?? string.Empty,
                    LocalIp = ip.ToString(),
                    SubnetCidr = NetworkUtilities.ToCidr(ip, mask),
                    Mode = _config["Agent:Mode"]?.Trim() ?? "ArpScan",
                    Version = _agentVersion,
                    GrpcTarget = _grpcTarget,
                    StartedAt = Timestamp.FromDateTime(_startedAtUtc)
                };
                await _client.ReportAgentInfoAsync(info, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("ReportAgentInfo failed: {Msg}", ex.Message);
            }
        }

        private async Task ReportAsync(IPAddress ip, string hostname, string vendor, string? mac)
        {
            if (_client is null) return;

            try
            {
                var data = new DeviceData
                {
                    IpAddress = ip.ToString(),
                    Hostname = hostname,
                    MacAddress = mac ?? FormatMacFromIp(ip),
                    Vendor = vendor,
                    CurrentTrafficMbps = 0
                };
                await _client.ReportDeviceAsync(data);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to report {Ip}: {Msg}", ip, ex.Message);
            }
        }

        private async Task StreamMetricsLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_client is null) { await Task.Delay(1000, ct); continue; }
                    if (_localInterface is null || _localIp is null || _localMac is null)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    using var call = _client.StreamMetrics(cancellationToken: ct);

                    while (!ct.IsCancellationRequested)
                    {
                        var sample = ReadLocalInterfaceSample();
                        if (sample.HasValue)
                        {
                            var (mbpsIn, mbpsOut, pps) = sample.Value;
                            await call.RequestStream.WriteAsync(new MetricSample
                            {
                                MacAddress = _localMac,
                                IpAddress = _localIp.ToString(),
                                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                                MbpsIn = mbpsIn,
                                MbpsOut = mbpsOut,
                                PacketsPerSec = pps
                            }, ct);
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    }

                    await call.RequestStream.CompleteAsync();
                    var resp = await call.ResponseAsync;
                    _logger.LogInformation("Metric stream closed: received={R}, anomalies={A}",
                        resp.SamplesReceived, resp.AnomaliesDetected);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning("Metric stream error: {Msg}. Reconnecting in 5s...", ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }

        private (double mbpsIn, double mbpsOut, uint pps)? ReadLocalInterfaceSample()
        {
            if (_localInterface is null) return null;

            try
            {
                var stats = _localInterface.GetIPv4Statistics();
                var now = DateTime.UtcNow;
                var bytesIn = stats.BytesReceived;
                var bytesOut = stats.BytesSent;

                if (_lastSampleAt == default)
                {
                    _lastBytesIn = bytesIn;
                    _lastBytesOut = bytesOut;
                    _lastSampleAt = now;
                    return null;
                }

                var elapsed = (now - _lastSampleAt).TotalSeconds;
                if (elapsed < 0.1) return null;

                var deltaIn = Math.Max(0, bytesIn - _lastBytesIn);
                var deltaOut = Math.Max(0, bytesOut - _lastBytesOut);

                _lastBytesIn = bytesIn;
                _lastBytesOut = bytesOut;
                _lastSampleAt = now;

                var mbpsIn = (deltaIn * 8.0) / (elapsed * 1_000_000);
                var mbpsOut = (deltaOut * 8.0) / (elapsed * 1_000_000);
                var pps = (uint)((stats.UnicastPacketsReceived + stats.UnicastPacketsSent) / Math.Max(1, elapsed));

                return (Math.Round(mbpsIn, 3), Math.Round(mbpsOut, 3), pps);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> TryGetMdnsNameAsync(IPAddress ip)
        {
            try
            {
                var bytes = ip.GetAddressBytes();
                var rev = $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}.in-addr.arpa";

                var ms = new System.IO.MemoryStream();
                ms.Write(new byte[] { 0xAB, 0xCD }, 0, 2); 
                ms.WriteByte(0x00); ms.WriteByte(0x00);    
                ms.WriteByte(0x00); ms.WriteByte(0x01);    
                ms.Write(new byte[] { 0, 0, 0, 0, 0, 0 }, 0, 6); 
                foreach (var label in rev.Split('.'))
                {
                    var raw = Encoding.ASCII.GetBytes(label);
                    ms.WriteByte((byte)raw.Length);
                    ms.Write(raw, 0, raw.Length);
                }
                ms.WriteByte(0x00);                         
                ms.Write(new byte[] { 0x00, 0x0C }, 0, 2);  
                ms.Write(new byte[] { 0x00, 0x01 }, 0, 2); 
                var query = ms.ToArray();

                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.ReceiveTimeout = 600;
                await udp.SendAsync(query, query.Length, new IPEndPoint(ip, 5353));

                using var cts = new CancellationTokenSource(600);
                var res = await udp.ReceiveAsync(cts.Token);
                var data = res.Buffer;
                if (data.Length < 12) return null;

                int pos = 12;
                while (pos < data.Length && data[pos] != 0)
                {
                    if ((data[pos] & 0xC0) == 0xC0) { pos += 2; goto qtype; }
                    pos += data[pos] + 1;
                }
                pos++;
            qtype:
                pos += 4;
                if (pos + 12 > data.Length) return null;

                pos += 10; 
                if (pos + 2 > data.Length) return null;
                int rdlen = (data[pos] << 8) | data[pos + 1];
                pos += 2;
                if (pos + rdlen > data.Length || rdlen < 1) return null;

                var name = ReadDnsName(data, pos);
                if (string.IsNullOrWhiteSpace(name)) return null;
                var first = name.Split('.')[0];
                return string.IsNullOrWhiteSpace(first) ? null : first;
            }
            catch { return null; }
        }

        private static string ReadDnsName(byte[] data, int pos)
        {
            var sb = new StringBuilder();
            int hops = 0;
            while (pos < data.Length && data[pos] != 0 && hops < 16)
            {
                if ((data[pos] & 0xC0) == 0xC0)
                {
                    if (pos + 1 >= data.Length) break;
                    pos = ((data[pos] & 0x3F) << 8) | data[pos + 1];
                    hops++;
                    continue;
                }
                int len = data[pos++];
                if (pos + len > data.Length) break;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.ASCII.GetString(data, pos, len));
                pos += len;
            }
            return sb.ToString();
        }

        private static string SynthesizeHostname(string? mac, IPAddress ip)
        {
            if (!string.IsNullOrWhiteSpace(mac))
            {
                var hex = mac.Replace(":", "").Replace("-", "");
                if (hex.Length >= 6)
                    return $"host-{hex[^6..].ToUpper()}";
            }
            return $"device-{ip.GetAddressBytes()[3]}";
        }

        private static async Task<string?> TryGetNbNameAsync(IPAddress ip)
        {
            try
            {
                byte[] query =
                [
                    0xAB, 0xCD, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
                    0x20, 0x43, 0x4B,                                                         
                    0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,             
                    0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                    0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                    0x00, 0x00, 0x21, 0x00, 0x01                                               
                ];

                using var udp = new UdpClient(AddressFamily.InterNetwork);
                await udp.SendAsync(query, query.Length, new IPEndPoint(ip, 137));

                using var cts = new CancellationTokenSource(500);
                var res = await udp.ReceiveAsync(cts.Token);
                var data = res.Buffer;

                int off = data.Length > 51 && data[50] == 0xC0 ? 62
                        : data.Length > 13 && data[12] == 0xC0 ? 24
                        : -1;

                if (off < 0 || data.Length < off + 17) return null;

                int numNames = data[off];
                if (numNames is 0 or > 32) return null;

                for (int i = 0; i < numNames; i++)
                {
                    int start = off + 1 + i * 18;
                    if (data.Length < start + 16) break;
                    byte nameType = data[start + 15];
                    if (nameType is 0x00 or 0x03)
                    {
                        var name = Encoding.ASCII.GetString(data, start, 15).TrimEnd('\0', ' ');
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
                return null;
            }
            catch { return null; }
        }

        private static string FormatMac(PhysicalAddress mac)
        {
            var bytes = mac.GetAddressBytes();
            if (bytes.Length == 0) return "00:00:00:00:00:00";
            return string.Join(":", bytes.Select(b => b.ToString("X2")));
        }

        private static string FormatMacFromIp(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            return $"02:00:{bytes[0]:X2}:{bytes[1]:X2}:{bytes[2]:X2}:{bytes[3]:X2}";
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            _channel?.Dispose();
        }
    }
}
