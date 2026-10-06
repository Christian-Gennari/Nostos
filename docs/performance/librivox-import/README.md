# LibriVox acquisition performance spike (#773)

Measurement-first spike for
<https://github.com/Christian-Gennari/Nostos/issues/773>. It reproduces the
current LibriVox → single chaptered M4B acquisition path against real,
redistributable LibriVox recordings and records phase timings and resources.
It changes no product code and no production data.

## Verdict

On this host the existing pipeline is **not pathologically slow**: a real
19-hour, 54-section recording was downloaded and assembled into a single
54-chapter M4B in **~18.8 minutes of measured work** (download 6.8 min,
encode ~11.9 min at ~95× realtime, probe 3.2 s, output validation 0.45 s,
commit < 0.01 s). Encoder peak RSS was ~255 MiB and CPU ~1.1 cores.

The reported “stall at transcoding” is therefore **not explained by a hung or
cryptographically slow encoder** in this environment. The highest-confidence
explanation is presentational: while `ffmpeg` runs, the API reports stage
`assembling` at a fixed **72 %** for the entire encode and only jumps to 92 %
afterwards (`AcquisitionService.AssembleAsync`,
`add-book-modal.component.ts` stage labels), so a ~12-minute encode looks
frozen. Whether the Cloud web process’s API responsiveness is materially hurt
by the in-process encode was **not measured** and remains unverified.

No OOM, crash, timeout, or throttling was observed on this host during any
completed run. That is not evidence that Cloud cannot OOM; the Cloud
environment was not exercised.

## Safety and isolation

- No production service was called; the running production backend on :5214
  was never touched.
- All benchmark data lives under
  `/home/dev/.hermes/cache/scratch/librivox-import/` (worktree
  `Nostos.Backend/Storage` is a symlink to production storage). The harness
  refuses any `--out-root` inside a `Nostos.Backend/Storage` directory or its
  symlink target, and only accepts the two allowed scratch roots.
- The isolated backend helper copies the worktree’s SQLite database into
  scratch and overrides `Persistence__DatabasePath`, `Storage__BooksRoot`,
  `Storage__BackupsRoot` and `Acquisition__WorkingRoot`; it was not run in this
  spike (see Limitations).

## Workloads (public-domain LibriVox)

| Label | LibriVox id | Sections | Feed duration | Source bytes | Result |
| --- | --- | ---: | --- | ---: | --- |
| `short-1601` | 2469 | 2 | 0:19:38 | 9,433,509 | complete |
| `long-donquixote` | 11035 | 54 | 18:58:10 | 546,358,784 | complete |
| `many-mobydick` | 21919 | 138 | 23:34:48 | (incomplete) | **incomplete** |

Cached feed fixtures: `fixtures/librivox-<id>.json`. Section ranges are the
provider’s own ordered catalogue data (`listen_url`, `section_number`).

## Method

`scripts/bench_librivox_import.py` mirrors the product pipeline phase for
phase: one catalogue fetch; section download with the product policy
(concurrency 3, 3 attempts, 900 s/attempt, 256 MiB/part, 3 GiB total);
per-section `ffprobe` with the exact `MediaProcessRunner` arguments; the exact
concat-list and `;FFMETADATA1` text formats; the exact `ffmpeg` vector
(`-f concat -safe 0 … -c:a aac -b:a 64k -ac 1 -ar 44100 -movflags +faststart
-f ipod`); the product’s chapter-count and duration-tolerance validation; and
a same-volume rename as the commit.

The only deviation is the observability-only `-progress pipe:1 -nostats` pair.
The `short-1601` run proved it neutral on real output with
`--instrumentation-check`: both encodes produced the same decoded PCM MD5
(`MD5=ad7fd488af752aac81ff831c5877da1e`; 14.42 s exact vs 14.36 s
instrumented).

## Measured results (completed runs)

Environment: AMD Ryzen 5 4600H, 12 logical CPUs, 16,091,504,640 B RAM, 79.7 GB
free on the scratch volume, ffmpeg/ffprobe 6.1.1, Linux 6.8.0-139
(`evidence/environment.txt`).

### `long-donquixote` — 54 sections, 68,286.3 s of audio

