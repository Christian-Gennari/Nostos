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
concept regeneration and idempotency.

No live microphone, external STT service or paid generation provider was used.
Model semantic fidelity remains model-dependent and was not certified by these
deterministic checks.
