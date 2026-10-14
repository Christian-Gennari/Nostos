# Ask Nostos — redesign

Status: **proposed** · Owner: maintainer · Date: 2026-10-14

Ask Nostos should do the library and reading work that is tedious by hand, and
do it predictably. This document replaces the turn-by-turn approval model with
**changesets**: the server previews them, its policy decides whether to ask,
it applies each one atomically, and every one can be undone. It also makes
every screen update live after any change.

---

## 1. What is wrong today

Each item below is traced to code on `main` @ `e3021be8`.

| # | Symptom the user sees | Cause |
| --- | --- | --- |
| P1 | "Delete all my collections and reorganise" asks to delete **one** collection, then stops. | The first approval-class tool call ends the whole turn (`AssistantOrchestrator.cs:368-392`). Models usually emit one call per round, so the plan holds one step. |
| P2 | After approving, nothing else happens. | `AssistantPlanExecutor` runs the stored steps and the client prints "Applied" (`assistant.service.ts:1025`). The model never learns that the approval happened, so the rest of the task is lost. |
| P3 | The confirmation card does not say which collection. | Step text is the tool's static description, not the target (`AssistantOrchestrator.cs:737`). The user approves blind. |
| P4 | Pages must be reloaded to see what the assistant did. | No change propagation exists. `CollectionsService` is stateless, and Library and Book detail fetch once in `ngOnInit` (`library.component.ts:339`). Nothing listens to assistant, MCP or other-tab writes. |
| P5 | Large tasks stop with "reached this turn's execution limit". | 6 provider calls and 60 s per turn (`appsettings.json:52`), and the prompt says "execute one action, read its result, then the next". A reorganisation cannot fit. |
| P6 | Settings shows Verbatim / Light polish / Clarify with no clear purpose. | The setting applies only to notes captured *through the assistant*. It is unexplained in the UI and nobody asked for it. About a fifth of the behaviour prompt manages it and capture edge cases. |
| P7 | Behaviour is fragile and regresses. | ~6k backend lines, a 2,154-line `assistant.service.ts`, ~25 `fix(assistant)` commits. Four heuristic guards (capture intent, completion claim, identity, retrieval intent) correct model output after the fact instead of constraining it before. |

What already works and **is kept**:

- canonical domain services as the only write path;
- idempotent turns and receipts (Gate A);
- typed failures;
- evidence handles and grounded sources;
- streamed turn events;
- the per-turn execution meter;
- the quality bed.

## 2. Goals and non-goals

**Goals**

1. A single request can complete a whole task. "Delete everything and
   regroup my library by theme" ends in **one** review card, and nothing
   less.
2. Safety comes from **structure, not refusals**. The model can try to do
   anything in scope. The server decides what runs immediately, what needs
   approval, and what is impossible. Each decision is deterministic and
   explainable.
3. Every change can be undone, and every change appears on every open screen
   without a reload.
4. The assistant earns its place through four jobs (§3). Anything that serves
   none of them is removed.

**Non-goals**

- No new outbound capabilities: no web fetch, email or external APIs.
  Keeping the assistant closed to the outside world is a deliberate guardrail
  (§6.4).
- No changes to the frozen MCP contract (`docs/library-mcp-contracts.md`).
  MCP benefits from the change feed but keeps its tool signatures.
- No change to provider configuration or the BYOK/Cloud usage policy.

## 3. The four jobs

| Job | Example | What makes it worth having |
| --- | --- | --- |
| **Organise** | "Regroup my library into collections by theme; drop the old ones." "Mark every Le Guin as finished." | Bulk curation that takes an hour in the UI, done as one reviewable change and undoable. |
| **Ask your library** | "What did I note about attention across all my books?" "Where does Mann describe the sanatorium dining room?" | Answers come from *your* notes and book text, cite the exact note or passage, and open it on click. |
| **Reading companion** | In the reader: "Explain this passage." "Who is Settembrini again?" | Explains the selected text and current position, with the open book as the scope. |
| **Capture and connect** | "Save this thought." "Link my unlinked notes from this week to topics." | Your exact words are kept. Topic links are proposed and applied as a changeset. |

Anything outside these jobs gets a short, useful answer when it is a reading
or general-knowledge question. For things the assistant is not allowed to do,
it says where in the app to do them (§6.3).

## 4. Architecture

