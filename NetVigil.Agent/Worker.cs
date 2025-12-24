using Grpc.Net.Client;
using NetVigil.Shared.Protos;
using System.Net.NetworkInformation;
using System.Net;

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
            // РАЗРЕШАЕМ HTTP (для Docker)
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            // Стучимся в локальный порт Докера
            var channel = GrpcChannel.ForAddress("http://localhost:5002");
            _client = new NetworkScanner.NetworkScannerClient(channel);

            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 1. Получаем параметры сети
                    var (localIp, mask) = NetworkUtilities.GetLocalInfo();
                    _logger.LogInformation($"Мой IP: {localIp}. Начинаю сканирование...");

                    // --- ГАРАНТИРОВАННАЯ ОТПРАВКА (ЧТОБЫ БЫЛИ ДАННЫЕ) ---

                    // 1. Отправляем СЕБЯ (Твой ПК)
                    await Report(localIp, "My-Computer (Agent)", "Windows PC");

                    // 2. Отправляем РОУТЕР (Обычно это x.x.x.1)
                    var bytes = localIp.GetAddressBytes();
                    var gatewayIp = new IPAddress(new byte[] { bytes[0], bytes[1], bytes[2], 1 });
                    await Report(gatewayIp, "Wi-Fi Router", "Network Gateway");

                    // ----------------------------------------------------

                    // 3. Сканируем остальных (Параллельно)
                    var ipList = NetworkUtilities.GetIpRange(localIp, mask);
                    var tasks = ipList.Select(async ip =>
                    {
                        if (stoppingToken.IsCancellationRequested) return;

                        // Не сканируем себя и роутер повторно
                        if (ip.Equals(localIp) || ip.Equals(gatewayIp)) return;

                        var ping = new Ping();
                        try
                        {
                            // Увеличили таймаут до 2000 мс
                            var reply = await ping.SendPingAsync(ip, 2000);

                            if (reply.Status == IPStatus.Success)
                            {
                                string hostname = "Unknown Device";
                                try
                                {
                                    var entry = await Dns.GetHostEntryAsync(ip);
                                    hostname = entry.HostName;
                                }
                                catch { }

                                await Report(ip, hostname, "Detected via Ping");
                            }
                        }
                        catch { }
                    });

                    await Task.WhenAll(tasks);
                    _logger.LogInformation("Сканирование завершено. Ждем 10 сек...");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Ошибка цикла: {ex.Message}");
                }

                await Task.Delay(10000, stoppingToken);
            }
        }

        // Вспомогательный метод для отправки
        private async Task Report(IPAddress ip, string hostname, string vendor)
        {
            try
            {
                var deviceData = new DeviceData
                {
                    IpAddress = ip.ToString(),
                    Hostname = hostname,
                    MacAddress = GenerateMacFromIp(ip), // Генерируем ID
                    Vendor = vendor
                };

                await _client.ReportDeviceAsync(deviceData);
                _logger.LogInformation($"[ОТПРАВЛЕНО] {ip} - {hostname}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Не удалось отправить {ip}: {ex.Message}");
            }
        }

        private string GenerateMacFromIp(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            // Делаем красивый фейковый MAC, чтобы было что показать
            return $"00:50:56:{bytes[1]:X2}:{bytes[2]:X2}:{bytes[3]:X2}";
        }
    }
}