| Phase | Seconds | Detail |
| --- | ---: | --- |
| catalogue plan | ~0.0 | 54 sections |
| download | 409.07 | 546,358,784 B; aggregate 1,335,616 B/s |
| probe inputs | 3.18 | 54 sections, 58.9 ms/section |
| write inputs | ~0.0 | concat list + chapter table |
| encode | 716.66 | 95.4× realtime; peak RSS 267,616,256 B; peak CPU 117.4 % (steady ~105–112 %) |
| validate output | 0.45 | 54 chapters; duration 68,286.302 s |
| commit (rename) | <0.01 | 568,033,244 B output |

Total measured work ≈ 1,129 s (~18.8 min). Source 521 MiB → output 542 MiB
(ratio 1.04). Disk high-water in staging is arithmetically ≈ 1.11 GiB (sources
plus result). Per-part download: 4/8/21 MiB min/median/max in
8.4/20.6/54.9 s; no retry was needed in this run. RSS grew from ~51 MiB to
~255 MiB across the encode; samples in
`evidence/long-donquixote-ffmpeg-samples-downsampled.csv` (every 20th of 1,351
samples; full file under the scratch run dir).

### `short-1601` — 2 sections, 1,179.1 s of audio

Download 38.02 s (9,433,509 B; 248 KB/s with only two connections), probe
0.12 s, encode 14.36 s (82.4×), validation 2 chapters, peak RSS ~51 MiB,
peak CPU 106.6 %, output 9,855,820 B.

### What the numbers support

- Encode is the largest single phase locally (63 % of measured work) but it is
  a full-quality re-encode at ~95× realtime; it is not hung, and the
  180-minute encode ceiling is not a plausible failure mode for a 20-hour book
  on hardware of this class.
- Download and network throughput are the other half of the cost
  (~1.3 MB/s aggregate here at concurrency 3); this host’s link and
  archive.org are the variables, not CPU.
- Per-section probing is negligible (≈59 ms/section, 3.2 s for 54).
- The commit is a rename on the same volume (<10 ms for 542 MiB).

## `many-mobydick` (138 sections) — incomplete; what was observed

Both attempts were stopped by transient archive.org errors, each after the
product’s 3-attempt retry policy:

- Attempt 1: 124/138 parts downloaded (606,092,463 B) before failing on
  `mobydickorthewhale_003_melville_64kb.mp3` with **HTTP 502 Bad Gateway**
  (`evidence/many-mobydick-attempt1.log.txt`).
- Attempt 2 (resumed from the 124 complete parts; only missing parts were
  fetched): 132/138 parts (647,441,357 B) before failing on
  `mobydickorthewhale_004_melville_64kb.mp3` with **HTTP 500 Internal Server
  Error** (`evidence/many-mobydick-attempt2.log.txt`,
  `evidence/many-mobydick-observed-state.txt`). No encode phase was reached;
  the run was stopped per instruction.

The exact failing URLs downloaded successfully in later sequential checks,
so this is labelled a **transient, part-level failure observed on this host**,
not proof of a product-wide outage or of a systemic LibriVox problem. It does
show that a single section out of 138 exhausting three attempts aborts the
whole acquisition (the pipeline cancels sibling downloads on first failure),
which makes many-section imports disproportionately failure-prone relative to
short books. The 138-section phase timings therefore remain **unmeasured**.

## UI progress behaviour (source-inspected, not a live UI test)

`AcquisitionService` reports `downloading` 10–65 %, `validating` 68 %,
`assembling` 72 %, then nothing until `assembling` 92 % after
`AssembleAsync` returns, then `importing` 94 % and `done` 100 %. The frontend
maps stage `assembling` to “Preparing the file”. A 12+ minute encode thus
renders as a constant 72 %.

## Cloud topology (source-inspected; no Cloud measurement)

