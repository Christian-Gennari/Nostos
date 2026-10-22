# Saved thought wording

Thoughts saved through Ask Nostos, typed or transcribed from speech, are kept
exactly as the user gave them, **unless the user asks for a rewrite**. There is
no setting for this. Only the user's words in that message can switch it on.

## Word for word by default

`content` is the user's message with only the instructions addressed to Nostos
removed ("can you add a note on…", "post it as a note for me"). Quotes in
`SelectedText` are always stored exactly and never reach the rewriter.

## A rewrite when asked

When the user asks for one ("rephrase this", "clean it up", "make it more
coherent", "formulera om"), the model passes `rewrite: true` on
`notes_capture`. The flag counts only when the user's own message asks for a
rewrite (`AssistantRewriteIntent.Requested`). A flag the model sets on its own
is ignored, and a thought that only mentions rewriting ("Mann seems to rewrite
Faust here") is not a request. A rewrite uses the Clarify processor: it
tightens the wording and adds no ideas. The exact words are kept in
`RawContent` and the stored mode is `clarify`. The acknowledgement says the
wording was tidied and the original kept.

The same works after the fact. "Clean that up" calls `notes_rewrite_capture`,
which rewrites a note from its original words. It only touches a note captured
in the same Ask Nostos conversation (proved by the capture's receipt), and only
when that message asks for a rewrite.

## Topics with a rewrite

A rewrite also carries `topics`: a few names the thought is plainly about,
preferring topics that already exist. They are appended as `[[Topic]]`
references after the rewrite, so the rewriter never sees or drops them. Without
a rewrite, topics are kept only when the user mentions topics or tags. A plain
capture waits in Unsorted.

## Where the note sits

A thought about the book as a whole (a character, a theme) is captured with
`wholeBook: true`. It is saved without a page or timestamp, and nobody is asked
for one. When an external audiobook's timestamp is asked for and the answer has
no number in it ("I don't have one"), the note is saved with no position, not
at 0:00.

## Notes processed earlier

Brain's Notes inspector and optional Review inspector show the effective saved
mode and offer **View original** and **Restore original** when an original is
available. Original wording stays hidden until **View original** opens a modal;
**Restore original** is available inside that modal. Original text comes from
the canonical `/api/notes/{id}/raw` endpoint, so this works after reload,
without conversation history. Restore replaces the current note text, sets the
mode to verbatim, keeps the original, and rebuilds topic links without changing
the quote or source anchor.

Reprocessing is also an API operation (`POST /api/notes/{id}/reprocess`). It
always uses the original transcript once one exists, never chained AI output.
A provider error during explicit reprocessing leaves the saved note unchanged
and returns a typed failure.

The legacy `AssistantTurnRequest.ProcessingMode` field and the
`AssistantSettings.CaptureProcessingMode` column remain for wire and archive
compatibility. Nothing reads them at capture time.

## Browser verification — 2026-10-22

Verified this branch in an isolated SelfHosted instance at
`http://127.0.0.1:36325`, with the production Angular bundle, a fresh migrated
SQLite database, synthetic physical/audiobook records, and the live configured
AI gateway (`cx/gpt-6-luna`). No production data was changed.

- A plain capture kept its wording. A whole-book audiobook thought with an
  inline rephrase request saved immediately without asking for a timestamp;
  the instruction text was absent from both the rewritten note and its original.
- A follow-up "Clean that up" rewrote the same note ID without creating another
  note, retained the original wording, and appended Peeperkorn/Dionysian topics.
  The chat showed "Changed · note rewritten".
- A separate moment-specific audiobook thought asked for a timestamp. Answering
  "i dont have one" saved it with `sourceAnchorKind: unknown` and a null value.
- Re-entry and reload retained the saved text and topics. "View original" opened
  the exact captured wording after re-entry.
- After restarting the backend with the final source build, a capture followed
  by cleanup on the Brain note page refreshed the visible text and original
  control without a manual refresh. "Don't rewrite this again" left the durable
  notes unchanged.
- Streamed turn IDs and UTC times (14:47:46–14:48:21) were correlated with
  provider requests and the database's note/topic writes in the isolated backend
  log. The browser reported no console errors, page errors or failed requests in
  these completed journeys.

Production-build screenshots under `Nostos.Frontend/e2e/visual-evidence/`:
`rewrite-followup-{1280,390}-{light,dark}.png` and
`rewrite-original-desktop.png`, inspected at their original size. The behavior
is shared between deployments; hosted Cloud authentication, PostgreSQL and the
managed AI path were not exercised and remain unverified by this local run.
