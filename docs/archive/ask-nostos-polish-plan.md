# Ask Nostos — polish plan (#1464)

> **Archived 2026-11-13.** The plan produced by the archived assessment; its
> items were filed as issues and are tracked there, not here.

Status: **proposed** · Base: `main` @ `f200e10c` · Date: 2026-11-11
Evidence: `docs/archive/ask-nostos-assessment.md` and `docs/archive/ask-nostos-assessment/`.

The assessment found no missing capability worth building. The work is to make
the existing one visible, honest and consistent. Every item below names the files
it touches; none adds a new page, panel or onboarding tour.

## 1. How a user finds out what it can do — without adding chrome

The empty state is already the one surface every user sees. Use it, and the
places that already describe Ask Nostos.

### 1.1 Empty state: three doors, one line each

Replace the current title + sentence + three question rows
(`assistant.component.html:78-99`) with the same block, regrouped by job:

```
Ask, capture, or change your library.
[ Ask        What did I note about attention?            → ]
[ Capture    Save this thought: …                        → ]
[ Organise   Move my Le Guin books into a collection     → ]
It can't delete books or notes, or change settings.
```

- **Capture and Organise prefill the composer; they do not send.** A prefilled
  `Save this thought: ` or `Move my Le Guin books into a collection` shows the
  shape of the request without risking a write the user did not type. Ask sends
  its question as today.
- **Per surface:** the third row becomes `Save the passage I selected` in the
  reader when there is a selection (the context already exists,
  `AssistantConversationBuilder.cs:56-66`), `Open my draft about …` in the
  Studio (`writings_read`), and `Suggest topics for this note` in a Brain review
  (`topics_propose_links`).
- **Height:** the block must stay under 180 px in the compact panel, so the
  composer does not move between the empty and non-empty states.
- **The last line is a boundary, not a disclaimer:** it is the only place in-app
  that states what is impossible (`AssistantConversationBuilder.cs:148`).

Files: `Nostos.Frontend/src/app/ui/assistant/assistant.component.html`,
`assistant.component.ts` (`starterPrompts`, a new `capabilityRows`),
`assistant.component.css`, `assistant.component.spec.ts`.

### 1.2 Settings: describe the product, not the reading mode

Replace `How Ask Nostos appears while you read.` and `Show the dock capsule for
capturing thoughts while reading.` (`settings.component.html:668,687`) with:

> **Ask Nostos**
> Ask about your books and notes, capture a thought, or reorganise your library.
> Changes are applied with Undo, or shown for review first.
> [ Show Ask Nostos ]

No new controls; one sentence and a second line. The usage card below keeps its
own copy (`settings.component.ts:881-899`).

Files: `Nostos.Frontend/src/app/settings/settings.component.html`.

### 1.3 The three small claims that drift

- Desktop selection menu: label the action `Ask Nostos` like the phone bar
  (`reader-shell.component.html:492-503` vs `:567-580`).
- Command palette: `Capture a thought` is the only AI action named when the
  query is empty (`command-palette.component.ts:210`); add `Ask Nostos` beside
  it with the current query when one is typed (the row already exists,
  `:689-694`).
- Prompt: the “what can you do” sentence omits Studio drafts, To read and quotes
  (`AssistantConversationBuilder.cs:160`). Make it name the registered jobs, not the
  six tool families it happens to remember.

## 2. The contract per capability

What a user asks, what happens, and what they see. “Applied” means policy
auto-applied with Undo; “Review” means it waits. Both come from
`AssistantChangePolicy.cs:32-65`.

