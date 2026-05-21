using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using NetVigil.Shared.Protos;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace NetVigil.Agent
{
    public sealed class GatewaySniffer : BackgroundService
    {
        private readonly ILogger<GatewaySniffer> _log;
        private readonly IConfiguration _config;
        private NetworkScanner.NetworkScannerClient? _client;
        private GrpcChannel? _channel;

        private readonly ConcurrentDictionary<string, MacCounters> _counters = new();

        private string? _localMac;
        private LibPcapLiveDevice? _device;

        public GatewaySniffer(ILogger<GatewaySniffer> log, IConfiguration config)
        {
            _log = log;
            _config = config;
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            var address = _config["Server:GrpcAddress"] ?? "http://localhost:5002";
            _channel = GrpcChannel.ForAddress(address);
            _client = new NetworkScanner.NetworkScannerClient(_channel);
            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { return; }

            if (!TryOpenDevice())
            {
                _log.LogWarning(
                    "GatewaySniffer mode requested but no capture device opened. " +
                    "Falling back to discovery-only. Install Npcap (Windows) or " +
                    "run with cap_net_raw / root (Linux), then restart agent.");
                return;
            }

            try
            {
                await FlushLoopAsync(stoppingToken);
            }
            finally
            {
                try { _device?.StopCapture(); _device?.Close(); } catch { }
            }
        }

        private bool TryOpenDevice()
        {
            try
            {
                LibPcapLiveDeviceList devices;
                try { devices = LibPcapLiveDeviceList.Instance; }
                catch (Exception ex)
                {
                    _log.LogError(ex,
                        "Failed to enumerate capture devices. " +
                        "Is Npcap (Windows) or libpcap (Linux) installed?");
                    return false;
                }

                if (devices.Count == 0)
                {
                    _log.LogError("No capture devices found.");
                    return false;
                }

                var (localIp, _, iface) = NetworkUtilities.GetLocalInfo();
                _localMac = string.Join(":",
                    iface.GetPhysicalAddress().GetAddressBytes()
                         .Select(b => b.ToString("X2")));

                var hint = _config["Agent:CaptureInterface"];
                LibPcapLiveDevice? chosen = null;

                if (!string.IsNullOrWhiteSpace(hint))
                {
                    chosen = devices.FirstOrDefault(d =>
                        d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                        (d.Description?.Contains(hint, StringComparison.OrdinalIgnoreCase) ?? false));
                }
                chosen ??= devices.FirstOrDefault(d =>
                    d.Addresses.Any(a => a.Addr?.ipAddress != null &&
                                         a.Addr.ipAddress.Equals(localIp)));
                chosen ??= devices.FirstOrDefault(d =>
                    !d.Name.Contains("loopback", StringComparison.OrdinalIgnoreCase) &&
                    !(d.Description?.Contains("loopback", StringComparison.OrdinalIgnoreCase) ?? false));

                if (chosen is null)
                {
                    _log.LogError("Could not pick a capture interface.");
                    return false;
                }

                chosen.Open(DeviceModes.Promiscuous, 1000);
                chosen.OnPacketArrival += OnPacket;
                chosen.StartCapture();
                _device = chosen;

                _log.LogInformation(
                    "GatewaySniffer capturing on {Iface} ({Desc}). Local MAC {Mac}, IP {Ip}. " +
                    "Per-device traffic numbers will reflect bytes seen on this interface only.",
                    chosen.Name, chosen.Description ?? "n/a", _localMac, localIp);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to open pcap device.");
                return false;
            }
        }

        private void OnPacket(object sender, PacketCapture e)
        {
            try
            {
                var raw = e.GetPacket();
                int len = raw.Data.Length;
                if (len < 14) return; 
                var data = raw.Data;
                var dst = MacToString(data, 0);
                var src = MacToString(data, 6);

                var srcCnt = _counters.GetOrAdd(src, _ => new MacCounters());
                Interlocked.Add(ref srcCnt.BytesOut, len);
                Interlocked.Increment(ref srcCnt.Packets);

                if ((data[0] & 0x01) == 0) 
                {
                    var dstCnt = _counters.GetOrAdd(dst, _ => new MacCounters());
                    Interlocked.Add(ref dstCnt.BytesIn, len);
                }
            }
            catch {}
        }

        private async Task FlushLoopAsync(CancellationToken ct)
        {
            if (_client is null) return;

            while (!ct.IsCancellationRequested)
            {
                using var call = _client.StreamMetrics(cancellationToken: ct);
                try
                {
                    var lastFlush = DateTime.UtcNow;
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);

                        var now = DateTime.UtcNow;
                        var elapsed = (now - lastFlush).TotalSeconds;
                        lastFlush = now;
                        if (elapsed < 0.1) continue;

                        var snapshot = _counters.ToArray();
                        _counters.Clear();

                        var arp = await NetworkUtilities.GetArpTableAsync();
                        var macToIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (ip, m) in arp)
                            macToIp[m] = ip.ToString();

                        foreach (var (mac, c) in snapshot)
                        {
                            if (string.Equals(mac, "FF:FF:FF:FF:FF:FF", StringComparison.OrdinalIgnoreCase))
                                continue;

                            double mbpsIn  = (c.BytesIn  * 8.0) / (elapsed * 1_000_000);
                            double mbpsOut = (c.BytesOut * 8.0) / (elapsed * 1_000_000);
                            uint   pps     = (uint)Math.Min(c.Packets / Math.Max(1, elapsed), uint.MaxValue);

                            await call.RequestStream.WriteAsync(new MetricSample
                            {
                                MacAddress    = mac,
                                IpAddress     = macToIp.TryGetValue(mac, out var ip) ? ip : "",
                                Timestamp     = Timestamp.FromDateTime(now),
                                MbpsIn        = Math.Round(mbpsIn, 3),
                                MbpsOut       = Math.Round(mbpsOut, 3),
                                PacketsPerSec = pps
                            }, ct);
                        }
                    }

                    await call.RequestStream.CompleteAsync();
                    var resp = await call.ResponseAsync;
                    _log.LogInformation("GatewaySniffer stream closed: received={R}, anomalies={A}",
                                        resp.SamplesReceived, resp.AnomaliesDetected);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.LogWarning("GatewaySniffer stream error: {Msg}. Reconnecting in 5s...", ex.Message);
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            try { _device?.StopCapture(); _device?.Close(); } catch { }
            await base.StopAsync(cancellationToken);
            _channel?.Dispose();
        }

        private static string MacToString(byte[] buf, int off)
        {
            return string.Create(17, (buf, off), (span, state) =>
            {
                const string hex = "0123456789ABCDEF";
                var (b, o) = state;
                int p = 0;
                for (int i = 0; i < 6; i++)
                {
                    byte v = b[o + i];
                    span[p++] = hex[v >> 4];
                    span[p++] = hex[v & 0xF];
                    if (i < 5) span[p++] = ':';
                }
            });
        }

        private sealed class MacCounters
        {
            public long BytesIn;
            public long BytesOut;
            public uint Packets;
        }
    }
}
