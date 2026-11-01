import unittest
from decimal import Decimal
from analyze_turn_capture_chain import direction, gap, local_dwell


class CaptureChainChecks(unittest.TestCase):
    def test_independent_clock_origins_and_ambiguous_packet(self):
        keys=[(42,480,1),(42,960,2)]
        def stream(origin,interval):return {keys[0]:[Decimal(origin)],keys[1]:[Decimal(origin)+Decimal(interval)]}
        def sdk(stage,origin,interval):
            return [dict(Stage=stage,At=float(at[0]),Ssrc=k[0],Timestamp=k[1],Sequence=k[2],Status=0)
                    for k,at in stream(origin,interval).items()]
        sender=dict(role='a',sdkBoundary=dict(enabled=True,data=dict(Records=sdk('socket-send-post','10','.01'),Skipped=0,Overwritten=0)))
        callbacks=[dict(Stage='authenticated-rtp-callback',At=at,Ssrc=42,RtpTimestamp=ts,Sequence=seq)
                   for at,ts,seq in [(500.003,480,1),(500.103,960,2)]]
        receiver=dict(role='b',sdkBoundary=dict(enabled=True,data=dict(Records=sdk('udp-receive-end','500','.1'),Skipped=0,Overwritten=0)),
                      baselineIncident=dict(Records=callbacks,SkippedContention=0))
        tx=dict(out=stream('100000','.01'))
        rx={'in':stream('200000','.1')}
        server={'in':stream('300000','.01'),'out':stream('300000.0001','.01')}
        result=direction(sender,receiver,tx,rx,server);event=result['events'][0]
        self.assertTrue(event['allBoundariesUnique'])
        self.assertAlmostEqual(event['intervalGrowthMs']['serverEgressToReceiverNpcap'],90)
        self.assertAlmostEqual(event['serverCurrentDwellMs'],.1)
        self.assertAlmostEqual(event['intervalGrowthMs']['receiverNpcapToUdpReturn'],0)
        # Duplicate capture identities must not silently select the first observation.
        server['out'][keys[1]].append(Decimal('300000.0102'))
        self.assertIsNone(gap(server['out'],*keys))
        self.assertIsNone(local_dwell(server['in'],server['out'],keys[1]))
        self.assertFalse(direction(sender,receiver,tx,rx,server)['events'][0]['allBoundariesUnique'])


if __name__=='__main__':unittest.main()
