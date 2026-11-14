/**
 * The assistant turn transport: one NDJSON POST and its silence watchdog (#1560).
 *
 * The server writes cumulative `DownloadProgress` text, one JSON product event
 * per line, then a terminal event. This client only slices that text into
 * complete lines and parses them; which event means what belongs to
 * `AssistantService`, because a terminal event updates conversation state.
 *
 * A turn that stays silent past every server clock is a black-holed connection:
 * the watchdog unsubscribes the request and reports a stall, so the panel can
 * offer the same retry a network failure offers.
 */
import { HttpClient, HttpEventType } from '@angular/common/http';
import type { Subscription } from 'rxjs';

/**
 * How long a turn may stay silent before the panel stops waiting (#1560).
 *
 * The server owns the clocks (`AssistantOptions`, #1561): one provider call
 * times out at 60 s (`RequestTimeoutSeconds`), and the whole turn ends at 90 s
 * (`MaxTurnElapsedMilliseconds`), which startup validation keeps above the
 * per-call timeout. So a healthy server always writes a terminal event inside
 * 90 s. Adding a 15 s transport grace makes 105 s the first moment a silent
 * stream cannot still be a legitimate turn. Waiting longer only strands the
 * reader on three dots; firing earlier would replace a slow but live turn with
 * a false failure.
 */
export const ASSISTANT_TURN_WATCHDOG_MS = 105_000;

/** What the conversation does with each parsed value and terminal condition. */
export interface AssistantStreamHandlers {
  /** One parsed NDJSON line, in order. */
  onValue(value: unknown): void;
  /** The response body ended without a terminal event. */
  onEnd(): void;
  /** The connection failed; `offline` is the browser's own opinion. */
  onError(offline: boolean): void;
  /** No value arrived for the whole watchdog window. */
  onStalled(): void;
}

export class AssistantTurnStream {
  private subscription: Subscription | null = null;
  private watchdogTimer: ReturnType<typeof setTimeout> | null = null;
  private stopped = true;

  constructor(private readonly http: HttpClient) {}

  /**
   * Starts a new stream, replacing any previous one. Every callback is ignored
   * after {@link stop}, so a late event from a superseded turn cannot mutate
   * the conversation it no longer belongs to.
   */
  start(
    request: unknown,
    handlers: AssistantStreamHandlers,
    watchdogMs: number = ASSISTANT_TURN_WATCHDOG_MS,
  ): void {
    this.stop();
    this.stopped = false;

    let receivedCharacters = 0;
    let pendingText = '';

    const consumeCumulative = (text: string, final: boolean): void => {
      const delta = text.slice(receivedCharacters);
      receivedCharacters = text.length;
      pendingText += delta;

      const lines = pendingText.split('\n');
      pendingText = lines.pop() ?? '';
      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed) continue;
        if (this.stopped) return;
        try {
          handlers.onValue(JSON.parse(trimmed));
        } catch {
          // A malformed product-event line is a transport failure, not a reason
          // to expose its raw contents in the transcript.
        }
      }

      if (final && pendingText.trim()) {
        try {
          handlers.onValue(JSON.parse(pendingText.trim()));
        } catch {
          // Handled by the caller as an incomplete/invalid transport.
        }
        pendingText = '';
      }
    };

    const subscription = this.http.post('/api/assistant/turn/stream', request, {
      observe: 'events',
      reportProgress: true,
      responseType: 'text',
    }).subscribe({
      next: (event) => {
        if (this.stopped) return;

        if (event.type === HttpEventType.DownloadProgress) {
          consumeCumulative(event.partialText ?? '', false);
          return;
        }

        if (event.type === HttpEventType.Response) {
          const body = typeof event.body === 'string'
            ? event.body
            : JSON.stringify(event.body ?? '');
          consumeCumulative(body, true);
          if (!this.stopped) handlers.onEnd();
        }
      },
      error: () => {
        if (this.stopped) return;
        const offline =
          typeof globalThis.navigator !== 'undefined'
          && globalThis.navigator.onLine === false;
        handlers.onError(offline);
      },
    });

    this.subscription = subscription;
    this.watchdogTimer = setTimeout(() => {
      this.watchdogTimer = null;
      if (this.stopped) return;
      this.stopped = true;
      subscription.unsubscribe();
      handlers.onStalled();
    }, watchdogMs);
  }

  /** Stops the request and its watchdog; late callbacks become no-ops. */
  stop(): void {
    this.stopped = true;
    if (this.watchdogTimer !== null) {
      clearTimeout(this.watchdogTimer);
      this.watchdogTimer = null;
    }
    if (this.subscription) {
      this.subscription.unsubscribe();
      this.subscription = null;
    }
  }

  /** True once this stream can no longer deliver anything. */
  get isStopped(): boolean {
    return this.stopped;
  }
}
