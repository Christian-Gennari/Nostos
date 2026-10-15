---
name: nostos-library
description: "Use when the user asks to add, find, update or organise books and collections in their Nostos library (e.g. 'add Dune to my library', 'put it in Sci-Fi', 'mark it finished'). Talks to a SelfHosted Nostos server over its REST API with curl."
---

# Nostos Library via the REST API

Talk to a SelfHosted Nostos server with `curl` against its REST API. A
SelfHosted server has no login: the API answers anyone who can reach it, so it
is normally only reachable from the machine it runs on. The default address is
the production port on that machine; set `NOSTOS_URL` to point elsewhere.

```bash
B=${NOSTOS_URL:-http://127.0.0.1:5214}
```

This is the user's real library. Read before you write, change only what was
asked, and tell the user what you did with the book title and its collections.

## Rules

1. **Look before you add.** Search first (`GET /api/books?search=...`). If the
   book is already there, say so instead of adding it again.
2. **Find books and collections by ID.** Never guess an ID. Get it from a
   search or the collection list.
3. **Never delete without the user saying so in this conversation.** That goes
   for books and collections. Deleting a book also deletes its files, notes
   and reading progress, and it cannot be undone.
4. **To change collections, use `PUT /api/books/{id}/collections`** with the
   full list you want. Do not send `collectionId` in a book update: it
   *replaces* every collection the book is in with that one.
5. **Only send the fields you are changing** in `PUT /api/books/{id}`.
6. **Stay inside the endpoints below.** For anything else (uploading book
   files, notes, imports, backups, settings), stop and ask the user.

## Books

### Search and list

```bash
curl -s "$B/api/books?search=dune"
curl -s "$B/api/books?filter=finished&sort=title&page=1&pageSize=50"
curl -s "$B/api/books?collectionId=<collection-id>"
curl -s "$B/api/books/<book-id>"
```

- Returns `{ items, totalCount, page, pageSize }`. `pageSize` is capped at 100.
  Page through with `page=` when `totalCount` is larger.
- `filter`: `all` (default), `favorites`, `finished`, `reading`, `notStarted`,
  `unsorted` (in no collection).
- `sort`: `recent` (default), `title`, `rating`, `lastRead`.
- Useful book fields: `id`, `type`, `title`, `author`, `isbn`, `asin`,
  `rating`, `isFavorite`, `progressPercent`, `finishedAt`, `collectionIds`,
  `editionCount`, `hasFile`.

### Look up metadata by ISBN

```bash
curl -s "$B/api/books/lookup/9780441013593"
```

Returns ready-made book fields (title, author, publisher, page count, and so
on). `400` = not a valid ISBN, `404` = no metadata found, `503` = metadata
services are down (add the book from what the user told you).

### Add a book

```bash
curl -s -w ' HTTP %{http_code}\n' -X POST "$B/api/books" \
  -H 'Content-Type: application/json' \
  -d '{"type":"physical","title":"Dune","author":"Frank Herbert","isbn":"9780441013593","collectionIds":["<collection-id>"]}'
```

- `type` is required: `physical`, `ebook` or `audiobook`. Use `physical` when
  the user owns a paper copy, and ask if it is not clear.
- `title` is required. Add `author` and `isbn` whenever you have them.
  Audiobooks use `asin`, not `isbn`. Sending an ISBN for an audiobook is
  rejected.
- The best input is the lookup result plus `type` and `collectionIds`.
- Other optional fields: `subtitle`, `publisher`, `publishedDate`, `pageCount`,
  `language`, `series`, `volumeNumber`, `description`, `rating` (0-5),
  `isFavorite`, `personalReview`.
- **`201`** = a new book was created. **`200`** = Nostos found the same book
  (same ISBN/ASIN, or same title + author + type) and returned the existing one
  without changing it. Tell the user it was already there.
- The same title in a different format (e.g. the ebook of a paperback they
  already have) is created as a separate edition of the same work. That is
  intended.

### Update a book

```bash
curl -s -X PUT "$B/api/books/<book-id>" -H 'Content-Type: application/json' \
  -d '{"rating":5,"isFavorite":true}'
```

- A field you leave out stays as it is. An empty string `""` clears a text
  field.
- `"isFinished": true` marks it finished (progress 100%). `false` clears the
  finished date but leaves progress where it was.
- Fields: `title`, `subtitle`, `author`, `translator`, `narrator`,
  `description`, `isbn`, `asin`, `publisher`, `publishedDate`, `edition`,
  `pageCount`, `language`, `categories`, `series`, `volumeNumber`, `rating`,
  `isFavorite`, `personalReview`, `isFinished`.

### Set a book's collections

This replaces the whole list. To **add** a book to a collection, read its
current `collectionIds`, append the new ID, and send the full list. To
**remove** one, send the list without it. `[]` takes the book out of every
collection.

```bash
curl -s "$B/api/books/<book-id>" | python3 -c 'import sys,json;print(json.load(sys.stdin)["collectionIds"])'
curl -s -X PUT "$B/api/books/<book-id>/collections" -H 'Content-Type: application/json' \
  -d '{"collectionIds":["<existing-id>","<new-id>"]}'
```

An unknown collection ID returns `404 collection_not_found` and changes
nothing.

### Set a cover

```bash
curl -s -o /tmp/cover.jpg '<image-url>'
curl -s -X POST "$B/api/books/<book-id>/cover" -F 'file=@/tmp/cover.jpg;type=image/jpeg'
```

### Delete a book (only when explicitly asked)

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X DELETE "$B/api/books/<book-id>"   # 204 = deleted
```

## Collections

Collections are folders. They can be nested, and a book can be in several at
once.

```bash
curl -s "$B/api/collections"          # flat list: [{id, name, parentId}]
curl -s "$B/api/collections/counts"   # [{collectionId, bookCount}], includes sub-collections
```

Build the tree from `parentId` (`null` = top level). Match the user's wording
to a collection name. If two names fit, ask.

### Create

```bash
curl -s -X POST "$B/api/collections" -H 'Content-Type: application/json' \
  -d '{"name":"Sci-Fi","parentId":null}'
```

If a collection with that name already exists in the same place, you get the
existing one back instead of a duplicate.

### Rename or move

Both fields are always required. Send the current name to only move it, and
the current `parentId` to only rename it. `"parentId": null` moves it to the
top level.

```bash
curl -s -X PUT "$B/api/collections/<id>" -H 'Content-Type: application/json' \
  -d '{"name":"Science Fiction","parentId":null}'
```

### Delete (only when explicitly asked)

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X DELETE "$B/api/collections/<id>"   # 204 = deleted
```

Books in it are **not** deleted. They just leave that collection. A collection
with sub-collections returns `409 collection_has_children`: move or delete the
children first, and ask the user which one they want.

## Errors

Errors come back as JSON with a `title` code and a readable `detail`, for
example `{"title":"invalid_book_identity","status":400,"detail":"Unknown book type: kindle. ..."}`.
Report the `detail` to the user. Do not retry a `4xx` with guessed changes.

If nothing answers at `$B`, the server is down. Tell the user; do not restart
it yourself. (On the maintainer's machine it runs under PM2 as `nostos`.)
