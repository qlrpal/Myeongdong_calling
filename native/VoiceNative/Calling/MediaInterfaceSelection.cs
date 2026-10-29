using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VoiceNative;

internal sealed record MediaInterfaceChoice(string? Address, int Index, string Label)
{
    public override string ToString() => Label;
}

// Explicit per-call selection only. The default preserves OS routing, including VPN routes.
internal static class MediaInterfaceSelection
{
    public static MediaInterfaceChoice[] Choices()
    {
        var result = new List<MediaInterfaceChoice> { new(null, 0, "OS 자동 (VPN 경로 유지)") };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            try
            {
                var properties = adapter.GetIPProperties();
                foreach (var address in properties.UnicastAddresses.Select(a => a.Address).Where(IsSelectable))
                {
                    var index = address.AddressFamily == AddressFamily.InterNetworkV6
                        ? properties.GetIPv6Properties().Index : properties.GetIPv4Properties().Index;
                    if (index > 0) result.Add(new(address.ToString(), index, $"{adapter.Name} · {address}"));
                }
            }
            catch (NetworkInformationException) { /* Adapter disappeared during enumeration; explicit selection then fails closed. */ }
        }
        return result.ToArray();
    }
    public static MediaInterfaceChoice? Resolve(string? address)
    {
        if (address is null) return null;
        if (!IPAddress.TryParse(address, out var parsed) || !IsSelectable(parsed))
            throw new ArgumentException("사용할 로컬 IPv4/IPv6 유니캐스트 주소를 선택하세요.", nameof(address));
        return Choices().FirstOrDefault(c => c.Address == parsed.ToString())
            ?? throw new ArgumentException("선택한 네트워크 주소를 현재 사용할 수 없습니다. 인터페이스를 다시 선택하세요.", nameof(address));
    }
    internal static void Apply(Socket socket, MediaInterfaceChoice choice)
    {
        if (!IPAddress.TryParse(choice.Address, out var address) || socket.AddressFamily != address.AddressFamily || choice.Index <= 0)
            throw new ArgumentException("Explicit media routing requires a matching address family/interface.", nameof(choice));
        var ipv6 = socket.AddressFamily == AddressFamily.InterNetworkV6;
        // Windows IPV6_UNICAST_IF uses host byte order; IPv4 IP_UNICAST_IF uses network order.
        socket.SetSocketOption(ipv6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP, (SocketOptionName)31,
            ipv6 ? choice.Index : IPAddress.HostToNetworkOrder(choice.Index));
    }
    // Windows GET returns host byte order for both families, unlike IPv4 SET.
    // https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options
    internal static int ReadIndex(Socket socket) => (int)socket.GetSocketOption(
        socket.AddressFamily == AddressFamily.InterNetworkV6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP,
        (SocketOptionName)31)!;
    internal static bool IsSelectable(IPAddress address) => !IPAddress.IsLoopback(address)
        && (address.AddressFamily == AddressFamily.InterNetwork
            ? !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast)
                && address.GetAddressBytes()[0] < 224 && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal)
            : address.AddressFamily == AddressFamily.InterNetworkV6 && !address.Equals(IPAddress.IPv6Any)
                && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.IsIPv4MappedToIPv6);
}