| You ask | What happens | What you see |
| --- | --- | --- |
| “What did I note about attention?” / “What have I noted from this book?” | `knowledge_search`, `notes_search`, `notes_list_for_book` (read) | Answer with `[^n]` markers and numbered source rows; a click opens the note or passage. |
| “Where does Mann describe the dining room?” | `book_text_search` on the indexed edition; reader scope first, widened once when implicit (`AssistantRetrievalTurnState.cs:178-194`) | Answer with page/CFI source rows. If the edition is not indexed: one line saying so (§3, R6). |
| “Save this thought: …” | `notes_capture` (capture, exactly-once) | One short acknowledgement, “Saved note”, topic chips, the note's page/position chip. With no book open: “Which book is this for?” once. |
| “Save the line about the bridges” / select → Ask Nostos | `book_text_find_quote` then `notes_capture` with `quoteRef` (`AssistantCapabilities.BookText.cs:61`, `Actions.cs:101-118`) | The quote is highlighted in the book; “Saved note”. Never a paraphrase. |
| “Move my Le Guin books into a collection.” | 8 staged capabilities (`AssistantCapabilities.Changes.cs:20-236`) → dry-run diff → policy | **Changed**: one-line summary, “Show every change”, **Undo**. Larger diffs: **Review** with reasons, tickable steps, Apply/Discard. |
| “Mark Piranesi finished” / “Change the author” | `library_update_book` (staged) | Same Changed/Review card, with a “Book details” group in the diff. |
| “Add The Left Hand of Darkness” | `library_create_or_match_book` (Act) | “Added …” or a confirmation choice when two books match (`confirmation_required`). |
| “Open my draft about the lighthouse keeper” | `writings_list` → `writings_read` | The draft's Markdown in the answer and a Studio link. |
| “What's on my To read list?” / “Add … to To read” | `reading_list_list` / `reading_list_add` | Next up then Someday; adding the same book returns the existing entry (`duplicate: true`). |
| “What connects my topics?” / “Suggest topics for this note” | `topics_list`, `topics_search`, `topics_propose_links` | Answer, or suggestion chips with a reason and “None of these”. |
| “Delete this book / this note / change my backup settings” | No capability; the prompt says where | One sentence and where: the book's page (Edit → Delete), Brain for a note, Settings for the rest. **No workaround.** |

The contract must be true on every surface: the same request in the reader, the
Library and the Studio differs only in scope and starters, never in what a card
means. The one deliberate difference is the reader's open book
(`AssistantContextPacker`), which is shown in the composer chip.

## 3. Reliability fixes, in order

Order is by damage to trust: saying something untrue about a write, then losing
the user's recovery path, then layout, then polish.

### P0 — R1. One failure, once, with a way forward

Today a failure shows the sentence as the entry, again as `Failed · …`, and the
raw code (screenshots 07/08/25;
`assistant.component.html:105-130,315-321`, `assistant.component.ts:660-672`,
`assistant.service.ts:1642`).

- Render **one** block: the server sentence, a quiet action row, no raw code in
  the main flow. Keep the code behind a `Details` disclosure for support.
- Actions by code: `llm_permission` / `llm_not_configured` → **Open AI
  connections**; `ai_usage_limit_reached` → **Manage allowance** (account URL,
  the existing `cloudAccountManagementUrl`) and the renewal date when the usage
  card has one (`settings.component.html:764-800`); `assistant_no_evidence` →
  **Search my notes instead**; indexing states → **Open the book**; everything
  retryable → **Try again** (R2).
- Never say “Failed” twice; drop the duplicate artifact label for failure
  entries. The test that pins `Failed · <message>`
  (`assistant.component.spec.ts:1509`) changes with the behaviour.
- Put the server failure strings through the app's copy convention (“Could
  not …”, one voice) and extend the design guardrail to the list in
  `AssistantEndpoints.cs` / `AssistantRetrievalTurnState.cs`, which frontend
  scanning never sees.

Files: `assistant.component.html`, `assistant.component.ts`,
`assistant.service.ts`, `assistant.component.spec.ts`,
`Nostos.Frontend/src/app/ui/assistant/assistant.component.css`; lift the account
URL/usage state out of `settings.component.ts` into a shared service so the panel
and Settings agree.

### P0 — R2. Make retry a control, and stop the infinite wait

- A retryable turn already stores the exact `PreparedAssistantTurn`
  (`assistant.service.ts:1452-1460`) but only says “Ready to retry”. Add a
  **Try again** button that calls the same submit path with the same `TurnId`
  (exactly-once is already proven by Gate A). The restored draft stays for
  editing.
- Add a client watchdog: if no `started`/`activity`/terminal event arrives within
  the turn budget plus 15 s, stop waiting and show the retry block. Today a
  black-holed connection leaves the three dots forever
  (`assistant.service.ts:1391-1396` is only a 350 ms indicator delay).

Files: `assistant.component.html`, `assistant.component.ts`,
`assistant.service.ts`, `assistant.component.spec.ts`.

### P0 — R3. A tall review must arrive with its title and Apply in view

When a changeset is present, scroll to the **start of the card**, not the end of
the transcript (`assistant.component.ts:898-906`). At 20 steps the current
behaviour puts “Review before applying” at y=33 while the transcript starts at
y=234 (screenshot 06b, `measurements.json`). Do the same when “Show every change”
expands a card: keep the card header pinned in view.

