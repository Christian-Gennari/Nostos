# Reader Contents drawer: hierarchy without rules

Before: the drawer under `origin/main` (the PR head before this change). It drew
a hairline under every row and a vertical rule at every nesting level, so the
book's parts and chapters read as table cells.

After: the same book and the same selected chapter on this branch. The rules are
gone. Parts are small tracked labels, chapters are quiet rounded rows, and the
selected row keeps the shared accent rail, inset inside its radius.

Shots are from a production build (`npm run build`) of a seeded multi-part EPUB
on the Playwright fixture, at 1280×900 and 390×844, light and dark, 2× DPR.
