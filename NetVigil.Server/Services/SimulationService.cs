using NetVigil.Shared;

namespace NetVigil.Server.Services
{
    public class SimulationService
    {
        public List<NetworkDevice> Devices { get; private set; } = new();
        public SystemStats Stats { get; private set; } = new();
        private Random _rnd = new();

        public SimulationService()
        {
            // Начальное состояние
            Devices.Add(new NetworkDevice { MacAddress = "AA:BB:CC:01", IpAddress = "192.168.1.10", Hostname = "Admin-PC", Vendor = "Dell", IsOnline = true, Type = "PC", CurrentTrafficMbps = 45.2 });
            Devices.Add(new NetworkDevice { MacAddress = "AA:BB:CC:02", IpAddress = "192.168.1.15", Hostname = "iPhone-User", Vendor = "Apple", IsOnline = true, Type = "Mobile", CurrentTrafficMbps = 2.1 });
            Devices.Add(new NetworkDevice { MacAddress = "AA:BB:CC:03", IpAddress = "192.168.1.200", Hostname = "Smart-TV", Vendor = "Samsung", IsOnline = false, Type = "IoT", CurrentTrafficMbps = 0 });

            UpdateStats();
        }

        // Метод для легкого колебания цифр (чтобы выглядело живым)
        public void Tick()
        {
            foreach (var dev in Devices)
            {
                if (dev.IsOnline)
                {
                    // Случайное изменение трафика +/- 5mbps
                    dev.CurrentTrafficMbps += (_rnd.NextDouble() * 10) - 5;
                    if (dev.CurrentTrafficMbps < 0) dev.CurrentTrafficMbps = 0;
                    if (dev.CurrentTrafficMbps > 1000) dev.CurrentTrafficMbps = 900;
                    dev.CurrentTrafficMbps = Math.Round(dev.CurrentTrafficMbps, 1);
                }
            }
            UpdateStats();
        }

        public void AddRandomDevice()
        {
            var vendors = new[] { "HP", "Xiaomi", "Unknown", "Sony" };
            var types = new[] { "IoT", "Mobile", "Laptop" };

            Devices.Add(new NetworkDevice
            {
                MacAddress = $"AA:BB:CC:DD:{_rnd.Next(10, 99)}",
                IpAddress = $"192.168.1.{_rnd.Next(20, 250)}",
                Hostname = $"New-Device-{_rnd.Next(100, 999)}",
                Vendor = vendors[_rnd.Next(vendors.Length)],
                IsOnline = true,
                Type = types[_rnd.Next(types.Length)],
                CurrentTrafficMbps = _rnd.Next(1, 50)
            });
        }

        public void TriggerAttack()
        {
            Stats.AlertsCount += 5;
            foreach (var dev in Devices) dev.CurrentTrafficMbps += 500; // DDOS effect
        }

        public void Reset()
        {
            Stats.AlertsCount = 0;
            foreach (var dev in Devices) dev.CurrentTrafficMbps = _rnd.Next(1, 20);
        }

        private void UpdateStats()
        {
            Stats.TotalDevices = Devices.Count;
            Stats.OnlineDevices = Devices.Count(d => d.IsOnline);
            Stats.TotalTrafficIn = Math.Round(Devices.Sum(d => d.CurrentTrafficMbps), 1);
        }
    }
}