Files: `assistant.component.ts`, `assistant.component.html`,
`Nostos.Frontend/e2e/` (new spec, §6).

### P0 — R4. Say what committed before the turn failed

A capture can commit and a later provider call fail; the endpoint returns a
failure with no body (`AssistantEndpoints.cs:195-201`), so the UI replaces the
turn's artifacts with the failure and never says the note was saved
(`assistant.service.ts:1540`).

- On a provider failure, return the turn's already-committed outcomes
  (`capturedNoteId`, executed capabilities, applied changeset) in the failure
  response, with a server sentence such as “The note was saved. The answer was
  interrupted.”
- The UI renders those artifacts before the failure block; **Try again** keeps
  the same `TurnId`, so the capture cannot duplicate.

Files: `Nostos.Product/Integrations/Assistant/AssistantOrchestrator.cs`,
`Nostos.Product/Endpoints/AssistantEndpoints.cs`,
`Nostos.Backend.Tests/Assistant/GateA/`, `assistant.service.ts`,
`assistant.component.html`, `assistant.component.spec.ts`.

### P1 — R5. Align “too long” with the turn budget

The 90 s turn budget cancels the provider call before the 95 s request timeout,
so “Ask Nostos took too long to answer. Try again.” is unreachable at defaults
(`AssistantOptions.cs:83,89`, `AssistantOrchestrator.cs:316-348`). Pick one:

- make the request timeout shorter than the turn budget (e.g. 60 s), so a slow
  provider is a provider timeout with its own message; or
- keep both and give the budget stop a distinct “This is taking longer than
  usual” state with Stop, not “execution limit”.

Either way, test the interaction of the two clocks.

Files: `Nostos.Product/Configuration/AssistantOptions.cs`,
`Nostos.Product/Integrations/Assistant/AssistantOrchestrator.cs`,
`AssistantEndpoints.cs`, tests.

### P1 — R6. Make every dead end lead somewhere

- Changeset expired after a restart: the card says “Ask again to prepare them
  afresh.” (`AssistantChangesetCoordinator.cs:243-244`) but has no control. Add
  a **Prepare again** action that prefills the composer with the original request
  (the conversation has it).
- Indexing pending/failed/unsupported: add **Open the book** so the user can see
  what is indexing, instead of a dead sentence
  (`AssistantRetrievalTurnState.cs:310-332`).
- “No evidence”: say which scope was searched (“in this book” / “in your
  library”) and offer the wider search before failing. “Nostos material” is
  internal jargon (`AssistantRetrievalTurnState.cs:169`).

Files: `assistant-changeset-card.component.html/.ts`, `assistant.component.html`,
`AssistantRetrievalTurnState.cs`, `AssistantChangesetCoordinator.cs`, tests.

### P1 — R7. Remove the duplicate question

The anchor prompt is both pushed as an assistant line and rendered as the
“One detail” block (`assistant.service.ts:1616`, screenshot 11). Keep the block
only, give it a field or focus the composer with the question as placeholder,
and drop the duplicate transcript entry.

Files: `assistant.service.ts`, `assistant.component.html`, `assistant.component.ts`.

## 4. UI polish pass

The goal is one surface that belongs to Nostos: the dialog radius, the editorial
type, and the same quiet density as Settings and the Studio.

### U1. Put the panel back on the token graph

Six radii today: 14 (panel), 12 (starter, literal), 16 (composer, literal),
8 (changeset card), 6 (details block), and the 4 px bubble tail (16/16/4/16). The graph has
`--radius-dialog: 14`, `--radius-surface: 10`, `--radius-action: 8`,
`--radius-field: 8` (`styles.css:435-523`).

- Panel: `--radius-dialog` (unchanged).
- Starter rows: `--radius-surface`.
- Composer: `--radius-field`.
- Changeset card: `--radius-action`; details block: `--radius-surface` minus
  the inner step, matching the settings cards.
- Keep the chat bubble's tail (16/16/4/16); it is the one intentional exception.

Files: `assistant.component.css`, `changeset-card/assistant-changeset-card.component.css`.

### U2. Give the expanded workspace a layout

The expanded empty state is a 46-character block in the top-left of a 1000 px
white card (screenshot 03). Centre the empty state in the available height and
use the width for the three capability doors in two columns; no new navigation.
When a conversation exists, keep the current 46-character reading measure.

Files: `assistant.component.css`, `assistant.component.html`.

### U3. Make the changeset card the best-looking thing in the app

