using NAudio.Wave;

namespace VoiceNative;

// Test-only PCM replacement after real capture/frame assembly, before encoding.
// Real microphone clocks and discontinuities remain observable; hardware audio is excluded.
internal sealed class SpeechFixture(short[] samples)
{
    private int position;
    private long injected;
    public static SpeechFixture Load(string path)
    {
        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.SampleRate != 48000 || format.Channels != 1 || format.BitsPerSample != 16)
            throw new ArgumentException("Speech fixture must be 48kHz mono 16-bit PCM WAV.");
        if (reader.Length < 96000 || reader.Length > 48000 * 2 * 120 || reader.Length % 2 != 0)
            throw new ArgumentException("Speech fixture must contain 1–120 seconds of aligned PCM.");
        var bytes = new byte[checked((int)reader.Length)];
        reader.ReadExactly(bytes);
        var pcm = new short[bytes.Length / 2]; Buffer.BlockCopy(bytes,0,pcm,0,bytes.Length);
        if (!pcm.Any(value => Math.Abs((int)value) >= 1000)) throw new ArgumentException("Speech fixture is silent or too quiet.");
        return new(pcm);
    }
    public void Fill(short[] frame)
    {
        for (var i=0;i<frame.Length;i++) { frame[i]=samples[position++]; if(position==samples.Length)position=0; }
        Interlocked.Add(ref injected,frame.Length);
    }
    public object Snapshot() => new { kind="looped-synthetic-speech", sampleRate=48000, fixtureSamples=samples.Length,
        injectedSamples=Interlocked.Read(ref injected), replacesMicrophonePcm=true, physicalInputNotMeasured=true };
    public static void Check()
    {
        var source = new SpeechFixture([1,2,3]); var frame=new short[5]; source.Fill(frame);
        if(!frame.SequenceEqual(new short[]{1,2,3,1,2}))throw new InvalidOperationException("Fixture wrapping failed.");
        source.Fill(frame);
        if(!frame.SequenceEqual(new short[]{3,1,2,3,1}) || source.injected!=10)throw new InvalidOperationException("Fixture continuity failed.");
        Console.WriteLine("PASS: speech fixture wraps continuously at capture frame boundaries.");
    }
}
