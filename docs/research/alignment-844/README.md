# Continue in another edition: measurements for #844

Part of [#844](https://github.com/Christian-Gennari/Nostos/issues/844). Run on
2026-10-12 against the product code of the branch that carries this file.

## What was measured

Six pairs of a public-domain text and its LibriVox recording. For each pair,
30 positions spread from 6 % to 94 % of the book in each direction:

- **Audio → text.** The audiobook is paused at a position; is the place found
  in the text?
- **Text → audio.** A page is showing; is the place found in the audiobook, and
  how long after playback starts does the page's first sentence begin?

Every search is cold: no anchors from earlier searches.

| Pair | Lang | Audio → text | Text → audio | Sentence starts after landing: min | median | p90 | max | Probes: mean | max |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| *A Christmas Carol*, Project Gutenberg EPUB | en | 30/30 | 29/30 | 0.8 s | 3.1 s | 4.3 s | 6.2 s | 2.7 | 4 |
| *A Christmas Carol*, 1911 scan (PDF with an OCR text layer), same audio | en | 30/30 | 30/30 | −0.2 s | 2.6 s | 5.2 s | 25.7 s | 2.9 | 5 |
| *Strange Case of Dr Jekyll and Mr Hyde*, Standard Ebooks EPUB | en | 30/30 | 30/30 | 0.3 s | 3.0 s | 4.5 s | 5.0 s | 2.1 | 3 |
| *Die Verwandlung*, Project Gutenberg EPUB | de | 30/30 | 30/30 | 2.1 s | 3.2 s | 4.0 s | 4.2 s | 2.0 | 3 |
| *阿Q正傳*, Project Gutenberg EPUB in Traditional characters | zh | 30/30 | 23/30 | 1.5 s | 3.2 s | 5.0 s | 5.9 s | 3.5 | 5 |
| *坊っちゃん*, Aozora Bunko text with ruby, as an EPUB | ja | 29/30 | 30/30 | 2.0 s | 3.8 s | 5.0 s | 5.9 s | 2.0 | 3 |

The landing is deliberately three seconds early (`Alignment:LeadInSeconds`).

Audio of a **different** book was offered 280 times and accepted 0 times: 160
clips across the English and German pairs, 60 clips of *坊っちゃん* against
Sōseki's *こころ*, and 60 clips of *阿Q正傳* against Lu Xun's *朝花夕拾*. The first
clip of each run is the LibriVox introduction.

Per-position values, every decline and its reason are in
[`results.json`](results.json).

## Reading the table

- **The seven Chinese misses** are six pages nobody narrates (the edition's
  notes and the Project Gutenberg licence) and one chapter-opening page. They
  declined; none landed somewhere else.
- **25.7 s on the scan** is a chapter-opening page. Playback starts at the
  chapter's first second, and the recording spends 25 seconds on the LibriVox
  announcement before the text. The next-largest value on that pair is 5.5 s.
- **−0.2 s on the scan** is one landing that began 0.2 s after the first word
  at the top of a page that opens mid-sentence. It is the only landing after
  its first word in 168 timed landings.
- **Timed** landings are those where an independent transcription of the audio
  after the landing contains the page's opening words, so that the start of
  the sentence can be read off its word timestamps: 168 of the 172 located.
  The other four opened with a chapter heading or with words the reference
  transcription wrote differently.
- **The one English miss** (*A Christmas Carol*, "Are there no prisons?") is
  the last page of a stave, next to a LibriVox section break; it used all five
  probes and declined.
- **The one Japanese miss** audio → text is a clip the transcriber wrote
  entirely in hiragana.

## Before

At `def4c4c9`, the same harness on 12 positions of *A Christmas Carol* (EPUB):
text → audio located 12 of 12, and the sentence began 1.6–10.4 s after
playback started (median 3.3 s). On 30 positions it is now 0.8–6.2 s (median
3.1 s).

The other gaps were each measured on this branch just before the change that
closes them, on 12 positions per direction, so each row isolates one change:

| Pair | Direction | Before that change | After it (12 positions) | Now (30 positions) |
| --- | --- | --- | --- | --- |
| *A Christmas Carol*, 1911 scan | out of the PDF | 8/12 | 11/12 | 30/30 |
| *Jekyll and Hyde*, Standard Ebooks | audio → text | 10/12 | 12/12 | 30/30 |
| *呐喊*, Traditional characters | audio → text | 0/12 | 12/12 | (pair not in the table, see below) |
| *坊っちゃん*, with ruby | text → audio | 6/12 | 11/12 | 30/30 |
| *坊っちゃん* | audio → text (30 positions) | 21/30 | 29/30 | 29/30 |

## Limits of this evidence

- **The transcriber is not the production one.** Clips were transcribed by a
  local Whisper `large-v3-turbo` (faster-whisper, int8, beam 5). The decided
  alignment model, MAI Transcribe, was not called: no key for it was available
  to this run. Timing does not depend on the model. Hit rates do: a different
  model spells Japanese and hears names differently, and this run's model
  invents a closing phrase over silence, which the product now tolerates.
- **Clips came from one concatenated MP3 per book**, cut by ffmpeg directly.
  The product's own cutter (ranges over storage, multi-track planning) was
  exercised separately, through the running app, on the same books.
- **The page excerpt is emulated**: for an EPUB, the text from the next
  sentence start as the reader computes it, without ruby; for the PDF, the
  first 280 characters of a page read by pdf.js, with the reader's look-ahead
  past a page of under 100 characters.
- **One recording, one narrator per pair.** No abridged or translated edition,
  no Thai, Lao, Khmer or Burmese text (those are tokenised per character but
  were not measured), no Japanese text without chapter breaks.
- The *坊っちゃん* EPUB was built for this run from the Aozora Bunko XHTML with
  pandoc, one resource per chapter. A first attempt with the whole novel in one
  460 KB resource never reached the mark: the EPUB reader's landing check
  failed closed ("Couldn't locate this passage"), which is independent of
  language and of this branch.

## How to run it

The harness in [`harness/`](harness/) is evidence tooling. It is not in the
solution and CI does not build it. It uses the product's `PassageMatcher`,
`AudioAlignmentSearch`, `AlignmentService` and text extractors unchanged, and
replaces three things: clips are cut from one local file, transcription is a
local Whisper over a pipe (no network listener), and anchors live in memory.

```sh
python3 -m venv venv && venv/bin/pip install faster-whisper   # plus CUDA libraries for a GPU
dotnet build docs/research/alignment-844/harness -c Release -o out
export ALIGNMENT_PYTHON=$PWD/venv/bin/python

# both directions, 30 positions each
dotnet out/Harness.dll carol.mp3 carol.epub en both 30 carol.json

# a scanned PDF: page excerpts as the reader would send them
npm install pdfjs-dist
node docs/research/alignment-844/harness/pdf-excerpts.mjs scan.pdf 30 excerpts.json
dotnet out/Harness.dll carol.mp3 scan.pdf en both 30 scan.json excerpts.json

# audio of a different book: every answer must be a decline
dotnet out/Harness.dll jekyll.mp3 carol.epub en neg 40 neg.json
```

Sources: Project Gutenberg ebooks 46, 22367 and 25332; Standard Ebooks
*The Strange Case of Dr. Jekyll and Mr. Hyde*; archive.org
`christmascarol00dick` (PDF); Aozora Bunko card 752; LibriVox recordings
`A_Christmas_Carol`, `jekyll_hyde_1111_librivox`, `verwandlung_mw_0811_librivox`,
`truestoryahq_1612_librivox` and `botchan_1310_librivox` (64 kbps MP3). No
audio or book file is committed.

A seventh pair is not in the table: Lu Xun's *呐喊* (Gutenberg 27166) with the
LibriVox recording `call_to_arms_jl_librivox`. Audio → text located 12 of 12,
but that edition prints the stories in a different order from the recording,
so text → audio, which assumes the two run in the same order, found 4 of 12.
An edition whose order differs from the recording is outside what this
feature can do.
