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
keep their current meaning. A release also tags the image built from its
revision as `ghcr.io/christian-gennari/nostos:vYYYY.MM.DD`, so the git tag,
the GitHub Release and the image tag always name the same revision. Each image
also carries its commit in the assemblies' informational version
(`1.0.0+<commit>`).

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

Every PR title must be a Conventional Commit: `type(scope): outcome`, or
`type(scope)!: outcome` for a breaking change. The required `PR title` check
(`.github/workflows/pr-title.yml`) rejects any other title, with these types:
`feat`, `fix`, `perf`, `ux`, `revert`, `refactor`, `docs`, `test`, `ci`,
`build`, `chore`, `style`. Only the title is checked: commits on the branch
never reach the notes, because the generator reads each merged PR's title.

Describe the reader's outcome in the title. The generator
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
| ⚠️ Before you upgrade | Any breaking change: `!` in the title, a `BREAKING CHANGE:` line in the body, or a `breaking-change` label |
| 📖 Reader & Notes | `reader`, `reading`, `epub`, `pdf`, `notes`, `brain`, `concepts` |
| 📚 Library & Discovery | `library`, `discovery`, `book`, `books`, `collections`, `upload`, `opds` |
| 🤖 Ask Nostos | `assistant`, `ask-nostos`, `ai` |
| 🛠️ Improvements & Fixes | Other public `feat`, `fix`, `perf`, `ux`, `revert` changes |

Product-area labels take precedence over scopes; if multiple area labels apply,
the table's section order breaks the tie. `area: reading` and `area:reading` both
work. PRs without conventional titles need a recognized area label or the
`release-note` label. Internal `chore`, `test`, `ci`, `build`, `docs`, `style`, and
`refactor` changes, plus fixes scoped to tests, CI, dependencies, agent tooling,
or release tooling, are omitted. A breaking change is listed whatever its type or scope, because a
`build!` that needs a newer database matters to every self-hoster. Its entry
uses, in order, the first paragraph under a `## Before you upgrade` heading in
the PR body, the text after `BREAKING CHANGE:`, then the usual sources. Say
what the reader must do before upgrading, not only what changed. A
`release-note:skip`, `release-note:none`, or `skip-changelog` label explicitly
suppresses an entry, breaking or not. Empty sections are omitted.
Every GitHub PR entry links to its source.

## Automated weekly release workflow

A GitHub Actions workflow (`.github/workflows/scheduled-release.yml`) runs weekly
(Sundays at 18:00 UTC) and can also be triggered manually via `workflow_dispatch`.

Scheduled and manual runs share one concurrency group, so they never overlap.

It executes the following steps:
1. Repairs the newest CalVer release if an earlier run stopped part-way: a
   missing image tag or GitHub Release is added, and existing pieces are
   checked against the tag's revision (`scripts/ensure-release.sh`).
2. Determines the latest CalVer tag (or fallback baseline pin).
3. Checks if any new reader-facing changes have landed on `main`.
4. If no changes exist, it exits cleanly without publishing an empty release.
5. Generates reader-facing release notes using `scripts/generate-release-notes.py`.
6. Computes the release version `vYYYY.MM.DD` (or `.1`, `.2` for same-day releases).
7. Stops if the SelfHosted image for that revision (`selfhosted-sha-<commit>`)
   is not published yet. Re-run the workflow once `Publish SelfHosted container`
   has finished.
8. Creates and pushes the git tag, adds the `vYYYY.MM.DD` tag to that image
   (same digest, nothing is rebuilt), and only then publishes the GitHub
   Release with the notes. A visible release therefore always has its image.

`scripts/ensure-release.sh <version> <revision> <previous-ref> [notes-file]`
does steps 1 and 7–8. Each piece is created only when missing; an existing git
tag must point at the revision and an existing image tag must have the same
digest as `selfhosted-sha-<revision>`, or the run fails without changing
anything. `DRY_RUN=1` prints the writes instead of making them.

## Manual release

To preview or publish a release manually:

```bash
git fetch origin --tags
python3 scripts/generate-release-notes.py \
  --from <previous-tag-or-pin> --to <target-main-sha> --version vYYYY.MM.DD > /tmp/release-notes.md

# Publish tag, image tag and release
git tag -a vYYYY.MM.DD <target-main-sha> -m "Nostos vYYYY.MM.DD"
git push origin refs/tags/vYYYY.MM.DD
docker buildx imagetools create \
  --tag ghcr.io/christian-gennari/nostos:vYYYY.MM.DD \
  ghcr.io/christian-gennari/nostos:selfhosted-sha-<target-main-sha>
gh release create vYYYY.MM.DD --verify-tag \
  --title "Nostos vYYYY.MM.DD" --notes-file /tmp/release-notes.md

# Or let the script do the three steps, skipping any that already exist:
scripts/ensure-release.sh vYYYY.MM.DD <target-main-sha> <previous-tag-or-pin> /tmp/release-notes.md
```

```bash
python3 -m unittest discover -s scripts/tests -v
cd Nostos.Frontend && npm run check
# From the repository root:
dotnet build Nostos.sln
```

The script tests run in public CI.

