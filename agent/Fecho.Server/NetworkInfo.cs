using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Fecho.Server;

public static class NetworkInfo
{
    /// <summary>
    /// Só aceitamos conexões de endereços de rede local. Um pedido vindo da
    /// internet (por redirecionamento de porta no roteador, por exemplo) é
    /// recusado antes de qualquer outra verificação.
    /// </summary>
    public static bool IsLocalAddress(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        }

        return false;
    }

    /// <summary>
    /// Escolhe o IPv4 da rede local que vai no QR code: interfaces ativas,
    /// Ethernet ou Wi-Fi, com gateway (a rede de casa) primeiro.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetLanAddresses()
    {
        var result = new List<(IPAddress Ip, int Score)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
            var virtualish = desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v")
                || desc.Contains("vethernet") || desc.Contains("wsl") || desc.Contains("docker") || desc.Contains("vpn");

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                            && !g.Address.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!IsLocalAddress(ua.Address) || IPAddress.IsLoopback(ua.Address)) continue;
                var score = (hasGateway ? 10 : 0) + (virtualish ? -20 : 0)
                    + (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 2 : 0)
                    + (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 3 : 0);
                result.Add((ua.Address, score));
            }
        }
        return result.OrderByDescending(r => r.Score).Select(r => r.Ip).ToList();
    }
}
