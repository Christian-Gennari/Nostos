# Nostos — Agent Instructions

This file is read automatically by coding agents working in this repo (Codex,
OpenCode, Antigravity CLI, Copilot, Cursor, and others — see
<https://agents.md>). If you are an agent, follow it.

**Read this before making changes.** It is not a style guide; it is the
workflow contract for this repository.

---

## 0. Working in parallel: isolate first, then branch

**If a task is parallel work — a new branch, your own branch, more than one
agent on this repo at once, or anything the user calls "in parallel" — do NOT
branch inside this shared checkout.** Create an isolated worktree with its own
branch, ports, and database copy instead, and do all work from there.

```bash
agent-worktree new <slug>          # sibling worktree + agent/<slug> branch
source ../"$(basename "$(git rev-parse --show-toplevel)")"-<slug>/.agent/env.sh
cd "$AGENT_WORKTREE"
```

`agent-worktree` gives you an isolated tree, a private writable copy of
`nostos.db`, shared `Storage`/`node_modules` (symlinked, never copied), and a
`frontend`/`backend` port pair that cannot collide with production (5214) or
another agent. It retires the worktree automatically once the branch merges.
See `agent-worktree` (no arguments) for usage.

**Why the shared checkout is not safe for a new branch.** `git checkout -b`
switches the branch of the *entire working tree*. Every other agent and the
human share those files, so:

- their uncommitted work silently follows you onto your branch;
- a build you trigger writes the same `wwwroot/` that production is serving;
- a `git stash` / `reset` / `clean` intended for your files can destroy theirs.

This has already happened in this repo, more than once.

**Staging and the shared tree.** Even when your change is not parallel work:

- Run parallel work in a worktree (above). In the shared tree, stage explicit
  paths — `git add AGENTS.md`, never `git add -A` or `git add .`.
- In a dedicated worktree, `git add -A` is safe: you own every file in it.
- Never `git stash`, `git checkout -- .`, `git reset --hard`, or `git clean`
  in the shared tree while other work may be present.

---

## 1. Never commit directly to `main`

Every change goes through a branch and a pull request. No exceptions for
"trivial" or "obvious" fixes. For parallel work, follow §0 and create the branch
in a worktree.

```bash
git fetch origin
agent-worktree new fix/short-description   # parallel work (§0)
# — or, for a single-threaded change in the shared tree —
git checkout main && git pull origin main
git checkout -b fix/short-description
```

### Why this matters here

Multiple agents (and the human) work in this repo **concurrently**. Pushing
straight to `main` has caused real damage:

- concurrent agents overwriting each other's uncommitted files;
- a stash/rebase cycle trampling another agent's in-flight edits;
- lost work that had to be manually recovered from backups.

A branch + PR makes work reviewable and merges atomic. A direct push does not.

### Branch names

Observed convention in this repo — match it:

| Prefix | Use |
| --- | --- |
| `feature/<slug>` | new functionality |
| `fix/<slug>` | bug fixes |
| `chore/<slug>` | tooling, config, deps |

Slugs are short and hyphenated: `fix/modal-zen-ux`, `feature/tinymce-nostos`.

### Commit messages

Conventional Commits, always: `type(scope): summary`.

Seen in this repo: `fix(ui):`, `feat(brand):`, `refactor(theme):`,
`docs(readme):`, `test(visual-qa):`, `chore:`.

Write the body to explain **why**, and record what you actually verified —
measured numbers, not adjectives. See `git log` for the house style: commits
here routinely cite before/after values and the exact command run.

**NO CO-AUTHORS / BOT TRAILERS:**
Never add `Co-authored-by: ...` trailers to commit messages or PR bodies
(such as `CommandCodeBot`, `Copilot`, bot handles, or placeholders).
Commits must only be authored by the repo maintainer.


---

## 2. Verify before you open the PR

Run the cheapest checks that cover your change, and **report the real result**.

```bash
# Frontend, from Nostos.Frontend/
npm run check          # CSS integrity + theme-token graph (fast, always run)
npm test               # unit tests
npm run build          # production build must succeed

# Backend, from the repo root
dotnet build Nostos.sln
```

Rules:

- **Never claim a check passed without running it.** Paste the real output.
- If a check fails for a reason unrelated to your change, say so explicitly
  rather than silently ignoring it.
- Do not describe work as done when it is verified only by reading the code.
  Exercise it.
- If something is genuinely unverified, label it as unverified in the PR body.

---

## 3. Open the PR

```bash
git push -u origin HEAD
gh pr create --title "fix(ui): short summary" --body "…"
```

### PR body format

House style, based on merged PRs in this repo:

```markdown
<One-line summary of what and why.>

**<Area>**
- Specific change
- Specific change

Verified: <exact checks run and their results>.

Closes #<issue-number>
```

Keep it factual. `Closes #N` links the issue — include it when one exists.

### Do not merge your own PR

Open it and stop. The human reviews and merges. Do not merge, and do not
enable auto-merge.

---

## 4. Working alongside other agents

This is the single biggest source of lost work in this repo. **§0 is the rule;
this section is the detail for when you are in the shared tree.**

- **Stay in your lane.** Only edit files your task requires. If you need to
  change something outside it, say so instead of doing it.
- **Stage explicit paths in the shared tree.** `git add path/a path/b`, never
  `git add -A` or `git add .` — the working tree usually contains other people's
  uncommitted work. Inside your own worktree (§0) `git add -A` is safe.
- **Never `git stash`, `git checkout -- .`, `git reset --hard`, or
  `git clean`** in the shared tree while other work may be present. These
  silently destroy uncommitted edits and have already done so here.
- **Before `git rebase`/`git pull --rebase`, check `git status`** and stop if
  files you did not touch are modified.
- If you find unexpected modifications, **leave them alone** and mention them
  in your report. They are not yours to clean up.

---

## 5. Project specifics

- **Stack:** .NET 10 + SQLite + EF Core (backend), Angular 21 standalone +
  Signals (frontend).
- **Run the app:** `npm start` (dev), `npm run prod` (production build).
- **Brand:** the mark is forest tile + paper arch, **theme-invariant** — see
  the README's Brand section and `Nostos.Frontend/public/`.
- **Never commit `Nostos.Backend/wwwroot/` or `Nostos.Frontend/dist/`** —
  both are gitignored build output.
- **Icons:** the app has ONE icon language, Phosphor, and ONE rendering path.
  Feature code never imports an icon package. Buttons use the attribute
  component (`<button appIconButton icon="trash">`); every other glyph uses
  `<nostos-icon name="book-open" />` or `<nostos-icon concept="audiobook" />`.
  Names are a closed union, so a typo is a compile error with a suggestion.
  Adding a glyph = one import in `src/app/ui/icon/nostos-icons.ts`; a stable
  product concept = one entry in `nostos-concepts.ts`. Do not import from
  `@phosphor-icons/*` anywhere else, and do not add another icon package
  without an architectural reason. Weights exist per glyph (`regular` is the
  default; `fill` for selected states); sizes are px numbers on a small shared
  scale (`NOSTOS_ICON_SIZE`).
- **UI/theme changes:** `styles.css` holds the token graph; a colour token
  added to `:root` needs its `:root[data-theme='dark']` counterpart or
  `npm run check:theme` fails. Genuinely theme-invariant tokens belong in that
  script's `INVARIANT` list instead.
- **Visual changes:** verify in a real browser and attach before/after
  evidence. Downscaled screenshots hide defects — check at 1:1.

---

## 6. Scope discipline

Do exactly what was asked, and no more.

If you believe adjacent work is needed, **finish the requested change first,
then raise the extra item** rather than folding it in. Unrequested "improvements"
to files the user has already finished are treated as a defect, not a bonus.

---

## 7. Traps found the hard way

Each of these has already cost time or data in this repo. They are not style
preferences.

### Multi-collection membership (books ↔ collections)

- **`BookCollections` is the only record of membership.** `Books.CollectionId`
  was dropped after a transition period; there is no mirror column, so
  `Unsorted` means "in no collection at all".
- **Anything that serialises a book must `Include(b => b.BookCollections)`.**
  The book row alone no longer says which collections it is in, so a missed
  include silently reports empty membership. `BackupService` is the one that
  mattered: without it, backups stop carrying collections and a restore loses
  them. `EnsureCreated`-based tests will NOT catch this.
- **`BookDto` is a positional record.** Append new fields; inserting one
  renumbers the JSON of every existing client.
- **The MCP surface is FROZEN** (`docs/library-mcp-contracts.md`). Its singular
  `collectionId`/`clearCollection` arguments are translated into set operations
  *in the service* — never change a tool signature to add a capability.
- **Deleting a collection deletes membership rows, never books.** The join
  table's collection FK is `Restrict`, so membership rows must go first.
- Membership is **per book (per edition)** by decision, not per work.

### Migrations

- **Tests use `EnsureCreated`, which never executes migrations.** A hand-written
  backfill or guard is unverified by the suite even when every test passes. Prove
  it on a real DB copy using a migration-proof procedure (below) and assert the
  numbers on the file you intended — `dotnet ef` prints "Done." even when the
  connection override did not take.
- **Never invent a migration filename.** Run `dotnet ef migrations add <Name>`,
  then patch the file it created; guessing the timestamp produces a duplicate
  partial class and `CS0111`.
- **A destructive migration must refuse to run when it would lose data.** SQLite
  cannot `RAISE` outside a trigger, so guard with a one-row table whose
  `CHECK (violation = 0)` is violated only when the invariant breaks:
  `INSERT INTO guard SELECT 1 WHERE EXISTS (<bad rows>)` inserts nothing when
  healthy and aborts with `SQLite Error 19` when not. See
  `DropLegacyBookCollectionId.Up()` for the exact shape.
- **`Down()` must backfill, never bare `AddColumn`** — restoring all-nulls
  silently discards what the column represented.
- EF warns that its generated `PRAGMA foreign_keys = 0` step cannot run in a
  transaction for FK-dropping migrations. That is inherent to SQLite; put any
  guard *before* it and note it in the PR rather than treating it as a failure.

### Verification

- **Never run a mutating call against production as a "check".** Exercise
  writes on a migrated DB copy instead, then confirm production read-only. A
  `PUT` with an empty body during a verification pass once stripped a real
  book's collection from live data.
- For a visual change, run the app and look at it at 1:1; a green test suite is
  not the same as a screen you have actually seen.

### Working in this repo

- Run parallel work in a worktree and **commit + push early**. A worktree missing
  from `.git/agent-worktrees/*.tsv` is classified as "0 commits → nothing to
  lose" by `prune` (which the post-merge hook fires), so it can be deleted
  mid-task, bypassing the 120-minute grace window.
- `npm test` is the runner (`ng test`) — run it bare. `npx vitest run <file>`
  fails with `describe is not defined`, and `ng test --include` breaks the
  `.html` loader.
- **Never `npx prettier --write`** — prettier is not a dependency and this
  codebase is not formatted to it.