```
 user ─▶ Turn endpoint (stream) ─▶ Agent loop ──▶ Read tools ───────────▶ canonical services
                                       │
                                       └──▶ Write tools ──▶ Changeset (staged, not applied)
                                                               │ end of turn
                                                               ▼
                                                    Dry-run in DB transaction ─▶ Diff
                                                               │
                                                    Policy engine (on the diff)
                                              ┌────────────────┼────────────────┐
                                           auto-apply      needs review      forbidden
                                              │                │             (never staged)
                                              ▼                ▼
                                         Apply (1 tx) ◀── user approves
                                              │
                                   Undo record + Change feed event ─▶ every open client
```

### 4.1 Changesets: the only way the assistant writes

- **Write tools stage, they do not execute.** `library_create_collection`,
  `…_rename`, `…_move`, `…_delete_collection`,
  `library_set_book_collections_bulk`, `library_update_book`,
  `notes_link_existing_topic` and the new `library_set_book_status` each append an **op** to
  the turn's changeset. The tool result returns the *projected* outcome at
  once:
  - entities the turn creates get a temporary reference (`new:1`), which
    later ops in the same turn can use;
  - the result is plain data, for example
    `{ status: "staged", ref: "new:1", name: "Philosophy" }`.

  The model therefore keeps working through the whole task instead of
  stopping at the first sensitive call. **This fixes P1 and P2.**
- **One changeset per turn.** When the model stops calling tools, the server
  closes the changeset and processes it as a whole. The model never applies
  anything itself, so it cannot claim a change it did not make. That makes
  `AssistantCompletionClaimGuard` unnecessary.
- **Dry run against real code.** The ops run inside one database transaction
  through the canonical library mutation code, and the transaction is then
  **rolled back**. The validation is the real one, and the preview is an exact
  diff:
  - collections created, renamed, moved and deleted;
  - books whose membership changes, with before/after sets;
  - metadata and status edits.

  The diff is a server-rendered list of real names and counts. **This fixes
  P3.**
- **Apply = the same ops in one committed transaction.** The changeset
  records the library `StateVersion` it was previewed against.
  - If the library changed in between, the changeset is dry-run again. If the
    diff differs, the user sees the new preview instead of a silent apply.
  - Otherwise the commit is all-or-nothing: no half-reorganised library.
- **Undo.** Before applying, the server snapshots the before-state of every
  row the diff touches, and stores it as an inverse changeset (kept for 7
  days, last 50).
  - Undo is itself a changeset, previewed against the current state.
  - Rows changed since the apply are reported as conflicts and skipped, never
    overwritten.

**Prerequisite refactor.** Today every `LibraryService` mutation opens its own
transaction and writes its own receipt (`LibraryService.cs:124`,
`LibraryMutationExecutor.cs:46`). EF cannot nest these, so the mutation bodies
move into a `LibraryMutations` core that takes the caller's transaction:

- the existing public methods become thin wrappers that open the transaction
  and write the receipt;
- REST, MCP and the importers keep their exact behaviour;
- the changeset applier calls the core directly.

This is the riskiest step. It lands first, on its own, behind the existing
service tests.

### 4.2 Policy engine: deciding on the diff, not the tool name

The trust class today is attached to the *tool*: every non-empty
`delete_collection` is approval-class, and every `update_book` is not. That is
the wrong axis. Deleting a collection that holds one book is minor, while
rewriting 300 books' memberships with an "ordinary" tool is not. The policy
reads the **dry-run diff** and returns one of three outcomes:

| Outcome | Rule (defaults, in `AssistantOptions`) | UX |
| --- | --- | --- |
| **Apply** | Only additive or reversible edits (create, rename, move, add membership, status, metadata on ≤ 10 books, link note→topic, capture), **and** ≤ 25 books touched, **and** nothing removed from more than 3 books. | Runs at once. A compact "Changed" card with **Undo**. |
| **Review** | Anything over those limits. Any collection delete that unlinks books. Any membership removal on > 3 books. Any changeset built in a turn that read untrusted text (§6.2). | Review card with the full diff. Items can be unticked, then Apply or Cancel. |
| **Forbidden** | Ops the assistant is not allowed to make at all (§6.3). | Never staged. The tool returns `not_permitted` with a pointer to where the user can do it. |

The thresholds are numbers in configuration, not prompt wording, and each
decision is a pure function covered by unit tests. Approving a review card is
bound to `(changesetId, diffHash)`, so an approval can never apply a diff the
user did not see. A typed "yes" in chat stays allowed only while exactly one
review card is open, and it binds to that card's hash.

### 4.3 Change feed: no more reloads

