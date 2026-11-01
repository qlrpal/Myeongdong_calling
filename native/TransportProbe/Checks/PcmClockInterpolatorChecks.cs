using VoiceNative;

internal static class PcmClockInterpolatorChecks
{
    public static void Run()
    {
        var random = new Random(135);
        var cases = 0;
        foreach (var count in new[] { 0, 1, 2, 120, 256, 480 })
            foreach (var wanted in new[] { 0, 1, 128, 480 })
                foreach (var rate in new[] { .998, 1.0, 1.01 })
                    foreach (var initialPhase in new[] { 0.0, .37, 1.0, 1.8 })
                        foreach (var initialFade in new[] { 0, 48, 240 })
                        {
                            var input = Enumerable.Range(0, count).Select(_ => (short)random.Next(short.MinValue, short.MaxValue + 1)).ToArray();
                            var expected = new short[wanted]; var actual = new short[wanted];
                            var p1 = initialPhase; var p2 = initialPhase;
                            short a1 = -11321, a2 = a1, b1 = 23117, b2 = b1;
                            var f1 = initialFade; var f2 = initialFade;
                            var reference = Reference(input, expected, rate, ref p1, ref a1, ref b1, ref f1);
                            var measured = PcmClockInterpolator.Render(input, actual, rate, ref p2, ref a2, ref b2, ref f2);
                            if (reference != measured || !expected.SequenceEqual(actual) || p1 != p2 || a1 != a2 || b1 != b2 || f1 != f2)
                                throw new InvalidOperationException($"Clock interpolation changed PCM/state: {count}/{wanted}/{rate}/{initialPhase}/{initialFade}");
                            cases++;
                        }
        var data = new short[480]; var output = new short[480];
        double phase = 1; short previous = 0, current = 0; int fade = 0;
        for (var i = 0; i < 1000; i++) PcmClockInterpolator.Render(data, output, 1, ref phase, ref previous, ref current, ref fade);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) PcmClockInterpolator.Render(data, output, 1.0001, ref phase, ref previous, ref current, ref fade);
        if (GC.GetAllocatedBytesForCurrentThread() != before) throw new InvalidOperationException("Interpolator allocated on the render path.");
        Console.WriteLine($"PASS: {cases} interpolation partitions match original PCM/state; render allocation=0.");
    }

    // Independent copy of the original sample recurrence, including partial advance.
    private static (int Consumed, int Written) Reference(ReadOnlySpan<short> input, Span<short> output,
        double rate, ref double phase, ref short previous, ref short current, ref int fade)
    {
        var consumed = 0; var written = 0;
        while (written < output.Length)
        {
            while (phase >= 1)
            {
                if (consumed == input.Length) return (consumed, written);
                previous = current; current = input[consumed++]; phase -= 1;
            }
            var sample = (short)(previous + (current - previous) * phase);
            if (fade > 0) { sample = (short)(sample * (240 - fade) / 240); fade--; }
            output[written++] = sample; phase += rate;
        }
        return (consumed, written);
    }
}
