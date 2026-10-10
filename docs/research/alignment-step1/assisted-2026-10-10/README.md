# Step 1 — English audio-to-text benchmark: assisted pilot

The owner approved independent cached local Whisper references after the session could not ingest audio. This is an assisted pilot, not a human-ground-truth benchmark. It supersedes the earlier unannotated candidate receipt; the original human gate remains unverified.

`manifest.json` records ten source URLs/hashes, all 32 clip timings/hashes, reference identities, directly inspected EPUB source intervals and cutoff uncertainty. `reference/` contains independent transcripts, word timing and decoder confidence. `freeze.json` predates all scored outputs and pins unchanged offline calibration. The original Pride introductions included narrative prose; replacements use first-edition LibriVox tracks at endpoints 20s and 25s. All positive endpoints remain 90s/180s. No padding or matcher-generated labels.

Verification: 9 focused matcher tests, 5 Python evidence/gate tests, ten independent synthetic calibration cases, and exact materialization of both book extractions/all 32 clips. Public CI passed on the implementation commit. No reader/Settings/browser acceptance is claimed.

Commands are documented in `tools/Nostos.Alignment.Spike/README.md`: build/test, materialize exact fixtures from this manifest, calibrate/freeze, replay cached index, then finalize with accepted inspection receipts. Run replay from the public repo root using `results/transcripts-index.json`. Private billing/model records are in Cloud #294. No public HTTP endpoint or database migration.

Final matching evidence and the single next-step recommendation are in `results/README.md`. Both research issues stay open; do not merge or enable auto-merge as part of this checkpoint.
