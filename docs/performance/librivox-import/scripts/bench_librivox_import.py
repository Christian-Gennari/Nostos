#!/usr/bin/env python3
"""Reproducible benchmark for the LibriVox -> chaptered M4B acquisition path.

This harness mirrors, phase for phase, the product pipeline in
Nostos.Product/Providers/Acquisition and LibriVoxM4bAssembler:

  1. catalogue plan fetch (LibriVox JSON feed, same id query the provider uses)
  2. section download with the product's concurrency/attempt/timeout policy
  3. per-section ffprobe (exact arguments from MediaProcessRunner)
  4. concat list + ffmetadata chapter table (exact text format from the assembler)
  5. one ffmpeg pass: -c:a aac -b:a 64k -ac 1 -ar 44100 -movflags +faststart -f ipod
  6. output validation (chapter count + duration tolerance, same rules as product)
  7. commit (same-volume rename, as FileStorageService.AdoptBookFileAsync does)

The only deviation from the product command is the observability-only
`-progress pipe:1 -nostats` pair, which ffmpeg documents as a progress
report and does not change the encoded output. `--instrumentation-check`
proves that claim on a real capture: it encodes twice, once exactly as the
product does, and compares the decoded PCM MD5 of both outputs.

SAFETY: this script refuses any --out-root inside a Nostos.Backend/Storage
directory (literal worktree path or the real path behind the symlink), and
only accepts scratch roots under /home/dev/.hermes/cache/scratch or the
worktree's .agent/ directory. It never reads or writes production media.

Usage:
    python3 bench_librivox_import.py run --id 11035 --label long
    python3 bench_librivox_import.py run --id 2469 --label short --instrumentation-check
    python3 bench_librivox_import.py micro-byte-concat --id 2469
    python3 bench_librivox_import.py micro-stream-copy --id 2469
    python3 bench_librivox_import.py micro-download-concurrency --id 2469 --parts 6
"""

from __future__ import annotations

import argparse
import concurrent.futures
import csv
import hashlib
import html
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

try:
    import psutil
except ImportError:  # pragma: no cover - psutil is standard on this host
    psutil = None

# --------------------------------------------------------------------------
# Guards: these paths must never be touched, and scratch must stay in bounds.
# --------------------------------------------------------------------------

WORKTREE = Path("/home/dev/coding/projects/nostos-rebirth-librivox-performance")
PREVIOUS_CHECKOUT = Path("/home/dev/coding/projects/nostos-rebirth")

FORBIDDEN_ROOTS = [
    WORKTREE / "Nostos.Backend" / "Storage",
    PREVIOUS_CHECKOUT / "Nostos.Backend" / "Storage",
]

ALLOWED_ROOTS = [
    Path("/home/dev/.hermes/cache/scratch"),
    WORKTREE / ".agent",
]

LIBRIVOX_API = "https://librivox.org/api/feed/audiobooks/"
USER_AGENT = "NostosLibriVoxSpike/1.0 (performance measurement; public-domain audio)"

# Product constants (AcquisitionOptions / LibriVoxProvider / LibriVoxM4bAssembler).
DOWNLOAD_CONCURRENCY = 3
DOWNLOAD_ATTEMPTS = 3
DOWNLOAD_TIMEOUT_SECONDS = 900
MAX_BYTES_PER_PART = 256 * 1024 * 1024
MAX_TOTAL_BYTES = 3 * 1024 * 1024 * 1024
OUTPUT_BITRATE = "64k"
BUFFER_SIZE = 131072


def die(message: str) -> None:
    print(f"REFUSING: {message}", file=sys.stderr)
    raise SystemExit(2)


def guard_out_root(root: Path) -> Path:
    resolved = root.expanduser().resolve()
    for forbidden in FORBIDDEN_ROOTS:
        try:
            forbidden_resolved = forbidden.resolve()
        except OSError:
            forbidden_resolved = forbidden
        if resolved == forbidden_resolved or forbidden_resolved in resolved.parents:
            die(
                f"--out-root {resolved} is inside a Nostos.Backend/Storage directory "
                f"({forbidden_resolved}). Production storage is never a benchmark target."
            )
    allowed = any(
        resolved == a.resolve() or a.resolve() in resolved.parents
        for a in ALLOWED_ROOTS
        if a.exists() or a == Path("/home/dev/.hermes/cache/scratch")
    )
    if not allowed:
        die(
            f"--out-root {resolved} is not under an allowed scratch root "
            f"({' or '.join(str(a) for a in ALLOWED_ROOTS)})."
        )
    return resolved