It already is close. Align the headline with the panel type scale (1rem), keep
the one-line summary and “Show every change”, and give the review reasons a
heading (“Why this is a review”) instead of a bare list. Do not add colour; the
review's left accent edge is enough.

Files: `changeset-card/assistant-changeset-card.component.html/.css`.

### U4. Fix the citation typography

- Keep the marker with its word: render `&#8239;` (narrow no-break space) before
  the `<sup>` so it cannot wrap alone onto the next line (screenshot 22;
  `assistant-markdown.pipe.ts:46-57`).
- On a grouped source row, show the numbers it covers (“1–2 · The Magic Mountain
  · 2 passages”) instead of no number (`assistant.component.html:135-180`,
  screenshot 04).

Files: `assistant-markdown.pipe.ts`, `assistant.component.html`,
`assistant.component.spec.ts`.

### U5. Entry points

- Selection menu: label `Ask Nostos` on desktop; keep the sparkle for the phone
  bar where it already has a label (`reader-shell.component.html:492-503`).
- Reader capsule on phone: keep the icon-only square (the pager strip has no
  room); it already has `aria-label` and a touch-sized target
  (`assistant.component.css:1084-1096`).
- Dock: unchanged; it is the clearest entry.

Files: `reader-shell.component.html`, `reader-shell.component.css`.

### U6. Settings

The copy in §1.2, and one compact list under the switch:

```
What it can do: search your notes and book text · capture thoughts and quotes ·
reorganise collections · open Studio drafts
```

No new card, no tour.

Files: `settings.component.html`, `settings.component.css`.

## 5. How we check it stays good

Extend the quality gate; do not replace it. The catalogue lives in
`Nostos.Backend.Tests/Assistant/QualityBed/` and the e2e in
`Nostos.Frontend/e2e/`.

| Scenario | Kind | Asserts |
| --- | --- | --- |
| **C34 capability disclosure** | live + deterministic | “What can you do?” names search, capture, organise (with Undo/Review), Studio drafts and To read; states it cannot delete books/notes or change settings; never claims web/email. Deterministic half: a builder test that the prompt's capability sentence covers every `AssistantCapabilityCategory`. |
| **C35 committed-then-failed** | deterministic + Gate A | A provider failure after a successful capture returns the capture in the failure body, the reply says it was saved, and a retry with the same TurnId creates no second note. |
| **C36 tall review** | Playwright | A 20-step review arrives with `Review before applying` and `Apply changes` inside the transcript viewport; expanding “Show every change” keeps them there. |
| **C37 empty-state doors** | component + live | Each door prefills the composer (never sends); the library/reader/Studio rows differ; the live lane scores the answer each row produces. |
| **C38 one failure** | component | A failed turn renders exactly one copy of the message and no raw code; a retryable one renders a Try again control. |
| **C15 extension** | deterministic | The existing typed-failure scenario also pins the user-facing sentence and the action code for allowance, rate limit, timeout, indexing and no-evidence. |

Implemented in #1563: C34, C35 and C37 run in `Nostos.Backend.Tests/Assistant/QualityBed/`
(`QualityCatalogueI.cs`), C36 in `Nostos.Frontend/e2e/assistant-tall-review.spec.ts`
and the review leg of `assistant-panel-states.spec.ts`, C38 in
`assistant.component.spec.ts`, and the C15 sentences in `QualityCatalogueC.cs`
plus `AssistantEndpointTests`. The scenario notes live in §13 of
`docs/ask-nostos-v3-quality-gate.md`.

Also add the **live consistency run** (assessment §6) to the manual
`quality-bed-live.yml`, with the $2 cap from #1466; this is where “same request
several times” is measured.

## 6. Ready-to-file sub-issues

