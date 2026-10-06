using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;

namespace VoiceNative;

internal static class LanUdpProbe
{
    public static async Task RunAsync(string address, Action<string> report, CancellationToken token, IPAddress? localAddress = null)
    {
        if (!IPAddress.TryParse(address, out var remote) || remote.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("상대 PC의 내부 IPv4 주소를 입력하세요.");
        if (localAddress is null && (IPAddress.IsLoopback(remote) || NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Any(entry => entry.Address.Equals(remote))))
            throw new ArgumentException("입력한 주소는 이 PC 자신의 IP입니다. 상대 PC의 내부 IP를 입력하세요.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var udp = new UdpClient(new IPEndPoint(localAddress ?? IPAddress.Any, 42000));
        var endpoint = new IPEndPoint(remote, 42000);
        var nonce = Guid.NewGuid().ToString("N");
        var requests = 0;
        var replies = 0;
        report($"LAN UDP 검사 시작 · 상대 {remote}:42000 · 양쪽에서 검사를 실행하세요.");
        async Task Receive()
        {
            while (!deadline.IsCancellationRequested)
            {
                UdpReceiveResult incoming;
                try { incoming = await udp.ReceiveAsync(deadline.Token); }
                // Windows reports ICMP port-unreachable as a UDP receive reset when
                // the other participant has not started listening yet.
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                if (!incoming.RemoteEndPoint.Address.Equals(remote) || incoming.RemoteEndPoint.Port != 42000) continue;
                var message = Encoding.ASCII.GetString(incoming.Buffer);
                if (message.StartsWith("VOICE-LAN-PING|") && message.Length < 100)
                {
                    if (++requests == 1) report("LAN UDP: 상대 요청 수신 성공");
                    var bytes = Encoding.ASCII.GetBytes(message.Replace("VOICE-LAN-PING|", "VOICE-LAN-PONG|"));
                    await udp.SendAsync(bytes, incoming.RemoteEndPoint, deadline.Token);
                }
                else if (message.StartsWith("VOICE-LAN-PONG|" + nonce))
                {
                    if (++replies == 1) report("LAN UDP: 왕복 응답 성공");
                }
            }
        }
        async Task Send()
        {
            while (!deadline.IsCancellationRequested)
            {
                var bytes = Encoding.ASCII.GetBytes("VOICE-LAN-PING|" + nonce);
                await udp.SendAsync(bytes, endpoint, deadline.Token);
                await Task.Delay(500, deadline.Token);
            }
        }
        try { await Task.WhenAll(Receive(), Send()); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally { report($"LAN UDP 검사 종료 · 상대 요청 {requests}개 · 왕복 응답 {replies}개"); }
    }
}