# --------------------------------------------------------------------------
# LibriVox feed (same shape the provider parses).
# --------------------------------------------------------------------------


@dataclass
class Section:
    number: int
    title: str
    url: str


@dataclass
class Recording:
    id: str
    title: str
    totaltime: str
    sections: list[Section] = field(default_factory=list)


def fetch_recording(recording_id: str, cache: Path | None) -> Recording:
    if cache and cache.exists():
        raw = json.loads(cache.read_text())
    else:
        url = f"{LIBRIVOX_API}?id={recording_id}&format=json&extended=1"
        request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = json.load(response)
        if cache:
            cache.parent.mkdir(parents=True, exist_ok=True)
            cache.write_text(json.dumps(raw))

    books = raw.get("books") or []
    if not books:
        die(f"LibriVox feed has no recording id={recording_id!r}")
    book = books[0]
    sections = []
    for element in book.get("sections") or []:
        listen = (element.get("listen_url") or "").strip()
        if not listen.startswith("https://"):
            continue
        sections.append(
            Section(
                number=int(element.get("section_number") or 0),
                title=(element.get("title") or "").strip(),
                url=listen,
            )
        )
    sections.sort(key=lambda s: s.number)
    return Recording(
        id=str(book["id"]),
        title=book["title"],
        totaltime=book.get("totaltime") or "",
        sections=sections,
    )


# --------------------------------------------------------------------------
# Phase 2: download with the product's policy (concurrency 3, 3 attempts).
# --------------------------------------------------------------------------


def download_one(url: str, destination: Path) -> dict:
    started = time.perf_counter()
    last_error: Exception | None = None
    delay = 2.0

    for attempt in range(1, DOWNLOAD_ATTEMPTS + 1):
        try:
            written = _stream_once(url, destination)
            if written == 0:
                raise RuntimeError("source returned an empty file")
            return {
                "url": url,
                "bytes": written,
                "seconds": time.perf_counter() - started,
                "attempts": attempt,
            }
        except Exception as error:  # noqa: BLE001 - retries are policy
            last_error = error
            try:
                destination.unlink()
            except OSError:
                pass
            if attempt < DOWNLOAD_ATTEMPTS:
                time.sleep(delay)
                delay = min(delay * 2, 30)

    raise RuntimeError(f"download failed after {DOWNLOAD_ATTEMPTS} attempts for {url}: {last_error}")


def _stream_once(url: str, destination: Path) -> int:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    written = 0
    with urllib.request.urlopen(request, timeout=DOWNLOAD_TIMEOUT_SECONDS) as response:
        destination.parent.mkdir(parents=True, exist_ok=True)
        with destination.open("wb") as handle:
            while True:
                chunk = response.read(BUFFER_SIZE)
                if not chunk:
                    break
                written += len(chunk)
                if written > MAX_BYTES_PER_PART:
                    raise RuntimeError("part exceeded the per-part cap")
                handle.write(chunk)
    return written


def download_parts(
    sections: list[Section],
    staging: Path,
    concurrency: int,
    budget_bytes: int,
    reuse_from: Path | None = None,
) -> tuple[list[Path], list[dict], float]:
    started = time.perf_counter()
    paths = [staging / f"part-{index:04d}.mp3" for index in range(len(sections))]
    records: list[dict | None] = [None] * len(sections)
    spent = 0
    lock = threading.Lock()

    for index, path in enumerate(paths):
        if reuse_from is None:
            continue
        previous = reuse_from / "staging" / path.name
        if not previous.exists() or previous.stat().st_size == 0:
            continue
        try:
            os.link(previous, path)
        except OSError:
            shutil.copy2(previous, path)
        records[index] = {
            "url": sections[index].url,
            "bytes": path.stat().st_size,
            "seconds": None,
            "attempts": 0,
            "reused": True,
        }
        spent += path.stat().st_size

    def worker(index: int) -> None:
        nonlocal spent
        if records[index] is not None:
            return
        with lock:
            remaining = budget_bytes - spent
        if remaining <= 0:
            raise RuntimeError("acquisition passed its total byte budget")
        record = download_one(sections[index].url, paths[index])
        record["reused"] = False
        with lock:
            spent += record["bytes"]
            records[index] = record

    with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as pool:
        futures = [pool.submit(worker, i) for i in range(len(sections))]
        for future in concurrent.futures.as_completed(futures):
            future.result()

    return paths, [r for r in records if r], time.perf_counter() - started


