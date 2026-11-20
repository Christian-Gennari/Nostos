# Ask Nostos: live-model walk and consistency run (#1584)

Run on 2026-11-20 against `main` @ `fc753ef1`. Model `openai/gpt-6-luna` through the managed gateway
(`https://ai-gateway.vercel.sh/v1`), the same model and gateway the quality bed uses
(`docs/ask-nostos-v3-quality-gate.md` §10). It runs the protocol that
`docs/archive/ask-nostos-assessment.md` §6 left pending.

**Spend: $0.80 of the $3 budget** (list price $0.10 / $0.50 per million tokens, provider-reported
tokens; [`spend.json`](ask-nostos-live-run-2026-11/spend.json)). Nothing in this change runs on its own: the
harness does nothing without `NOSTOS_QG_CONSISTENCY=1` and the workflow is `workflow_dispatch` only.

## Result in one paragraph

The surface the assessment criticised has been fixed and the live model drives it well: every entry point
opens the right context, answers are grounded and cited, a failed turn reads cleanly with a Try again button,
and the server-owned parts (changeset status, `diffHash`, review policy, undo of a simple change) were
identical in every rep. What varies is everything the model decides, and the live run found ten real problems,
filed as separate issues below. The two that matter most: **Undo cannot restore a collection that a regroup
deleted and recreated** (#1739; the library ended with no collections), and **the same regroup request produces a
different plan almost every time** (#1740).

## Filed issues

| # | Title | Severity |
| --- | --- | --- |
| [#1739](https://github.com/Christian-Gennari/Nostos/issues/1739) | undo cannot restore collections that a regroup deleted and recreated | high |
| [#1740](https://github.com/Christian-Gennari/Nostos/issues/1740) | the same regroup request gives a different plan almost every time | medium |
| [#1741](https://github.com/Christian-Gennari/Nostos/issues/1741) | Studio drafts written by Ask Nostos end with dangling `[^n]` markers | medium |
| [#1742](https://github.com/Christian-Gennari/Nostos/issues/1742) | a read-only request with text selected makes Ask Nostos save a note | medium |
| [#1743](https://github.com/Christian-Gennari/Nostos/issues/1743) | answers built from `topics_get` carry no citations | medium |
| [#1744](https://github.com/Christian-Gennari/Nostos/issues/1744) | "what did I note about X" misses notes filed under topic X | medium |
| [#1746](https://github.com/Christian-Gennari/Nostos/issues/1746) | a reply says changes are staged for review when no review exists (seen once) | medium |
| [#1745](https://github.com/Christian-Gennari/Nostos/issues/1745) | a request that asks and changes in one message loses its answer and is forced to review | low |
| [#1747](https://github.com/Christian-Gennari/Nostos/issues/1747) | "What can you do?" never says what Ask Nostos cannot do | low |
| [#1748](https://github.com/Christian-Gennari/Nostos/issues/1748) | panel copy contradicts the card state in six small places | low |

## How it was run

**Credentials.** The Actions secrets `NOSTOS_QG_BASE_URL` / `NOSTOS_QG_KEY` exist on the repository, and the
managed gateway key (`NOSTOS_CLOUD_AI_GATEWAY_API_KEY`) is also available locally in the Hermes secrets file, so
both parts ran from this machine. The key lived only in one process environment variable, never in a file in
this repository (checked: the committed files do not contain it).

**Consistency run** ([`QualityConsistencyRun.cs`](../Nostos.Backend.Tests/Assistant/QualityBed/QualityConsistencyRun.cs)).
It uses the quality bed's host, seeded library, budget cap (`QualityBudget`) and metering. Each rep is a fresh
conversation on a freshly seeded library, through the real streamed turn route, with the product's 12 tool
rounds per turn (the deterministic bed pins 6; see "Caveats"). Per rep it records the ordered tool calls, the
changeset (status, `diffHash`, steps, review reasons), citations (markers, source rows, evidence handles), the
reply text and its shape, provider tokens, wall-clock latency and the tool results the model read. An applied
changeset is undone through the card's route; a review is applied and then undone and the library compared with
what it was.

- Six requests, 5 reps each and 10 for the changeset, review and disclosure requests (the issue's numbers),
  plus 3 repeats of each request in one conversation.
- Repros of what the walk found: a selection with a read-only request (W1, W1b), a topic synthesis in Brain (W3),
  a change followed by a regroup in one conversation (W2).
- The assessment names Mann and Le Guin; the bed's library is synthetic, so the six requests keep their shape and
  use the bed's titles. The walk used a small library of real titles and author names instead.

**UI walk.** A throwaway SelfHosted backend ([`ask-live-walk.mjs`](../Nostos.Frontend/e2e/support/ask-live-walk.mjs),
temp database, product defaults) seeded with eight books (an EPUB with real text, a PDF, six paper books), six
notes with topics and two collections. The backend talked to a local metering proxy that held the key, injected
the same `reasoning_effort: none` as the bed, logged every call
([`walk-model-calls.jsonl`](ask-nostos-live-run-2026-11/walk-model-calls.jsonl)) and would have refused past $1.20. The Ridealong browser drove the
production bundle at 1440 x 900 (and 390 x 844 for the phone checks), dark theme, my own tab, closed afterwards.

Re-run: `gh workflow run ask-consistency-live.yml` (cap defaults to $1), or locally with the variables listed at
the top of `QualityConsistencyRun.cs`; `NOSTOS_QG_CONSISTENCY_ONLY=W3-topics-synthesis` reruns one request for cents.

## The walk: every entry point

| Entry point | What I did | Result |
| --- | --- | --- |
| Library dock and panel | Ctrl+J, starters, three questions | Starters now group as Ask / Capture / Organise with a capability line ("It can't delete books or notes, or change settings"). "Which topics come up most in my notes?" answered correctly (Attention 3, Solitude 2, Time 2). [w02](ask-nostos-live-run-2026-11/screenshots/w02-library-panel.png), [w03](ask-nostos-live-run-2026-11/screenshots/w03-library-topics-answer.png) |
| Notes question | `What did I note about attention?` | All three notes tagged Attention, quoted, with numbered markers and source chips. [w04](ask-nostos-live-run-2026-11/screenshots/w04-library-notes-answer.png) |
| Book-text question | `Where in Dracula does Harker say the train was late?` | Correct passage and quote, one marker, "1-5 Dracula · 5 passages" chip that opens the reader at the passage. [w05](ask-nostos-live-run-2026-11/screenshots/w05-library-booktext.png), [w07](ask-nostos-live-run-2026-11/screenshots/w07-reader-from-source.png) |
| Reader, open book | Question with the reading-position chip | Grounded answer; the panel keeps the conversation across pages. The panel covers the right column of text. |
| Reader selection menu | Selected text, "Ask Nostos" (now labelled on desktop) | Opens the panel; the selection reaches the model. The Highlight button wraps to a second row of the menu (see #1722). [w09](ask-nostos-live-run-2026-11/screenshots/w09-selection-menu.png) |
| Selection + read-only request | `Repeat back the exact words I have selected right now.` | **Saved a note instead of answering** (#1742). [w12](ask-nostos-live-run-2026-11/screenshots/w12-selection-repeat.png) |
| Highlight card | Sparkle button on an open highlight, `Quote back the exact highlighted phrase I have open. Do not save anything.` | Correct. The card's Ask is an unlabelled sparkle. [w35](ask-nostos-live-run-2026-11/screenshots/w35-highlight-card.png), [w37](ask-nostos-live-run-2026-11/screenshots/w37-highlight-ask2.png) |
| Quote save | `Save the quote where Harker says he found his smattering of German very useful.` | Exact quote saved (checked through the API); the card does not show it (#1748). [w13](ask-nostos-live-run-2026-11/screenshots/w13-save-quote.png) |
| Capture with no book | `Save this thought: …` then `Ways of Seeing` | Anchor prompt, then saved to the right book. The question shows twice (#1748). [w14](ask-nostos-live-run-2026-11/screenshots/w14-anchor-prompt.png) |
| Auto-applied change + Undo | `Move my Le Guin books into a new collection called Le Guin.` | Changed card, collection appears in the sidebar, Undo removes it. Reply says "staged" (#1748). [w16](ask-nostos-live-run-2026-11/screenshots/w16-changed-card.png), [w17](ask-nostos-live-run-2026-11/screenshots/w17-undone.png) |
| Review card | `Regroup everything into Fiction and Essays.` | Review card with the plan, Discard / Apply. Apply works; **Undo leaves no collections** (#1739). [w19](ask-nostos-live-run-2026-11/screenshots/w19-regroup-fresh.png), [w20](ask-nostos-live-run-2026-11/screenshots/w20-review-applied.png), [w21](ask-nostos-live-run-2026-11/screenshots/w21-after-undo.png) |
| Review claimed, no card | Same request in a long conversation | Reply says "staged for review", no card (#1746). [w18](ask-nostos-live-run-2026-11/screenshots/w18-review-card.png) |
| Brain | New conversation, "What connects my topics?" | Brain-specific starters; the synthesis has no citations (#1743). [w23](ask-nostos-live-run-2026-11/screenshots/w23-brain-starters.png), [w24](ask-nostos-live-run-2026-11/screenshots/w24-brain-answer.png) |
| Studio | `Start a Studio draft titled "On attention" …` | Draft created; body ends with dangling `[^1][^2][^3][^4]` (#1741). [w25](ask-nostos-live-run-2026-11/screenshots/w25-studio-draft.png), [w27](ask-nostos-live-run-2026-11/screenshots/w27-studio-draft-view.png) |
| Book detail | `What have I noted about this book, and rate it 5 stars.` | Answer dropped, forced review (#1745). [w29](ask-nostos-live-run-2026-11/screenshots/w29-bookdetail-answer.png) |
| Command palette | `attention`, "Ask Nostos: attention" row | Opens the panel with the query in the composer, not sent. Notes, topic and the new draft also appear. [w30](ask-nostos-live-run-2026-11/screenshots/w30-palette.png) |
| Settings → Assistant | Opened the section | One switch and a sentence that names read, capture, organise and Undo/review. [w33](ask-nostos-live-run-2026-11/screenshots/w33-settings.png) |
| Reader capsule | Reader with chrome shown | "Ask Nostos Ctrl J", labelled. [w41](ask-nostos-live-run-2026-11/screenshots/w41-reader-chrome.png) |
| Phone (390 x 844) | Panel, To read add, failed turn | Full-height panel, no horizontal scroll. `Add Piranesi to my To read list.` worked. With the gateway down: "The AI service could not complete this turn. Try again.", a Try again button and Details. [w39](ask-nostos-live-run-2026-11/screenshots/w39-phone-toread.png), [w40](ask-nostos-live-run-2026-11/screenshots/w40-phone-error.png) |

Compared with the stub-era assessment (§1 of the archived doc): the capability story is in the panel and in
Settings, a retryable failure has a retry control and shows once, the capsule and desktop selection entry are
labelled. The walk used the dark theme only; the phone selection bar was not exercised.

Walk spend: 51 model calls, 512,961 prompt and 3,236 completion tokens, $0.053.

## The consistency run

Two full runs (A and B) with the product's 12 rounds, 63 and 78 records; B also applies and undoes and adds the repros. Same
numbers in both unless noted. Raw: [`consistency-raw.jsonl`](ask-nostos-live-run-2026-11/consistency-raw.jsonl) (B),
[`consistency-raw-first-run.jsonl`](ask-nostos-live-run-2026-11/consistency-raw-first-run.jsonl) (A),
[`consistency-raw-extra-repros.jsonl`](ask-nostos-live-run-2026-11/consistency-raw-extra-repros.jsonl) (W1b, W3).

| Request | Reps | Tool path | Changeset | Citations | Reply shape | Latency (median) | Tokens / turn |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R1 `What did I note about attention?` | 5 | `knowledge_search`, 5/5 | none | 1 marker, 0 source rows, 5/5 | prose, 28–31 words | 4.6 s | 17.6k (2 calls) |
| R2 `Where does The Salt Meridian describe coiling a rope?` | 5 | `library_resolve_book > book_text_search` 4/5, `knowledge_search` 1/5 | none | 1 marker; 2 source rows (6 on the other path) | prose, 22–39 words | 5.6 s | 24.7k (2–3 calls) |
| R3 `Save this thought: …` (no book) | 5 | `notes_capture`, 5/5 | none; anchor prompt "Which book is this for?" 5/5, nothing saved | none | 5 words | 2.3 s | 8.2k (1 call) |
| R4 `Move the books by A. Ek and M. Lindqvist into a new collection called Sea Stories.` | 10 | `library_overview > library_create_collection > library_add_books_to_collection`, 10/10 | **applied, 2 steps, one `diffHash`, 10/10** (also identical in run A); Undo restored the library 10/10 | none | prose, 21–26 words, wording differs each time | 6.7 s | 35.5k (4 calls) |
| R5 `Regroup everything into Fiction and Essays.` | 10 | **7 distinct paths** (A: 8) | **review 10/10**; 3–10 steps; **5 distinct `diffHash`** (A: 7) | none | canned sentence, 10 words | 11.4 s (6.7–15.9) | 51k (3–9 calls) |
| R6 `What can you do?` | 10 | no tool, 10/10 | none | none | 7/10 two paragraphs, 3/10 one; 56–79 words | 3.1 s | 8.2k (1 call) |

What is the same every time: the tool path for R1, R3, R4 and R6; the review decision for R5; the applied
changeset and its hash for R4; the anchor prompt for R3. What varies: R5's plan (#1740), the sentence wording
and, in R2, whether the book is resolved first. "Move" is read as "add to the new collection and keep the
other memberships" in 20/20 reps (the reply says so), not as "move out of Fiction".

**Same conversation (3 repeats of each request).** Matches the fresh reps, with one difference. Repeating R4
twice more says "I staged adding the two books … to the existing 'Sea Stories'" with no changeset: the books were
already members, so nothing was staged (covered in #1748). R5's three repeats gave 2 distinct plans.

**Latency and cost.** Median 2.1–2.3 s for a capture, 3 s for the disclosure, 4.6–5.6 s for a retrieval, 6.7 s for an applied change and 11 s for a regroup (up to 17 s).
Each upstream call resends about 9k prompt tokens, so a turn costs about $0.001 (disclosure) to $0.006 (regroup) at list price.

**Unwanted writes.** None in R1–R6 (R3 correctly asks first). The one in the walk is #1742.

## Caveats

- **Synthetic library for the run, real titles for the walk.** Both are small (8–9 books). Retrieval ranking on a
  large library was not tested.
- **One model, SelfHosted-style configuration.** Cloud's managed provider wrapper, its cost-budget seam (#1735) and
  its own tool-round setting were not exercised.
- **First two runs discarded.** They ran with the deterministic bed's 6 tool rounds instead of the product's 12
  ($0.32 of the spend is in `spend.json`). They produced "I stopped before I could finish" on 1 of 10 R4 and
  1 of 10 R5 reps, which the 12-round runs did not reproduce, so that is not listed as a defect.
- **Synthetic selection in the walk.** The text selection was created in the page and a selection event
  dispatched; the menu and context behaved as for a real selection. The failure state was provoked by stopping
  the metering proxy.
- **Spend is computed at list price from provider-reported tokens**, not read from the gateway's billing.
- **Intermittent items are labelled.** #1742 saved a note once in the walk; in the harness the model attempted the
  write in 4 of 5 reps but no note was stored. #1746 was seen once and not reproduced.
- **Observed, not filed:** the selection menu's Highlight button wraps to a second row beside Add note (#1722 is
  about this popup), and the panel covers part of the reader text and the book title on book detail.
