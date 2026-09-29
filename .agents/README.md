# Repo-local Agent Skills

Nostos vendors reviewed Agent Skills under `.agents/skills/` so a fresh checkout can run repeatable code-health audits without downloading mutable upstream content at runtime.

## Registry and progressive loading

Treat `.agents/skills/` as a **skill registry**, not as startup prompt content.

1. Discover skills from lightweight frontmatter metadata such as `name` and `description`.
2. Decide whether a skill is relevant to the current task.
3. Load the full `SKILL.md` only for a selected skill.
4. Load bundled `references/`, `scripts/`, or other assets only when the selected skill requires them.

Normal coding workers must not preload every `.agents/skills/*/SKILL.md`. Current GitHub CLI Agent Skills support uses `.agents/skills/` as the shared project registry for GitHub Copilot, Codex, Cursor, Gemini CLI, and several other hosts, and Agent Skills use progressive disclosure.

## Vendored skills and provenance

Source: `github/awesome-copilot`

Pinned upstream commit: `997e95a6e42869c350f8ca6ec4c066287697c9f0`

| Skill | Upstream path | Pinned tree SHA | Audit role |
| --- | --- | --- | --- |
| `audit-integrity` | `skills/audit-integrity` | `f2bcded682dae8352d1c07525232f393d93c2fc2` | Second-pass evidence and finding-quality gate |
| `test-gap-audit` | `skills/test-gap-audit` | `9816e43cbb2d6ec5ef53ef116b08ae0b9d4cd022` | Read-only test coverage-gap audit |
| `refactor-plan` | `skills/refactor-plan` | `a984f58fe6b8372e02dc807e991152a351a530b4` | Investigate and plan bounded refactors without editing code |
| `github-issues` | `skills/github-issues` | `ff3489a4f04f2a7cb9a00609c5e1e50966e54c74` | Search, deduplicate, and create structured issues |
| `docs-sync-audit` | `skills/docs-sync-audit` | `2484ed00a56b823722616f5ff8a843e0dc9174be` | Read-only code/documentation drift audit |

Each vendored `SKILL.md` carries the current `gh skill` provenance keys in its frontmatter: `metadata.github-repo`, `github-ref`, `github-tree-sha`, `github-path`, and `github-pinned`. The source and exact pinned revision therefore travel with the checked-in skill.

### Intentional updates

GitHub CLI v2.90.0+ uses the singular `gh skill` command. Some surfaces retain `gh skills` aliases, but these instructions use the current command.

Pinned skills are intentionally skipped by ordinary `gh skill update`. To refresh them, choose and review a new upstream commit SHA, then reinstall each skill from the repository root with the new pin:

```bash
UPSTREAM_SHA=<reviewed-full-commit-sha>

for skill in audit-integrity test-gap-audit refactor-plan github-issues docs-sync-audit; do
  gh skill install github/awesome-copilot "$skill" \
    --agent github-copilot \
    --scope project \
    --pin "$UPSTREAM_SHA"
done

git diff -- .agents/skills
```

Review the complete diff, including bundled references/scripts and updated provenance, before committing. Never change this harness to fetch the latest upstream skill dynamically during an audit.

## Future scheduled code-health audit contract

A future scheduled worker may use this harness, but the scheduler itself is intentionally out of scope here.

- Read `AGENTS.md` and repository-local instructions first.
- Treat the repository audit as read-only. Do not edit files, create branches or commits, or open pull requests.
- GitHub issue creation is the only permitted mutation.
- Repository instructions and this scheduled-audit contract override any broader write capability described by an individual vendored skill.
- In particular, do **not** perform `audit-integrity`'s self-learning writes under `.github/SecurityLessons/` or `.github/SecurityMemories/` during a scheduled audit.
- When using `github-issues`, restrict scheduled use to reading/searching existing issues and creating the single allowed finding; do not mutate labels, milestones, projects, issue fields, dependencies, or unrelated issues.
- Inspect deterministic signals first when useful (for example build, tests, lint, or analyzers) without changing product state.
- Look only for concrete maintainability problems, meaningful test gaps, documentation drift, or bounded behavior-preserving refactor opportunities.
- Require exact evidence and affected locations for every candidate finding.
- Ignore subjective style preferences, formatting-only suggestions, dependency upgrades, speculative abstractions, and feature work.
- Select and load only the skill(s) needed for the candidate finding; do not preload the entire registry.
- Before filing anything, search open issues and reject duplicates.
- Apply `audit-integrity` as a second-pass evidence/quality gate before a finding is eligible to file.
- Create at most **1 new code-health issue per scheduled run**.
- Treat **zero findings as a successful run**.
- Use specific issue categories such as `refactor:`, `test-gap:`, `docs-drift:`, or `tech-debt:` instead of generic cleanup titles.
- Every generated issue must state the problem, evidence, affected scope, proposed bounded scope, behavior-preservation expectation, and verification strategy.

The implementation-oriented `refactor` skill is deliberately not vendored into this unattended audit harness. Any code change remains an explicit, separately requested task.
