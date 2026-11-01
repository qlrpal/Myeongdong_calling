import unittest
from analyze_steady_pipeline import distribution,unique,read_exclusions


class SteadyPipelineChecks(unittest.TestCase):
    def test_identity_and_local_duration_guards(self):
        self.assertEqual(distribution([3,1,2])['medianMs'],2)
        with self.assertRaises(ValueError):distribution([-1])
        with self.assertRaises(ValueError):distribution([float('nan')])
        a={'ssrc':1,'timestamp':2};b=dict(a)
        with self.assertRaisesRegex(ValueError,'Ambiguous'):unique([a,b],lambda r:(r['ssrc'],r['timestamp']))
        self.assertEqual(len(unique([a],lambda r:r['ssrc'])),1)

    def test_consecutive_plc_reads_are_all_excluded(self):
        reads=[dict(CalledAt=i,ReturnedAt=i+.01,PlcFrames=n,PlayoutStarted=True,ProvidedSamples=128,RequestedSamples=128)
               for i,n in enumerate([0,1,1,2,1,0])]
        self.assertEqual(read_exclusions(reads),[(-.25,1.26),(.75,2.26),(1.75,3.26),(2.75,4.26)])


if __name__=='__main__':unittest.main()
