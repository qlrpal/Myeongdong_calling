import unittest
from analyze_directional_load import summarize


class DirectionalLoadChecks(unittest.TestCase):
    def test_sender_window_and_timeouts(self):
        raw={'label':'test','phases':[dict(name=n,start=i*30,end=(i+1)*30) for i,n in enumerate(['a1','upload','a2'])],
             'records':[dict(lane='gateway',sequence=i,at=at,rttMs=rtt) for i,(at,rtt) in enumerate([(1,900),(5,2),(31,900),(35,20),(65,3),(89,900)])]}
        raw['records'].append(dict(lane='gateway',sequence=99,at=36,error='timeout5s'))
        result=summarize(raw)
        self.assertEqual([p['lanes']['gateway']['maxMs'] for p in result['phases']],[2,20,3])
        self.assertEqual(result['phases'][1]['lanes']['gateway']['errors'],{'timeout5s':1})
        self.assertEqual(result['contrasts'][0]['p95AboveBothBaselinesMs'],17)
        raw['records'].append(dict(raw['records'][0]))
        with self.assertRaisesRegex(ValueError,'Reused probe identity'):summarize(raw)


if __name__=='__main__':unittest.main()
