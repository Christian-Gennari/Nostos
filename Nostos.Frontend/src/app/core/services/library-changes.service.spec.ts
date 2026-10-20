import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { LibraryChangesService, POSITION_SETTLE_MS } from './library-changes.service';

/**
 * The library change feed's rules, driven through a fake EventSource: when the
 * stream is open, which revisions count as a change, and what a hidden tab or
 * a screen that mounts later is told.
 */
describe('LibraryChangesService', () => {
  let service: LibraryChangesService;
  let visibility: DocumentVisibilityState;

  class FakeEventSource {
    static instances: FakeEventSource[] = [];

    closed = false;
    private readonly listeners = new Map<string, ((event: MessageEvent) => void)[]>();

    constructor(readonly url: string) {
      FakeEventSource.instances.push(this);
    }

    addEventListener(type: string, listener: (event: MessageEvent) => void): void {
      this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
    }

    close(): void {
      this.closed = true;
    }

    announce(stateVersion: string, progressOnly = false): void {
      const data = progressOnly ? { stateVersion, progressOnly } : { stateVersion };
      for (const listener of this.listeners.get('library') ?? []) {
        listener({ data: JSON.stringify(data) } as MessageEvent);
      }
    }
  }

  const latest = () => FakeEventSource.instances[FakeEventSource.instances.length - 1];
  const open = () => FakeEventSource.instances.filter((source) => !source.closed);

  function setVisibility(state: DocumentVisibilityState): void {
    visibility = state;
    TestBed.inject(DOCUMENT).dispatchEvent(new Event('visibilitychange'));
  }

  function watch(): { count: () => number; subscription: Subscription } {
    let emitted = 0;
    const subscription = service.watch().subscribe(() => emitted++);
    return { count: () => emitted, subscription };
  }

  beforeEach(() => {
    vi.useFakeTimers();
    FakeEventSource.instances = [];
    visibility = 'visible';

    TestBed.configureTestingModule({});
    const document = TestBed.inject(DOCUMENT);
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility);

    service = TestBed.inject(LibraryChangesService);
    vi.spyOn(
      service as unknown as { createEventSource: (url: string) => EventSource },
      'createEventSource',
    ).mockImplementation((url: string) => new FakeEventSource(url) as unknown as EventSource);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('opens nothing until a screen watches', () => {
    expect(FakeEventSource.instances).toHaveLength(0);

    const watcher = watch();

    expect(open()).toHaveLength(1);
    expect(latest().url).toBe('/api/library/changes');
    watcher.subscription.unsubscribe();
  });

  it('treats the first revision as the baseline and emits when it moves', () => {
    const watcher = watch();

    latest().announce('7');
    vi.advanceTimersByTime(300);
    expect(watcher.count()).toBe(0);

    latest().announce('8');
    vi.advanceTimersByTime(300);
    expect(watcher.count()).toBe(1);

    watcher.subscription.unsubscribe();
  });

  it('gathers a burst of revisions into one re-read', () => {
    const watcher = watch();
    latest().announce('1');

    latest().announce('2');
    latest().announce('3');
    latest().announce('4');
    vi.advanceTimersByTime(300);

    expect(watcher.count()).toBe(1);
    watcher.subscription.unsubscribe();
  });

  it('gathers reading-position changes for half a minute before one re-read', () => {
    const watcher = watch();
    latest().announce('1');

    // An audiobook saving its position every two seconds.
    for (let revision = 2; revision <= 10; revision++) {
      latest().announce(String(revision), true);
      vi.advanceTimersByTime(2_000);
    }
    expect(watcher.count()).toBe(0);

    vi.advanceTimersByTime(POSITION_SETTLE_MS);
    expect(watcher.count()).toBe(1);
    watcher.subscription.unsubscribe();
  });

  it('re-reads at once when a change is more than a reading position', () => {
    const watcher = watch();
    latest().announce('1');

    latest().announce('2', true);
    latest().announce('3');
    vi.advanceTimersByTime(300);

    expect(watcher.count()).toBe(1);
    watcher.subscription.unsubscribe();
  });

  it('shares one stream between screens and closes it with the last one', () => {
    const first = watch();
    const second = watch();

    expect(FakeEventSource.instances).toHaveLength(1);

    first.subscription.unsubscribe();
    expect(open()).toHaveLength(1);

    second.subscription.unsubscribe();
    expect(open()).toHaveLength(0);
  });

  it('drops the stream while hidden and reports a change made in the meantime', () => {
    const watcher = watch();
    latest().announce('10');

    setVisibility('hidden');
    expect(open()).toHaveLength(0);

    setVisibility('visible');
    expect(open()).toHaveLength(1);
    latest().announce('12');
    vi.advanceTimersByTime(300);

    expect(watcher.count()).toBe(1);
    watcher.subscription.unsubscribe();
  });

  it('does not open while the tab is hidden', () => {
    visibility = 'hidden';
    const watcher = watch();

    expect(FakeEventSource.instances).toHaveLength(0);
    watcher.subscription.unsubscribe();
  });

  it('forgets the baseline when nobody watches, so a later screen is not told to re-read', () => {
    const first = watch();
    latest().announce('20');
    first.subscription.unsubscribe();

    const later = watch();
    latest().announce('25');
    vi.advanceTimersByTime(300);

    expect(later.count()).toBe(0);
    later.subscription.unsubscribe();
  });

  it('ignores frames it cannot read', () => {
    const watcher = watch();
    latest().announce('1');

    for (const listener of (latest() as unknown as {
      listeners: Map<string, ((event: MessageEvent) => void)[]>;
    }).listeners.get('library') ?? []) {
      listener({ data: 'not json' } as MessageEvent);
      listener({ data: JSON.stringify({ stateVersion: 3 }) } as MessageEvent);
    }
    vi.advanceTimersByTime(300);

    expect(watcher.count()).toBe(0);
    watcher.subscription.unsubscribe();
  });

  it('keeps working without EventSource', () => {
    (service as unknown as { createEventSource: { mockImplementation: (f: () => never) => void } })
      .createEventSource.mockImplementation(() => {
        throw new Error('EventSource is not defined');
      });

    expect(() => watch().subscription.unsubscribe()).not.toThrow();
  });
});
