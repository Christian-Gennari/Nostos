# How Ask Nostos changes your library

Ask Nostos can reorganise the library: create, rename, move and delete
collections, file books into them or take them out, edit a book's own details
(title, author, description, rating, favourite, review, finished), and give
**topic kinds** to topics or create a new kind. It does this as one
**changeset** per request, whatever the size of the request.

## What you see

- **Everyday changes apply at once, with Undo.** Renaming a collection, making
  a new one, filing a few books or marking one finished happens immediately. A
  "Changed" card in the conversation says what changed, and **Undo** takes it
  back.
- **Larger changes wait for your review.** A "Review before applying" card
  says what would change and why it is asking. It lists each step with a
  checkbox, and **Show every change** lists every collection and book
  affected. **Apply changes** applies all the ticked steps together; **Discard**
  changes nothing. Right after the card appears, replying "yes" or "no" does
  the same.
- **Topic kinds are grouped by kind.** A kind the changeset creates comes
  first, with its colour and a "New" marker; every kind shows its palette
  colour, existing or new. Each group has one checkbox, and **Show every
  change** lists the topics in it so a wrong guess can be unticked on its own.
  A topic or group you untick keeps its place, unticked — it does not move
  into the plain step list or disappear. A partly ticked group shows a dash in
  its box, its count reads included/total, and its preview names only the
  topics Apply would actually change. A new kind's create step counts only
  while at least one of its topics is ticked, so **Apply changes** is disabled
  once nothing is left ticked. Once the card is applied, undone or set aside,
  **Show every change** lists only what was applied, as plain rows with no
  checkboxes.
- **Nothing is applied half way.** A review changes nothing until you apply it,
  and an apply is all or nothing.

"Delete all my collections and reorganise my library" is one request: one
card with every delete, every new collection and every book filed.

## When Ask Nostos asks first

The change policy (`AssistantChangePolicy`) reads what the changes would
actually do, not which tool proposed them. Changes wait for review when they:

- delete a collection that holds books;
- delete more than 3 empty collections;
- take more than 3 books out of collections;
- touch more than 25 books;
- edit the details of more than 10 books;
- give, change or clear the kind of more than 10 topics
  (`Assistant:AutoApplyTopicKinds`), or create a topic kind at all;
- were worked out after reading book text, notes or book descriptions, which
  can carry instructions the user did not write; or
- come from a turn that stopped early (step, token or time limit, or a
  repeated tool loop), because Nostos cannot know the model had finished.

The limits are `Assistant:AutoApply*` settings (`AssistantOptions`).

## Topic kinds

Ask Nostos reads kinds with `topics_list_kinds`, and each topic's kind and who
set it (`reader`, `assistant`, `connected-app`) with `topics_list` and
`topics_get`. It classifies through `topics_set_kind` (many topics, one kind, or
clear) and creates a kind with `topics_create_kind`.

- **A kind you set is yours.** Ask Nostos never replaces — or clears — a kind
  whose source is `reader` unless you named that topic in the same request.
  "Named" means the model passed that topic's id in `namedTopicIds` and the
  name occurs in your own words as a whole token, folded like a topic name
  (case and composed accents). A word inside a longer word, or the request's
  own vocabulary ("a kind", "my topics"), does not count. The model must pass
  ids: an omitted `namedTopicIds` means you named no topic, so every reader
  kind is protected — Nostos does not guess from the prose. When verifying an
  id the model passed, the three-letter rule does not apply, so a topic called
  "AI" or "1984" is recognised when it is in your text. The result tells the
  model "Kept N kinds you set yourself", so it can report what it left alone.
- **Creating a kind always waits for review**, even inside an otherwise small
  changeset.
- Applied kinds carry the `assistant` source. They appear in the Brain's
  "Set by Ask Nostos" filter (issue #1769) and can be sorted again by hand.
- Undo restores every topic's kind and source from before the changeset, then
  deletes a kind the changeset created once no topic uses it any more. A kind
  another topic started using is kept. A kind you deleted, renamed or recoloured
  after the apply is yours: Undo keeps it and lists what it left alone, rather
  than failing.

**Where you start it.** The map legend's **No kind (n)** row offers two
separate actions when an AI provider is configured: **Sort them by hand** (the
kinds page, filtered to topics with no kind) and **Sort with Ask Nostos**. The
second opens Ask Nostos with the request already in the composer — "Give a kind
to my topics that have none. Use the kinds I have; propose a new kind only if
many topics need it." — visible and editable before you send it. A draft you
had already typed is kept: the request is appended below it, never written
over.

On a large library the model reads `topics_list` a page at a time (the
`untyped` filter returns only each topic's id and name, up to 250 a page with a
`nextPage`), and may stage several `topics_set_kind` calls (up to 250 topics
each); they arrive on one review card. If a turn's execution budget runs out
first, the reply above the card says how many it sorted and how many still have
no kind, and a second run continues from the topics that have no kind.

## What Ask Nostos never does

It has no tool to delete a book or its files, delete notes, or change
settings, AI connections, backups or imports. Asked to, it says where in the
app to do it. It has no outbound tools, so private data only leaves Nostos
through the AI provider you configured.

## How it works

1. During a turn, library tools **stage** operations into a draft changeset
   (`AssistantChangesetDraft`). Each staging previews everything staged so far
   through the canonical library code (`ILibraryChangesetService.PreviewAsync`)
   in a transaction that is rolled back, so a refused step is reported to the
   model at once and never staged.
2. When the model stops, `AssistantChangesetCoordinator.FinishTurnAsync` applies
   the changeset or stores it for review. A retried delivery of the same turn
   reports the earlier result instead of applying again.
3. Applying runs the stored steps in one transaction and commits only if they
   still produce the diff the user reviewed (the diff hash). If the library
   changed in between, the card is refreshed with what the steps would do now.
4. Undo restores each row the changeset touched from a before/after snapshot,
   but only rows still as the changeset left them. Anything changed since is
   kept and listed on the card.

Changesets live in server memory (`AssistantChangesetStore`): a review for an
hour, an applied changeset's undo for a day, at most 500. They do not survive a
server restart; the card then says the changes are no longer available.

## HTTP

All routes take the changeset's `actionToken` and answer with
`AssistantChangesetActionResponse` (failures are data). An unknown changeset
is `404` and a wrong token `403`, with the same body. They do not need the AI
provider, so a review can still be applied or undone when it is unavailable.

| Route | Body | Does |
| --- | --- | --- |
| `POST /api/assistant/changesets/{id}/preview` | `{ actionToken, excluded }` | Recomputes a review with steps left out (and the steps that depend on them). |
| `POST /api/assistant/changesets/{id}/apply` | `{ actionToken, excluded, diffHash }` | Applies the review; a retry after a lost response reports the applied result. |
| `POST /api/assistant/changesets/{id}/discard` | `{ actionToken }` | Sets a review aside. |
| `POST /api/assistant/changesets/{id}/undo` | `{ actionToken }` | Undoes an applied changeset and lists conflicts. |

A turn response carries the changeset in `changeset` (`AssistantChangesetDto`).
