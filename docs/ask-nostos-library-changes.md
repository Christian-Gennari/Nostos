# How Ask Nostos changes your library

Ask Nostos can reorganise the library: create, rename, move and delete
collections, file books into them or take them out, and edit a book's own
details (title, author, description, rating, favourite, review, finished). It
does this as one **changeset** per request, whatever the size of the request.

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
- were worked out after reading book text, notes or book descriptions, which
  can carry instructions the user did not write; or
- come from a turn that stopped early (step, token or time limit, or a
  repeated tool loop), because Nostos cannot know the model had finished.

The limits are `Assistant:AutoApply*` settings (`AssistantOptions`).

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
