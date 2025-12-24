using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetVigil.Agent
{
    public static class NetworkUtilities
    {
        public static (IPAddress Ip, IPAddress Mask) GetLocalInfo()
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var ni in interfaces)
            {
                // 1. Отсеиваем выключенные и Loopback
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                // 2. Отсеиваем по имени (VirtualBox, Docker, WSL, VPN)
                string name = ni.Name.ToLower();
                string desc = ni.Description.ToLower();
                if (name.Contains("wsl") || name.Contains("docker") || name.Contains("hyper-v") ||
                    name.Contains("virtual") || desc.Contains("virtual"))
                    continue;

                var ipProps = ni.GetIPProperties();

                // 3. САМОЕ ГЛАВНОЕ: Ищем сеть с Шлюзом (Gateway). 
                // У настоящего Wi-Fi всегда есть шлюз (адрес роутера). У виртуалок его обычно нет.
                if (ipProps.GatewayAddresses.Count == 0) continue;

                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    // Ищем IPv4
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        // Исключаем 192.168.56.x (стандарт VirtualBox, если он вдруг прошел проверки выше)
                        if (unicast.Address.ToString().StartsWith("192.168.56.")) continue;

                        return (unicast.Address, unicast.IPv4Mask);
                    }
                }
            }

            throw new Exception("Не найден активный Wi-Fi или Ethernet с выходом к роутеру!");
        }

        public static List<IPAddress> GetIpRange(IPAddress ip, IPAddress mask)
        {
            var ips = new List<IPAddress>();
            byte[] ipBytes = ip.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();

            // Вычисляем базовый адрес сети
            byte[] startIp = new byte[4];
            for (int i = 0; i < 4; i++) startIp[i] = (byte)(ipBytes[i] & maskBytes[i]);

            // Сканируем только последний октет (от 1 до 254)
            // Это покрывает стандартную домашнюю сеть /24
            for (int i = 1; i < 255; i++)
            {
                // Формируем IP: 192.168.X.i
                var newIp = new IPAddress(new byte[] { startIp[0], startIp[1], startIp[2], (byte)i });

                // Не добавляем свой собственный IP (зачем себя пинговать?)
                if (!newIp.Equals(ip))
                {
                    ips.Add(newIp);
                }
            }
            return ips;
        }
    }
}