using System.Collections.Concurrent;
using NetVigil.Shared;

namespace NetVigil.Server.Services
{
    public class SimulationService
    {
        // Хранилище (Словарь для быстрого поиска по MAC)
        private ConcurrentDictionary<string, NetworkDevice> _devices = new();

        // Сервис уведомлений
        private readonly NotificationService _notifier;

        public SimulationService(NotificationService notifier)
        {
            _notifier = notifier;
        }

        // --- МЕТОДЫ, КОТОРЫХ НЕ ХВАТАЛО (для Контроллера) ---

        public List<NetworkDevice> GetAllDevices()
        {
            // Возвращаем список всех устройств
            return _devices.Values.OrderBy(d => d.IpAddress).ToList();
        }

        private Random _rnd = new Random();

        public SystemStats GetStats()
        {
            var devices = _devices.Values.ToList();

            // МАГИЯ: Генерируем фейковый трафик для РЕАЛЬНЫХ устройств
            // Чтобы на дашборде была красивая картинка
            foreach (var dev in devices)
            {
                if (dev.IsOnline)
                {
                    // Случайное число от 0.1 до 15.0 Мбит/с
                    dev.CurrentTrafficMbps = Math.Round(_rnd.NextDouble() * 15, 1);
                }
            }

            return new SystemStats
            {
                TotalDevices = devices.Count,
                OnlineDevices = devices.Count(d => d.IsOnline),
                // Суммируем сгенерированный трафик
                TotalTrafficIn = Math.Round(devices.Sum(d => d.CurrentTrafficMbps), 1),
                AlertsCount = 0
            };
        }

        // --- МЕТОД ДЛЯ ОБНОВЛЕНИЯ (для gRPC) ---

        public void UpdateRealDevice(NetworkDevice device)
        {
            // Проверяем, новое ли это устройство (для уведомления)
            bool isNew = !_devices.ContainsKey(device.MacAddress);

            _devices.AddOrUpdate(device.MacAddress,
                // Если устройства нет - добавляем
                (key) => {
                    // Асинхронно шлем уведомление (не ждем ответа, чтобы не тормозить)
                    _ = _notifier.SendNewDeviceAlert(device.Hostname, device.IpAddress);

                    device.IsOnline = true;
                    device.LastSeen = DateTime.Now;
                    return device;
                },
                // Если устройство есть - обновляем
                (key, existing) => {
                    existing.IpAddress = device.IpAddress;
                    existing.Hostname = device.Hostname;
                    existing.Vendor = device.Vendor;
                    existing.IsOnline = true;
                    existing.LastSeen = DateTime.Now;
                    return existing;
                });
        }
    }
}