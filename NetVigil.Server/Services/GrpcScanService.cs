using Grpc.Core;
using NetVigil.Shared.Protos;

namespace NetVigil.Server.Services
{
    public class GrpcScanService : NetworkScanner.NetworkScannerBase
    {
        private readonly ILogger<GrpcScanService> _logger;
        private readonly MetricsService _metrics;
        private readonly AgentRegistry _agents;

        public GrpcScanService(ILogger<GrpcScanService> logger, MetricsService metrics, AgentRegistry agents)
        {
            _logger = logger;
            _metrics = metrics;
            _agents = agents;
        }

        public override Task<ScanResponse> ReportAgentInfo(AgentInfo request, ServerCallContext context)
        {
            _agents.Update(new NetVigil.Shared.AgentInfoSnapshot
            {
                AgentId = request.AgentId,
                Hostname = request.Hostname,
                InterfaceName = request.InterfaceName,
                InterfaceDesc = request.InterfaceDesc,
                LocalIp = request.LocalIp,
                SubnetCidr = request.SubnetCidr,
                Mode = request.Mode,
                Version = request.Version,
                GrpcTarget = request.GrpcTarget,
                StartedAt = request.StartedAt?.ToDateTime() ?? DateTime.UtcNow
            });
            return Task.FromResult(new ScanResponse { Success = true, Message = "OK" });
        }

        public override Task<ScanResponse> ReportDevice(DeviceData request, ServerCallContext context)
        {
            _logger.LogInformation("gRPC discover: {Hostname} ({Ip})", request.Hostname, request.IpAddress);

            var device = new NetVigil.Shared.NetworkDevice
            {
                MacAddress = request.MacAddress,
                IpAddress = request.IpAddress,
                Hostname = request.Hostname,
                Vendor = request.Vendor,
                IsOnline = true,
                CurrentTrafficMbps = request.CurrentTrafficMbps
            };

            _metrics.UpdateRealDevice(device);

            return Task.FromResult(new ScanResponse { Success = true, Message = "OK" });
        }

        public override async Task<StreamResponse> StreamMetrics(
            IAsyncStreamReader<MetricSample> requestStream,
            ServerCallContext context)
        {
            uint received = 0;
            uint anomalies = 0;

            await foreach (var sample in requestStream.ReadAllAsync(context.CancellationToken))
            {
                received++;
                var ts = sample.Timestamp?.ToDateTime() ?? DateTime.UtcNow;
                var mbps = sample.MbpsIn + sample.MbpsOut;
                var result = _metrics.IngestSample(sample.MacAddress, mbps, ts);
                if (result.IsAnomalous) anomalies++;
            }

            return new StreamResponse
            {
                Success = true,
                SamplesReceived = received,
                AnomaliesDetected = anomalies
            };
        }
    }
}
