namespace VoiceNative;

internal static class PairBarrierChecks
{
    internal static void Run()
    {
        static void Require(bool value) { if (!value) throw new InvalidOperationException("Pair barrier protocol regression"); }
        var a = new PairBarrier("run", "a", "same"); var b = new PairBarrier("run", "b", "same");
        a.Receive(b.Message("ready")); Require(!a.Ready("ready"));
        b.Receive(a.Message("ready")); Require(b.Ready("ready"));
        a.Receive(b.Message("ready")); Require(a.Ready("ready"));
        a.Receive(b.Message("ready")); Require(!a.Ready("baseline"));
        a.Receive(new PairBarrier("other", "b", "same").Message("baseline")); Require(!a.Ready("baseline"));
        a.Receive(new PairBarrier("run", "a", "same").Message("baseline")); Require(!a.Ready("baseline"));
        a.Receive("run|b|same|rogue|baseline|1"); Require(!a.Ready("baseline"));
        a.Receive(new string('x', 513)); Require(a.Error is null);
        a.Receive(new PairBarrier("run", "b", "different").Message("baseline")); Require(a.Error is not null && !a.Ready("ready"));
        var loopback = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .First(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback);
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        var index = loopback.GetIPProperties().GetIPv4Properties().Index;
        MediaInterfaceSelection.Apply(socket, new("127.0.0.1", index, "loopback check"));
        Require(MediaInterfaceSelection.ReadIndex(socket) == index);
        Console.WriteLine("PASS: pair phase acknowledgements, duplicate/stale/wrong-role rejection and configuration mismatch.");
        Console.WriteLine("PASS: Windows IPv4 UNICAST_IF set/get byte-order round trip on an actual socket.");
    }
}
