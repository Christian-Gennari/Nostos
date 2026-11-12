# Ask Nostos capability copy (#1559) — before / after

Captured from the production build against the Playwright fixture
(`e2e/support/launch-fixture.mjs launch`), with a seeded library and the
assistant status stubbed to available. No model was configured or called.

| Surface | Files |
| --- | --- |
| Empty state, compact | `empty-compact-desktop-{light,dark}.png`, `empty-compact-phone-{light,dark}.png` |
| Empty state, expanded | `empty-expanded-desktop-{light,dark}.png` |
| Settings → Assistant | `settings-assistant-desktop-{light,dark}.png`, `settings-assistant-phone-{light,dark}.png` |
| Reader selection action | `selection-desktop-{light,dark}.png`, `selection-phone-{light,dark}.png` |

- **before**: base branch head, `fix/assistant-partial-writes-1558` at `59cdd755` (PR #1575).
- **after**: this branch, `feat/assistant-capabilities-1559`.

To reproduce, from `Nostos.Frontend/` after `npm run build`:

```bash
node e2e/support/launch-fixture.mjs launch
node e2e/support/ask-nostos-capabilities-shots.mjs before   # or after
node e2e/support/launch-fixture.mjs kill
```

`ASK_NOSTOS_SHOTS_OUT` overrides the output directory, so one script can run
in a worktree of the base branch while writing next to the PR's branch.
