"""Offline fixture preparation, evidence freeze and replay. No network or ASR calls."""
import argparse
import hashlib
import json
import re
import statistics
import subprocess
import shutil
from pathlib import Path


def load(path):
    return json.loads(Path(path).read_text())


def save(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n")


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def cli(dll, *args):
    subprocess.run(["dotnet", str(dll), *map(str, args)], check=True)


def prepare(args):
    root = Path(args.root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    manifest = root / "manifest.json"
    if manifest.exists():
        raise ValueError("Manifest already exists; preserve independent annotations.")
    sources = load(Path(__file__).with_name("sources.json"))
    for source in sources:
        source["sha256"] = digest(root / source["file"])
    books = []
    clips = []
    for book, tracks in (("carol", [1, 3, 5]), ("pride", [1, 2, 3])):
        extracted = root / f"{book}.extracted.json"
        cli(args.dll, "extract", root / f"{book}.epub", extracted)
        books.append({"id": book, "epubFile": f"{book}.epub", "extractedFile": extracted.name,
                      "extractedSha256": digest(extracted)})
        # Candidate positions only: listen and move these BEFORE freezing if needed.
        positions = [(track, end, "passage") for track in tracks for end in (90, 180)]
        positions += [(track, 20, "introduction") for track in tracks[:2]]
        for position, (track, end, kind) in enumerate(positions, 1):
            for duration in (10, 20):
                clip_id = f"{book}-{position:02}-{duration}"
                filename = f"{clip_id}.wav"
                subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin",
                                "-ss", str(end-duration), "-i", str(root / f"{book}-{track}.mp3"),
                                "-t", str(duration), "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le",
                                "-y", str(root / filename)], check=True)
                clips.append({"id": clip_id, "positionId": f"{book}-{position:02}", "book": book,
                              "trackFile": f"{book}-{track}.mp3", "file": filename,
                              "startSeconds": end-duration, "endSeconds": end, "durationSeconds": duration,
                              "sha256": digest(root / filename), "kind": kind,
                              "reviewedBy": None, "reviewedAt": None, "heardExcerpt": None,
                              "expectedEnd": None, "expectedRejection": None})
    save(manifest, {"schemaVersion": 1, "language": "en", "status": "awaiting-independent-listening",
                    "sources": sources, "books": books, "clips": clips})
    print("32 candidate clips prepared. These are NOT verified benchmark fixtures.")


def materialize(args):
    """Reproduce a recorded fixture revision; never manufacture fresh annotations."""
    root = Path(args.root).resolve()
    template = Path(args.manifest).resolve()
    manifest = load(template)
    root.mkdir(parents=True, exist_ok=True)
    if (root / "manifest.json").exists():
        raise ValueError("Manifest already exists; preserve evidence.")
    for source in manifest["sources"]:
        if digest(root / source["file"]) != source["sha256"]:
            raise ValueError("Download the exact recorded source bytes: " + source["file"])
    for book in manifest["books"]:
        cli(args.dll, "extract", root / book["epubFile"], root / book["extractedFile"])
    for clip in manifest["clips"]:
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin",
                        "-ss", str(clip["startSeconds"]), "-i", str(root / clip["trackFile"]),
                        "-t", str(clip["durationSeconds"]), "-ar", "16000", "-ac", "1",
                        "-c:a", "pcm_s16le", "-y", str(root / clip["file"])], check=True)
        if clip.get("referenceFile"):
            destination = root / clip["referenceFile"]
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(template.parent / clip["referenceFile"], destination)
    validate(manifest, root)
    shutil.copyfile(template, root / "manifest.json")
    print("Recorded fixtures reproduced and hashes verified; annotations remain assisted when recorded as such.")


