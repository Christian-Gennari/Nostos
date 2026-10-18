# The Brain

The Brain is where you return to what you noticed while reading. It has two
things in it, **notes** and **topics**, and one picture of how they connect,
the **map**. Everything else is a way to find a note or a topic.

It is built like the Library: a sidebar of places on the left and one reading
column of notes on the right. Topics play the part collections play for
books.

## Places

| Place | Address | Shows |
| --- | --- | --- |
| All notes | `/brain` | Every note and passage, newest first |
| Passages | `/brain/passages` | Passages only (kind `passage`) |
| Unsorted | `/brain/unsorted` | Notes that declare no `[[Topic]]` |
| Map | `/brain/map` | The topic graph. `?focus=<topicId>` centers a topic |
| A topic | `/brain/topics/:id` | The topic's notes. `?with=<topicId>` narrows to notes shared with another topic |
| A note | `/brain/notes/:id` | One note on its own, for links from elsewhere |
| Sort one by one | `/brain/unsorted/sort` | Full screen, one unsorted note at a time |

`/second-brain` and its old query parameters (`topicId`, `conceptId`, `noteId`)
redirect to these addresses.

## The list

Every place that lists notes has the same toolbar, and its state lives in the
URL so Back and shared links reopen the same view:

| Control | Options | Default | URL |
| --- | --- | --- | --- |
| Search | Note text, quote, book, author; matching topic names show as chips above the results | Empty | `q` |
| Book | All books, or one book that has notes in this place | All books | `book` |
| Order | Newest first · Oldest first · By book | Newest first (By book on a topic) | `order` |
| Select | Turns on checkboxes and the selection bar | Off | — |

They combine one way only: place AND book AND search, in the chosen order.
Active filters show as chips with a remove button above the list.

## The note card

One card, everywhere a note appears:

- the quote, with the highlight colour's rail, cut after about five lines;
- your thought;
- topic chips: a chip opens the topic, its × removes the link, **+ Topic**
  adds one (type to filter, Enter to link, a new name creates the topic;
  AI suggestions appear in the picker when AI is available);
- book · author · date · location, and **Open in book**;
- a ⋯ menu: Edit note, Keep with writing…, More from this book, Select,
  View original (when the wording was changed at capture), Delete note.

A passage shows its title and size and opens in the passage viewer.

Removing a topic turns `[[virtue]]` back into plain `virtue`, so no words are
lost.

## Topics

A topic page shows the name, your own words about the topic (optional), how
many notes it has from how many books, and **Often with**: the topics that
share notes with it. Clicking one narrows the list to the shared notes, which
is why the two are related. The ⋯ menu holds Rename, Merge into…, Keep notes
with writing… and Delete topic.

## Sorting one by one

Unsorted has **Sort one by one**, a full-screen mode with one note and one
question: which topics does it belong to? Enter adds a topic and, on an empty
field, saves and moves on. → skips, ← goes back, Esc leaves. Recent topics and
(when AI is on) suggestions are one tap away. Your place is remembered.

## The map

The map is the topic graph: a node's size is how many notes it has, a line
means two topics share notes. Selecting a topic shows a card with **Open
topic**; Back returns to the map as you left it.