# --------------------------------------------------------------------------
# Phases 3-6: probe, metadata files, encode, validate (product-exact).
# --------------------------------------------------------------------------


def ffprobe_duration(path: Path) -> float | None:
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(path)],
        capture_output=True,
        text=True,
    )
    text = result.stdout.strip()
    try:
        value = float(text)
    except ValueError:
        return None
    return value if value > 0 else None


def ffprobe_chapter_count(path: Path) -> int | None:
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_chapters", "-print_format", "json", str(path)],
        capture_output=True,
        text=True,
    )
    try:
        return len(json.loads(result.stdout or "{}").get("chapters", []))
    except json.JSONDecodeError:
        return None


def probe_inputs(paths: list[Path]) -> tuple[list[float], list[dict], float]:
    started = time.perf_counter()
    durations: list[float] = []
    records: list[dict] = []
    for path in paths:
        part_started = time.perf_counter()
        duration = ffprobe_duration(path)
        if duration is None:
            raise RuntimeError(f"could not probe {path.name}")
        durations.append(duration)
        records.append(
            {"file": path.name, "duration_s": duration, "probe_s": time.perf_counter() - part_started}
        )
    return durations, records, time.perf_counter() - started


def build_concat_list(paths: list[Path]) -> str:
    return "".join("file '" + str(p).replace("'", "'\\''") + "'\n" for p in paths)


def escape_metadata(value: str) -> str:
    flattened = value.replace("\n", " ").replace("\r", " ")
    return "".join(("\\" + c if c in "=;#\\" else c) for c in flattened)


def build_chapter_metadata(sections: list[Section], durations: list[float]) -> str:
    lines = [";FFMETADATA1\n"]
    cursor_ms = 0
    for index, duration in enumerate(durations):
        label = sections[index].title or f"Section {index + 1}"
        length_ms = round(duration * 1000)
        lines.append("[CHAPTER]\n")
        lines.append("TIMEBASE=1/1000\n")
        lines.append(f"START={cursor_ms}\n")
        lines.append(f"END={cursor_ms + length_ms}\n")
        lines.append(f"title={escape_metadata(label)}\n")
        cursor_ms += length_ms
    return "".join(lines)


def product_ffmpeg_args(concat_list: Path, chapters: Path, output: Path) -> list[str]:
    """The exact argument vector from LibriVoxM4bAssembler.AssembleCoreAsync."""
    return [
        "-f", "concat", "-safe", "0", "-i", str(concat_list),
        "-i", str(chapters),
        "-map", "0:a",
        "-map_metadata", "1",
        "-map_chapters", "1",
        "-c:a", "aac",
        "-b:a", OUTPUT_BITRATE,
        "-ac", "1",
        "-ar", "44100",
        "-movflags", "+faststart",
        "-f", "ipod",
        str(output),
    ]


