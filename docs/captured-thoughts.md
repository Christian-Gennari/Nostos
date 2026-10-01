# Saved thought wording

The setting in Settings → Ask Nostos controls new thoughts saved through Ask
Nostos, whether typed or transcribed from speech. It is a server-wide preference
in a self-hosted instance. Hosted instances retain their existing tenant scope.

- **Verbatim** saves exact wording without a thought-processing provider call.
- **Light polish** asks AI to tidy grammar and filler, preserving meaning and voice.
- **Clarify** asks AI to reorganize and rephrase the thought without adding ideas.

AI output remains model-dependent; these are instructions, not a guarantee of
semantic equivalence. The original text is preserved before processing. Quotes
in `SelectedText` are never passed to the thought processor.

Manual Book Detail notes, reader quick notes, highlights, imports and ordinary
edits do not consult this preference. Changing it never rewrites existing notes.
Concept extraction is a separate existing operation.

The stored default is the only Ask Nostos capture-mode control. There is no
per-capture override, including natural-language instructions such as “save this
verbatim”. The legacy `AssistantTurnRequest.ProcessingMode` field remains an
ignored wire-compatibility field; tool arguments cannot override the preference.

Brain's Notes inspector and optional Review inspector expose the effective saved
mode and offer **View original** and **Restore original** when an original is
available. Original text comes from the canonical `/api/notes/{id}/raw` endpoint,
so this works after reload, without conversation history. Restore replaces the
current note text, sets the mode to verbatim, retains the original, and rebuilds
concept links without changing the quote or source anchor.

When a capture's provider fails or returns empty output, the original words are
saved with effective mode `verbatim`. The deterministic capture confirmation
reports this fallback, including captures resumed after a required-input prompt.
Reprocessing remains an API operation; no new reprocessing control is exposed.
That API always uses the original transcript once one exists, never chained AI
output. A provider error during explicit reprocessing leaves the saved note
unchanged and returns a typed failure.
