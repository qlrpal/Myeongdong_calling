namespace VoiceNative;

internal static class ClockEstimatorChecks
{
    public static void Run()
    {
        foreach (var ppm in new[] { -500, -200, 0, 200, 500 })
        {
            var fit = new RtpClockEstimator();
            for (long output = 0; output < 180 * 48000; output += 488)
            {
                var source = 4294967000L + (long)Math.Floor(output * (1 + ppm / 1000000.0) / 480) * 480;
                fit.Observe(output, source);
            }
            if (!fit.Ready || Math.Abs(fit.Bias * 1000000 - ppm) > 40)
                throw new InvalidOperationException($"Clock regression accuracy: {ppm} -> {fit.Bias * 1000000}");
            fit.Reset();
            if (fit.Ready || fit.Bias != 0) throw new InvalidOperationException("Old source clock estimate survived reset");
        }
        var legacy = new PlayoutClockController(240, true, true);
        var fine = new PlayoutClockController(240, true, true, fineClock: true);
        legacy.SetRateBias(.0002); fine.SetRateBias(.0002);
        for (var i = 0; i < 400; i++) { legacy.Observe(240, 480); fine.Observe(240, 480); }
        if (legacy.Rate != 1 || Math.Abs(fine.Rate - 1.0002) > .000001)
            throw new InvalidOperationException("Opt-in bias affected legacy mode or failed to correct consumption");
        fine.SetRateBias(.01);
        for (var i = 0; i < 400; i++) fine.Observe(240, 480);
        if (fine.Rate > 1.001001) throw new InvalidOperationException("Clock bias escaped bounded range");
        Console.WriteLine("PASS: quantized RTP/local-clock regression +/-200/500ppm, source reset, bounded feed-forward and unchanged legacy consumption.");
    }
}
