# Reading-history fixtures (Goodreads and StoryGraph)

Issue #1521. These files are written by hand from the public export formats
below. **They are not a fresh export from either service** and hold no real
person's reading history: every title and author is invented.

## Current header rows

`goodreads/goodreads_library_export.csv` (24 columns, the format Goodreads has
produced since the obsolete purchase/BCID columns were removed in 2022):

```
Book Id,Title,Author,Author l-f,Additional Authors,ISBN,ISBN13,My Rating,Average Rating,Publisher,Binding,Number of Pages,Year Published,Original Publication Year,Date Read,Date Added,Bookshelves,Bookshelves with positions,Exclusive Shelf,My Review,Spoiler,Private Notes,Read Count,Owned Copies
```

`storygraph/storygraph_export.csv` (23 columns):

```
Title,Authors,Contributors,ISBN/UID,Format,Read Status,Date Added,Last Date Read,Dates Read,Read Count,Moods,Pace,Character- or Plot-Driven?,Strong Character Development?,Loveable Characters?,Diverse Characters?,Flawed Characters?,Star Rating,Review,Content Warnings,Content Warning Description,Tags,Owned?
```

Where the headers come from (checked 2026-11-10):

- Goodreads: the column list in
  <https://github.com/epw/find-libraries-for-books> (`README.md`, "This
  produces a CSV with the following headers"), the same list in Goodreads'
  own forum thread
  <https://www.goodreads.com/topic/show/22566592-what-s-in-an-export-file>
  (updated 2023), and a real export at
  <https://gwern.net/doc/personal/goodreads.csv>.
- StoryGraph: the header posted in
  <https://github.com/mateusz-bak/openreads/issues/525> and the recorded test
  export in BookWyrm,
  <https://git.nr18.space/qbi/bookwyrm/src/branch/main/bookwyrm/tests/data/storygraph.csv>.
  StoryGraph's own export page is behind a login, so no official public copy
  of the file exists to cite.

## Details the fixtures deliberately cover

- Goodreads writes ISBN and ISBN13 as `="9780000000019"`. An empty identifier
  is `=""`. The fixture carries both shapes, plus an ISBN-10-only row.
- Dates are written as `YYYY/MM/DD` by both services in their public examples.
  (StoryGraph's help-style tables sometimes show `YYYY-MM-DD`; the parser
  accepts both separators.)
- StoryGraph `Star Rating` allows quarter stars (`4.75`); Nostos stores whole
  stars, so the parser rounds. `2.25` and `4.25` are in the fixture.
- `Read Count 2` and a two-range `Dates Read` cover re-reads. StoryGraph
  `Last Date Read` is empty on one row, so the end of the last `Dates Read`
  range has to answer instead.
- Reviews and one title contain commas, doubled quotes and a line break.
- `Exclusive Shelf` / `Read Status` cover `read`, `to-read`,
  `currently-reading` / `currently reading`, `did-not-finish` and `paused`.
- `Bookshelves` / `Tags` carry custom shelves, which are only ever turned into
  collections when the owner asks for it.
- The last row of each file has no title and must be reported as skipped.

The invented ISBN-13s are valid (978-0-00-0000xx-x) so the real checksum
validator accepts them; the ISBN-10s are their valid 10-digit counterparts.
