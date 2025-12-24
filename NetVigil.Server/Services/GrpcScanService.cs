using Grpc.Core;
using NetVigil.Shared.Protos;

namespace NetVigil.Server.Services
{
    public class GrpcScanService : NetworkScanner.NetworkScannerBase
    {
        private readonly ILogger<GrpcScanService> _logger;
        private readonly SimulationService _sim; 

        public GrpcScanService(ILogger<GrpcScanService> logger, SimulationService sim)
        {
            _logger = logger;
            _sim = sim;
        }

        public override Task<ScanResponse> ReportDevice(DeviceData request, ServerCallContext context)
        {
            _logger.LogInformation($"gRPC update: {request.Hostname} ({request.IpAddress})");

            var deviceModel = new NetVigil.Shared.NetworkDevice
            {
                MacAddress = request.MacAddress,
                IpAddress = request.IpAddress,
                Hostname = request.Hostname,
                Vendor = request.Vendor,
                IsOnline = true,
                CurrentTrafficMbps = 0 // Агент пока не шлет трафик, ставим 0
            };

            // Сохраняем в наше обновленное хранилище
            _sim.UpdateRealDevice(deviceModel);

            return Task.FromResult(new ScanResponse { Success = true, Message = "OK" });
        }
    }
}