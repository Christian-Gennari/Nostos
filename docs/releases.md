# Releases and reader-facing notes

Public Nostos uses Calendar Versioning: **`vYYYY.MM.DD`**, with zero-padded month
and day. Use the release's **UTC deployment date**. The first release on a date
has no suffix; further releases are `.1`, `.2`, and so on, for example
`v2026.10.02`, `v2026.10.02.1`, `v2026.10.02.2`. Dates must be valid calendar
dates. A tag identifies one immutable revision on `main`; never move a published
tag. CalVer records when a release shipped, not API compatibility.

Merges remain continuous. A merge is not automatically a release. The maintainer
chooses a deployment revision after public product CI passes and publishes its
tag and notes. The existing `latest` and `selfhosted-sha-<commit>` container tags
keep their current meaning; this spike does not add CalVer container aliases.

## Generator choice

Use `scripts/generate-release-notes.py`: Python 3.10+ standard library, Git, and
the authenticated GitHub CLI (`gh auth login`). There are no package dependencies
or model calls. `release-it` would introduce a Node release lifecycle and plugins;
`git-cliff` would add a binary/configuration layer and still need GitHub metadata
for labels and PR summaries. The small script handles Nostos's four sections and
its mix of squash and merge commits directly.

The range is **previous revision excluded, target revision included**. Both
arguments accept tags or full commit pins; the previous revision must be an
ancestor of the target. GitHub mode walks the first-parent history and looks up
each exact merged PR, so it neither depends on merge-date search windows nor
silently truncates a PR list. It fails if metadata cannot be fetched or does not
match the merge revision. Direct conventional commits in the range are also
included. No tags, releases, commits, or pushes are created by the generator.

## Writing notes in a PR

Use a Conventional Commit title describing the reader's outcome. The generator
removes the type/scope prefix and trailing PR number. For wording that needs more
care, add a short prose paragraph to the PR body:

```markdown
## Release notes

EPUB pages follow light and dark mode as you switch, without reloading your book.
```

An HTML comment block `<!-- release-note -->` / `<!-- /release-note -->` also
works. Priority is that block, then `Release notes`, then the first prose paragraph
under `Summary`, then the cleaned title. Lists, tables, verification sections,
and arbitrary PR-body introductions are not copied. Notes should be concrete,
calm, and readable: name the action or result, use familiar product names, and
leave file paths, test counts, internal identifiers, and implementation details
in the PR. Review the preview once per release; automation cannot guarantee that
every historical PR title is suitable editorial copy.

| Section | Scopes or `area:` labels |
| --- | --- |
| 📖 Reader & Notes | `reader`, `reading`, `epub`, `pdf`, `notes`, `brain`, `concepts` |
| 📚 Library & Discovery | `library`, `discovery`, `book`, `books`, `collections`, `upload`, `opds` |
| 🤖 Ask Nostos | `assistant`, `ask-nostos`, `ai` |
| 🛠️ Improvements & Fixes | Other public `feat`, `fix`, `perf`, `ux`, `revert` changes |

Product-area labels take precedence over scopes; if multiple area labels apply,
the table's section order breaks the tie. `area: reading` and `area:reading` both
work. PRs without conventional titles need a recognized area label or the
`release-note` label. Internal `chore`, `test`, `ci`, `build`, `docs`, `style`, and
`refactor` changes, plus fixes scoped to tests, CI, dependencies, agent tooling,
or release tooling, are omitted. A `release-note:skip`, `release-note:none`, or
`skip-changelog` label explicitly suppresses an entry. Empty sections are omitted.
Every GitHub PR entry links to its source.

## Preview and update

From the repository root:

```bash
git fetch origin --tags
python3 scripts/generate-release-notes.py \
  --from v2026.10.01 --to <target-main-sha> --version v2026.10.02

# After reviewing the preview, write the same range on a release-notes worktree.
python3 scripts/generate-release-notes.py \
  --from v2026.10.01 --to <target-main-sha> --version v2026.10.02 \
  --write CHANGELOG.md
```

Writing prepends the version and keeps older releases. Re-running that version
replaces its section rather than duplicating it. Writing a dated version replaces
the `Unreleased` draft; ensure the chosen range includes all draft changes before
writing. The range pins are recorded in an HTML comment for reproducibility.
Stdout contains only the generated Markdown section and can be redirected to a
notes file for GitHub Releases or a future website / What's New view.

For an offline clone, explicitly choose `--source git`. This reads non-merge
commits across the same range, including commits from merged branches, and
deduplicates identical descriptions. It lacks PR labels and PR summaries, so the
wording and granularity can differ. GitHub failures never silently switch modes.
An optional `--repo owner/repo` avoids repository inference and adds a compare
link in git mode too.

## Publish a release

1. Choose a full target SHA on `origin/main` with passing CI and the next unused
   CalVer for the UTC deployment date. Use the preceding release tag as `--from`.
2. Create an isolated worktree with `agent-worktree new release-notes-<date>`.
   Preview, review, and write the changelog. Commit it, push, and open a PR. The
   human reviews and merges; agents do not merge or enable auto-merge.
3. Fetch the merged `main`, run CI for that exact revision, and deploy it. The
   notes PR may add only documentation / release tooling after the chosen target;
   if product changes intervened, regenerate notes through the actual deployment
   revision before releasing.
4. The maintainer tags the **actual deployed main revision**, not the notes branch:

   ```bash
   git fetch origin --tags
   git merge-base --is-ancestor <deployed-main-sha> origin/main
   git tag -a v2026.10.02 <deployed-main-sha> -m "Nostos v2026.10.02"
   git push origin refs/tags/v2026.10.02
   python3 scripts/generate-release-notes.py \
     --from v2026.10.01 --to v2026.10.02 --version v2026.10.02 > /tmp/nostos-release-notes.md
   gh release create v2026.10.02 --verify-tag \
     --title "Nostos v2026.10.02" --notes-file /tmp/nostos-release-notes.md
   ```

The committed changelog and release section should describe the same product
changes; the notes-only commit is filtered from the GitHub release output.
Use the immutable full revision when a downstream deployment updates its pin;
the public generator accepts old/new pins without requiring private-repo access.

## Initial snapshot and checks

There were no CalVer tags or published GitHub releases when this spike ran. The
initial `CHANGELOG.md` is therefore an **Unreleased** snapshot of recent merges,
not an assertion that a release shipped. It covers the commits after
`7db4e3ddd484f1c56486d92a24475c6df544c570` through
`b2c48819df931dbb61756062ea65cf78f0e39167` (29 September–2 October 2026).
For the first release, use that same baseline pin to retain the seeded changes,
then use published CalVer tags thereafter. The initial scope is deliberately
recent history rather than a retroactive account of every past merge.

```bash
python3 -m unittest discover -s scripts/tests -v
cd Nostos.Frontend && npm run check
# From the repository root:
dotnet build Nostos.sln
```

The script tests run in public CI. Publishing remains an explicit maintainer
action; automated tagging, container aliases, an in-app view, and private Cloud
release orchestration are future work.
