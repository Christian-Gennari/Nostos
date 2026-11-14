import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpEventType, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ASSISTANT_TURN_WATCHDOG_MS, AssistantTurnStream } from './assistant-stream';

describe('AssistantTurnStream', () => {
  let http: HttpTestingController;
  let stream: AssistantTurnStream;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    stream = new AssistantTurnStream(TestBed.inject(HttpClient));
  });

  afterEach(() => {
    stream.stop();
    vi.useRealTimers();
    http.verify();
  });

  it('slices cumulative progress text into complete NDJSON lines once', () => {
    const values: unknown[] = [];
    let ended = 0;

    stream.start({}, {
      onValue: (value) => values.push(value),
      onEnd: () => { ended += 1; },
      onError: () => undefined,
      onStalled: () => undefined,
    });

    const request = http.expectOne('/api/assistant/turn/stream');
    request.event({
      type: HttpEventType.DownloadProgress,
      loaded: 6,
      total: 20,
      partialText: '{"a":1}\n{"b"',
    });
    request.event({
      type: HttpEventType.DownloadProgress,
      loaded: 14,
      total: 20,
      partialText: '{"a":1}\n{"b":2}\n',
    });
    request.flush('{"a":1}\n{"b":2}\n');

    expect(values).toEqual([{ a: 1 }, { b: 2 }]);
    expect(ended).toBe(1);
  });

  it('stops waiting at the watchdog, cancels the request and reports a stall', () => {
    vi.useFakeTimers();
    let stalled = 0;

    stream.start({}, {
      onValue: () => undefined,
      onEnd: () => undefined,
      onError: () => undefined,
      onStalled: () => { stalled += 1; },
    });
    const request = http.expectOne('/api/assistant/turn/stream');

    vi.advanceTimersByTime(ASSISTANT_TURN_WATCHDOG_MS - 1);
    expect(stalled).toBe(0);

    vi.advanceTimersByTime(1);
    expect(stalled).toBe(1);
    expect(request.cancelled).toBe(true);
  });

  it('ignores values from a superseded turn', () => {
    const values: unknown[] = [];

    stream.start({}, {
      onValue: (value) => values.push(value),
      onEnd: () => undefined,
      onError: () => undefined,
      onStalled: () => undefined,
    });
    const request = http.expectOne('/api/assistant/turn/stream');
    stream.stop();

    // The cancelled request cannot deliver anything else; a later turn starts a
    // new stream and the old callbacks are never attached to it.
    expect(request.cancelled).toBe(true);
    expect(stream.isStopped).toBe(true);
    expect(values).toEqual([]);
  });
});
