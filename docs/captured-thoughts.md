# Saved thought wording

Thoughts saved through Ask Nostos, typed or transcribed from speech, are kept
exactly as the user gave them. There is no setting for this and no
per-capture override: neither a natural-language instruction nor the model's
tool call can ask for a rewrite. The legacy `AssistantTurnRequest.ProcessingMode`
field remains an ignored wire-compatibility field. Quotes in `SelectedText` are
always stored exactly.

Earlier versions offered a *Light polish* and a *Clarify* setting that asked AI
to rephrase captured thoughts. That setting and its `/api/settings/assistant`
endpoint are gone. The stored `AssistantSettings.CaptureProcessingMode` column
is kept so portable archives and hosted databases stay compatible; nothing
reads it at capture time.

Notes processed under those modes keep what was stored. Brain's Notes inspector
and optional Review inspector show the effective saved mode and offer **View
original** and **Restore original** when an original is available. Original
wording stays hidden until **View original** opens a modal; **Restore
original** is available inside that modal. Original text comes from the
canonical `/api/notes/{id}/raw` endpoint, so this works after reload, without
conversation history. Restore replaces the current note text, sets the mode to
verbatim, retains the original, and rebuilds topic links without changing the
quote or source anchor.

Reprocessing remains an API operation (`POST /api/notes/{id}/reprocess`); no
reprocessing control is exposed. That API always uses the original transcript
once one exists, never chained AI output. A provider error during explicit
reprocessing leaves the saved note unchanged and returns a typed failure.
