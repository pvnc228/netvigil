using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetVigil.Shared
{
    public class SystemStats
    {
        public int TotalDevices { get; set; }
        public int OnlineDevices { get; set; }
        public double TotalTrafficIn { get; set; }
        public double TotalTrafficOut { get; set; }
        public int AlertsCount { get; set; }
    }
}