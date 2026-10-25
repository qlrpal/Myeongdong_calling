using System.Reflection;
using SIPSorcery.Net;

namespace VoiceNative;

public sealed record SdkBuildIdentity(string? AssemblyVersion, string? InformationalVersion, string? PatchRevision)
{
    public bool ProductionPatched => PatchRevision == "udp1-turn1";
    public static SdkBuildIdentity Read()
    {
        var assembly=typeof(UdpReceiver).Assembly;
        return new(assembly.GetName().Version?.ToString(),
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a=>a.Key=="MyeongdongSdkPatch")?.Value);
    }
}