def validate(manifest, root):
    if manifest.get("language") != "en" or len(manifest["clips"]) != 32:
        raise ValueError("Expected exactly 32 English clips.")
    method = manifest.get("annotationMethod", "human")
    if method not in ("human", "machine-assisted"):
        raise ValueError("Unknown annotation method.")
    for source in manifest["sources"]:
        if digest(root / source["file"]) != source["sha256"]:
            raise ValueError("Source hash changed: " + source["file"])
    books = {b["id"]: b for b in manifest["books"]}
    if set(books) != {"carol", "pride"}:
        raise ValueError("Expected the two approved books.")
    documents = {}
    for book in books.values():
        path = root / book["extractedFile"]
        if digest(path) != book["extractedSha256"]:
            raise ValueError("Extraction changed.")
        artifact = load(path)
        source = next(s for s in manifest["sources"] if s["file"] == book["epubFile"])
        if artifact["sourceSha256"] != source["sha256"]:
            raise ValueError("EPUB identity mismatch.")
        documents[book["id"]] = artifact["document"]
    ids, positions = set(), {}
    for clip in manifest["clips"]:
        if not re.fullmatch(r"[a-z0-9-]+", clip["id"]) or clip["id"] in ids:
            raise ValueError("Invalid or duplicate clip id.")
        ids.add(clip["id"])
        if digest(root / clip["file"]) != clip["sha256"]:
            raise ValueError("Clip hash changed.")
        if (clip["durationSeconds"] not in (10, 20) or clip["startSeconds"] < 0 or
                clip["endSeconds"]-clip["startSeconds"] != clip["durationSeconds"]):
            raise ValueError("Invalid clip timing.")
        timing = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_entries",
                    "format=duration", "-of", "json", str(root / clip["file"])]))
        if abs(float(timing["format"]["duration"])-clip["durationSeconds"]) > 0.01:
            raise ValueError("Actual clip duration differs.")
        if not clip.get("reviewedBy") or not clip.get("reviewedAt") or not clip.get("heardExcerpt"):
            raise ValueError("Reference inspection receipt missing: " + clip["id"])
        if method == "machine-assisted":
            if clip.get("annotationStatus") != "confirmed-reference":
                raise ValueError("Uncertain assisted annotation: " + clip["id"])
            reference = root / clip["referenceFile"]
            if digest(reference) != clip["referenceSha256"] or load(reference)["audioSha256"] != clip["sha256"]:
                raise ValueError("Assisted reference identity changed.")
        group = positions.setdefault(clip["positionId"], [])
        group.append(clip)
        if clip["kind"] == "introduction":
            if clip.get("expectedRejection") is not True or clip.get("expectedEnd") is not None:
                raise ValueError("Introduction must be independently confirmed narration-only.")
        elif clip["kind"] == "passage":
            expected = clip.get("expectedEnd")
            if not expected or clip.get("expectedRejection") is not False:
                raise ValueError("Passage endpoint missing.")
            matching = [s["locator"] for b in documents[clip["book"]]["blocks"] for s in b["sourceSegments"]
                        if s["locator"].get("resourceHref") == expected["resourceHref"]
                        and s["locator"].get("spineIndex") == expected["spineIndex"]]
            if not any((loc.get("startTextOffset") or 0) <= expected["minTextOffset"] <=
                       expected["maxTextOffset"] <= loc["endTextOffset"] for loc in matching):
                raise ValueError("Expected endpoint is outside the extracted source segment.")
            if expected["maxTextOffset"]-expected["minTextOffset"] > 300:
                raise ValueError("Expected endpoint must be bounded to the final spoken sentence (<=300 chars).")
        else:
            raise ValueError("Unknown fixture kind.")
    for group in positions.values():
        if (sorted(c["durationSeconds"] for c in group) != [10, 20] or
                len({(c["book"], c["kind"], c["trackFile"], c["endSeconds"]) for c in group}) != 1):
            raise ValueError("Each position needs identical endpoints at 10 and 20 seconds.")
    for book in books:
        groups = [g for g in positions.values() if g[0]["book"] == book]
        if sum(g[0]["kind"] == "passage" for g in groups) != 6 or sum(g[0]["kind"] == "introduction" for g in groups) != 2:
            raise ValueError("Expected six narrated positions and two intros per book.")


def freeze(args):
    root = Path(args.root).resolve()
    if (root / "freeze.json").exists():
        raise ValueError("Already frozen. Never recalibrate on scored audio.")
    validate(load(root / "manifest.json"), root)
    calibration = load(args.calibration)
    if (not calibration.get("passed") or calibration.get("caseCount", 0) < 8 or
            calibration["settingsSha256"] != digest(args.settings)):
        raise ValueError("Offline calibration did not pass with these settings.")
    save(root / "freeze.json", {"manifestSha256": digest(root / "manifest.json"),
          "annotationMethod": load(root / "manifest.json").get("annotationMethod", "human"),
          "settingsSha256": digest(args.settings), "calibrationSha256": digest(args.calibration),
          "calibrationReceipt": load(args.calibration)})
    print("Fixture and offline calibration hashes frozen. Keep this receipt before model output.")


