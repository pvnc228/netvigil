using NetVigil.Shared;

namespace NetVigil.Server.Services
{
    public class SimulationService
    {
        public List<NetworkDevice> DemoDevices { get; private set; } = new();
        public SystemStats DemoStats { get; private set; } = new();

        public List<NetworkDevice> RealDevices { get; private set; } = new();

        private Random _rnd = new();
        private bool _isDemoAttackActive = false;

        public SimulationService()
        {
            InitDemoData();
        }

        private void InitDemoData()
        {
            DemoDevices.Clear();
            DemoDevices.Add(new NetworkDevice { Hostname = "Director-PC", IpAddress = "192.168.1.10", Vendor = "Dell", IsOnline = true, CurrentTrafficMbps = 15.5 });
            DemoDevices.Add(new NetworkDevice { Hostname = "Office-Printer", IpAddress = "192.168.1.50", Vendor = "HP", IsOnline = true, CurrentTrafficMbps = 0.5 });
            DemoDevices.Add(new NetworkDevice { Hostname = "Lobby-Camera", IpAddress = "192.168.1.99", Vendor = "Hikvision", IsOnline = true, CurrentTrafficMbps = 4.2 });
            DemoDevices.Add(new NetworkDevice { Hostname = "Unknown-Tablet", IpAddress = "192.168.1.144", Vendor = "Android", IsOnline = true, CurrentTrafficMbps = 1.1 });
        }

        public void TickDemo()
        {
            foreach (var dev in DemoDevices)
            {
                double jitter = (_rnd.NextDouble() * 2.0) - 1.0;

                if (_isDemoAttackActive) jitter += _rnd.Next(20, 50);

                dev.CurrentTrafficMbps += jitter;

                if (dev.CurrentTrafficMbps < 0) dev.CurrentTrafficMbps = 0.1;
                if (dev.CurrentTrafficMbps > 1000) dev.CurrentTrafficMbps = 999;

                dev.CurrentTrafficMbps = Math.Round(dev.CurrentTrafficMbps, 1);
            }

            DemoStats.OnlineDevices = DemoDevices.Count;
            DemoStats.TotalDevices = DemoDevices.Count + 2; 
            DemoStats.TotalTrafficIn = Math.Round(DemoDevices.Sum(d => d.CurrentTrafficMbps), 1);

            
            if (_isDemoAttackActive) DemoStats.AlertsCount = 5;
            else DemoStats.AlertsCount = 0;
        }

        public void StartDemoAttack() => _isDemoAttackActive = true;
        public void StopDemoAttack() => _isDemoAttackActive = false;

        public void AddFakeDevice()
        {
            DemoDevices.Add(new NetworkDevice
            {
                Hostname = $"Guest-WiFi-{_rnd.Next(100, 999)}",
                IpAddress = $"192.168.1.{_rnd.Next(100, 200)}",
                Vendor = "Apple",
                IsOnline = true,
                CurrentTrafficMbps = 5.0
            });
        }
        public void UpdateRealDevice(NetworkDevice device)
        {
            var existing = RealDevices.FirstOrDefault(d => d.MacAddress == device.MacAddress);

            if (existing != null)
            {
                existing.IsOnline = true;
                existing.LastSeen = DateTime.Now;
                existing.IpAddress = device.IpAddress; 
            }
            else
            {
                device.LastSeen = DateTime.Now;
                device.IsOnline = true;
                RealDevices.Add(device);
            }
        }
    }
}