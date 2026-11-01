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


if __name__=='__main__':unittest.main()
