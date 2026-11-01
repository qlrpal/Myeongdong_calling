"""Host-local frame stages, with explicit normal-window exclusions and full quality.
Usage: python analyze_steady_pipeline.py PARTICIPANT.json OUTPUT.json
Never adds stages across hosts or labels render supply as physical playback.
"""
from pathlib import Path
import json,math,sys


def distribution(values):
    ordered=sorted(values)
    if any(not math.isfinite(v) or v<0 for v in ordered):raise ValueError('Invalid local duration')
    return {'n':len(ordered),**({} if not ordered else {
        'medianMs':ordered[math.ceil(len(ordered)*.5)-1],
        'p95Ms':ordered[math.ceil(len(ordered)*.95)-1],
        'p99Ms':ordered[math.ceil(len(ordered)*.99)-1],'maxMs':ordered[-1]})}


def unique(records,key):
    result={}
    for row in records:
        identity=key(row)
        if identity in result:raise ValueError('Ambiguous frame identity')
        result[identity]=row
    return result


def read_exclusions(reads):
    result=[]
    for index,current in enumerate(reads):
        previous=reads[max(0,index-1)]
        # PipelineRead.PlcFrames is this read's delta, not a cumulative counter.
        if current['PlcFrames']>0 or not current['PlayoutStarted'] or current['ProvidedSamples']<current['RequestedSamples']:
            result.append((previous['CalledAt']-.25,current['ReturnedAt']+.25))
    return result


