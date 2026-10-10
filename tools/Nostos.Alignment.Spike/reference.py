"""Decode independent pilot references with explicitly supplied, cached Whisper weights.

Install faster-whisper separately. This produces reference evidence, never human
ground truth or expected source locations. Inspect EPUB text before annotation.
"""
import argparse
import hashlib
import importlib.metadata
import json
import os
import time
from datetime import datetime, timezone
from pathlib import Path


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--weights", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("clips", nargs="+", type=Path)
    args = parser.parse_args()
    weights = args.weights.resolve()
    if not (weights / "model.bin").is_file():
        parser.error("Supply an existing local model directory; downloads are disabled.")
    os.environ["HF_HUB_OFFLINE"] = "1"
    from faster_whisper import WhisperModel
    model = WhisperModel(str(weights), device="cpu", compute_type="int8",
                         cpu_threads=2, local_files_only=True)
    weights_hash = digest(weights / "model.bin")
    args.output.mkdir(parents=True, exist_ok=True)
    for clip in args.clips:
        target = args.output / (clip.stem + ".json")
        if target.exists():
            raise ValueError("Preserve existing evidence; use another output directory.")
        start = time.monotonic()
        segments, _ = model.transcribe(str(clip), language="en", beam_size=5,
                                       condition_on_previous_text=False, word_timestamps=True)
        rows = [{"start": s.start, "end": s.end, "text": s.text,
                 "avgLogprob": s.avg_logprob, "noSpeechProbability": s.no_speech_prob,
                 "words": [{"start": w.start, "end": w.end, "word": w.word,
                            "probability": w.probability} for w in s.words or []]} for s in segments]
        receipt = {"kind": "machine-assisted-reference",
                   "decoder": "faster-whisper " + importlib.metadata.version("faster-whisper"),
                   "weightsSha256": weights_hash, "audioSha256": digest(clip),
                   "recordedAt": datetime.now(timezone.utc).isoformat(),
                   "elapsedSeconds": time.monotonic() - start,
                   "transcript": "".join(s["text"] for s in rows).strip(), "segments": rows,
                   "networkUsed": False, "gatewaySpendUsd": 0}
        target.write_text(json.dumps(receipt, indent=2) + "\n")
        print(clip.stem + ": " + receipt["transcript"], flush=True)


if __name__ == "__main__":
    main()
