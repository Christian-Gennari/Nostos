/**
 * Retry and deterministic-continuation state for Ask Nostos.
 *
 * Two related facts decide what a submission means:
 *   - the one logical turn whose HTTP result is uncertain, which may be replayed
 *     with its exact TurnId and idempotency key (#560, #1560);
 *   - the server-held follow-up the next real user turn answers (#286), plus the
 *     application snapshot that follow-up was asked in.
 *
 * They are kept together because a failed continuation must restore both, and
 * a reset must clear both. Nothing here touches the ledger or transport.
 */
import { computed, signal } from '@angular/core';

import type { AssistantContext } from './assistant-context.service';
import type { PreparedAssistantTurn } from './assistant-session';
import type { AssistantAnchorPrompt } from './assistant.service';

/** True when a prepared turn can answer this exact text/continuation again. */
export function retryMatchesTurn(
  turn: PreparedAssistantTurn | null,
  text: string,
  continuationId: string | null,
  continuationSkipped: boolean,
): boolean {
  return !!turn
    && turn.text === text
    && turn.request.continuationId === continuationId
    && turn.request.continuationSkipped === continuationSkipped;
}

export class AssistantRetryState {
  /**
   * The one logical turn whose HTTP result is uncertain. Retrying the unchanged
   * draft resends this exact request and TurnId; editing starts a new turn.
   */
  readonly retryableTurn = signal<PreparedAssistantTurn | null>(null);

  /** A deterministic follow-up awaiting the user's next real turn. */
  readonly pendingAnchor = signal<AssistantAnchorPrompt | null>(null);

  /** The application snapshot the pending follow-up belongs to. */
  readonly pendingContinuationContext = signal<AssistantContext | null>(null);

  /**
   * The turn a visible "Try again" control belongs to, if any. The failure
   * block renders the control only for its own turn, so a later successful
   * turn cannot leave an older retry action armed.
   */
  readonly retryableTurnId = computed(() => this.retryableTurn()?.turnId ?? null);

  remember(turn: PreparedAssistantTurn): void {
    this.retryableTurn.set(turn);
  }

  /** Clears the retry when it is this turn's (or when no turn is named). */
  forget(turnId?: string): void {
    if (turnId === undefined || this.retryableTurn()?.turnId === turnId) {
      this.retryableTurn.set(null);
    }
  }

  armContinuation(prompt: AssistantAnchorPrompt, context: AssistantContext): void {
    this.pendingAnchor.set(prompt);
    this.pendingContinuationContext.set(context);
  }

  clearContinuation(): void {
    this.pendingAnchor.set(null);
    this.pendingContinuationContext.set(null);
  }

  /** Consumes the pending follow-up and the snapshot it was asked in. */
  takeContinuation(fallback: AssistantContext): {
    prompt: AssistantAnchorPrompt;
    context: AssistantContext;
  } | null {
    const prompt = this.pendingAnchor();
    if (!prompt) return null;
    const context = this.pendingContinuationContext() ?? fallback;
    this.clearContinuation();
    return { prompt, context };
  }

  /** Restores a prepared turn after a reload; delivery stays uncertain. */
  restore(turn: PreparedAssistantTurn | null): void {
    this.retryableTurn.set(turn);
    if (turn?.continuationPrompt) this.armContinuation(turn.continuationPrompt, turn.context);
  }

  reset(): void {
    this.retryableTurn.set(null);
    this.clearContinuation();
  }
}
