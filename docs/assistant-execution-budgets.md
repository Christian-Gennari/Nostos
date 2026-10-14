# Ask Nostos per-turn execution limits

Ask Nostos bounds each interactive turn independently of any host access or usage policy. The call, token, and elapsed-time limits protect SelfHosted BYOK deployments from runaway tool loops and stalled provider requests.

| Limit | Default | Purpose |
| --- | ---: | --- |
| Provider calls | 12 | Final runaway ceiling; repeated equivalent tool batches and input boundaries stop earlier. |
| Cumulative reported tokens | 120,000 | Bounds one turn when the provider reports token usage. Unknown usage stays unknown and does not count as zero. |
| Elapsed time | 90,000 ms | Cancels an in-flight provider request at the remaining turn deadline; stays below the 95 s per-request timeout. |

The limits were first chosen after exercising representative library, notes, capture, approval, and required-input workflows, where the largest legitimate scenario used four calls. They were raised (2026-10-14) when library changes became one changeset per request: a whole reorganisation reads the library overview, stages deletes, new collections and filings, and corrects a refused step, which takes six to ten calls and re-sends the overview on each. A turn that reaches a limit after staging library changes never applies them; it offers them for review (see `docs/ask-nostos-library-changes.md`).

A separate provider run recorded 900 turns. It found a 31,875-token maximum and eight calls that stalled for 141–145 seconds. Those observations supported the token and time headroom. They describe that test provider and transport only; BYOK provider prices and token accounting vary, so Nostos does not estimate customer spend in the public product.

On reaching a limit, Ask Nostos returns the completed work it can identify and explains that the turn stopped before finishing. Staged library changes become a review marked as possibly incomplete. User-input prompts keep their own result type. Missing token usage is never reported as zero, and no prompt, reply, tool arguments, tool results, or library content are stored in the execution metrics.

Provider-specific usage reporting and any commercial allowance policy belong to the host. The public SelfHosted host uses BYOK credentials and has no Nostos subscription or metering requirement.