def calibrate(args):
    root = Path(args.output).resolve(); root.mkdir(parents=True, exist_ok=True)
    examples_path = Path(__file__).with_name("calibration-examples.json")
    cases = load(examples_path)
    rows = []
    for i, case in enumerate(cases):
        blocks = [{"order": j, "text": text, "headingPath": [], "sourceSegments": [
            {"textStart": 0, "textLength": len(text), "locator": {"type": "epub", "spineIndex": j,
             "resourceHref": f"synthetic-{j}.xhtml", "startTextOffset": 0, "endTextOffset": len(text)}}]}
            for j, text in enumerate(case["blocks"])]
        source = root / f"{i}.source.json"; transcript = root / f"{i}.transcript.json"; match_path = root / f"{i}.match.json"
        save(source, {"sourceSha256": "0"*64, "extractorVersion": "offline-synthetic",
                      "document": {"format": "Epub", "blocks": blocks}})
        save(transcript, {"audioSha256": "0"*64, "recordedAt": "2026-10-10T00:00:00Z", "elapsedMilliseconds": 0,
                          "result": {"text": case["transcript"], "language": "en", "durationSeconds": None}})
        cli(args.dll, "match", source, args.settings, transcript, match_path)
        match = load(match_path)["match"]
        passed = match["accepted"] == case["expectedAccepted"]
        if match["accepted"]:
            passed = passed and match["start"]["blockOrder"] == case["expectedBlock"]
        rows.append({"name": case["name"], "passed": passed, "match": match})
    receipt = {"settingsSha256": digest(args.settings), "examplesSha256": digest(examples_path),
               "cliSha256": digest(args.dll), "caseCount": len(rows), "passed": all(r["passed"] for r in rows), "cases": rows}
    save(root / "calibration.json", receipt)
    if not receipt["passed"]:
        raise ValueError("Offline examples failed. Calibrate BEFORE any recorded audio is scored.")
    print(f"Offline calibration: {len(rows)} cases passed; thresholds fixed independently of recorded audio.")


def replay(args):
    root = Path(args.root).resolve()
    manifest = load(root / "manifest.json")
    frozen = load(root / "freeze.json")
    if digest(root / "manifest.json") != frozen["manifestSha256"] or digest(args.settings) != frozen["settingsSha256"]:
        raise ValueError("Frozen benchmark inputs changed.")
    validate(manifest, root)
    method = manifest.get("annotationMethod", "human")
    entries = load(args.index)
    seen, results = set(), []
    books = {b["id"]: b for b in manifest["books"]}
    clips = {c["id"]: c for c in manifest["clips"]}
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    for entry in entries:
        tag, clip_id = entry["providerTag"], entry["clipId"]
        if not re.fullmatch(r"[a-zA-Z0-9_-]+", tag) or (tag, clip_id) in seen:
            raise ValueError("Invalid provider tag or duplicate result.")
        seen.add((tag, clip_id))
        clip = clips[clip_id]
        transcript = Path(entry["artifactPath"]).resolve()
        data = load(transcript)
        if data["audioSha256"] != clip["sha256"]:
            raise ValueError("Transcript is for a different clip.")
        target = output / f"{tag}-{clip_id}.json"
        cli(args.dll, "match", root / books[clip["book"]]["extractedFile"], args.settings, transcript, target)
        match = load(target)["match"]
        expected = clip["expectedEnd"]
        error = None
        if match["accepted"]:
            loc = match["end"]["locator"]
            same = expected and all(loc[k] == expected[k] for k in ("resourceHref", "spineIndex"))
            if same:
                error = max(expected["minTextOffset"]-loc["endTextOffset"], loc["endTextOffset"]-expected["maxTextOffset"], 0)
            outcome = "correct" if error == 0 else "incorrect_accepted"
        else:
            outcome = "correct_rejection" if clip["expectedRejection"] else "rejected_passage"
        results.append({"providerTag": tag, "clipId": clip_id, "positionId": clip["positionId"],
                        "kind": clip["kind"], "durationSeconds": clip["durationSeconds"],
                        "clipSha256": clip["sha256"], "transcriptSha256": digest(transcript),
                        "matchFile": target.name, "matchSha256": digest(target), "outcome": outcome,
                        "sourceLocationErrorCharacters": error, "latencyMilliseconds": data["elapsedMilliseconds"],
                        "manualAcceptance": None})
    summary = []
    for tag in sorted({r["providerTag"] for r in results}):
        rows = [r for r in results if r["providerTag"] == tag]
        correct = len({r["positionId"] for r in rows if r["kind"] == "passage" and r["outcome"] == "correct"})
        bad = sum(r["outcome"] == "incorrect_accepted" for r in rows)
        summary.append({"providerTag": tag, "correctPositionsOf12": correct,
                        "incorrectAccepted": bad, "scoredClipsOf32": len(rows),
                        "correctLocations": sum(r["outcome"] == "correct" for r in rows),
                        "correctRejections": sum(r["outcome"] == "correct_rejection" for r in rows),
                        "rejectedPassages": sum(r["outcome"] == "rejected_passage" for r in rows),
                        "medianLatencyMilliseconds": statistics.median(r["latencyMilliseconds"] for r in rows),
                        "sourceErrorsCharacters": [r["sourceLocationErrorCharacters"] for r in rows],
                        "audioSeconds": sum(r["durationSeconds"] for r in rows),
                        "automatedGate": len(rows) == 32 and correct >= 11 and bad == 0,
                        "continuationGate": False, "assistedContinuationGate": False,
                        "reason": "Accepted locations still require reference/source inspection; assisted labels cannot establish human ground truth."})
    save(output / "report.json", {"freeze": frozen, "annotationMethod": method, "results": results, "summary": summary})
    print("Replay recorded. Automated scores alone do not authorize continuation.")


