# English audio → EPUB experiment

Part of [Nostos #747](https://github.com/Christian-Gennari/Nostos/issues/747).
This offline CLI tests passage location without a database, reader integration,
or HTTP endpoint. It uses the existing EPUB extractor and `ISTtProvider`; an
adapter assembly supplies transcription. It never configures the application.
SelfHosted Settings remain the user's source of provider configuration. Managed
host configuration and economics stay outside this public experiment.

## Build and focused verification

From the public repository root, with .NET 10, Python 3 and FFmpeg installed:

```sh
dotnet test tools/Nostos.Alignment.Spike.Tests/Nostos.Alignment.Spike.Tests.csproj --configuration Release
python3 -m unittest discover -s tools/Nostos.Alignment.Spike.Tests -p 'test_*.py' -v
```

The executable is `tools/Nostos.Alignment.Spike/bin/Release/net10.0/Nostos.Alignment.Spike.dll`.
All examples below use `$ALIGNMENT_DLL` for that path, `$ALIGNMENT_DATA` for an
evidence directory outside Git, and `$ALIGNMENT_SETTINGS` for
`tools/Nostos.Alignment.Spike/matcher-settings.json`.

## Prepare before transcription

Download the eight sources listed in `sources.json` to `$ALIGNMENT_DATA` under
their listed filenames. Downloads are public-domain sources; retain their
original bytes. Gutenberg can regenerate its EPUBs, so compare the source hashes
with the committed candidate manifest; changed bytes mean a new fixture revision.
Archive.org's metadata API exposes storage mirrors if its download redirect fails.

```sh
python3 tools/Nostos.Alignment.Spike/benchmark.py prepare "$ALIGNMENT_DATA" "$ALIGNMENT_DLL"
python3 tools/Nostos.Alignment.Spike/benchmark.py calibrate "$ALIGNMENT_DATA/calibration" "$ALIGNMENT_DLL" "$ALIGNMENT_SETTINGS"
```

Preparation extracts both whole books through `EpubBookTextExtractor`, and makes
32 mono 16-kHz PCM WAV candidate clips: six narrated positions and two proposed
intros per book, each at 10 and 20 seconds. The provisional endpoints are 90/180
seconds on three tracks and 20 seconds on two tracks. **They are candidates,
not established narrated positions or confirmed narration-only intros.**

Listen to each clip before seeing any model transcript. Move provisional
endpoints if necessary and regenerate both clip durations, hashes, and timings
together. Confirm intros contain no book passage for either duration; pick a
different intro if 20 seconds reaches book text. Never pad audio to make an intro.
Annotate every manifest clip with `reviewedBy`, `reviewedAt`, and `heardExcerpt`.
For intros set `expectedRejection: true` and leave `expectedEnd: null`.
For passages set `expectedRejection: false` and `expectedEnd` to:

```json
{"spineIndex": 1, "resourceHref": "chapter.xhtml", "minTextOffset": 200, "maxTextOffset": 280}
```

Use the actual extracted resource and normalized text offsets of the final spoken
sentence, a range at most 300 characters wide. A range is preferable to pretending
an audio cutoff maps to an exact character. Expected locations belong only to
scoring; the matcher never receives them, headings, or chapter hints.

```sh
python3 tools/Nostos.Alignment.Spike/benchmark.py freeze "$ALIGNMENT_DATA" "$ALIGNMENT_SETTINGS" "$ALIGNMENT_DATA/calibration/calibration.json"
```

The freeze refuses absent listening receipts, changed hashes, bad durations,
missing source locations, or an incorrect fixture count. Retain and date the
freeze receipt before any scored transcription. Do not tune settings on held-out
audio. The synthetic examples establish initial lexical thresholds only; their
success says nothing about audiobook feasibility.

## Transcribe, replay, inspect

`transcribe <adapter.dll> <clip.wav> <transcript.json>` instantiates one concrete,
parameterless `ISTtProvider`, calls once with the English hint and WAV media type,
and stores clip identity, latency and the existing `SttResult`. It has a 90-second
timeout and no retry. Paid operators must invoke their budgeted runner, rather
than call this primitive directly. An interrupted request may still be billed.

For offline replay, supply an index containing provider-neutral tags:

```json
[{"providerTag":"model-1","clipId":"carol-01-10","artifactPath":"/absolute/path/to/transcript.json"}]
```

```sh
python3 tools/Nostos.Alignment.Spike/benchmark.py replay "$ALIGNMENT_DATA" "$ALIGNMENT_DLL" "$ALIGNMENT_SETTINGS" /path/to/transcripts-index.json /path/to/replay
```

Each result contains hashes, transcript, candidate EPUB locators, confidence,
runner-up score, and outcome. Location error is the endpoint's character distance
outside the expected source interval; a different spine resource is incorrect,
with no meaningful character distance. Latency includes the provider round trip.
Correct positions count each of the 12 positive endpoints once, even when both
durations work. Missing clips prevent the continuation gate.

Inspect **every accepted** location against the original audio and EPUB. Record:

```json
[{"providerTag":"model-1","clipId":"carol-01-10","matchSha256":"<hash>","reviewedBy":"<reviewer>","reviewedAt":"<ISO timestamp>","observation":"<what was heard and found>","outcome":"correct"}]
```

Use `incorrect_accepted` when inspection disproves the match. Manual review cannot
erase a mismatch against frozen ground truth. Then:

```sh
python3 tools/Nostos.Alignment.Spike/benchmark.py finalize /path/to/replay/report.json /path/to/accepted-reviews.json
```

Continue only with at least 11/12 correctly located narrated positions at 10 or
20 seconds, all 32 cases scored for that model, zero incorrect accepted matches,
complete manual acceptance, and separately reconciled cost. No output from this
tool certifies Settings or reader browser acceptance.
