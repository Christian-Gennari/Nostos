"""Evidence and decision gates; independent of provider output or secrets."""
import importlib.util
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location("benchmark", Path(__file__).parents[1] / "Nostos.Alignment.Spike" / "benchmark.py")
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class EvidenceGateTests(unittest.TestCase):
    def test_empty_fixture_set_cannot_be_frozen(self):
        with self.assertRaisesRegex(ValueError, "32 English clips"):
            benchmark.validate({"language": "en", "clips": []}, Path("."))

    def test_accepted_location_needs_manual_receipt(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            benchmark.save(root / "report.json", {"results": [{"providerTag": "model-1", "clipId": "clip-1",
                           "outcome": "correct", "matchSha256": "0"*64}], "summary": []})
            benchmark.save(root / "reviews.json", [])
            with self.assertRaisesRegex(ValueError, "inspection receipt"):
                benchmark.finalize(SimpleNamespace(report=root / "report.json", reviews=root / "reviews.json"))
            self.assertFalse((root / "final-report.json").exists())

    def test_manual_review_cannot_erase_incorrect_frozen_location(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            benchmark.save(root / "match.json", {})
            hashed = benchmark.digest(root / "match.json")
            benchmark.save(root / "report.json", {"results": [{"providerTag": "model-1", "clipId": "clip-1",
                           "outcome": "incorrect_accepted", "matchSha256": hashed, "matchFile": "match.json"}], "summary": []})
            benchmark.save(root / "reviews.json", [{"providerTag": "model-1", "clipId": "clip-1", "matchSha256": hashed,
                           "outcome": "correct", "reviewedBy": "offline-test", "reviewedAt": "2026-10-10", "observation": "Must not override fixed labels"}])
            with self.assertRaisesRegex(ValueError, "frozen ground truth"):
                benchmark.finalize(SimpleNamespace(report=root / "report.json", reviews=root / "reviews.json"))

    def test_duplicate_manual_receipts_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            benchmark.save(root / "report.json", {"results": [], "summary": []})
            receipt = {"providerTag": "model-1", "clipId": "clip-1"}
            benchmark.save(root / "reviews.json", [receipt, receipt])
            with self.assertRaisesRegex(ValueError, "Duplicate"):
                benchmark.finalize(SimpleNamespace(report=root / "report.json", reviews=root / "reviews.json"))


if __name__ == "__main__":
    unittest.main()
