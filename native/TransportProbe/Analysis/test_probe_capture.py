from decimal import Decimal
import unittest
from analyze_probe_capture import matched_duration


class ProbeCaptureChecks(unittest.TestCase):
    def test_durations_and_duplicate_rejection(self):
        self.assertAlmostEqual(matched_duration([Decimal('1700000000')],[Decimal('1700000000.2')]),200)
        self.assertAlmostEqual(matched_duration([Decimal('100')],[Decimal('100.2')]),200)
        self.assertIsNone(matched_duration([],[Decimal('100.2')]))
        self.assertIsNone(matched_duration([Decimal('100'),Decimal('100.01')],[Decimal('100.2')]))


if __name__=='__main__':unittest.main()