class ResourceSampler:
    """Polls one process tree's CPU/RSS/IO while a phase runs."""

    def __init__(self, pid: int):
        self.pid = pid
        self.samples: list[dict] = []
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, daemon=True)

    def _loop(self) -> None:
        process = psutil.Process(self.pid)
        start = process.cpu_times()
        previous_cpu = start.user + start.system
        previous_at = time.perf_counter()
        while not self._stop.is_set():
            time.sleep(0.5)
            try:
                if not process.is_running():
                    break
                processes = [process] + process.children(recursive=True)
                cpu = sum(p.cpu_times().user + p.cpu_times().system for p in processes if p.is_running())
                rss = sum(p.memory_info().rss for p in processes if p.is_running())
                now = time.perf_counter()
                elapsed = now - previous_at
                # A stopped process can make the cumulative counter appear to
                # move backwards; never record a negative utilisation sample.
                cpu_percent = max(0.0, ((cpu - previous_cpu) / elapsed) * 100)
                previous_cpu, previous_at = cpu, now
                io_read = io_write = 0
                for p in processes:
                    try:
                        counters = p.io_counters()
                        io_read += counters.read_bytes
                        io_write += counters.write_bytes
                    except (psutil.Error, AttributeError):
                        pass
                self.samples.append(
                    {
                        "t_s": round(now, 3),
                        "cpu_percent": round(cpu_percent, 1),
                        "rss_bytes": rss,
                        "io_read_bytes": io_read,
                        "io_write_bytes": io_write,
                    }
                )
            except psutil.Error:
                break

    def __enter__(self) -> "ResourceSampler":
        self._thread.start()
        return self

    def __exit__(self, *_: object) -> None:
        self._stop.set()
        self._thread.join(timeout=2)


def run_ffmpeg(
    args: list[str],
    staging: Path,
    label: str,
    instrument: bool,
) -> dict:
    stdout_path = staging / f"ffmpeg-{label}-progress.log"
    stderr_path = staging / f"ffmpeg-{label}-stderr.log"
    command = ["ffmpeg", "-hide_banner", "-nostdin", "-y"]
    if instrument:
        command += ["-progress", "pipe:1", "-nostats"]
    command += args

    started = time.perf_counter()
    with stdout_path.open("w") as stdout, stderr_path.open("w") as stderr:
        process = subprocess.Popen(command, stdout=stdout, stderr=stderr)
    with ResourceSampler(process.pid) as sampler:
        return_code = process.wait()
    elapsed = time.perf_counter() - started

    if return_code != 0:
        tail = stderr_path.read_text(errors="replace")[-2000:]
        raise RuntimeError(f"ffmpeg failed with exit code {return_code}:\n{tail}")

    peak_rss = max((s["rss_bytes"] for s in sampler.samples), default=0)
    peak_cpu = max((s["cpu_percent"] for s in sampler.samples), default=0)
    speed = parse_last_progress_speed(stdout_path)
    return {
        "seconds": elapsed,
        "peak_rss_bytes": peak_rss,
        "peak_cpu_percent": peak_cpu,
        "samples": sampler.samples,
        "ffmpeg_reported_speed": speed,
        "progress_log": str(stdout_path),
        "stderr_log": str(stderr_path),
        "command": command,
    }


def parse_last_progress_speed(progress_log: Path) -> str | None:
    speed = None
    try:
        for line in progress_log.read_text(errors="replace").splitlines():
            if line.startswith("speed="):
                speed = line.split("=", 1)[1].strip()
    except OSError:
        pass
    return speed


def decoded_pcm_md5(path: Path) -> str:
    result = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a", "-f", "md5", "-"],
        capture_output=True,
        text=True,
        check=True,
    )
    return result.stdout.strip().splitlines()[-1]


# --------------------------------------------------------------------------
# Orchestration
# --------------------------------------------------------------------------


def resource_snapshot(root: Path) -> dict:
    snapshot = {
        "at": datetime.now(timezone.utc).isoformat(),
        "cpu_logical": os.cpu_count(),
        "cpu_freq_mhz": None,
        "ram_total_bytes": None,
        "disk_total_bytes": None,
        "disk_free_bytes": None,
        "net_bytes_recv": None,
        "net_bytes_sent": None,
    }
    if psutil:
        frequencies = psutil.cpu_freq()
        snapshot["cpu_freq_mhz"] = round(frequencies.max, 0) if frequencies else None
        snapshot["ram_total_bytes"] = psutil.virtual_memory().total
        usage = psutil.disk_usage(str(root))
        snapshot["disk_total_bytes"] = usage.total
        snapshot["disk_free_bytes"] = usage.free
        network = psutil.net_io_counters()
        snapshot["net_bytes_recv"] = network.bytes_recv
        snapshot["net_bytes_sent"] = network.bytes_sent
    return snapshot


