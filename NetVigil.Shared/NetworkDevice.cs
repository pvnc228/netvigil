using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetVigil.Shared
{
    public class NetworkDevice
    {
        public string MacAddress { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Hostname { get; set; } = "Unknown";
        public string Vendor { get; set; } = "Generic";
        public bool IsOnline { get; set; }
        public DateTime LastSeen { get; set; }
        public double CurrentTrafficMbps { get; set; } // Текущий трафик
        public string Type { get; set; } = "Device"; // PC, Mobile, IoT
    }
}