def finalize(args):
    report_path = Path(args.report).resolve()
    report = load(report_path)
    method = report.get("annotationMethod", "human")
    reviews = load(args.reviews)
    review_map = {(r["providerTag"], r["clipId"]): r for r in reviews}
    if len(review_map) != len(reviews):
        raise ValueError("Duplicate manual receipt.")
    for row in report["results"]:
        accepted = row["outcome"] in ("correct", "incorrect_accepted")
        if not accepted:
            continue
        receipt = review_map.get((row["providerTag"], row["clipId"]))
        if (not receipt or receipt["matchSha256"] != row["matchSha256"] or
                digest(report_path.parent / row["matchFile"]) != row["matchSha256"] or
                not receipt.get("reviewedBy") or not receipt.get("reviewedAt") or not receipt.get("observation") or
                receipt.get("outcome") not in ("correct", "incorrect_accepted")):
            raise ValueError("Accepted location requires an audio/EPUB inspection receipt: " + row["clipId"])
        if receipt["outcome"] == "correct" and row["outcome"] != "correct":
            raise ValueError("Manual review cannot erase a mismatch against frozen ground truth.")
        if method == "machine-assisted" and receipt.get("verificationMethod") != "machine-assisted":
            raise ValueError("Assisted reference inspection must not be described as manual listening.")
        row["manualAcceptance"] = receipt
    for summary in report["summary"]:
        rows = [r for r in report["results"] if r["providerTag"] == summary["providerTag"]]
        outcome = lambda r: r["manualAcceptance"]["outcome"] if r["manualAcceptance"] else r["outcome"]
        correct = len({r["positionId"] for r in rows if r["kind"] == "passage" and outcome(r) == "correct"})
        bad = sum(outcome(r) == "incorrect_accepted" for r in rows)
        passed = len(rows) == 32 and correct >= 11 and bad == 0
        summary.update(inspectedCorrectPositionsOf12=correct, inspectedIncorrectAccepted=bad,
                       continuationGate=passed and method == "human",
                       assistedContinuationGate=passed and method == "machine-assisted",
                       reason="Assisted reference agreement only; human zero-error gate remains unverified." if method == "machine-assisted"
                           else "Independent acceptance receipts applied; economics must be reconciled separately.")
    save(report_path.parent / "final-report.json", report)
    print("Manual acceptance applied. Read the gate and economic evidence before recommending a next step.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("prepare"); p.add_argument("root"); p.add_argument("dll"); p.set_defaults(run=prepare)
    p = commands.add_parser("materialize"); p.add_argument("root"); p.add_argument("dll"); p.add_argument("manifest"); p.set_defaults(run=materialize)
    p = commands.add_parser("freeze"); p.add_argument("root"); p.add_argument("settings"); p.add_argument("calibration"); p.set_defaults(run=freeze)
    p = commands.add_parser("calibrate"); p.add_argument("output"); p.add_argument("dll"); p.add_argument("settings"); p.set_defaults(run=calibrate)
    p = commands.add_parser("replay"); p.add_argument("root"); p.add_argument("dll"); p.add_argument("settings"); p.add_argument("index"); p.add_argument("output"); p.set_defaults(run=replay)
    p = commands.add_parser("finalize"); p.add_argument("report"); p.add_argument("reviews"); p.set_defaults(run=finalize)
    args = parser.parse_args()
    try:
        args.run(args)
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(2, f"Benchmark stopped: {error}\n")


if __name__ == "__main__":
    main()
