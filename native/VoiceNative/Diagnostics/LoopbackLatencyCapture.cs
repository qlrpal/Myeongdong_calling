using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace VoiceNative;

// Diagnostic-only render endpoint capture. Does not replace or retransmit the microphone.
internal sealed class LoopbackLatencyCapture : IDisposable
{
    private readonly WasapiRecorder recorder;
    private readonly MMDevice endpoint;
    private readonly AcousticCaptureProbe detector = new();
    private readonly List<byte> pending = new();
    private double? frameAt;
    public string Device { get; }
    public LoopbackLatencyCapture(string endpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        endpoint = enumerator.GetDevice(endpointId);
        Device = endpoint.FriendlyName;
        recorder = new WasapiRecorderBuilder().WithDevice(endpoint).WithSharedMode().WithEventSync()
            .WithLoopbackCapture().WithBufferLength(20).WithFormat(new WaveFormat(48000, 16, 1)).Build();
        recorder.DataAvailable += (data, flags, _, qpc) =>
        {
            if ((flags & (AudioClientBufferFlags.TimestampError | AudioClientBufferFlags.DataDiscontinuity)) != 0)
            { pending.Clear(); frameAt = null; return; }
            if (pending.Count == 0) frameAt = qpc / 10000000.0;
            pending.AddRange(data.ToArray());
            while (pending.Count >= 1920)
            {
                var pcm = new short[960];
                Buffer.BlockCopy(pending.GetRange(0, 1920).ToArray(), 0, pcm, 0, 1920);
                pending.RemoveRange(0, 1920);
                detector.Process(pcm, frameAt);
                if (frameAt is not null) frameAt += 0.020;
            }
        };
        try { recorder.StartRecording(); }
        catch { recorder.Dispose(); endpoint.Dispose(); throw; }
    }
    public void Arm(string id, Action<double> detected) => detector.Arm(id, detected);
    public void Cancel(string? id = null) => detector.Cancel(id);
    public void Dispose() { detector.Cancel(); recorder.StopRecording(); recorder.Dispose(); endpoint.Dispose(); }
}