def run_benchmark(args: argparse.Namespace) -> int:
    out_root = guard_out_root(Path(args.out_root))
    run_dir = out_root / f"{args.label}-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}"
    staging = run_dir / "staging"
    books = run_dir / "books"
    staging.mkdir(parents=True)
    books.mkdir(parents=True)

    metadata_cache = Path(__file__).resolve().parent.parent / "fixtures" / f"librivox-{args.id}.json"
    if not metadata_cache.exists():
        metadata_cache = None

    print(f"[bench] output root: {run_dir}")
    phases: list[dict] = []
    summary: dict = {
        "schema": "nostos-librivox-bench/1",
        "label": args.label,
        "started_at": datetime.now(timezone.utc).isoformat(),
        "run_dir": str(run_dir),
        "out_root_guarded": str(out_root),
        "download_concurrency": args.concurrency,
        "resources_before": resource_snapshot(out_root),
    }

    # 1. catalogue plan
    started = time.perf_counter()
    plan_cache = run_dir / "plan.json"
    recording = fetch_recording(args.id, metadata_cache)
    plan_payload = {
        "id": recording.id,
        "title": recording.title,
        "totaltime": recording.totaltime,
        "sections": [
            {"number": s.number, "title": s.title, "url": s.url} for s in recording.sections
        ],
    }
    plan_cache.write_text(json.dumps(plan_payload, indent=2))
    phases.append(
        {
            "phase": "catalog_plan",
            "seconds": time.perf_counter() - started,
            "detail": f"{len(recording.sections)} sections",
        }
    )
    summary.update(
        {
            "recording_id": recording.id,
            "recording_title": recording.title,
            "feed_totaltime": recording.totaltime,
            "section_count": len(recording.sections),
        }
    )

    # 2. download
    if args.concurrency > 8:
        die("bounded concurrency only: --concurrency max 8 (safe host policy)")
    reuse_from = Path(args.resume_from).resolve() if args.resume_from else None
    if reuse_from is not None and not reuse_from.exists():
        die(f"--resume-from {reuse_from} does not exist")
    network_before = resource_snapshot(out_root)
    paths, parts, download_seconds = download_parts(
        recording.sections, staging, args.concurrency, MAX_TOTAL_BYTES, reuse_from=reuse_from
    )
    network_after = resource_snapshot(out_root)
    total_source_bytes = sum(p["bytes"] for p in parts)
    fresh = [p for p in parts if not p.get("reused")]
    fresh_bytes = sum(p["bytes"] for p in fresh)
    fresh_seconds = sum(p["seconds"] or 0 for p in fresh)
    phases.append(
        {
            "phase": "download",
            "seconds": download_seconds,
            "detail": (
                f"{len(parts)} parts, {total_source_bytes} bytes"
                + (f", {len(fresh)} freshly fetched" if reuse_from else "")
            ),
        }
    )
    summary["download"] = {
        "parts": parts,
        "reused_parts": len(parts) - len(fresh),
        "total_source_bytes": total_source_bytes,
        "seconds": download_seconds,
        "fresh_bytes": fresh_bytes,
        "fresh_seconds": fresh_seconds,
        "fresh_aggregate_bytes_per_second": (fresh_bytes / fresh_seconds) if fresh_seconds else None,
        "aggregate_bytes_per_second": total_source_bytes / download_seconds,
        "network_bytes_delta": (
            network_after["net_bytes_recv"] - network_before["net_bytes_recv"]
            if network_after["net_bytes_recv"] is not None
            else None
        ),
    }

    # 3. probe inputs
    durations, probes, probe_seconds = probe_inputs(paths)
    phases.append(
        {
            "phase": "probe_inputs",
            "seconds": probe_seconds,
            "detail": f"{len(paths)} sections, {sum(durations):.1f}s audio",
        }
    )
    summary["probe"] = {
        "per_section": probes,
        "seconds": probe_seconds,
        "sum_section_duration_s": sum(durations),
    }

    # 4. write concat list + chapter table
    started = time.perf_counter()
    concat_path = staging / "concat.txt"
    chapters_path = staging / "chapters.txt"
    concat_path.write_text(build_concat_list(paths))
    chapters_path.write_text(build_chapter_metadata(recording.sections, durations))
    write_seconds = time.perf_counter() - started
    phases.append({"phase": "write_inputs", "seconds": write_seconds, "detail": "concat + chapters"})

    # 5. encode
    output_path = staging / "result.m4b"
    ffmpeg_args = product_ffmpeg_args(concat_path, chapters_path, output_path)
    ffmpeg_record = run_ffmpeg(ffmpeg_args, staging, "encode", instrument=not args.no_progress)
    phases.append(
        {
            "phase": "encode",
            "seconds": ffmpeg_record["seconds"],
            "detail": f"speed={ffmpeg_record['ffmpeg_reported_speed']}",
        }
    )
    audio_seconds = sum(durations)
    summary["encode"] = {
        key: value for key, value in ffmpeg_record.items() if key != "samples"
    }
    summary["encode"]["realtime_factor"] = audio_seconds / ffmpeg_record["seconds"]
    samples_path = run_dir / "ffmpeg-samples.csv"
    with samples_path.open("w", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=["t_s", "cpu_percent", "rss_bytes", "io_read_bytes", "io_write_bytes"])
        writer.writeheader()
        writer.writerows(ffmpeg_record["samples"])
    summary["encode"]["samples_csv"] = str(samples_path)

    # 6. validate output exactly as the assembler does
    started = time.perf_counter()
    produced_chapters = ffprobe_chapter_count(output_path)
    produced_duration = ffprobe_duration(output_path)
    tolerance = max(2.0, audio_seconds * 0.005)
    validation_ok = (
        produced_chapters == len(recording.sections)
        and produced_duration is not None
        and abs(produced_duration - audio_seconds) <= tolerance
    )
    validate_seconds = time.perf_counter() - started
    phases.append(
        {
            "phase": "validate_output",
            "seconds": validate_seconds,
            "detail": f"chapters={produced_chapters} duration={produced_duration}",
        }
    )
    summary["validation"] = {
        "produced_chapters": produced_chapters,
        "expected_chapters": len(recording.sections),
        "produced_duration_s": produced_duration,
        "expected_duration_s": audio_seconds,
        "tolerance_s": tolerance,
        "ok": validation_ok,
    }
    if not validation_ok:
        raise RuntimeError("produced output failed the product's own validation rules")

    # Optional: prove the progress instrumentation did not change the encode.
    if args.instrumentation_check:
        exact_path = staging / "result-exact.m4b"
        exact_args = product_ffmpeg_args(concat_path, chapters_path, exact_path)
        exact_record = run_ffmpeg(exact_args, staging, "exact", instrument=False)
        exact_md5 = decoded_pcm_md5(exact_path)
        instrumented_md5 = decoded_pcm_md5(output_path)
        summary["instrumentation_check"] = {
            "exact_encode_seconds": exact_record["seconds"],
            "instrumented_encode_seconds": ffmpeg_record["seconds"],
            "exact_decoded_pcm_md5": exact_md5,
            "instrumented_decoded_pcm_md5": instrumented_md5,
            "identical_decoded_audio": exact_md5 == instrumented_md5,
        }
        if exact_md5 != instrumented_md5:
            raise RuntimeError("instrumented and exact encodes differ; progress flag is not neutral here")

    # 7. commit: same-volume rename, as AdoptBookFileAsync does.
    started = time.perf_counter()
    book_folder = books / uuid.uuid4().hex
    book_folder.mkdir()
    final_path = book_folder / "book.m4b"
    os.replace(output_path, final_path)
    commit_seconds = time.perf_counter() - started
    phases.append(
        {
            "phase": "commit_rename",
            "seconds": commit_seconds,
            "detail": f"{final_path.stat().st_size} bytes",
        }
    )
    summary["output"] = {
        "bytes": final_path.stat().st_size,
        "path": str(final_path),
        "content_type": "audio/mp4",
        "extension": ".m4b",
    }

    summary["phases"] = phases
    summary["total_wall_seconds"] = sum(p["seconds"] for p in phases)
    summary["resources_after"] = resource_snapshot(out_root)

    summary_path = run_dir / "summary.json"
    summary_path.write_text(json.dumps(summary, indent=2))
    phases_path = run_dir / "phases.csv"
    with phases_path.open("w", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=["phase", "seconds", "detail"])
        writer.writeheader()
        for phase in phases:
            writer.writerow(phase)

    print_phase_table(summary)
    print(f"[bench] summary: {summary_path}")

    if not args.keep_media:
        # Keep the small textual evidence beside the summary before the audio
        # staging (which can be gigabytes) is removed.
        for log_name in (
            "ffmpeg-encode-progress.log",
            "ffmpeg-encode-stderr.log",
            "ffmpeg-exact-progress.log",
            "ffmpeg-exact-stderr.log",
        ):
            source = staging / log_name
            if source.exists():
                shutil.copy2(source, run_dir / log_name)
        shutil.rmtree(staging, ignore_errors=True)
        shutil.rmtree(books, ignore_errors=True)
        print("[bench] staging media removed (self-cleaning); summaries and ffmpeg logs retained")

    return 0 if validation_ok else 1


