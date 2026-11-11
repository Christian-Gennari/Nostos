Before/after evidence for #1558 (report writes that completed before a turn failed).

Stubbed assistant turn; no model was configured or called.

before: the failed event carries no body, exactly as the base branch sends it.
after: the failed event carries the capture that committed before the failure.

Both phases were captured at 1440x900 and 390x844, light and dark, device
scale 1, against the Playwright fixture (e2e/support/launch-fixture.mjs).

Regenerate from Nostos.Frontend/:

  node e2e/support/launch-fixture.mjs launch
  node e2e/support/assistant-partial-writes-shots.mjs before   # on the base
  node e2e/support/assistant-partial-writes-shots.mjs after    # on this head
  node e2e/support/launch-fixture.mjs kill

The `before` shots came from the stacked PR's base commit (the same backend
that sends a failure with no body); `after` came from this PR's head.
