# Final assisted replay — 2026-10-10

All 64 fixed responses are cached here with provider-neutral tags; exact model mapping, routing and economics are in private Cloud #294. `transcripts-index.json` uses paths relative to the public repository root. Replay against the reproduced parent manifest and unchanged matcher settings; finalize using `accepted-reviews.json`.

| Provider | Correct at 10s | Correct at 20s | Incorrect accepts | Intro rejections | Median transcription latency |
|---|---:|---:|---:|---:|---:|
| model-1 | 12/12 | 12/12 | 0 | 8/8 | 2.904s |
| model-3 | 8/12 | 12/12 | 4 | 8/8 | 2.490s |

Every accepted location (48) was inspected against the independent local Whisper reference and EPUB source text. model-1 passes the assisted gate; model-3 fails because of four lagging endpoints at 10s. Both human continuation gates remain false. Rejected passages: zero. Source error is distance **outside the frozen bounded endpoint interval**, not exact audio/character timing. Incorrect endpoints lag by 13, 87, 11 and 33 characters outside their intervals; one candidate lies in the preceding paragraph. Transcription latency includes the provider round trip, excluding clip recording, book extraction, matching and billing reconciliation.

The omitted model has no scored calls because its output-cost ceiling could not be verified. Two books and six narrated positions per book do not establish general audiobook reliability. No thresholds, source ranges or audio bytes changed after freeze.

Recommended next checkpoint: independently listen to and verify the winning provider's 32 frozen clips against the EPUB source, using these cached responses and a mode that can directly ingest audio. Verify the introductions and all accepted endpoints before reader integration. Do not widen intervals or request more scored audio to rescue the failing provider.
