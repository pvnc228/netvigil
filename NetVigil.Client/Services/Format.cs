using System.Globalization;

namespace NetVigil.Client.Services
{
    public static class Format
    {
        public static (double value, string unit) Bandwidth(double mbps)
        {
            if (double.IsNaN(mbps) || mbps < 0) mbps = 0;
            if (mbps >= 1000.0) return (mbps / 1000.0, "Gbps");
            if (mbps < 1.0)     return (mbps * 1000.0, "Kbps");
            return (mbps, "Mbps");
        }

        public static string BandwidthValue(double mbps)
        {
            var (v, u) = Bandwidth(mbps);
            string fmt = u switch
            {
                "Gbps" => "F2",
                "Mbps" => v >= 100 ? "F0" : "F2",
                _      => v >= 100 ? "F0" : "F1"
            };
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        public static string BandwidthUnit(double mbps) => Bandwidth(mbps).unit;

        public static string BandwidthText(double mbps) =>
            $"{BandwidthValue(mbps)} {BandwidthUnit(mbps)}";
    }
}
