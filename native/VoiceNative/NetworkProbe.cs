using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
namespace VoiceNative;
internal static class NetworkProbe
{
    public static async Task RunAsync(string address, Action<string> report, CancellationToken token)
    {
        if (!IPAddress.TryParse(address, out var remote) || remote.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("상대 IPv4 주소를 입력하세요.");
        using var route = new UdpClient(); route.Connect(remote, 42000);
        report($"경로 검사 · 대상 {remote} · OS 선택 송신 주소 {route.Client.LocalEndPoint} (통화 ICE 경로와 비교)");
        using var ping = new Ping();
        for (var i = 0; i < 3; i++)
        {
            token.ThrowIfCancellationRequested();
            var reply = await ping.SendPingAsync(remote, 1000).WaitAsync(token);
            report(reply.Status == IPStatus.Success ? $"ICMP ping {reply.RoundtripTime}ms · TTL {reply.Options?.Ttl} · 전체 음성 지연은 아님"
                : $"ICMP ping {reply.Status} · ping 차단만으로 UDP 실패를 판단할 수 없습니다.");
        }
    }
}
