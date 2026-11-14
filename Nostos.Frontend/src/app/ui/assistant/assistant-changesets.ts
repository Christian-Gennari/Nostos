/**
 * Changeset HTTP actions and the one cancellation boundary they share with
 * the Stop request.
 *
 * A conversation switch, a reset or the shell's teardown must not leave a
 * changeset apply/discard/preview/undo in flight: its response would land in a
 * conversation that no longer exists. Every request the assistant starts is
 * tracked here and unsubscribed together.
 */
import { HttpClient } from '@angular/common/http';
import type { Subscription } from 'rxjs';

import type {
  AssistantChangesetActionResponse,
  AssistantChangesetDto,
} from './assistant.service';

export type AssistantChangesetAction = 'preview' | 'apply' | 'discard' | 'undo';

/** Tracks short-lived assistant HTTP requests so a reset can cancel them all. */
export class AssistantRequestTracker {
  private readonly requests = new Set<Subscription>();

  /** Registers a request; it unregisters itself when it settles. */
  track(subscription: Subscription): void {
    this.requests.add(subscription);
    subscription.add(() => this.requests.delete(subscription));
  }

  /** How many requests are currently in flight (tests and diagnostics). */
  get size(): number {
    return this.requests.size;
  }

  /** Unsubscribes every request; no callback runs for a cancelled request. */
  cancelAll(): void {
    const pending = [...this.requests];
    this.requests.clear();
    for (const request of pending) request.unsubscribe();
  }
}

/** Transport for one changeset action. State stays with `AssistantService`. */
export class AssistantChangesetGateway {
  constructor(
    private readonly http: HttpClient,
    private readonly tracker: AssistantRequestTracker,
  ) {}

  post(
    changesetId: string,
    action: AssistantChangesetAction,
    body: Record<string, unknown>,
    onResponse: (response: AssistantChangesetActionResponse) => void,
    onError: (response: AssistantChangesetActionResponse | null) => void,
  ): void {
    const subscription = this.http
      .post<AssistantChangesetActionResponse>(
        `/api/assistant/changesets/${encodeURIComponent(changesetId)}/${action}`,
        body,
      )
      .subscribe({
        next: (response) => onResponse(response),
        error: (error: unknown) => onError(changesetActionFromError(error)),
      });
    this.tracker.track(subscription);
  }
}

/** The steps of a changeset the user has left out. */
export function excludedSteps(changeset: AssistantChangesetDto): number[] {
  return changeset.steps.filter((step) => !step.included).map((step) => step.index);
}

/**
 * A changeset route answers 404/403 with the same body as a refusal, so the
 * card can say what happened instead of a generic network failure.
 */
export function changesetActionFromError(
  error: unknown,
): AssistantChangesetActionResponse | null {
  const body = (error as { error?: unknown } | null)?.error;
  if (
    body
    && typeof body === 'object'
    && typeof (body as { errorCode?: unknown }).errorCode === 'string'
  ) {
    const refusal = body as Partial<AssistantChangesetActionResponse>;
    return {
      success: false,
      errorCode: refusal.errorCode ?? null,
      errorMessage: refusal.errorMessage ?? null,
      changeset: refusal.changeset ?? null,
      conflicts: refusal.conflicts ?? [],
    };
  }
  return null;
}
