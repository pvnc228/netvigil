using Grpc.Net.Client;
using NetVigil.Shared.Protos;

namespace NetVigil.Agent
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private NetworkScanner.NetworkScannerClient _client;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            var url = Environment.GetEnvironmentVariable("SERVER_URL") ?? "https://localhost:7186";
            var channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                HttpHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }
            });
            _client = new NetworkScanner.NetworkScannerClient(channel);

            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Агент: Сканирую сеть...");

                    var request = new DeviceData
                    {
                        MacAddress = "AA-FF-00-11-22-33",
                        IpAddress = "192.168.1.55",
                        Hostname = "Real-Agent-PC",
                        Vendor = "Intel"
                    };

                    var reply = await _client.ReportDeviceAsync(request);
                    _logger.LogInformation($"Сервер ответил: {reply.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Ошибка соединения: {ex.Message}");
                }

                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}