def print_phase_table(summary: dict) -> None:
    print()
    print(f"=== {summary['label']} | {summary['recording_title']} "
          f"({summary['section_count']} sections, feed {summary['feed_totaltime']}) ===")
    print(f"{'phase':<20} {'seconds':>10}  detail")
    for phase in summary["phases"]:
        print(f"{phase['phase']:<20} {phase['seconds']:>10.2f}  {phase['detail']}")
    encode = summary.get("encode", {})
    print(f"{'encode realtime x':<20} {encode.get('realtime_factor', 0):>10.1f}")
    print(f"source bytes:  {summary['download']['total_source_bytes']:,}")
    print(f"output bytes:  {summary['output']['bytes']:,}")
    print(f"ffmpeg speed:  {encode.get('ffmpeg_reported_speed')}")
    print(f"peak rss:      {encode.get('peak_rss_bytes', 0):,} bytes")
    print(f"peak cpu:      {encode.get('peak_cpu_percent', 0)} %")


# --------------------------------------------------------------------------
# Micro-benchmarks
# --------------------------------------------------------------------------


def prepare_small_sections(args: argparse.Namespace, count: int) -> tuple[Path, Recording, list[Path]]:
    out_root = guard_out_root(Path(args.out_root))
    work = out_root / f"micro-{int(time.time())}"
    staging = work / "staging"
    staging.mkdir(parents=True)
    metadata_cache = Path(__file__).resolve().parent.parent / "fixtures" / f"librivox-{args.id}.json"
    recording = fetch_recording(args.id, metadata_cache if metadata_cache.exists() else None)
    sections = recording.sections[:count]
    paths, _, _ = download_parts(sections, staging, min(DOWNLOAD_CONCURRENCY, count), MAX_TOTAL_BYTES)
    return work, recording, paths