Titles are Conventional Commits. Labels are from
`gh label list -R Christian-Gennari/Nostos`. Filed on 2026-11-11 as #1556–#1563
(sub-issues of #1464), in the order below.

### 1. `fix(assistant): show each failure once and offer the next step` — #1556

Today a failure is rendered as the entry, `Failed · <sentence>` and the raw
code, with no link to Settings, the book or the allowance.
Render one sentence, one action (Try again / Open AI connections / Manage
allowance / Open the book), and keep the code behind a Details disclosure.
Cover allowance renewal and refill with the existing account link.
Labels: `bug`, `frontend`, `priority: high`, `ai: mixed` · Priority: **P0**
Files: `assistant.component.html`, `assistant.component.ts`, `assistant.service.ts`, `assistant.component.spec.ts`.

### 2. `fix(assistant): keep a tall review card's title and Apply in view` — #1557

A review taller than the panel scrolls to its end on arrival, hiding “Review
before applying”, the headline and the first steps (20-step case: label at y=33,
viewport at y=234).
Scroll to the card's start when a changeset arrives, and keep the header in view
when “Show every change” expands it.
Labels: `bug`, `frontend`, `priority: high` · Priority: **P0**
Files: `assistant.component.ts`, `assistant.component.html`, new e2e spec in `Nostos.Frontend/e2e/`.

### 3. `fix(assistant): report writes that completed before a turn failed` — #1558

A capture can commit and a later provider call fail; the failure body is empty,
so the panel never says the note was saved (and a retry is only safe because the
TurnId is reused).
Return the turn's committed outcomes in the failure response and render them
above the failure; add a Gate A test for the no-duplicate retry.
Labels: `bug`, `backend`, `frontend`, `priority: high`, `ai: mixed` · Priority: **P0**
Files: `AssistantOrchestrator.cs`, `AssistantEndpoints.cs`, `Nostos.Backend.Tests/Assistant/GateA/`, `assistant.service.ts`, `assistant.component.html`.

### 4. `feat(assistant): say what Ask Nostos can do where it is used` — #1559

The empty state names asking and capturing, the Settings card names reading and
capturing, and the model prompt omits Studio drafts, To read and quotes.
Regroup the empty state into Ask / Capture / Organise doors (prefill, never
send), fix the Settings copy, label the desktop selection action, and make the
prompt's capability sentence complete.
Labels: `enhancement`, `frontend`, `priority: high`, `ai: mixed` · Priority: **P1**
Files: `assistant.component.html/.ts/.css`, `settings.component.html`, `reader-shell.component.html`, `AssistantConversationBuilder.cs`, `README.md`.

### 5. `fix(assistant): give retryable turns a retry control and a watchdog` — #1560

“Ready to retry” has no button; a stalled connection leaves the three dots
forever.
Add a Try again button that replays the stored turn with the same TurnId, and a
client watchdog that turns a silent stream into the retry block.
Labels: `bug`, `frontend`, `priority: high` · Priority: **P1**
Files: `assistant.component.html`, `assistant.component.ts`, `assistant.service.ts`, `assistant.component.spec.ts`.

### 6. `fix(assistant): align the slow-turn budget with the provider timeout` — #1561

The 90 s turn budget fires first, so the 95 s “took too long” message is
unreachable; a slow turn reads as “execution limit”.
Make the request timeout shorter than the turn budget or give the budget stop
its own “taking longer” state with Stop; test both clocks together.
Labels: `bug`, `backend`, `priority: medium` · Priority: **P1**
Files: `AssistantOptions.cs`, `AssistantOrchestrator.cs`, `AssistantEndpoints.cs`, tests.

### 7. `fix(assistant): polish the conversation surface to match Nostos` — #1562

Six radii (two off-token), an expanded empty state pinned to the top-left of a
1000 px card, citation markers that wrap alone, and grouped sources with no
numbers.
Move the panel onto `--radius-*`, centre and lay out the expanded empty state,
keep the marker with its word, number grouped sources.
Labels: `enhancement`, `frontend`, `priority: medium` · Priority: **P2**
Files: `assistant.component.css/.html`, `assistant-markdown.pipe.ts`, `changeset-card/assistant-changeset-card.component.css`, `assistant.component.spec.ts`.

### 8. `test(assistant): cover the panel's failure, retry and review states` — #1563

The e2e suite covers the capsule and quote highlighting but not the panel's
empty, answer, changeset, error or retry states; the failure duplication is
currently pinned by a unit test.
Add the C34–C38 scenarios, a Playwright spec for the tall review and the error
retry, and extend C15 with the user-facing copy.
Labels: `type: testing`, `frontend`, `backend`, `priority: high` · Priority: **P1**
Files: `Nostos.Backend.Tests/Assistant/QualityBed/`, `Nostos.Frontend/e2e/`, `assistant.component.spec.ts`.

## 7. Pending: needs a live model

The assessment's §6 protocol is the source of truth: six requests, 5 reps each,
10 for the changeset and disclosure requests, recording tool order, changeset
status/diffHash, citations, reply shape, tokens and latency. The only parts of this plan that cannot be
verified without one are the **“use it for real” walk of answers**, **C34's live
lane** and the **consistency run**; everything else has a deterministic or UI
test.
