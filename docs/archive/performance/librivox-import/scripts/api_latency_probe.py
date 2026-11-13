#!/usr/bin/env python3
"""API-responsiveness probe for a running Nostos SelfHosted instance.

Runs an idle baseline, queues one or two LibriVox imports through the public
provider API, and measures:

  * latency of /health/ready and ordinary library requests throughout;
  * acquisition job stage transitions, timestamped (queue wait vs work);
  * backend process CPU / RSS samples while the imports run.

It only talks to the --base-url given (an isolated test instance), never a
production origin, and writes metrics under --out.

Usage:
    python3 api_latency_probe.py --base-url http://127.0.0.1:5330 \
        --out /home/dev/.hermes/cache/scratch/librivox-import/app \
        --pid <backend-pid> --first-id 11035 --second-id 2469 --second-delay 90
"""

from __future__ import annotations

import argparse
import csv
import json
import statistics
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

try:
    import psutil
except ImportError:  # pragma: no cover
    psutil = None

PROBE_INTERVAL_SECONDS = 0.5
JOB_POLL_SECONDS = 1.0


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        return float("nan")
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, int(round(fraction * (len(ordered) - 1)))))
    return ordered[index]


class ApiProbe(threading.Thread):
    def __init__(self, base_url: str, paths: list[str], interval: float):
        super().__init__(daemon=True)
        self.base_url = base_url.rstrip("/")
        self.paths = paths
        self.interval = interval
        self.samples: list[dict] = []
        self._stop = threading.Event()
        self.phase = "baseline"

    def run(self) -> None:
        while not self._stop.is_set():
            for path in self.paths:
                url = f"{self.base_url}{path}"
                started = time.perf_counter()
                status = None
                error = None
                try:
                    with urllib.request.urlopen(url, timeout=10) as response:
                        status = response.status
                        response.read(4096)
                except urllib.error.HTTPError as http_error:
                    status = http_error.code
                except Exception as exception:  # noqa: BLE001
                    error = type(exception).__name__
                elapsed = time.perf_counter() - started
                self.samples.append(
                    {
                        "at": now_iso(),
                        "phase": self.phase,
                        "path": path,
                        "seconds": elapsed,
                        "status": status,
                        "error": error,
                    }
                )
            self._stop.wait(self.interval)

    def stop(self) -> None:
        self._stop.set()


class BackendSampler(threading.Thread):
    def __init__(self, pid: int, interval: float = 1.0):
        super().__init__(daemon=True)
        self.pid = pid
        self.interval = interval
        self.samples: list[dict] = []
        self._stop = threading.Event()
        self.phase = "baseline"

    def run(self) -> None:
        process = psutil.Process(self.pid)
        previous = process.cpu_times().user + process.cpu_times().system
        previous_at = time.perf_counter()
        while not self._stop.is_set():
            time.sleep(self.interval)
            try:
                processes = [process] + process.children(recursive=True)
                cpu = sum(
                    p.cpu_times().user + p.cpu_times().system
                    for p in processes
                    if p.is_running()
                )
                rss = sum(p.memory_info().rss for p in processes if p.is_running())
                now = time.perf_counter()
                elapsed = now - previous_at
                self.samples.append(
                    {
                        "at": now_iso(),
                        "phase": self.phase,
                        "cpu_percent": round((cpu - previous) / elapsed * 100, 1),
                        "rss_bytes": rss,
                        "process_count": len(processes),
                    }
                )
                previous, previous_at = cpu, now
            except psutil.Error:
                break

    def stop(self) -> None:
        self._stop.set()


def post_json(url: str, payload: dict) -> tuple[int, dict]:
    data = json.dumps(payload).encode()
    request = urllib.request.Request(
        url, data=data, headers={"Content-Type": "application/json"}, method="POST"
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, json.loads(response.read() or b"{}")
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read() or b"{}")


