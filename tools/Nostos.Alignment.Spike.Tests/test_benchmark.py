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

    def test_assisted_success_cannot_pass_human_gate(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            benchmark.save(root / "match.json", {})
            hashed = benchmark.digest(root / "match.json")
            rows, reviews = [], []
            for i in range(32):
                accepted = i < 24
                rows.append({"providerTag": "model-1", "clipId": str(i), "positionId": str(i // 2),
                             "kind": "passage" if accepted else "introduction", "manualAcceptance": None,
                             "outcome": "correct" if accepted else "correct_rejection",
                             "matchSha256": hashed, "matchFile": "match.json"})
                if accepted:
                    reviews.append({"providerTag": "model-1", "clipId": str(i), "matchSha256": hashed,
                                    "outcome": "correct", "reviewedBy": "test", "reviewedAt": "2026-10-10",
                                    "verificationMethod": "machine-assisted", "observation": "Reference agreement"})
            benchmark.save(root / "report.json", {"annotationMethod": "machine-assisted", "results": rows,
                           "summary": [{"providerTag": "model-1"}]})
            benchmark.save(root / "reviews.json", reviews)
            benchmark.finalize(SimpleNamespace(report=root / "report.json", reviews=root / "reviews.json"))
            result = benchmark.load(root / "final-report.json")["summary"][0]
            self.assertTrue(result["assistedContinuationGate"])
            self.assertFalse(result["continuationGate"])


if __name__ == "__main__":
    unittest.main()
