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
                foreach (var address in properties.UnicastAddresses.Select(a => a.Address).Where(a =>
                    a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
                    && !a.Equals(IPAddress.Any) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)))
                    result.Add(new(address.ToString(), properties.GetIPv4Properties().Index, $"{adapter.Name} · {address}"));
            }
            catch (NetworkInformationException) { /* Adapter disappeared during enumeration; explicit selection then fails closed. */ }
        }
        return result.ToArray();
    }
    public static MediaInterfaceChoice? Resolve(string? address)
    {
        if (address is null) return null;
        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("사용할 로컬 IPv4 주소를 선택하세요.", nameof(address));
        return Choices().FirstOrDefault(c => c.Address == parsed.ToString())
            ?? throw new ArgumentException("선택한 네트워크 주소를 현재 사용할 수 없습니다. 인터페이스를 다시 선택하세요.", nameof(address));
    }
    internal static void Apply(Socket socket, MediaInterfaceChoice choice)
    {
        if (socket.AddressFamily != AddressFamily.InterNetwork || choice.Index <= 0)
            throw new ArgumentException("Explicit media routing requires a valid IPv4 interface.", nameof(choice));
        socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31 /* Windows SDK IP_UNICAST_IF; interface index is network byte order. */, IPAddress.HostToNetworkOrder(choice.Index));
    }
}
