import type { Book, Contents, Rendition } from 'epubjs';
import { Subject } from 'rxjs';

import {
  EpubGroundedNavigator,
  GROUNDED_SETTLE_QUIET_MS,
  SELECTION_ANCHOR_GRACE_MS,
} from './epub-grounded-navigator';

describe('EpubGroundedNavigator', () => {
  const TARGET_CFI = 'epubcfi(/6/46!/4/2:0)';
  const STALE_CFI = 'epubcfi(/6/46!/4/1:0)';

  function createHarness() {
    const resizeEvents$ = new Subject<void>();
    const listeners: Record<string, Array<() => void>> = {};
    let currentCfi: string | null = STALE_CFI;
    let progressUnlocked = true;
    let pendingSelectionStart: string | null = null;
    let rangeVisible: boolean | null = null;

    const doc = document.implementation.createHTMLDocument('Chapter');
    doc.body.innerHTML = '<p>Grounded passage sentence one. Second sentence.</p>';

    const rangeStub = {
      get getBoundingClientRect() {
        if (rangeVisible === null) return undefined;
        return () =>
          (rangeVisible
            ? { left: 20, top: 20, right: 40, bottom: 30 }
            : { left: 900, top: 20, right: 920, bottom: 30 }) as DOMRect;
      },
    };
    const frame = { getBoundingClientRect: () => ({ left: 0, top: 0, width: 800, height: 600 }) };
    const content = {
      document: doc,
      section: { href: 'ch1.xhtml' },
      window: { frameElement: frame },
      range: vi.fn(() => rangeStub as unknown as Range),
      cfiFromRange: vi.fn(() => TARGET_CFI),
    } as unknown as Contents;

    const rendition = {
      display: vi.fn(() => Promise.resolve()),
      on: vi.fn((type: string, cb: () => void) => {
        (listeners[type] ??= []).push(cb);
      }),
      off: vi.fn((type: string, cb: () => void) => {
        listeners[type] = (listeners[type] ?? []).filter((fn) => fn !== cb);
      }),
    } as unknown as Rendition;

    const book = {
      spine: {
        get: vi.fn(() => ({ href: 'ch1.xhtml' })),
      },
    } as unknown as Book;

    const releasePageHold = vi.fn();
    const clearLandingMark = vi.fn();
    const showLandingMark = vi.fn();
    const onConfirmed = vi.fn();

    const navigator = new EpubGroundedNavigator({
      rendition: () => rendition,
      book: () => book,
      renderedContents: () => [content],
      readingViewRect: () => ({ left: 0, top: 0, right: 800, bottom: 600 } as DOMRect),
      contentMatchesHref: () => true,
      spineSources: () => [{ href: 'ch1.xhtml', index: 0 }],
      currentLocation: () => currentCfi,
      progressUnlocked: () => progressUnlocked,
      pendingSelectionStart: () => pendingSelectionStart,
      resizeEvents$: resizeEvents$.asObservable(),
      releasePageHold,
      clearLandingMark,
      showLandingMark,
      onConfirmed,
    });

    return {
      navigator,
      rendition,
      resizeEvents$,
      releasePageHold,
      clearLandingMark,
      showLandingMark,
      onConfirmed,
      setCurrentCfi: (cfi: string | null) => {
        currentCfi = cfi;
      },
      setProgressUnlocked: (unlocked: boolean) => {
        progressUnlocked = unlocked;
      },
      setPendingSelectionStart: (start: string | null) => {
        pendingSelectionStart = start;
      },
      setRangeVisible: (visible: boolean | null) => {
        rangeVisible = visible;
      },
      emitResized: () => {
        (listeners['resized'] ?? []).forEach((cb) => cb());
      },
    };
  }

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('queues grounded navigation before unlock and confirms after settle', async () => {
    vi.useFakeTimers();
    const h = createHarness();
    h.setProgressUnlocked(false);

    await h.navigator.goToSource({
      type: 'epub',
      epubCfi: TARGET_CFI,
      epubResourceHref: 'ch1.xhtml',
      epubSpineIndex: 0,
      epubTextOffset: null,
      excerpt: 'Grounded',
    });

    expect(h.navigator.hasPendingSource()).toBe(true);
    expect(h.rendition.display).not.toHaveBeenCalled();

    h.setProgressUnlocked(true);
    h.setCurrentCfi(TARGET_CFI);
    const pending = h.navigator.consumePendingSource();
    expect(pending).not.toBeNull();
    await h.navigator.applySource(pending!);

    expect(h.rendition.display).toHaveBeenCalledWith(TARGET_CFI);
    expect(h.navigator.isProtectingProgress()).toBe(true);

    await vi.advanceTimersByTimeAsync(GROUNDED_SETTLE_QUIET_MS);

    expect(h.onConfirmed).toHaveBeenCalledWith(TARGET_CFI);
    expect(h.navigator.isProtectingProgress()).toBe(false);
    expect(h.navigator.message()).toBeNull();
  });

  it('keeps selection anchor through grace window and re-anchors on resize', async () => {
    vi.useFakeTimers();
    const h = createHarness();
    h.setPendingSelectionStart(TARGET_CFI);
    h.setCurrentCfi(TARGET_CFI);

    h.navigator.holdSelectionAnchor();
    expect(h.navigator.isProtectingProgress()).toBe(true);

    h.navigator.releaseSelectionAnchor();
    h.navigator.reanchorOnResize();
    await vi.advanceTimersByTimeAsync(0);
    expect(h.rendition.display).toHaveBeenCalledWith(TARGET_CFI);

    await vi.advanceTimersByTimeAsync(GROUNDED_SETTLE_QUIET_MS);
    expect(h.navigator.isProtectingProgress()).toBe(true);

    await vi.advanceTimersByTimeAsync(SELECTION_ANCHOR_GRACE_MS);
    expect(h.navigator.isProtectingProgress()).toBe(false);
  });

  it('cancels in-flight settle timers on destroy', async () => {
    vi.useFakeTimers();
    const h = createHarness();
    h.setCurrentCfi(STALE_CFI);

    const restorePromise = h.navigator.restoreSavedPosition(h.rendition, TARGET_CFI);
    await vi.advanceTimersByTimeAsync(0);
    h.emitResized();

    h.navigator.destroy();
    const outcome = await restorePromise;
    expect(outcome).toBe('superseded');

    await vi.advanceTimersByTimeAsync(3000);
    expect(h.rendition.display).toHaveBeenCalledTimes(1);
    expect(h.onConfirmed).not.toHaveBeenCalled();
  });
});