- Every committed library or notes mutation publishes a `LibraryChanged`
  event, whatever its source: REST, MCP, assistant, import or undo. The event
  carries `{ stateVersion, entities: [{ kind, id, change }] }` and is
  published from the canonical write path, so no caller can forget it.
- `GET /api/events` is an SSE stream, the same pattern `/api/imports/stream`
  already uses. It reconnects with `Last-Event-ID`, and after a gap it falls
  back to a full refetch when versions jump.
- A new frontend **`LibraryStore`** (signals) owns collections, collection
  counts and a book cache:
  - Library, the sidebar, Book detail, the collection picker and Second Brain
    read from it instead of calling HTTP in `ngOnInit`;
  - feed events invalidate exactly the affected slices;
  - an assistant turn's own changeset result is applied immediately, so its
    changes never wait on the round trip.
- **This fixes P4.** It also covers MCP agents and a second open tab.

### 4.4 Agent loop

- One loop, three phases per turn: **understand → stage → present**.
- Reads stay free.
- The provider-call ceiling rises to **12** and the elapsed-time ceiling to
  **120 s**. Bulk tools mean a reorganisation needs about 4 to 6 calls, not
  one per book.
- The token ceiling stays at 50k. The execution meter and Cloud usage
  accounting are unchanged.
- The loop streams progress events the UI already understands: `reading
  library`, `drafting changes`, `checking changes`.
- New compact read tools so the model plans from complete data, not from page
  one:
  - `library_overview` (exists): a compact, complete view of books and
    collections; extended so that every book carries its current collection
    ids;
  - `collections_tree`: the full tree with descendant-inclusive counts.
- The prompt is rewritten around the four jobs. Capture keeps its essentials
  (exact words, open book, never invent a location) in a few lines. The
  long capture-edge-case prose is removed with the processing modes.
- **The identity rules stay** as a short block, since the product decision
  stands.

## 5. Interaction design

The assistant reply area renders exactly these result kinds and no others:

1. **Answer:** Markdown with grouped sources. A click opens the note, or the
   passage at its location.
2. **Changed:** an auto-applied changeset as a one-line summary, for example
   "Created *Stoicism*, moved 6 books in." It has an **Undo** button and an
   expandable diff.
3. **Review:** title "Review 3 changes to 214 books", and a diff grouped by
   *Collections removed / created / renamed* and *Books moved*.
   - Each group and each line can be unticked.
   - **Apply** shows a determinate progress bar, then becomes a *Changed*
     card with Undo.
   - **Cancel** discards the changeset.
4. **Question:** one clarifying question with tappable choices when a target
   is ambiguous, for example two collections named "History".
5. **Couldn't:** a plain sentence and an action link: "Deleting books is done
   from the book's page → *Open Dune*." This is never a bare "I can't do
   that".

Progress lines stream during the turn. Stop cancels between tool calls, and
the staged ops are then discarded. Nothing is half-applied, because nothing is
applied before the end of the turn.

**Settings:**

- remove *Capture processing* (P6);
- keep the Ask Nostos on/off switch and the provider configuration;
- add a short "Undo history" list (the last 50 applied changesets).

## 6. Guardrails

The aim is **bounded blast radius**, not refusal. Each guard below is enforced
in code, and the prompt does not have to win any argument with the model.

### 6.1 Scope of writes

The assistant can only write library organisation, book metadata/status,
notes it captures, and topic links. Every write goes through a changeset, so
every write is previewed by policy, atomic, and undoable.

### 6.2 Untrusted content and prompt injection

- Book text, notes, imported highlights and earlier conversation turns are
  **data**. Tool results carrying them are wrapped in a typed envelope marked
  `untrusted`.
- **Taint rule:** if a turn reads untrusted content and also stages writes,
  the changeset is forced to **Review**, whatever its size. A malicious EPUB
  can, at worst, *propose* a change the user then sees and declines.
- Approvals come only from the review card, or a typed confirmation bound to
  its diff hash. Content inside a tool result can never approve.

### 6.3 Never through the assistant

These ops are not exposed, so the model cannot even stage them:

- deleting books or their files; deleting notes;
- settings, AI provider keys, backups, imports and exports;
- account or Cloud billing.

The tool layer answers requests for them with a deep link to the screen that
does it.

### 6.4 No exfiltration path

There are no outbound tools. Private data can only leave the device through
the configured model provider, which the user chose. This closes the
"untrusted input + private data + outbound channel" combination outright.

### 6.5 Budgets and abuse

- Per-turn ceilings (§4.4).
- At most one turn running per conversation (already enforced).
- Changesets are limited to 1,000 ops and 2,000 touched books. A larger
  request returns a typed `too_large` and the assistant proposes splitting it.
