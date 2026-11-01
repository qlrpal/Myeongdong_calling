import unittest
from analyze_sdk_boundaries import analyze


class BoundaryJoinChecks(unittest.TestCase):
    def test_clock_offset_gaps_and_window_limit(self):
        def record(stage, at, seq):
            return dict(Stage=stage, At=at, Sequence=seq, Ssrc=42, Timestamp=seq*480,
                        RtpTimestamp=seq*480, Status=0)
        stages = ['udp-receive-end','srtp-unprotect-enter','srtp-unprotect-return']
        sdk = [record(stage, 10 + i*.1 + j*.001, i+1)
               for i in range(2) for j,stage in enumerate(stages)]
        local = dict(sdkBoundary=dict(enabled=True,data=dict(Records=sdk,Skipped=0,Overwritten=0)),
                     baselineIncident=dict(Records=[record('authenticated-rtp-callback',10.003,1),
                                                   record('authenticated-rtp-callback',10.103,2)],SkippedContention=0))
        # Completely different host clock origin, same packet spacing of 10ms.
        peer = dict(sdkBoundary=dict(data=dict(Records=[record('socket-send-post',9000,1),
                                                       record('socket-send-post',9000.01,2),
                                                       record('socket-send-post',9000.02,3)])))
        result = analyze(local,peer)
        self.assertEqual(result['matchedCallbacks'],2)
        self.assertAlmostEqual(result['durations']['udpReturnToAppMs']['maxMs'],3)
        self.assertAlmostEqual(result['gapsOver50Ms'][0]['udpMinusPeerPostGapMs'],90)
        self.assertEqual(result['peerPostsWithoutUdpObservationInMatchedWindow'],[])
        self.assertEqual(analyze(dict(sdkBoundary=dict(enabled=False)),peer),dict(enabled=False))
        local['baselineIncident']['Records'].append(record('authenticated-rtp-callback',10.113,3))
        incomplete = analyze(local,peer)
        self.assertEqual(incomplete['duplicateUdpIdentities'],0)
        self.assertEqual(incomplete['missingObservations']['callbackWithoutAllSdkObservations'],1)
        self.assertEqual(incomplete['peerPostsWithoutUdpObservationInMatchedWindow'],[])


if __name__ == '__main__': unittest.main()
