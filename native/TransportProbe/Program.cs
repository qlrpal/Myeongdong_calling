using VoiceNative;

try
{
    if (args is ["--sdk-build-check"])
    {
        var sdk=SdkBuildIdentity.Read();
        if(!sdk.ProductionPatched)throw new InvalidOperationException("The production patched SDK was not loaded.");
        Console.WriteLine("PASS: loaded production SIPSorcery "+sdk.InformationalVersion+" / "+sdk.PatchRevision);
    }
    else if (args is ["--turn-allocation-lifecycle", var lifecycleServer, var lifecycleReport, var lifecycleAddress])
        await TurnAllocationLifecycleProbe.RunAsync(lifecycleServer,lifecycleReport,lifecycleAddress);
    else if (args is ["--sdk-receive-race", var longRaceReport, var longRaceAddress, var longRaceCount])
        await SdkReceiveRaceProbe.RunAsync(longRaceReport,longRaceAddress,int.Parse(longRaceCount,System.Globalization.CultureInfo.InvariantCulture));
    else if (args is ["--sdk-receive-race", var raceReport, var raceAddress])
        await SdkReceiveRaceProbe.RunAsync(raceReport,raceAddress);
    else if (args is ["--two-host-group-boundary-pinned", var boundaryServer, var boundaryRoom, var boundaryReport, var boundaryRole, var boundaryWave, var boundaryEpoch, var boundaryAddress])
        await TwoHostGroupProbe.RunAsync(boundaryServer, boundaryRoom, boundaryReport, boundaryRole, boundaryWave, long.Parse(boundaryEpoch, System.Globalization.CultureInfo.InvariantCulture), false, boundaryAddress, true, 90);
    else if (args is ["--two-host-group-quick-pinned", var groupPinServer, var groupPinRoom, var groupPinReport, var groupPinRole, var groupPinWave, var groupPinEpoch, var groupPinAddress])
        await TwoHostGroupProbe.RunAsync(groupPinServer, groupPinRoom, groupPinReport, groupPinRole, groupPinWave, long.Parse(groupPinEpoch, System.Globalization.CultureInfo.InvariantCulture), true, groupPinAddress);
    else if (args is ["--two-host-group-soak-pinned", var groupSoakServer, var groupSoakRoom, var groupSoakReport, var groupSoakRole, var groupSoakWave, var groupSoakEpoch, var groupSoakAddress])
        await TwoHostGroupProbe.RunAsync(groupSoakServer, groupSoakRoom, groupSoakReport, groupSoakRole, groupSoakWave, long.Parse(groupSoakEpoch, System.Globalization.CultureInfo.InvariantCulture), false, groupSoakAddress, true);
    else if (args is ["--two-host-group-quick", var groupQuickServer, var groupQuickRoom, var groupQuickReport, var groupQuickRole, var groupQuickWave, var groupQuickEpoch])
        await TwoHostGroupProbe.RunAsync(groupQuickServer, groupQuickRoom, groupQuickReport, groupQuickRole, groupQuickWave, long.Parse(groupQuickEpoch, System.Globalization.CultureInfo.InvariantCulture), true);
    else if (args is ["--two-host-group", var groupServer, var groupRoom, var groupReport, var groupRole, var groupWave, var groupEpoch])
        await TwoHostGroupProbe.RunAsync(groupServer, groupRoom, groupReport, groupRole, groupWave, long.Parse(groupEpoch, System.Globalization.CultureInfo.InvariantCulture));
    else if (args is ["--reserve-learning-soak", var learnedSoakReport]) AdaptiveNetworkChecks.Run(learnedSoakReport, 900, true, true);
    else if (args is ["--reserve-continuous-soak", var continuousSoakReport]) AdaptiveNetworkChecks.Run(continuousSoakReport, 900, true, false);
    else if (args is ["--reserve-learning-checks", var learnedReport]) AdaptiveNetworkChecks.Run(learnedReport, 300, true, true);
    else if (args is ["--adaptive-clock-baseline-checks", var adaptiveClockBaseline]) AdaptiveNetworkChecks.Run(adaptiveClockBaseline, 300, false);
    else if (args is ["--adaptive-clock-candidate-checks", var adaptiveClockReport]) AdaptiveNetworkChecks.Run(adaptiveClockReport, 300, true);
    else if (args is ["--adaptive-network-long-checks", var adaptiveLongReport]) AdaptiveNetworkChecks.Run(adaptiveLongReport, 300);
    else if (args is ["--adaptive-network-checks", var adaptiveNetworkReport]) AdaptiveNetworkChecks.Run(adaptiveNetworkReport);
    else if (args is ["--audio-hotpath-benchmark", var hotpathReport]) AudioHotpathBenchmark.Run(hotpathReport);
    else if (args is ["--sender-scheduling-benchmark", var senderSchedulingReport]) await SenderSchedulingBenchmark.RunAsync(senderSchedulingReport);
    else if (args is ["--adaptive-qualification", var shortQualServer, var shortQualPrefix, var shortQualDirectory, var shortQualRole, var shortQualWave])
    {
        foreach (var adaptiveRoute in new[] { "direct", "relay" })
        foreach (var adaptiveFloor in new[] { "5", "10" })
            await ExtremeSoak.RunAsync(shortQualServer, shortQualPrefix + "-" + adaptiveRoute + "-" + adaptiveFloor,
                Path.Combine(shortQualDirectory, shortQualRole + "-" + adaptiveRoute + "-adaptive" + adaptiveFloor + ".json"),
                shortQualRole, shortQualWave, "qual-adaptive" + adaptiveFloor + ".json", 60, route: adaptiveRoute);
    }
    else if (args is ["--qualification-summary", var qualificationInput, var qualificationOutput]) QualificationSummary.Run(qualificationInput, qualificationOutput);
    else if (args is ["--priming-device-check", var primingDeviceOutput]) await ExtremeChecks.PrimingDeviceAsync(primingDeviceOutput);
    else if (args is ["--extreme-qualification", var qualificationServer, var qualificationPrefix, var qualificationDirectory, var qualificationRole, var qualificationWave])
    {
        foreach (var qualificationRoute in new[] { "direct", "relay" })
        foreach (var (label, qualificationMode) in new[] { ("baseline-a1", "baseline"), ("r10-head1", "qual-r10.json"), ("r5-head1", "qual-r5.json"), ("baseline-a2", "baseline") })
            await ExtremeSoak.RunAsync(qualificationServer, qualificationPrefix + "-" + qualificationRoute + "-" + label,
                Path.Combine(qualificationDirectory, qualificationRole + "-" + qualificationRoute + "-" + label + ".json"),
                qualificationRole, qualificationWave, qualificationMode, 30, route: qualificationRoute);
    }
    else if (args is ["--route-extreme-soak", var pinnedServer, var pinnedRoom, var pinnedReport, var pinnedRole, var pinnedWave, var pinnedMode, var pinnedSeconds, var pinnedRoute, var pinnedAddress])
        await ExtremeSoak.RunAsync(pinnedServer, pinnedRoom, pinnedReport, pinnedRole, pinnedWave, pinnedMode, int.Parse(pinnedSeconds, System.Globalization.CultureInfo.InvariantCulture), route: pinnedRoute, mediaAddress: pinnedAddress);
    else if (args is ["--route-extreme-soak", var routeExtremeServer, var routeExtremeRoom, var routeExtremeReport, var routeExtremeRole, var routeExtremeWave, var routeExtremeMode, var routeExtremeSeconds, var extremeRoute])
        await ExtremeSoak.RunAsync(routeExtremeServer, routeExtremeRoom, routeExtremeReport, routeExtremeRole, routeExtremeWave, routeExtremeMode, int.Parse(routeExtremeSeconds, System.Globalization.CultureInfo.InvariantCulture), route: extremeRoute);
    else if (args is ["--extreme-replay", var extremeInput, var extremeOutput]) ExtremeReplay.Run(extremeInput, extremeOutput);
    else if (args is ["--extreme-soak", var extremeServer, var extremeRoom, var extremeReport, var extremeRole, var extremeWave, var extremeMode, var extremeSeconds])
        await ExtremeSoak.RunAsync(extremeServer, extremeRoom, extremeReport, extremeRole, extremeWave, extremeMode, int.Parse(extremeSeconds, System.Globalization.CultureInfo.InvariantCulture));
    else if (args is ["--extreme-series", var seriesServer, var seriesRoom, var seriesDirectory, var seriesRole, var seriesWave, var seriesEpoch])
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(long.Parse(seriesEpoch, System.Globalization.CultureInfo.InvariantCulture));
        var modes = new[] { "baseline", "opus-5", "opus-2.5", "opus-zero", "pcm-1" };
        for (var i = 0; i < modes.Length; i++) await ExtremeSoak.RunAsync(seriesServer, seriesRoom + "-" + i,
            Path.Combine(seriesDirectory, seriesRole + "-" + modes[i] + "-call.json"), seriesRole, seriesWave, modes[i], 15, start.AddSeconds(i * 40));
    }
    else if (args is ["--extreme-checks"]) ExtremeChecks.Run();
    else if (args is ["--product-smoke-os-ports", var osSmokeServer, var osSmokeRoom, var osSmokeReport, var osSmokeRole, var osSmokeWave]) await ProductSmoke.RunAsync(osSmokeServer, osSmokeRoom, osSmokeReport, osSmokeRole, osSmokeWave, true);
    else if (args is ["--product-smoke", var smokeServer, var smokeRoom, var smokeReport, var smokeRole, var smokeWave]) await ProductSmoke.RunAsync(smokeServer, smokeRoom, smokeReport, smokeRole, smokeWave);
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
    else if (args is ["--path-isolation-ports", var portSource, var portPeer, var portServer, var portReference, var portSeconds, var portRate, var portBase, var portOutput])
        await PathIsolationProbe.RunAsync(portSource, portPeer, portServer, portReference, int.Parse(portSeconds, System.Globalization.CultureInfo.InvariantCulture), portOutput, int.Parse(portRate, System.Globalization.CultureInfo.InvariantCulture), portBase: int.Parse(portBase, System.Globalization.CultureInfo.InvariantCulture));
    else if (args is ["--path-isolation-voice", var qosSource, var qosPeer, var qosServer, var qosReference, var qosSeconds, var qosRate, var qosOutput])
        await PathIsolationProbe.RunAsync(qosSource, qosPeer, qosServer, qosReference, int.Parse(qosSeconds, System.Globalization.CultureInfo.InvariantCulture), qosOutput, int.Parse(qosRate, System.Globalization.CultureInfo.InvariantCulture), false, true);
    else if (args is ["--path-isolation-load", var loadSource, var loadPeer, var loadServer, var loadReference, var loadSeconds, var loadLane, var loadRate, var loadOutput] && loadLane is "lan" or "oci")
        await PathIsolationProbe.RunAsync(loadSource, loadPeer, loadServer, loadReference, int.Parse(loadSeconds, System.Globalization.CultureInfo.InvariantCulture), loadOutput, int.Parse(loadRate, System.Globalization.CultureInfo.InvariantCulture), loadLane == "lan");
    else if (args is ["--path-isolation", var isolationSource, var isolationPeer, var isolationServer, var isolationReference, var isolationSeconds, var isolationOutput])
        await PathIsolationProbe.RunAsync(isolationSource, isolationPeer, isolationServer, isolationReference, int.Parse(isolationSeconds, System.Globalization.CultureInfo.InvariantCulture), isolationOutput);
    else if (args is ["--path-control", var pathTarget, var pathGateway, var pathSeconds, var pathOutput])
        await IcmpProfile.RunAsync(pathTarget, pathGateway, pathOutput, int.Parse(pathSeconds, System.Globalization.CultureInfo.InvariantCulture), 200, false);
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
