import copy
import unittest
from analyze_playout_failures import analyze


def sample(at, plc=0, empty=0):
    return dict(MonotonicSeconds=at, Peers=[dict(Id='peer', Audio=dict(UnderfillMs=0,
        RtpPlayout=dict(ConcealedMs=plc, Rebuffers=0), Render=dict(EmptyEvents=empty)))])


def fixture():
    return dict(completed=True, stage='complete', error=None, reserveMs=10,
        windows=[dict(phase='baseline', samples=[sample(100), sample(101, empty=1),
                                               sample(110, plc=2.5, empty=1)])],
        baselineIncident=dict(State='Frozen', Trigger='render-empty', SkippedContention=1, Overwritten=20,
            Records=[dict(At=100.8, Stage='playout-read', Outcome='read', DurationMs=0, QueueMs=20),
                dict(At=100.81, Stage='render-submit', WorkMs=.05, QueueMs=3, Samples=128, SampleRate=48000),
                dict(At=100.82, Stage='render-empty', Outcome='wake'),
                dict(At=100.83, Stage='playout-read', Outcome='read', DurationMs=0, QueueMs=24)]))


class PlayoutFailureChecks(unittest.TestCase):
    def test_separate_drain_and_later_plc_without_inventing_cause(self):
        r = analyze(fixture())
        self.assertEqual(r['totalDelta'], dict(plcMs=2.5, renderEmpty=1, underfillMs=0, rebuffers=0))
        self.assertEqual(r['renderDrains'][0]['observation'], 'wake-empty-with-source-reserve')
        self.assertAlmostEqual(r['renderDrains'][0]['previousObservedSubmitGapMs'], 10)
        self.assertFalse(r['counterEvents'][-1]['snapshotIntervalInsideCapsule'])
        self.assertEqual(r['capsule']['concealedMsInRecords'], 0)

    def test_missing_or_concealing_read_is_not_source_ready_evidence(self):
        for missing in (False, True):
            d = fixture()
            if missing: d['baselineIncident']['Records'].pop()
            else: d['baselineIncident']['Records'][-1]['DurationMs'] = 2.5
            self.assertEqual(analyze(d)['renderDrains'][0]['observation'], 'insufficient-or-other-state')
        d = fixture(); d.pop('baselineIncident')
        self.assertIsNone(analyze(d)['capsule'])
        self.assertEqual(analyze(d)['renderDrains'], [])

    def test_counter_reset_and_bad_timing_are_rejected(self):
        for change in ('reset', 'clock', 'nan', 'missing'):
            d = copy.deepcopy(fixture()); s = d['windows'][0]['samples']
            if change == 'reset': s[-1]['Peers'][0]['Audio']['Render']['EmptyEvents'] = 0
            if change == 'clock': s[-1]['MonotonicSeconds'] = 100
            if change == 'nan': s[-1]['MonotonicSeconds'] = float('nan')
            if change == 'missing': s[-1]['Peers'][0]['Audio'].pop('UnderfillMs')
            with self.assertRaises((ValueError, KeyError)): analyze(d)

    def test_incomplete_window_or_changed_peer_cannot_be_a_control(self):
        for change in ('window', 'peer', 'report'):
            d = fixture()
            if change == 'window': d['windows'][0]['completed'] = False
            if change == 'peer': d['windows'][0]['samples'][-1]['Peers'][0]['Id'] = 'replacement'
            if change == 'report': d['error'] = 'capture stopped'
            with self.assertRaises(ValueError): analyze(d)


if __name__ == '__main__':
    unittest.main()