def micro_byte_concat(args: argparse.Namespace) -> int:
    work, recording, paths = prepare_small_sections(args, args.parts)
    joined = work / "byte-concat.mp3"
    with joined.open("wb") as handle:
        for path in paths:
            handle.write(path.read_bytes())
    duration = ffprobe_duration(joined)
    chapters = ffprobe_chapter_count(joined)
    id3_offsets = []
    for path in paths[1:]:
        data = path.read_bytes()
        index = data.find(b"ID3")
        if index >= 0:
            id3_offsets.append(index)
    summary = {
        "experiment": "byte-concatenated-mp3",
        "parts": [p.name for p in paths],
        "bytes": joined.stat().st_size,
        "ffprobe_duration_s": duration,
        "ffprobe_chapters": chapters,
        "mid_stream_id3_header_offsets": id3_offsets,
        "expected_total_s": sum(ffprobe_duration(p) or 0 for p in paths),
    }
    (work / "summary.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary, indent=2))
    print(f"[micro] artifacts: {work}")
    return 0


def micro_stream_copy(args: argparse.Namespace) -> int:
    work, recording, paths = prepare_small_sections(args, args.parts)
    concat_path = work / "concat.txt"
    concat_path.write_text(build_concat_list(paths))
    durations = [ffprobe_duration(p) or 0 for p in paths]
    chapters_path = work / "chapters.txt"
    chapters_path.write_text(build_chapter_metadata(recording.sections, durations))
    output = work / "stream-copy.m4b"
    args_vec = product_ffmpeg_args(concat_path, chapters_path, output)
    codec_index = args_vec.index("-c:a") + 1
    args_vec[codec_index] = "copy"
    record = run_ffmpeg(args_vec, work, "streamcopy", instrument=False)
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_streams", "-print_format", "json", str(output)],
        capture_output=True,
        text=True,
        check=True,
    )
    streams = json.loads(probe.stdout).get("streams", [])
    summary = {
        "experiment": "stream-copy-m4b",
        "arguments": record["command"],
        "encode_seconds": record["seconds"],
        "output_bytes": output.stat().st_size if output.exists() else 0,
        "ffprobe_chapters": ffprobe_chapter_count(output),
        "ffprobe_duration_s": ffprobe_duration(output),
        "streams": [
            {
                "codec_name": s.get("codec_name"),
                "sample_rate": s.get("sample_rate"),
                "channels": s.get("channels"),
                "bit_rate": s.get("bit_rate"),
            }
            for s in streams
        ],
    }
    (work / "summary.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary, indent=2))
    print(f"[micro] artifacts: {work}")
    return 0


def micro_download_concurrency(args: argparse.Namespace) -> int:
    out_root = guard_out_root(Path(args.out_root))
    results = []
    for concurrency in (1, 3, 6):
        work = out_root / f"dl-c{concurrency}-{int(time.time())}"
        staging = work / "staging"
        staging.mkdir(parents=True)
        metadata_cache = Path(__file__).resolve().parent.parent / "fixtures" / f"librivox-{args.id}.json"
        recording = fetch_recording(args.id, metadata_cache if metadata_cache.exists() else None)
        sections = recording.sections[: args.parts]
        paths, parts, seconds = download_parts(sections, staging, concurrency, MAX_TOTAL_BYTES)
        total = sum(p["bytes"] for p in parts)
        results.append(
            {
                "concurrency": concurrency,
                "parts": len(parts),
                "bytes": total,
                "seconds": round(seconds, 3),
                "bytes_per_second": round(total / seconds, 1),
            }
        )
        shutil.rmtree(staging, ignore_errors=True)
    print(json.dumps(results, indent=2))
    return 0


# --------------------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        "--out-root",
        default="/home/dev/.hermes/cache/scratch/librivox-import",
        help="scratch root; must be under an allowed temp path and outside production Storage",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    run = sub.add_parser("run", help="run the full mirrored pipeline")
    run.add_argument("--id", required=True)
    run.add_argument("--label", required=True)
    run.add_argument("--concurrency", type=int, default=DOWNLOAD_CONCURRENCY)
    run.add_argument("--keep-media", action="store_true")
    run.add_argument("--no-progress", action="store_true")
    run.add_argument("--instrumentation-check", action="store_true")
    run.add_argument(
        "--resume-from",
        default=None,
        help="previous run directory whose staging/part-*.mp3 are reused when present",
    )

    concat = sub.add_parser("micro-byte-concat", help="byte-concatenate real sections and probe the result")
    concat.add_argument("--id", default="2469")
    concat.add_argument("--parts", type=int, default=2)

    stream = sub.add_parser("micro-stream-copy", help="encode with -c:a copy into the M4B container")
    stream.add_argument("--id", default="2469")
    stream.add_argument("--parts", type=int, default=2)

    downloads = sub.add_parser("micro-download-concurrency", help="bounded download-concurrency comparison")
    downloads.add_argument("--id", default="2469")
    downloads.add_argument("--parts", type=int, default=6)

    args = parser.parse_args()

    if args.command == "run":
        return run_benchmark(args)
    if args.command == "micro-byte-concat":
        return micro_byte_concat(args)
    if args.command == "micro-stream-copy":
        return micro_stream_copy(args)
    if args.command == "micro-download-concurrency":
        return micro_download_concurrency(args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
