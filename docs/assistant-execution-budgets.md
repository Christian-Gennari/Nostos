# Ask Nostos per-turn execution limits

Ask Nostos bounds each interactive turn independently of any host access or usage policy. The call, token, and wall-clock limits protect SelfHosted BYOK deployments from runaway tool loops and stalled provider requests.

| Limit | Default | Purpose |
| --- | ---: | --- |
| Provider calls | 12 | Final runaway ceiling; repeated equivalent tool batches and input boundaries stop earlier. |
| Cumulative reported tokens | 120,000 | Bounds one turn when the provider reports token usage. Unknown usage stays unknown and does not count as zero. |
| Provider request | 60,000 ms | The inner clock: one stalled call fails with its own timeout and message, and leaves room for one tool round. |
| Elapsed time (whole turn) | 90,000 ms | The outer clock: bounds every request and tool round in the turn, and reports the execution limit. |

## The clocks, in order

Two wall clocks bound a turn, and a third timeout is a transport backstop. They
are ordered on purpose: a single provider request always times out before the
whole-turn budget can, and the turn still has one tool round of room.

1. **Per provider request — 60 s (`Assistant:RequestTimeoutSeconds`).** The
   orchestrator arms this deadline with the turn's `TimeProvider` and links it
   to the provider call. A call that runs past it ends the turn with
   `assistant_provider_timeout` and “Ask Nostos took too long to answer. Try
   again.” (retryable). The named assistant `HttpClient` gets this value plus a
   5 s grace, a transport backstop for the case where the in-process clock
   cannot run; it maps to the same typed timeout, so the person sees the same
   message either way.
2. **Whole turn — 90 s (`Assistant:MaxTurnElapsedMilliseconds`).** This bounds
   every provider request and every tool round in the turn. It is checked
   before another request is spent, and a request started near the deadline
   receives only the time that is left. If less than a full request timeout is
   left, the turn clock is the one that fires — there is nothing left for the
   request clock to be ordered against. This is the only clock that reports
   `assistant_execution_budget_exhausted` and “This turn reached its execution
   limit before it could finish.”
3. **No per-tool or server-side stream-idle clock.** Tools run in-process over
   the local database and return in milliseconds; a mutating tool that has
   already begun is deliberately allowed to reach its terminal state instead of
   being abandoned mid-write. The stream itself is bounded by clock 2. (The
   panel's client-side watchdog for a stream that goes silent is a separate
   client concern, not a server budget.)

Startup validation (`AssistantExecutionBudget.ValidateClocks`) refuses a
configuration where one request plus a tool round does not fit inside the turn:
`Assistant:RequestTimeoutSeconds × 1000 + 15,000 ms` must be at most
`Assistant:MaxTurnElapsedMilliseconds`. The shipped 60 s / 90 s pair leaves
30 s. A configuration naming the old 95 s request timeout against the 90 s turn
budget now fails startup with a message that says to use 75 s or less, raise the
turn budget, or set `Assistant:MaxTurnElapsedMilliseconds` to 0 to disable the
turn ceiling. The request timeout must stay positive; setting the turn ceiling
to zero or below disables only clock 2.

The limits were first chosen after exercising representative library, notes, capture, approval, and required-input workflows, where the largest legitimate scenario used four calls. They were raised (2026-10-14) when library changes became one changeset per request: a whole reorganisation reads the library overview, stages deletes, new collections and filings, and corrects a refused step, which takes six to ten calls and re-sends the overview on each. A turn that reaches a limit after staging library changes never applies them; it offers them for review (see `docs/ask-nostos-library-changes.md`).

A separate provider run recorded 900 turns. It found a 31,875-token maximum and eight calls that stalled for 141–145 seconds. Those observations supported the token and time headroom. The 60 s request clock cuts those stalls well before the 90 s turn budget can. They describe that test provider and transport only; BYOK provider prices and token accounting vary, so Nostos does not estimate customer spend in the public product.

On reaching a limit, Ask Nostos returns the completed work it can identify and explains that the turn stopped before finishing. Staged library changes become a review marked as possibly incomplete. User-input prompts keep their own result type. Missing token usage is never reported as zero, and no prompt, reply, tool arguments, tool results, or library content are stored in the execution metrics.

Provider-specific usage reporting and any commercial allowance policy belong to the host. The public SelfHosted host uses BYOK credentials and has no Nostos subscription or metering requirement.
