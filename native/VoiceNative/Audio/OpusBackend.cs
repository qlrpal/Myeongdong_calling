using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;

namespace VoiceNative;

public sealed record OpusBackendInfo(string Requested, bool NativeActive, string Encoder, string Decoder,
    string Version, string? FallbackReason, string? NativeLibrarySha256);

// Select once, before creating any codecs. Never change process CWD or let the
// legacy loader copy/delete a DLL there. The module stays loaded for codec lifetimes.
public static class OpusBackend
{
    private static readonly Lazy<OpusBackendInfo> State = new(Initialize);
    public static OpusBackendInfo Snapshot() => State.Value;
    private static OpusBackendInfo Initialize()
    {
        var requested = Environment.GetEnvironmentVariable("VOICE_OPUS_BACKEND") ?? "auto";
        if (requested is not ("auto" or "managed" or "native"))
            throw new ArgumentException("VOICE_OPUS_BACKEND must be auto, managed or native.");
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        string? reason = requested == "managed" ? "Managed backend explicitly selected for comparison." : null;
        string? nativeHash = null;
        if (requested != "managed")
        {
            var rid = "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var candidates = new[] { Path.Combine(AppContext.BaseDirectory, "opus.dll"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "opus.dll") };
            var path = OperatingSystem.IsWindows() ? candidates.FirstOrDefault(File.Exists) : null;
            if (path is null) reason = "Bundled native Opus for this process architecture is unavailable.";
            else
            {
                try
                {
                    // An absolute, application-owned path also makes Concentus' legacy
                    // basename probe find this already-loaded module without CWD writes.
                    var module = NativeLibrary.Load(path);
                    NativeLibrary.SetDllImportResolver(typeof(OpusCodecFactory).Assembly,
                        (name, _, _) => name == "opus" ? module : nint.Zero);
                    using var file = File.OpenRead(path);
                    nativeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
                    OpusCodecFactory.AttemptToUseNativeLibrary = true;
                }
                catch (Exception error) when (error is DllNotFoundException or BadImageFormatException)
                { reason = "Bundled native Opus could not be loaded: " + error.GetType().Name; }
            }
        }
        using var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        using var decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        var native = encoder.GetType().FullName == "Concentus.Native.NativeOpusEncoder"
            && decoder.GetType().FullName == "Concentus.Native.NativeOpusDecoder";
        if (!native)
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            reason ??= "Codec factory did not select the prepared native backend.";
        }
        if (requested == "native" && !native) throw new DllNotFoundException(reason);
        return new(requested, native, encoder.GetType().Name, decoder.GetType().Name,
            encoder.GetVersionString(), reason, native ? nativeHash : null);
    }
}
