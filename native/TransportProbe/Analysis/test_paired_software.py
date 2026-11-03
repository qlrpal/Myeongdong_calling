import unittest
from analyze_paired_software import combine,summarize


class PairedSoftwareChecks(unittest.TestCase):
    def test_splitting_frame_does_not_invent_latency_gain(self):
        old=combine([dict(ssrc=1,timestamp=0,samples=480,firstSampleAgeMs=19)],
                    [dict(ssrc=1,timestamp=0,receiveToSupplyMs=25)])
        short=combine([dict(ssrc=1,timestamp=0,samples=240,firstSampleAgeMs=19),dict(ssrc=1,timestamp=240,samples=240,firstSampleAgeMs=14)],
                      [dict(ssrc=1,timestamp=0,receiveToSupplyMs=25),dict(ssrc=1,timestamp=240,receiveToSupplyMs=30)])
        self.assertEqual(old['sampleWeightedMeanMs'],44)
        self.assertEqual(short['sampleWeightedMeanMs'],44)
        self.assertEqual(old['matchedAudioMs'],short['matchedAudioMs'])
        with self.assertRaises(ValueError):combine([dict(ssrc=1,timestamp=0,samples=480,firstSampleAgeMs=19)]*2,[])

    def test_pair_configuration_must_match(self):
        a=dict(run='same',role='a',buildHash='app',probeHash='probe',fixtureHash='wave',route='direct',shared=False,reserveMs=20,observeSeconds=45)
        for key,value in [('fixtureHash','other'),('route','relay'),('shared',True),('reserveMs',15),('observeSeconds',30)]:
            b=dict(a,role='b');b[key]=value
            with self.assertRaisesRegex(ValueError,'Mismatched trial configuration'):summarize(a,b)

    def test_optional_comparison_metadata_cannot_be_mixed(self):
        original=dict(run='same',role='a',buildHash='app',probeHash='probe',fixtureHash='wave',route='direct',shared=False,reserveMs=20,observeSeconds=45)
        for key,value in [('captureMode','native'),('outputSettle',True),('integralControl',True),('diagnosticFrameMs',5),('warmupSeconds',60)]:
            a=dict(original);a[key]=value
            for b in [dict(original,role='b'),dict(a,role='b',**{key:None})]:
                with self.subTest(key=key,value=b.get(key)):
                    with self.assertRaisesRegex(ValueError,'Mismatched trial configuration: '+key):summarize(a,b)


if __name__=='__main__':unittest.main()
