using SIPSorcery.Net;

// Optional test SDK API. Stock package returns null; no private fields are replaced.
internal static class SdkReceiveDiagnostics
{
    internal static object? Snapshot() => typeof(UdpReceiver).Assembly
        .GetType("SIPSorcery.Net.UdpReceiveExperiment")?
        .GetMethod("Snapshot", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?
        .Invoke(null, null);
}
