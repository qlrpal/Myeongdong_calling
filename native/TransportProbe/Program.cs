using VoiceNative;

try
{
    if (args is ["--playout-policy-test"]) await PlayoutTargetChecks.PolicyAsync();
    else if (args is ["--playout-soak", var soakPolicyServer, var soakPolicyRoom, var soakPolicyReport, var soakPolicyRole, var soakPolicySeconds])
        Environment.ExitCode = await RtpSoakProbe.RunAsync(soakPolicyServer, soakPolicyRoom, soakPolicyReport, soakPolicyRole, int.Parse(soakPolicySeconds, System.Globalization.CultureInfo.InvariantCulture), PlayoutPolicy.LowLatency, measure: false);
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
