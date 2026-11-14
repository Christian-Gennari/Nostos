/**
 * The voice transcript's pre-dispatch grace window (issue #262 §6).
 *
 * `auto` (the decision): the transcript enters the composer and is dispatched
 * after {@link TRANSCRIPT_AUTO_SEND_DELAY_MS}. While that grace window is open
 * an Undo cancels the dispatch BEFORE anything is sent. Pre-dispatch on purpose:
 * the app has no delete capability, so a capture must never be created wrongly
 * in the first place.
 *
 * `review`: the transcript waits in the composer until the user sends it.
 *
 * ONE SWITCH, deliberately. Both branches converge on `submit()` — the exact
 * entry point a typed message uses — so there is no second send path and no
 * mode asymmetry between an ordinary capture and a follow-up answer.
 */
import { signal } from '@angular/core';

export const TRANSCRIPT_SEND_POLICY: 'review' | 'auto' = 'auto';

/** The grace window before an auto-sent transcript is dispatched. */
export const TRANSCRIPT_AUTO_SEND_DELAY_MS = 2000;

/**
 * One pending auto-send, owned by the conversation rather than the composer:
 * closing the panel or resetting the conversation abandons it, and teardown
 * clears the timer so nothing is dispatched behind a surface nobody can Undo
 * from.
 */
export class AssistantAutoSend {
  private timer: ReturnType<typeof setTimeout> | null = null;
  private readonly pendingState = signal(false);

  /** True while an auto transcript is waiting out its Undo window. */
  readonly pending = this.pendingState.asReadonly();

  schedule(delayMs: number, dispatch: () => void): void {
    this.cancel();
    this.pendingState.set(true);
    this.timer = setTimeout(() => {
      this.timer = null;
      this.pendingState.set(false);
      dispatch();
    }, delayMs);
  }

  /** Pre-dispatch Undo: stop the pending auto-send before anything is sent. */
  cancel(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    this.pendingState.set(false);
  }

  /** Lifecycle alias: teardown abandons the pending dispatch. */
  destroy(): void {
    this.cancel();
  }
}
