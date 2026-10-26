using System.Reflection;
using SIPSorcery.Net;

namespace VoiceNative;

public sealed record SdkBuildIdentity(string? AssemblyVersion, string? InformationalVersion, string? PatchRevision)
{
    public bool ProductionPatched => PatchRevision == "udp1-turn1-rtp0";
    public static SdkBuildIdentity Read()
    {
        var assembly=typeof(UdpReceiver).Assembly;
        var revision=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a=>a.Key=="MyeongdongSdkPatch")?.Value;
        return new(assembly.GetName().Version?.ToString(),
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            revision);
    }
}
