# Releases and reader-facing notes

Public Nostos uses Calendar Versioning: **`vYYYY.MM.DD`**, with zero-padded month
and day. Use the release's **UTC deployment date**. The first release on a date
has no suffix; further releases are `.1`, `.2`, and so on, for example
`v2026.10.02`, `v2026.10.02.1`, `v2026.10.02.2`. Dates must be valid calendar
dates. A tag identifies one immutable revision on `main`; never move a published
tag. CalVer records when a release shipped, not API compatibility.

Merges remain continuous. Releases are published automatically on a weekly
schedule using CalVer tags and GitHub Releases, or manually when cutting an off-schedule release.
The maintainer can also trigger the release workflow on-demand via `workflow_dispatch`.
The existing `latest` and `selfhosted-sha-<commit>` container tags
keep their current meaning.

## Generator choice

Use `scripts/generate-release-notes.py`: Python 3.10+ standard library, Git, and
the authenticated GitHub CLI (`gh auth login`). There are no package dependencies
or model calls. `release-it` would introduce a Node release lifecycle and plugins;
`git-cliff` would add a binary/configuration layer and still need GitHub metadata
for labels and PR summaries. The small script handles Nostos's four sections and
its mix of squash and merge commits directly.

Use a clone with full history (`fetch-depth: 0` for an Actions checkout); unshallow
a shallow clone and fetch the required tags or pins before generating notes.

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

## Automated weekly release workflow

A GitHub Actions workflow (`.github/workflows/scheduled-release.yml`) runs weekly
(Sundays at 18:00 UTC) and can also be triggered manually via `workflow_dispatch`.

It executes the following steps:
1. Determines the latest CalVer tag (or fallback baseline pin).
2. Checks if any new reader-facing changes have landed on `main`.
3. If no changes exist, it exits cleanly without publishing an empty release.
4. Generates reader-facing release notes using `scripts/generate-release-notes.py`.
5. Computes the release version `vYYYY.MM.DD` (or `.1`, `.2` for same-day releases).
6. Creates and pushes the tag, and publishes the GitHub Release with the notes.

## Manual release

To preview or publish a release manually:

```bash
git fetch origin --tags
python3 scripts/generate-release-notes.py \
  --from <previous-tag-or-pin> --to <target-main-sha> --version vYYYY.MM.DD > /tmp/release-notes.md

# Publish tag and release
git tag -a vYYYY.MM.DD <target-main-sha> -m "Nostos vYYYY.MM.DD"
git push origin refs/tags/vYYYY.MM.DD
gh release create vYYYY.MM.DD --verify-tag \
  --title "Nostos vYYYY.MM.DD" --notes-file /tmp/release-notes.md
```

```bash
python3 -m unittest discover -s scripts/tests -v
cd Nostos.Frontend && npm run check
# From the repository root:
dotnet build Nostos.sln
```

The script tests run in public CI. Publishing remains an explicit maintainer
action; automated tagging, container aliases, an in-app view, and private Cloud
release orchestration are future work.