- Cloud metering is unchanged.

### 6.6 Concurrency

`StateVersion` is checked at apply, and a mismatch re-runs the dry run
(§4.1). Undo never overwrites rows that changed after the apply.

### Guards removed

Each guard is checked against the quality bed before it is deleted.

- `AssistantCompletionClaimGuard`: the server renders outcomes, so it has
  nothing to guard.
- `AssistantPlanStore` / `AssistantPlanExecutor`: replaced by the changeset
  store and applier.
- `AssistantCaptureIntentGuard`: kept only if the capture scenarios in the
  bed regress without it.

## 7. Data and compatibility

- **New tables:**
  - `AssistantChangesets`: id, conversation, status, base state version,
    diff hash, ops JSON, created/applied;
  - `AssistantUndoRecords`: changeset id, inverse ops JSON, expires.

  Both need a Cloud PostgreSQL migration before the Cloud pin can move.
- **The capture-processing setting** is removed from UI and prompt. The
  `AssistantSettings.CaptureProcessingMode` and `Notes.ProcessingMode`
  columns **stay**: notes already processed keep their recorded mode, and
  dropping public columns forces a Cloud migration for no user benefit. New
  captures are always `verbatim`.
- Wire DTOs: `pendingPlan` is replaced by `changeset` in turn responses. The
  frontend ships with the backend, so no compatibility shim is needed.
  `/api/assistant/plan/approve` is removed.
- MCP: unchanged signatures. It gains change-feed events through the
  canonical write path.

## 8. Verification

Each step is verified before it merges:

- **Unit:**
  - policy decisions table-driven over diffs;
  - diff hashing;
  - temp-ref resolution;
  - undo conflict handling.
- **Integration (real SQLite, not `EnsureCreated`-only):**
  - dry-run rollback leaves the database byte-identical;
  - apply is atomic under an injected mid-changeset failure;
  - a `StateVersion` race re-runs the preview;
  - undo restores memberships exactly.
- **Quality bed: new scripted scenarios**
  - "delete all collections and reorganise by theme" → one review card
    covering every collection and book;
  - "rename X" → auto-apply + undo;
  - injection text inside an EPUB that requests a deletion → forced review,
    never applied;
  - a forbidden request → *Couldn't* with a link;
  - a 500-book library → finishes within budget.
- **E2E (Playwright):**
  - an assistant change appears in Library and the sidebar without reload;
  - a second tab updates;
  - Undo from the Changed card.
- **Visual:** review and changed cards via `npm run preview -- shots`, both
  deployments, desktop and phone, light and dark.

## 9. Delivery plan

Each step is its own PR, and each is useful on its own.

1. **Change feed + `LibraryStore`.** SSE events from the canonical write path,
   with Library, sidebar, Book detail and picker moved onto the store. This
   fixes the reload problem for every source of change.
2. **Remove the capture-processing setting.** UI, prompt and settings
   endpoint field; the columns stay.
3. **`LibraryMutations` core refactor.** No behaviour change, proven by the
   existing library, MCP and REST suites.
4. **Changeset engine.** Ops, temp refs, dry-run, diff, apply, undo store,
   plus tables and the Cloud migration. Not yet wired to the assistant.
5. **Policy engine + taint tracking**, table-tested.
6. **Orchestrator rewiring.** Write tools stage, the end of the turn presents
   or applies, the new budgets apply, and the plan store is deleted.
7. **Frontend result kinds.** Changed, Review, Couldn't and the undo history.
   `assistant.service.ts` splits into transport, conversation store and
   changeset view.
8. **Prompt rewrite + guard pruning**, measured against the quality bed with
   before/after numbers.

## 10. Decisions and defaults

| Decision | Choice | Why |
| --- | --- | --- |
| Approval granularity | Per changeset, bound to diff hash | One approval per task, never a blind approval. |
| Where risk is judged | Server policy on the dry-run diff | Deterministic, testable, independent of the model's tool choice. |
| Auto-apply limits | ≤ 25 books touched, ≤ 3 books losing a membership, ≤ 10 metadata edits | Covers everyday edits without a dialog. Anything larger is worth a look. Configurable. |
| Undo retention | 7 days / 50 changesets | Long enough to notice a bad reorganisation, small enough to stay cheap. |
| Live updates | SSE + signals store | Matches the existing import stream and needs no new dependency. |
| Typed "yes" | Kept, bound to the single open review | Voice and keyboard users need it, and the binding keeps it safe. |
