# Add Book — the format no longer starts on "Physical book"

Evidence for the Add Book default changed in this branch. Captured from the
production build (`npm run build`) served by `npm run preview -- serve`, at
DPR 1, with the API answered by this branch's backend on an empty database.
Steps: Library → **Add book** → **Add manually**.

- **Before** (`origin/main` modal default): *Book type* reads **Physical book**,
  and the paper-only *Identify by ISBN* panel shows above the form. A scanned
  PDF chosen afterwards is uploaded onto a physical record.
- **After**: *Book type* reads **E-book (EPUB, PDF)**. A chosen file still sets
  ebook or audiobook from its extension; *Physical book* stays one choice in
  the dropdown.

| Shot | Before | After |
| --- | --- | --- |
| 1280 light | `before-1280-light.png` | `after-1280-light.png` |
| 1280 dark | `before-1280-dark.png` | `after-1280-dark.png` |
| 390 light | `before-390-light.png` | `after-390-light.png` |

No page errors at any size; the dialog does not scroll sideways at 390px.
