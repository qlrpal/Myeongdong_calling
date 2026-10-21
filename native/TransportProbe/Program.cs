using VoiceNative;

try
{
    if (args is ["--product-smoke", var smokeServer, var smokeRoom, var smokeReport, var smokeRole, var smokeWave]) await ProductSmoke.RunAsync(smokeServer, smokeRoom, smokeReport, smokeRole, smokeWave);
    else if (args is ["--product-checks", var productDirectory]) await ProductChecks.RunAsync(productDirectory);
    else if (args is ["--scheduled-route-speech-once", var onceServer, var onceRoom, var onceReport, var onceRole, var onceWave, var onceEpoch, var onceMode])
    {
        if (onceMode is not ("direct" or "relay")) throw new ArgumentException("Route must be direct or relay.");
        Environment.ExitCode = await RtpSoakProbe.RunAsync(onceServer, onceRoom, onceReport, onceRole, 120,
            PlayoutPolicy.LowLatency, measure: false, profile: OpusProfile.LowDelay64,
            speech: SpeechFixture.Load(onceWave), forceRelay: onceMode == "relay", requireDirect: onceMode == "direct",
            connectTimeoutSeconds: 40, intermediateCapture: false, tracePipeline: true,
            startAt: DateTimeOffset.FromUnixTimeSeconds(long.Parse(onceEpoch, System.Globalization.CultureInfo.InvariantCulture)));
    }
    else if (args is ["--scheduled-route-speech-ab", var routeServer, var routeRoom, var routeDirectory, var routeRole, var routeWave, var routeEpoch])
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(long.Parse(routeEpoch, System.Globalization.CultureInfo.InvariantCulture));
        var round = 0;
        foreach (var (label, relay) in new[] { ("direct-a1", false), ("relay-b", true), ("direct-a2", false) })
        {
            var code = await RtpSoakProbe.RunAsync(routeServer, routeRoom + "-" + label,
                Path.Combine(routeDirectory, routeRole + "-" + label + "-call.json"), routeRole, 120,
                PlayoutPolicy.LowLatency, measure: false, profile: OpusProfile.LowDelay64,
                speech: SpeechFixture.Load(routeWave), forceRelay: relay, requireDirect: !relay, connectTimeoutSeconds: 40,
                intermediateCapture: false, tracePipeline: true, startAt: start.AddSeconds(round++ * 150));
            if (code != 0) { Environment.ExitCode = code; break; }
        }
    }
    else if (args is ["--scheduled-responsive-speech-ab", var scheduledServer, var scheduledRoom, var scheduledDirectory, var scheduledRole, var scheduledWave, var scheduledEpoch])
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(long.Parse(scheduledEpoch, System.Globalization.CultureInfo.InvariantCulture));
        var round = 0;
        foreach (var (label, responsive) in new[] { ("a1", false), ("b", true), ("a2", false) })
        {
            var code = await RtpSoakProbe.RunAsync(scheduledServer, scheduledRoom + "-" + label,
                Path.Combine(scheduledDirectory, scheduledRole + "-" + label + "-call.json"), scheduledRole, 120,
                PlayoutPolicy.LowLatency, measure: false, profile: OpusProfile.LowDelay64,
                speech: SpeechFixture.Load(scheduledWave), forceRelay: true, connectTimeoutSeconds: 40,
                intermediateCapture: false, responsiveReserve: responsive, tracePipeline: true,
                startAt: start.AddSeconds(round++ * 150));
            if (code != 0) { Environment.ExitCode = code; break; }
        }
    }
    else if (args is ["--gap-restart-device", var gapDeviceOutput]) await RenderLatencyProbe.GapRestartAsync(gapDeviceOutput);
    else if (args is ["--gap-restart-test"]) GapRestartChecks.Run();
    else if (args is ["--gap-pressure-replay", var pressureInput, var pressureOutput]) PlayoutReplay.Run(pressureInput, pressureOutput, comparePressure: true);
    else if (args is ["--restart-reserve-replay", var restartInput, var restartOutput]) PlayoutReplay.Run(restartInput, restartOutput, compareRestart: true);
    else if (args is ["--responsive-speech-ab", var responsiveServer, var responsiveRoom, var responsiveDirectory, var responsiveRole, var responsiveWave])
    {
        foreach (var (label, responsive) in new[] { ("a1", false), ("b", true), ("a2", false) })
        {
            var code = await RtpSoakProbe.RunAsync(responsiveServer, responsiveRoom + "-" + label,
                Path.Combine(responsiveDirectory, responsiveRole + "-" + label + "-call.json"), responsiveRole, 120,
                PlayoutPolicy.LowLatency, measure: false, profile: OpusProfile.LowDelay64,
                speech: SpeechFixture.Load(responsiveWave), forceRelay: true, connectTimeoutSeconds: 40,
                intermediateCapture: false, responsiveReserve: responsive, tracePipeline: true);
            if (code != 0) { Environment.ExitCode = code; break; }
        }
    }
    else if (args is ["--recovery-policy-replay", var policyInput, var policyOutput]) PlayoutReplay.Run(policyInput, policyOutput, compareRecovery: true);
    else if (args is ["--recovery-policy-test", var policyTestOutput]) RecoveryGapChecks.Run(policyTestOutput, comparePolicies: true);
    else if (args is ["--measurement-clock-respond"]) await MeasurementClockProbe.Respond();
    else if (args is ["--measurement-clock", var clockSyncOutput]) await MeasurementClockProbe.Sync(clockSyncOutput);
    else if (args is ["--reserve-speech-ab", var speechAbServer, var speechAbRoom, var speechAbDirectory, var speechAbRole, var speechAbWave])
    {
        foreach (var (label, reserve) in new[] { ("a1", 20), ("b", 10), ("a2", 20) })
        {
            var code = await RtpSoakProbe.RunAsync(speechAbServer, speechAbRoom + "-" + label,
                Path.Combine(speechAbDirectory, speechAbRole + "-speech-" + label + ".json"), speechAbRole, 120,
                PlayoutPolicy.LowLatency, measure: false, profile: OpusProfile.LowDelay64,
                speech: SpeechFixture.Load(speechAbWave), forceRelay: true, connectTimeoutSeconds: 120,
                intermediateCapture: false, reserveMs: reserve);
            if (code != 0) { Environment.ExitCode = code; break; }
        }
    }
    else if (args is ["--stage-optimize", var stageServer, var stageRoom, var stageOutput, var stageRole, var stageReserve])
        Environment.ExitCode = await StageOptimizationProbe.RunAsync(stageServer, stageRoom, stageOutput, stageRole, int.Parse(stageReserve, System.Globalization.CultureInfo.InvariantCulture));
    else if (args is ["--crossapp-calibrate",var calibrationOutput]) await CrossAppProbe.Calibrate(calibrationOutput);
    else if (args is ["--crossapp-receive",var crossReceiveOutput]) await CrossAppProbe.Receiver(crossReceiveOutput);
    else if (args is ["--crossapp-send",var crossSendOutput]) await CrossAppProbe.Sender(crossSendOutput);
    else if (args is ["--crossapp-call",var commonServer,var commonRoom,var commonOutput,var commonRole])
        Environment.ExitCode=await RtpSoakProbe.RunAsync(commonServer,commonRoom,commonOutput,commonRole,60,PlayoutPolicy.LowLatency,measure:false,forceRelay:true,connectTimeoutSeconds:120,captureDeviceId:Environment.GetEnvironmentVariable("VOICE_COMPARE_CAPTURE_ID"),intermediateCapture:false);
    else if (args is ["--capture-pipeline", var captureServer, var captureRoom, var captureReport, var captureRole, var captureMode])
    {
        if (captureMode is not ("standard" or "five")) throw new ArgumentException("Capture mode must be standard or five.");
        Environment.ExitCode = await PipelineProbe.RunAsync(captureServer, captureRoom, captureReport, captureRole,
            measure: false, fixedReserveClockTarget: true, transientEmptyQueueHold: true, freshRestart: true, intermediateCapture: captureMode == "five");
    }
    else if (args is ["--recovery-gap-test", var recoveryGapOutput]) RecoveryGapChecks.Run(recoveryGapOutput);
    else if (args is ["--external-speech-soak", var externalServer, var externalRoom, var externalReport, var externalRole, var externalMode, var externalWave])
    {
        if (externalMode is not ("auto" or "relay")) throw new ArgumentException("External mode must be auto or relay.");
        Environment.ExitCode = await RtpSoakProbe.RunAsync(externalServer,externalRoom,externalReport,externalRole,300,PlayoutPolicy.LowLatency,measure:false,profile:OpusProfile.LowDelay64,speech:SpeechFixture.Load(externalWave),forceRelay:externalMode=="relay",connectTimeoutSeconds:120);
    }
    else if (args is ["--bound-speech-soak", var speechServer, var speechRoom, var speechReport, var speechRole, var speechAddress, var speechWave])
        Environment.ExitCode = await RtpSoakProbe.RunAsync(speechServer,speechRoom,speechReport,speechRole,600,PlayoutPolicy.LowLatency,measure:false,mediaAddress:speechAddress,profile:OpusProfile.LowDelay64,speech:SpeechFixture.Load(speechWave));
    else if (args is ["--speech-fixture-test"]) SpeechFixture.Check();
    else if (args is ["--bound-lowdelay-soak", var codecServer, var codecRoom, var codecReport, var codecRole, var codecAddress])
        Environment.ExitCode = await RtpSoakProbe.RunAsync(codecServer,codecRoom,codecReport,codecRole,120,PlayoutPolicy.LowLatency,measure:false,mediaAddress:codecAddress,profile:OpusProfile.LowDelay64);
    else if (args is ["--bound-call-soak", var boundCallServer, var boundCallRoom, var boundCallReport, var boundCallRole, var boundCallAddress])
        Environment.ExitCode = await RtpSoakProbe.RunAsync(boundCallServer,boundCallRoom,boundCallReport,boundCallRole,300,PlayoutPolicy.LowLatency,measure:true,mediaAddress:boundCallAddress);
    else if (args is ["--render-call-ab", var renderCallServer, var renderCallPrefix, var renderCallReport, var renderCallRole])
        Environment.ExitCode = await RtpAbProbe.RunAsync(renderCallServer, renderCallPrefix, renderCallReport, renderCallRole, compareRender: true);
    else if (args is ["--output-clock-acoustic-device", var acousticClockOutput]) await RenderLatencyProbe.ClockAsync(acousticClockOutput, acoustic: true);
    else if (args is ["--output-clock-focused-device", var focusedClockOutput]) await RenderLatencyProbe.ClockAsync(focusedClockOutput, focused: true);
    else if (args is ["--output-clock-device", var clockOutput]) await RenderLatencyProbe.ClockAsync(clockOutput);
    else if (args is ["--codec-render-reference-test"]) RenderLatencyProbe.CodecReferenceCheck();
    else if (args is ["--render-codec-path-device", var codecOutput]) await RenderLatencyProbe.RunAsync(codecOutput, codecComparison: true);
    else if (args is ["--render-reserve-path-device", var reserveOutput]) await RenderLatencyProbe.RunAsync(reserveOutput, reserveComparison: true);
    else if (args is ["--render-combined-path-device", var renderCombinedOutput]) await RenderLatencyProbe.RunAsync(renderCombinedOutput, combinedComparison: true);
    else if (args is ["--render-category-path-device", var renderCategoryOutput]) await RenderLatencyProbe.RunAsync(renderCategoryOutput, categoryComparison: true);
    else if (args is ["--render-raw-path-device", var renderRawOutput]) await RenderLatencyProbe.RunAsync(renderRawOutput, rawComparison: true);
    else if (args is ["--render-path-test"]) RenderLatencyProbe.SelfTest();
    else if (args is ["--render-path-device", var renderPathOutput]) await RenderLatencyProbe.RunAsync(renderPathOutput);
    else if (args is ["--render-load-soak", var renderLoadServer, var renderLoadRoom, var renderLoadOutput, var renderLoadRole])
        Environment.ExitCode = await RenderDeviceChecks.LoadSoakAsync(renderLoadServer, renderLoadRoom, renderLoadOutput, renderLoadRole);
    else if (args is ["--render-restart-device", var renderRestartOutput]) await RenderDeviceChecks.RestartAsync(renderRestartOutput);
    else if (args is ["--render-target-test"]) RenderDeviceChecks.Targets();
    else if (args is ["--render-device", var renderDeviceOutput]) await RenderDeviceChecks.RunAsync(renderDeviceOutput);
    else if (args is ["--render-pipeline", var renderServer, var renderRoom, var renderOutput, var renderRole, var renderMode])
    {
        if (renderMode is not ("standard" or "bounded")) throw new ArgumentException("Invalid render mode.");
        Environment.ExitCode = await PipelineProbe.RunAsync(renderServer, renderRoom, renderOutput, renderRole,
            measure: false, fixedReserveClockTarget: true, transientEmptyQueueHold: true, freshRestart: true, boundedRender: renderMode == "bounded");
    }
    else if (args is ["--render-budget", var renderBudgetOutput]) RenderBudgetChecks.Run(renderBudgetOutput);
    else if (args is ["--capture-period-test"]) CapturePeriodChecks.Periods();
    else if (args is ["--capture-period-device", var capturePeriodOutput]) await CapturePeriodChecks.DeviceAsync(capturePeriodOutput);
    else if (args is ["--playout-policy-test"]) await PlayoutTargetChecks.PolicyAsync();
    else if (args is ["--playout-soak", var soakPolicyServer, var soakPolicyRoom, var soakPolicyReport, var soakPolicyRole, var soakPolicySeconds])
        Environment.ExitCode = await RtpSoakProbe.RunAsync(soakPolicyServer, soakPolicyRoom, soakPolicyReport, soakPolicyRole, int.Parse(soakPolicySeconds, System.Globalization.CultureInfo.InvariantCulture), PlayoutPolicy.LowLatency, measure: false);
    else if (args is ["--playout-reserve-replay", var reserveInput, var reserveReplayOutput]) PlayoutReplay.Run(reserveInput, reserveReplayOutput, compareReserve: true);
    else if (args is ["--playout-replay", var replayInput, var replayOutput]) PlayoutReplay.Run(replayInput, replayOutput);
    else if (args is ["--playout-target-test", var targetReport]) PlayoutTargetChecks.Run(targetReport);
    else if (args is ["--playout-target-probe", var targetServer, var targetRoom, var targetPath, var targetRole, var targetMode])
    {
        if (targetMode is not ("learned" or "fixed" or "transient" or "fresh")) throw new ArgumentException("Target mode must be learned, fixed, transient or fresh.");
        Environment.ExitCode = await PipelineProbe.RunAsync(targetServer, targetRoom, targetPath, targetRole, measure: false, fixedReserveClockTarget: targetMode != "learned", transientEmptyQueueHold: targetMode is "transient" or "fresh", freshRestart: targetMode == "fresh");
    }
    else if (args is ["--wlan-metrics", var wlanSeconds, var wlanOutput])
        await WlanMetricsProbe.RunAsync(int.Parse(wlanSeconds, System.Globalization.CultureInfo.InvariantCulture), wlanOutput);
    else if (args is ["--wlan-metrics-test"]) WlanMetricsProbe.SelfTest();
    else if (args is ["--udp-pattern", var patternPeer, var patternLocal, var patternRemote, var patternSeconds, var patternRole, var patternRun, var patternFile, var patternMode, var patternOutput])
    {
        if (patternMode is not ("uniform" or "replay")) throw new ArgumentException("Invalid pattern mode.");
        Environment.ExitCode = await PacedUdpProbe.RunAsync(patternPeer, int.Parse(patternLocal, System.Globalization.CultureInfo.InvariantCulture), int.Parse(patternRemote, System.Globalization.CultureInfo.InvariantCulture), 32, int.Parse(patternSeconds, System.Globalization.CultureInfo.InvariantCulture), patternRole, patternRun, patternOutput, patternFile, patternMode == "uniform");
    }
    else if (args is ["--udp-bound", var boundPeer, var boundSource, var boundRole, var boundRun, var boundSeconds, var boundPath])
    {
        Environment.ExitCode = await PacedUdpProbe.RunAsync(boundPeer,42000,42000,64,int.Parse(boundSeconds,System.Globalization.CultureInfo.InvariantCulture),boundRole,boundRun,boundPath,sourceAddress:boundSource);
    }
    else if (args is ["--udp-paced", var udpPeer, var udpLocal, var udpRemote, var udpBytes, var udpSeconds, var udpRole, var udpRun, var udpPath])
        Environment.ExitCode = await PacedUdpProbe.RunAsync(udpPeer, int.Parse(udpLocal, System.Globalization.CultureInfo.InvariantCulture), int.Parse(udpRemote, System.Globalization.CultureInfo.InvariantCulture), int.Parse(udpBytes, System.Globalization.CultureInfo.InvariantCulture), int.Parse(udpSeconds, System.Globalization.CultureInfo.InvariantCulture), udpRole, udpRun, udpPath);
    else if (args is ["--icmp-profile", var icmpTarget, var icmpGateway, var icmpPath])
        await IcmpProfile.RunAsync(icmpTarget, icmpGateway, icmpPath);
    else if (args is ["--pipeline-passive", var passiveServer, var passiveRoom, var passivePath, var passiveRole])
        Environment.ExitCode = await PipelineProbe.RunAsync(passiveServer, passiveRoom, passivePath, passiveRole, measure: false);
    else if (args is ["--pipeline-probe", var pipelineServer, var pipelineRoom, var pipelinePath, var pipelineRole])
        Environment.ExitCode = await PipelineProbe.RunAsync(pipelineServer, pipelineRoom, pipelinePath, pipelineRole);
    else if (args is ["--adaptive-ab-test", var adaptiveServer, var adaptivePrefix, var adaptiveReport, var adaptiveRole])
        Environment.ExitCode = await RtpAbProbe.RunAsync(adaptiveServer, adaptivePrefix, adaptiveReport, adaptiveRole, compareAdaptive: true);
    else if (args is ["--rtp-stress-test", var stressReport])
        RtpStressChecks.Run(stressReport);
    else if (args is ["--rtp-soak-test", var soakServer, var soakRoom, var soakReport, var soakRole, var soakSeconds])
        Environment.ExitCode = await RtpSoakProbe.RunAsync(soakServer, soakRoom, soakReport, soakRole, int.Parse(soakSeconds, System.Globalization.CultureInfo.InvariantCulture));
    else if (args is ["--rtp-ab-test", var rtpServer, var rtpPrefix, var rtpReport, var rtpRole])
        Environment.ExitCode = await RtpAbProbe.RunAsync(rtpServer, rtpPrefix, rtpReport, rtpRole);
    else if (args is ["--opus-ab-test", var server, var prefix, var report, var role])
        Environment.ExitCode = await OpusAbProbe.RunAsync(server, prefix, report, role);
    else if (args is ["--automated-call", var address, var room, var name, var path, var side])
        Environment.ExitCode = await AutomatedCallProbe.RunAsync(address, room, name, path, side == "receiver");
    else if (!await SelfChecks.TryRunAsync(args))
        await WebRtcProbe.RunAsync(args);
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL: " + error);
    Environment.ExitCode = 1;
}