def analyze(report,include_frames=False):
    if report.get('error') or report.get('stage')!='complete':raise ValueError('Incomplete functional trial')
    trace=report['baselineTrace']
    if trace is None or trace['EvictedRecords']!=0:raise ValueError('Missing/evicted trace')
    sends=trace['Sends'];receives=trace['Receives'];reads=trace['Reads'];plays=trace['Playout']
    low=max(sends[0]['SendStart'],receives[0]['At'],reads[0]['CalledAt'])+10
    high=min(sends[-1]['SendEnd'],receives[-1]['At'],reads[-1]['ReturnedAt'])-.25
    if high-low<10:raise ValueError('Insufficient settled interior')
    identity=lambda row:(row['Peer'],row['Ssrc'],row['Timestamp'])
    entries=unique([a for a in trace['Admissions'] if a['Outcome']=='queued'],identity)
    supplied=unique([p for p in plays if p['Outcome']=='played'],identity)
    receive_map=unique(receives,identity)
    exclusions=[]
    for previous,current in zip(receives,receives[1:]):
        if current['Peer']!=previous['Peer']:raise ValueError('Expected one stable receiver')
        if current['At']-previous['At']>.03:exclusions.append((previous['At']-.25,current['At']+.25))
    exclusions.extend(read_exclusions(reads))
    tx_exclusions=[]
    callbacks=trace.get('CaptureCallbacks') or []
    for previous,current in zip(callbacks,callbacks[1:]):
        if current['CalledAt']-previous['CalledAt']>.03 or current['ReturnedAt']-current['CalledAt']>.01:
            tx_exclusions.append((previous['CalledAt']-.25,current['ReturnedAt']+.25))
    def normal(a,b,mask):return low<=a<=b<=high and not any(a<=y and b>=x for x,y in mask)
    tx={};negative_device=0;tx_points=[];rx_points=[]
    for send in sends:
        origin=send.get('Capture')
        if not origin or not normal(origin['FirstCallbackAt'],send['SendEnd'],tx_exclusions):continue
        def add(name,a,b):tx.setdefault(name,[]).append((b-a)*1000)
        add('firstCallbackToFrameReady',origin['FirstCallbackAt'],origin['FrameReadyAt'])
        add('encode',origin['EncodeStart'],origin['EncodeEnd'])
        add('encodeEndToApi',origin['EncodeEnd'],send['SendStart'])
        add('queueWait',send['QueuedAt'],send['DequeuedAt'])
        if origin['FirstSampleAt'] is not None:
            if origin['FirstSampleAt']>origin['FirstCallbackAt']:negative_device+=1
            else:
                add('deviceReportedSampleToFirstCallback',origin['FirstSampleAt'],origin['FirstCallbackAt'])
                add('deviceReportedSampleToApi',origin['FirstSampleAt'],send['SendStart'])
                age=(send['SendStart']-origin['FirstSampleAt'])*1000
                samples=send.get('FrameSamples')
                if samples is None or samples<=0:raise ValueError('Missing frame sample count')
                tx_points.append(dict(ssrc=send['Ssrc'],timestamp=send['Timestamp'],samples=samples,firstSampleAgeMs=age))
    rx={};counts={'queued':0,'played':0,'queuedWithoutPlayed':0,'excludedPlayed':0}
    for key,entry in entries.items():
        if not low<=entry['CalledAt']<=high:continue
        counts['queued']+=1;play=supplied.get(key)
        if play is None:counts['queuedWithoutPlayed']+=1;continue
        counts['played']+=1
        if not normal(entry['CalledAt'],play['At'],exclusions):counts['excludedPlayed']+=1;continue
        if play['DecodeStart'] is None or play['DecodeEnd'] is None:raise ValueError('Missing decode chronology')
        if not entry['CalledAt']<=play['DecodeStart']<=play['DecodeEnd']<=play['At']:raise ValueError('Invalid frame chronology')
        for name,a,b in [('admissionToDecode',entry['CalledAt'],play['DecodeStart']),('decode',play['DecodeStart'],play['DecodeEnd']),
                         ('decodeEndToSupply',play['DecodeEnd'],play['At']),('admissionToSupply',entry['CalledAt'],play['At'])]:
            rx.setdefault(name,[]).append((b-a)*1000)
        received=receive_map.get(key)
        if received:
            rx.setdefault('receiveCallbackToAdmission',[]).append((entry['CalledAt']-received['At'])*1000)
            rx_points.append(dict(ssrc=entry['Ssrc'],timestamp=entry['Timestamp'],receiveToSupplyMs=(play['At']-received['At'])*1000))
    window=next(w for w in report['windows'] if w['phase']=='baseline');a,b=window['samples'][0],window['samples'][-1]
    pa,pb=a['Peers'][0]['Audio'],b['Peers'][0]['Audio'];ra,rb=pa['RtpPlayout'],pb['RtpPlayout']
    quality={k:rb[k]-ra[k] for k in ['ConcealedMs','Rebuffers','LatePackets','InvalidPackets','DuplicatePackets','RecoveryDiscardedMs']}
    quality.update(underfillMs=pb['UnderfillMs']-pa['UnderfillMs'],renderEmpty=pb['Render']['EmptyEvents']-pa['Render']['EmptyEvents'],
                   safetyFallback=pb['Render']['SafetyFallbacks']-pa['Render']['SafetyFallbacks'],
                   sendDrops=b['Sender']['DroppedFrames']-a['Sender']['DroppedFrames'],
                   captureDiscontinuities=b['Microphone']['CaptureDiscontinuities']-a['Microphone']['CaptureDiscontinuities'],
                   captureTimestampErrors=b['Microphone']['CaptureTimestampErrors']-a['Microphone']['CaptureTimestampErrors'])
    if any(v<0 for v in quality.values()):raise ValueError('Counter reset')
    result={'role':report['role'],'run':report['run'],'buildHash':report['buildHash'],'probeHash':report['probeHash'],
            'requestedFrameMs':report.get('testFrameMs'),'settledSeconds':high-low,'rxExclusionIntervals':len(exclusions),'txExclusionIntervals':len(tx_exclusions),'frameCounts':counts,
            'negativeDeviceTimes':negative_device,'tx':{k:distribution(v) for k,v in tx.items()},'rx':{k:distribution(v) for k,v in rx.items()},
            'wholeBaselineQuality':quality,'cpuCores':window['cpuCores'],'allocatedBytes':window['allocatedBytes'],'gcPauseMs':window['gcPauseMs'],
            'actual':{'capturePeriodMs':b['Microphone']['EnginePeriodMs'],'renderPeriodMs':pb['EnginePeriodMs'],
                      'outputPaddingMs':pb['Render']['TargetPaddingMs'],'outputCapacityMs':pb['Render']['CapacityMs'],
                      'rtpReserveMs':rb['TargetReserveMs'],'clockTargetMs':rb['ClockTargetMs'],
                      'clockFilteredQueueMs':rb['ClockFilteredQueueMs'],'clockUpperBandMs':rb.get('ClockUpperDeadbandMs'),
                      'clockLowerBandMs':rb.get('ClockLowerDeadbandMs')},
            'limits':'Host-local frame timing only. First 10s and +/-250ms around >30ms arrivals, PLC, unstarted/short reads excluded from stage distributions; full quality counters are unfiltered. Matched played frames have survivor bias; counts retained. Render supply is not device playback. Device-reported sample time is not acoustic mic time. Do not sum medians or cross-host clocks into mouth-to-ear latency.'}
    if include_frames:result['frameDetails']={'tx':tx_points,'rx':rx_points}
    return result


if __name__=='__main__':
    data=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
    Path(sys.argv[2]).write_text(json.dumps(analyze(data),ensure_ascii=False,indent=2),encoding='utf-8')
