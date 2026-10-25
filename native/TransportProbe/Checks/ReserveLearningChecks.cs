using VoiceNative;

internal static class ReserveLearningChecks
{
    public static void Run()
    {
        static void Require(bool ok, string message)
        { if (!ok) throw new InvalidOperationException(message); }
        static void Advance(AdaptivePlayoutReserve value, int seconds, bool bad = false)
        { for (var i = 0; i < seconds * 100; i++) value.Advance(480, bad); }
        var learned = new AdaptivePlayoutReserve(240, true, shortFrames: true,
            maximumShortSamples: 960, initialLearning: true);
        Advance(learned, 30);
        Require(learned.Frozen && learned.TargetSamples == 240, "Clean learning must retain minimum latency");
        var target = learned.TargetSamples;
        learned.Advance(480, true); Advance(learned, 3);
        Require(learned.Frozen && learned.TargetSamples == target && learned.Relearns == 0, "Isolated glitch reopened learning");
        Advance(learned, 3, true);
        Require(!learned.Frozen && learned.Relearns == 1, "Sustained degradation must reopen learning");
        Require(learned.TryHold(120), "Relearning must permit bounded growth");
        Advance(learned, 30);
        Require(!learned.Frozen && learned.LearningState == "ContinuousFallback", "Safety fallback must remain continuous until source reset");
        learned.Rebuffer();
        Require(!learned.Frozen && learned.Relearns == 1 && learned.TargetSamples == 960, "Long gap must reopen bounded recovery");
        learned.Reset();
        Require(learned.TargetSamples == 240 && !learned.Frozen, "Source reset must start at configured floor");
        var hold = new AdaptivePlayoutReserve(240, true, shortFrames: true, initialLearning: true);
        Advance(hold, 30);
        Require(hold.TryHold(120) && !hold.Frozen && hold.LearningState == "ContinuousFallback",
            "Required timestamp-preserving hold must retain continuous safety behavior");
        var spacedLate = new AdaptivePlayoutReserve(240, true, shortFrames: true, initialLearning: true);
        spacedLate.Arrival(0, 0); spacedLate.Arrival(480, 30);
        for (var i = 2; i < 34; i++) spacedLate.Arrival(i * 480, 30 + (i - 1) * 30);
        Advance(spacedLate, 30);
        spacedLate.Late(); Advance(spacedLate, 2); spacedLate.Late();
        Require(spacedLate.Frozen && spacedLate.TargetSamples == 240,
            "Isolated late arrivals must not accumulate across the one-second pressure window while held");
        spacedLate.Reset(); Advance(spacedLate, 30);
        Require(spacedLate.Frozen && spacedLate.LearningState == "Holding", "Source reset must permit a new learning cycle");
        var elevated = new AdaptivePlayoutReserve(240, true, shortFrames: true, initialLearning: true);
        elevated.TryHold(120);
        Advance(elevated, 31, true);
        Require(!elevated.Frozen && elevated.TargetSamples > 240, "Elevated reserve must not be locked in");
        var continuous = new AdaptivePlayoutReserve(240, true, shortFrames: true);
        Advance(continuous, 60);
        Require(!continuous.Frozen && continuous.TryHold(120), "Legacy continuous behavior changed");
        var disabled = new AdaptivePlayoutReserve(240, false, initialLearning: true);
        Advance(disabled, 60, true);
        Require(disabled.LearningState == "Disabled" && !disabled.TryHold(), "Disabled policy changed");
        foreach (var name in LatencyExperiment.Names) LatencyExperiment.FromName(name)?.Validate();
        Console.WriteLine("PASS: initial reserve learning, transient hold, sustained recovery, source reset, legacy policy");
    }
}
