namespace VoiceNative;

// One decoded PCM block at a time. Carries the original causal one-sample
// interpolation state; never reads beyond this block or speculates a PLC frame.
internal static class PcmClockInterpolator
{
    public static (int Consumed, int Written) Render(ReadOnlySpan<short> input, Span<short> output,
        double rate, ref double phase, ref short previous, ref short current, ref int fadeRemaining)
    {
        var count = Math.Min(input.Length, output.Length);
        if (rate == 1 && phase == 1 && fadeRemaining == 0 && count > 0)
        {
            output[0] = current;
            input[..(count - 1)].CopyTo(output[1..]);
            previous = count > 1 ? input[count - 2] : current;
            current = input[count - 1];
            return (count, count);
        }
        var p = phase; var before = previous; var sample = current; var fade = fadeRemaining;
        var consumed = 0; var written = 0;
        while (written < output.Length)
        {
            while (p >= 1)
            {
                if (consumed == input.Length) goto Finished;
                before = sample; sample = input[consumed++]; p -= 1;
            }
            var value = (short)(before + (sample - before) * p);
            if (fade > 0) { value = (short)(value * (240 - fade) / 240); fade--; }
            output[written++] = value;
            p += rate;
        }
    Finished:
        phase = p; previous = before; current = sample; fadeRemaining = fade;
        return (consumed, written);
    }
}