- Cloud’s `scripts/cloud/app-spec.json` runs the web component as
  `apps-s-1vcpu-1gb-fixed` and a separate `book-text-ingestion` component with
  `dotnet Nostos.Cloud.Host.dll --run-once` (the #149–#151 isolation).
- The inspected Cloud source registers `NostosCloudAcquisitionJobManager` as a
  hosted service **inside the web host**
  (`Nostos.Cloud.Hosting/Composition/NostosCloudHostServices.cs`); its job
  store is in-memory with the same 1–3 consumer workers and the same
  process-wide `TranscodeLimiter` as SelfHosted. The `--run-once` role returns
  before the web host is built, so it does not process acquisitions.
- The local Cloud clone’s pinned SHA may not be the deployed revision; these
  are code facts, not a deployment measurement. No Cloud API latency, CPU, RSS,
  disk or throttling numbers exist in this spike.

## Limitations / not measured

- **API responsiveness during import and bounded-concurrency behaviour were
  NOT measured.** `scripts/api_latency_probe.py` and
  `scripts/start_isolated_backend.sh` are provided and syntax-checked but were
  not executed; there are no p50/p95 latency numbers, no queue-wait numbers,
  and no two-job overlap measurement.
- The 138-section workload is incomplete and its per-phase timings are absent.
- No OOM was produced or ruled out: there was no memory-limit test, and the
  Cloud 1 GiB container was not emulated.
- No Cloud bottleneck is proven; all measured numbers are from one local
  12-thread/16 GB machine and one network path.
- The missing 14 parts mean the many-section download statistics are partial.

## Candidate directions (ranked; none validated as an optimisation here)

1. **Progress fidelity first (cheapest, contract-safe).** Surface real encoder
   progress (`out_time`/speed) or split `assembling` into measurable sub-phases
   so a long encode is visibly working. This addresses the reported symptom
   without touching the media contract.
2. **Resilient section fetch/resume.** More attempts with jittered backoff,
   retry at part level, and resume without re-downloading completed sections;
   consider tolerating a bounded number of retries before aborting the whole
   acquisition. Directly supported by the 500/502 observations above.
3. **Measure, then isolate, Cloud acquisition work.** Run the (already
   provided) API/concurrency benchmark against an isolated Cloud-like
   1 vCPU / 1 GiB deployment of the same build before changing composition;
   only if it shows API contention should an isolated acquisition worker (like
   the book-text worker) be considered. A second process on the same 1 vCPU
   container adds no CPU capacity.
4. **Encode alternatives.** Do not replace the AAC re-encode on current
   evidence: ~95× realtime, ~255 MiB RSS and a correct 54-chapter M4B. Any
   change must re-verify chapters, duration, seeking and reader compatibility.
5. **Byte-concatenation is not a drop-in shortcut (conceptual).** Joining MP3
   bytes produces neither M4B muxing nor a chapter table, mixes per-section
   ID3/Xing/bit-reservoir semantics, and preserves volunteer recordings’
   differing sample rates; it cannot meet the single chaptered M4B contract.
   Stream-copy would retain the source codec/sample rate and does not perform
   the uniform `-ar 44100` normalisation the assembler documents as required.
   No microbenchmark of either shortcut was completed in this spike.

## Follow-up issues recommended

- **Public:** API-latency-and-concurrency benchmark for LibriVox imports on an
  isolated Cloud-like deployment (the two provided scripts are the starting
  point), including queue wait across two imports.
- **Public:** UI progress for `assembling` (encoder progress or finer stages).
- **Public:** resilient/ resumable section downloads with a bounded-retry
  budget.
- **Cloud:** acquisition job/worker isolation and durable ownership
  (in-memory store in the inspected revision; relates to Cloud #70), only
  after the benchmark above shows contention.

## Reproducing

Validated commands (already run; `short-1601` and `long-donquixote`):

```bash
python3 docs/performance/librivox-import/scripts/bench_librivox_import.py \
  --out-root /home/dev/.hermes/cache/scratch/librivox-import \
  run --id 2469 --label short-1601 --instrumentation-check

python3 docs/performance/librivox-import/scripts/bench_librivox_import.py \
  --out-root /home/dev/.hermes/cache/scratch/librivox-import \
  run --id 11035 --label long-donquixote
```

`bench_librivox_import.py --help` documents `--resume-from`, `--keep-media`
and the bounded micro commands. `start_isolated_backend.sh` and
`api_latency_probe.py` are provided for the follow-up API benchmark and were
only syntax-checked here.

## Evidence index

- `evidence/environment.txt` — host/tool versions and commit measured.
- `evidence/short-1601-summary.json`, `evidence/long-donquixote-summary.json` —
  full per-phase and per-part records, exact commands.
- `evidence/long-donquixote-phases.csv`,
  `evidence/long-donquixote-ffmpeg-samples-downsampled.csv`.
- `evidence/many-mobydick-attempt1.log.txt`, `many-mobydick-attempt2.log.txt`,
  `many-mobydick-observed-state.txt` — 502/500 evidence and stopped state.
- Full-run scratch data (including every ffmpeg progress line) remains under
  `/home/dev/.hermes/cache/scratch/librivox-import/` on this host.

Measurement commit: `36aa6ec014f63fa3e34ab53da17dc6005cc993ba`
(branch `spike/librivox-performance`).
