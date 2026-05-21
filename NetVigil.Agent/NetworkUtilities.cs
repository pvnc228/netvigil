using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;

namespace NetVigil.Agent
{
    public static class NetworkUtilities
    {
        private static readonly Lazy<Dictionary<string, string>> OuiTable
            = new(LoadOuiTable, isThreadSafe: true);

        private static Dictionary<string, string> LoadOuiTable()
        {
            var dict = new Dictionary<string, string>(40_000, StringComparer.Ordinal);
            try
            {
                var asm = typeof(NetworkUtilities).Assembly;
                var name = asm.GetManifestResourceNames()
                              .FirstOrDefault(n => n.EndsWith("oui-vendors.tsv",
                                                              StringComparison.Ordinal));
                if (name is null) return dict;

                using var stream = asm.GetManifestResourceStream(name);
                if (stream is null) return dict;
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var tab = line.IndexOf('\t');
                    if (tab != 6) continue; 
                    dict[line[..6]] = line[(tab + 1)..];
                }
            }
            catch {}
            return dict;
        }

        private static readonly Dictionary<string, string> Aliases =
            new(StringComparer.Ordinal)
            {
                ["4C5E0C"] = "MikroTik", ["CC2DE0"] = "MikroTik",
                ["DC2C6E"] = "MikroTik", ["B869F4"] = "MikroTik",
                ["E48D8C"] = "MikroTik", ["000C42"] = "MikroTik",
                ["6C3B6B"] = "MikroTik", ["744D28"] = "MikroTik",
                ["C4AD34"] = "MikroTik", ["488F5A"] = "MikroTik",
                ["18FD74"] = "MikroTik", ["2CC81B"] = "MikroTik",
                ["50FF20"] = "Keenetic", ["A0E4CB"] = "Keenetic",
                ["EC43F6"] = "Keenetic",
                ["525400"] = "QEMU/KVM VM",
                ["080027"] = "VirtualBox VM", ["0A0027"] = "VirtualBox VM",
                ["00155D"] = "Hyper-V VM",
                ["00163E"] = "Xen VM",
            };

        public static (IPAddress Ip, IPAddress Mask, NetworkInterface Interface) GetLocalInfo()
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                string name = ni.Name.ToLower();
                string desc = ni.Description.ToLower();
                if (name.Contains("wsl") || name.Contains("docker") || name.Contains("hyper-v") ||
                    name.Contains("virtual") || desc.Contains("virtual"))
                    continue;

                var ipProps = ni.GetIPProperties();

                if (ipProps.GatewayAddresses.Count == 0) continue;

                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        if (unicast.Address.ToString().StartsWith("192.168.56.")) continue;

                        return (unicast.Address, unicast.IPv4Mask, ni);
                    }
                }
            }

            throw new Exception("Не найден активный Wi-Fi или Ethernet с выходом к роутеру!");
        }

        public static string ToCidr(IPAddress ip, IPAddress mask)
        {
            var ipBytes = ip.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            if (ipBytes.Length != 4 || maskBytes.Length != 4)
                return ip.ToString();

            int prefix = 0;
            var network = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                prefix += BitOperations.PopCount((uint)maskBytes[i]);
                network[i] = (byte)(ipBytes[i] & maskBytes[i]);
            }
            return $"{new IPAddress(network)}/{prefix}";
        }

        public static async Task<Dictionary<IPAddress, string>> GetArpTableAsync()
        {
            var result = new Dictionary<IPAddress, string>();
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("arp", "-a")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                foreach (var line in output.Split('\n'))
                {
                    var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && IPAddress.TryParse(parts[0], out var ip))
                        result[ip] = parts[1].Replace('-', ':').ToUpper();
                }
            }
            catch { }
            return result;
        }

        public static bool IsRandomMac(string mac)
        {
            if (string.IsNullOrEmpty(mac) || mac.Length < 2) return false;
            return byte.TryParse(mac[..2], System.Globalization.NumberStyles.HexNumber,
                                 null, out var first) && (first & 0x02) != 0;
        }

        public static string GetVendorFromMac(string mac)
        {
            if (string.IsNullOrEmpty(mac) || mac.Length < 8) return "Unknown vendor";
            if (IsRandomMac(mac)) return "Private MAC";

            Span<char> key = stackalloc char[6];
            key[0] = char.ToUpperInvariant(mac[0]);
            key[1] = char.ToUpperInvariant(mac[1]);
            key[2] = char.ToUpperInvariant(mac[3]);
            key[3] = char.ToUpperInvariant(mac[4]);
            key[4] = char.ToUpperInvariant(mac[6]);
            key[5] = char.ToUpperInvariant(mac[7]);
            var oui = new string(key);

            if (Aliases.TryGetValue(oui, out var alias)) return alias;
            if (OuiTable.Value.TryGetValue(oui, out var name)) return name;
            return "Unknown vendor";
        }

        public static List<IPAddress> GetIpRange(IPAddress ip, IPAddress mask)
        {
            var ips = new List<IPAddress>();
            byte[] ipBytes = ip.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();

            byte[] startIp = new byte[4];
            for (int i = 0; i < 4; i++) startIp[i] = (byte)(ipBytes[i] & maskBytes[i]);

            for (int i = 1; i < 255; i++)
            {
                var newIp = new IPAddress(new byte[] { startIp[0], startIp[1], startIp[2], (byte)i });

                if (!newIp.Equals(ip))
                {
                    ips.Add(newIp);
                }
            }
            return ips;
        }
    }
}