# Captured thoughts — verification for #663

Baseline: `e445d1bbc241f57d4afc5a9b67c692ff06c4884c`.

Before images use the actual baseline Angular build, served from an isolated
worktree. After images use this branch's production build. Both talk to a fresh
local Nostos fixture. The provider adapter uses a scripted local HTTP completion
server; the displayed provider address is fixture configuration.

| View | Before | After |
| --- | --- | --- |
| Desktop note, light | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-desktop-light-before.png) | [After](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-desktop-light-after.png) |
| Desktop note, dark | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-desktop-dark-before.png) | [After](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-desktop-dark-after.png) |
| Phone note, light | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-mobile-light-before.png) | [After](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-mobile-light-after.png) |
| Phone note, dark | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-mobile-dark-before.png) | [After](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-mobile-dark-after.png) |
| Desktop Settings, light | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-settings-desktop-light-before.png) | [After](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-settings-desktop-light-after.png) |
| Desktop Settings, dark | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-settings-desktop-dark-before.png) | [After](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-settings-desktop-dark-after.png) |
| Phone Settings, light | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-settings-mobile-light-before.png) | [After](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-settings-mobile-light-after.png) |
| Phone Settings, dark | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-settings-mobile-dark-before.png) | [After](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-settings-mobile-dark-after.png) |

Viewed at native dimensions: 1440×900 desktop and 390×844 phone, device scale 1.
The note stays primary, original text wraps, and Settings has no horizontal
overflow. Phone content remains scrollable above the fixed navigation dock.
Remote font downloads were unavailable; these images exercise the shipped font
fallbacks in both builds.

## Checks run

- `npm run check`: CSS integrity, theme tokens and design checks pass.
- `npm test`: 73 files, **1,204 tests passed**.
- `npm run build`: production build passes.
- `dotnet build Nostos.sln --no-restore --disable-build-servers -m:1`: passes.
- `dotnet test Nostos.Backend.Tests/Nostos.Backend.Tests.csproj --no-restore --disable-build-servers -m:1 --filter 'FullyQualifiedName~NoteProcessing|FullyQualifiedName~ThoughtProcessor|FullyQualifiedName~AssistantOrchestratorTests|FullyQualifiedName~AssistantSettings|FullyQualifiedName~NoteIdempotency|FullyQualifiedName~Koreader|FullyQualifiedName~NoteServiceTests|FullyQualifiedName~AssistantGateA'`: **203 passed**.
- `e2e/captured-thoughts.spec.ts`: **four browser cases passed**, one per
  viewport/theme. Each verifies original loading, reload, restore, retained quote
  and source anchor, effective mode, persisted Settings selection, and a manual
  save under Clarify that incurs no thought-processing call.

The sandbox required Chromium headless shell with `--no-zygote --single-process`.
Each browser case ran in a separate Playwright invocation because that browser
mode cannot reliably reuse a process across closed contexts. The fixture reused
already verified build outputs (`E2E_SKIP_BUILDS=1`); the normal repository
configuration builds its own isolated fixture.

Typed/transcribed input equivalence is covered by the frontend assistant request
tests. Backend regressions cover stored-mode enforcement for text/voice captures,
quote-only exclusion, fallback acknowledgement, continuation/replay, original-only
reprocessing, direct note/reader-style saves, KOReader imports, ordinary edits,
topic regeneration and idempotency.

No live microphone, external STT service or paid generation provider was used.
Model semantic fidelity remains model-dependent and was not certified by these
deterministic checks.

## Original wording modal — follow-up for PR #666

Baseline: `002d837372de23c78d50bcd08ca3541e28798561` (the inline original view).
The original wording now stays outside the note inspector and opens only through
**View original**, in the shared modal shell. The restore action is inside the
modal. Phone layouts use the shell's full-screen sheet with pinned header/actions.

The before links below are the prior PR screenshots from that exact baseline.
The new inspector and modal images were captured from this follow-up's production
build against fresh isolated backend fixtures, at device scale 1. Images were
viewed at native 1440×900 and 390×844 dimensions.

| View | Before: inline original | After: inspector | After: original modal |
| --- | --- | --- | --- |
| Desktop, light | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-desktop-light-after.png) | [Inspector](../../Nostos.Frontend/e2e/visual-evidence/captured-original-modal-desktop-light-inspector.png) | [Modal](../../Nostos.Frontend/e2e/visual-evidence/captured-original-modal-desktop-light-open.png) |
| Desktop, dark | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-desktop-dark-after.png) | [Inspector](../../Nostos.Frontend/e2e/visual-evidence/captured-original-modal-desktop-dark-inspector.png) | [Modal](../../Nostos.Frontend/e2e/visual-evidence/captured-original-modal-desktop-dark-open.png) |
| Phone, light | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-mobile-light-after.png) | [Inspector](../../Nostos.Frontend/e2e/visual-evidence/captured-original-modal-mobile-light-inspector.png) | [Modal](../../Nostos.Frontend/e2e/visual-evidence/captured-original-modal-mobile-light-open.png) |
| Phone, dark | [Before](../../Nostos.Frontend/e2e/visual-evidence/captured-thoughts-note-mobile-dark-after.png) | [Inspector](../../Nostos.Frontend/e2e/visual-evidence/captured-original-modal-mobile-dark-inspector.png) | [Modal](../../Nostos.Frontend/e2e/visual-evidence/captured-original-modal-mobile-dark-open.png) |

Checks run for this follow-up:

- `npm run check`: CSS, theme and design checks pass.
- `npm run build`: production build passes.
- `dotnet build Nostos.sln --disable-build-servers -m:1`: 0 errors,
  10 existing warnings.
- `npm test`: 70 files and **1,092 tests passed**; three suites could not load
  Node imports (`reader-shell`, `pdf-reader`, `motion-contract`). Running the same
  command on an untouched archive of the baseline with the same dependencies
  reproduces those three failures, with 1,090 tests passed. The added modal
  regressions pass; the complete unit suite remains blocked by that existing
  runner/dependency issue. Actual follow-up output:

  ```text
  Error: No such built-in module: node:
  Test Files  3 failed | 70 passed (73)
       Tests  1092 passed (1092)
  ```

- `E2E_SKIP_BUILDS=1 npx playwright test --config
  playwright.original-modal.config.ts --project desktop-chromium --grep
  '<case>' captured-thoughts.spec.ts`: four separate invocations, with cases
  `desktop light`, `desktop dark`, `mobile light`, and `mobile dark`;
  each reports **1 passed**. The temporary local config extends the repository
  config with Chromium headless shell and `--no-zygote --single-process`, as in
  the earlier verification. It is not part of the change.

Browser assertions cover hidden defaults before opening and after reload, focus
entry/trapping/return, Escape, desktop backdrop dismissal, phone close controls,
original restoration with the inspector retained, and long transcript scrolling
with the close action still in the viewport and no horizontal overflow. Unit
regressions cover dismissal during loading and reuse of the pending request,
restore dismissal locking, load-error retry, and note-switch response isolation.
Only fresh fixture databases were mutated. Remote fonts remained unavailable.