def get_json(url: str) -> tuple[int, dict]:
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            return response.status, json.loads(response.read() or b"{}")
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read() or b"{}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--pid", type=int, required=True, help="backend process id to sample")
    parser.add_argument("--first-id", required=True)
    parser.add_argument("--second-id", default=None)
    parser.add_argument("--second-delay", type=float, default=90.0)
    parser.add_argument("--baseline-seconds", type=float, default=20.0)
    parser.add_argument("--timeout-minutes", type=float, default=90.0)
    parser.add_argument(
        "--probe-paths",
        default="/health/ready,/api/books?limit=1",
        help="comma-separated paths to poll",
    )
    args = parser.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    baseline_paths = [p.strip() for p in args.probe_paths.split(",") if p.strip()]

    probe = ApiProbe(args.base_url, baseline_paths, PROBE_INTERVAL_SECONDS)
    backend = BackendSampler(args.pid) if psutil else None
    probe.start()
    if backend:
        backend.start()

    result: dict = {
        "schema": "nostos-api-latency-probe/1",
        "started_at": now_iso(),
        "base_url": args.base_url,
        "backend_pid": args.pid,
        "probe_paths": baseline_paths,
        "jobs": [],
        "stage_transitions": [],
    }

    print(f"[probe] baseline for {args.baseline_seconds:.0f}s...")
    time.sleep(args.baseline_seconds)

    def start_job(recording_id: str, label: str) -> dict | None:
        status, body = post_json(
            f"{args.base_url}/api/providers/librivox/acquire",
            {"externalId": recording_id, "assetId": "m4b", "includeCover": False},
        )
        print(f"[probe] POST acquire {label} id={recording_id} -> HTTP {status}: {body}")
        if status not in (200, 202):
            result.setdefault("errors", []).append(
                {"at": now_iso(), "action": "start_job", "label": label, "status": status, "body": body}
            )
            return None
        return body

    first = start_job(args.first_id, "first")
    if first:
        result["jobs"].append({"label": "first", "recording_id": args.first_id, "started_at": now_iso(), **first})

    second_started = False
    second_start_at = None
    deadline = time.time() + args.timeout_minutes * 60
    last_states: dict[str, str] = {}
    job_ids = [job["jobId"] for job in result["jobs"]]

    while time.time() < deadline:
        if args.second_id and not second_started:
            if second_start_at is None:
                second_start_at = time.time()
            elif time.time() - second_start_at >= args.second_delay:
                second = start_job(args.second_id, "second")
                second_started = True
                if second:
                    result["jobs"].append(
                        {"label": "second", "recording_id": args.second_id, "started_at": now_iso(), **second}
                    )
                    job_ids.append(second["jobId"])

        terminal = 0
        for job_id in job_ids:
            status, body = get_json(f"{args.base_url}/api/providers/acquisitions/{job_id}")
            if status != 200:
                terminal += 1
                continue
            state = f"{body.get('state')}:{body.get('stage')}"
            if last_states.get(job_id) != state:
                last_states[job_id] = state
                transition = {
                    "at": now_iso(),
                    "job_id": job_id,
                    "state": body.get("state"),
                    "stage": body.get("stage"),
                    "percent": body.get("percent"),
                    "detail": body.get("detail"),
                    "message": body.get("message"),
                }
                result["stage_transitions"].append(transition)
                print(f"[probe] job {job_id[:8]} -> {state} {body.get('percent')}% {body.get('detail') or ''}")
            if body.get("state") in ("succeeded", "failed", "cancelled"):
                terminal += 1
        if terminal >= len(job_ids) and (not args.second_id or second_started):
            break
        time.sleep(JOB_POLL_SECONDS)

    probe.stop()
    if backend:
        backend.stop()
    probe.join(timeout=3)
    if backend:
        backend.join(timeout=3)

    # Latency analysis by endpoint and phase.
    latency: dict = {}
    for path in baseline_paths:
        for phase in sorted({s["phase"] for s in probe.samples} | {"baseline"}):
            values = [
                s["seconds"]
                for s in probe.samples
                if s["path"] == path and s["phase"] == phase and s["error"] is None
            ]
            if not values:
                continue
            latency.setdefault(path, {})[phase] = {
                "samples": len(values),
                "p50_ms": round(percentile(values, 0.50) * 1000, 1),
                "p95_ms": round(percentile(values, 0.95) * 1000, 1),
                "max_ms": round(max(values) * 1000, 1),
                "mean_ms": round(statistics.fmean(values) * 1000, 1),
            }

    result["latency"] = latency
    result["job_results"] = [
        {
            "job_id": job_id,
            "status": get_json(f"{args.base_url}/api/providers/acquisitions/{job_id}")[1],
        }
        for job_id in job_ids
    ]
    result["finished_at"] = now_iso()

    (out / "api-probe-summary.json").write_text(json.dumps(result, indent=2))

    samples_path = out / "api-probe-samples.csv"
    with samples_path.open("w", newline="") as handle:
        writer = csv.DictWriter(
            handle, fieldnames=["at", "phase", "path", "seconds", "status", "error"]
        )
        writer.writeheader()
        writer.writerows(probe.samples)

    if backend:
        backend_samples_path = out / "backend-samples.csv"
        with backend_samples_path.open("w", newline="") as handle:
            writer = csv.DictWriter(
                handle, fieldnames=["at", "phase", "cpu_percent", "rss_bytes", "process_count"]
            )
            writer.writeheader()
            writer.writerows(backend.samples)
        peak_rss = max((s["rss_bytes"] for s in backend.samples), default=0)
        result["backend_peak_rss_bytes"] = peak_rss
        result["backend_peak_rss_samples"] = sorted(
            backend.samples, key=lambda s: s["rss_bytes"], reverse=True
        )[:3]

    print(json.dumps({k: result[k] for k in ("latency", "stage_transitions")}, indent=2))
    print(f"[probe] summary: {out / 'api-probe-summary.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
