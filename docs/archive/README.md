# Archive

Historical material kept for the record. Nothing here describes the current
product; it is what was agreed, measured or planned at a point in time, and it
stays here so the reasoning is not lost when a decision is revisited.

Active docs live one level up in `docs/` (plus `docs/cloud/`, `docs/adr/`,
`docs/runbooks/`). If a file here still looks useful, copy the live part into
the current docs and link back — do not treat the archived copy as current.

| Path | Status | Why it is here |
| --- | --- | --- |
| `ask-nostos-redesign.md` | Implemented | The changeset design that shipped; the HTTP contract now lives in `docs/ask-nostos-library-changes.md` and `Nostos.Shared/Dtos/AssistantDtos.cs` |
| `ask-nostos-assessment.md`, `ask-nostos-assessment/` | Point-in-time (2026-11-11) | Screenshot-and-measurement evidence for the #1464 polish plan, taken against `main @ f200e10c` |
| `ask-nostos-polish-plan.md` | Proposed plan (#1464) | The concrete fixes and sub-issues produced by that assessment |
| `manage-library-slice-1-design.md` | Implemented | Design of the Manage library page (`/settings/library`) |
| `private-host-extraction-plan.md` | Completed cutover (#438/#462) | Public/private repository handoff; the live boundary is `docs/adr/cloud-public-private-boundary.md` |
| `performance/librivox-import/` | Measurement spike (#773) | LibriVox import timings and harness from one host; the commands and scratch paths in it are machine-specific |
| `research/alignment-844/`, `research/alignment-step1/` | Investigation notes | Audio ↔ EPUB alignment measurements that informed the shipped alignment work |

The alignment spike (`tools/Nostos.Alignment.Spike*`) that accompanied
`research/alignment-step1/` was removed in #1653. Its 28 `PassageMatcher` tests
now run in `Nostos.Backend.Tests/Alignment/PassageMatcherTests.cs` against the
shipped matcher, and CI no longer runs a separate test step for it. The spike's
benchmark harness and CLI are recoverable from git history.
