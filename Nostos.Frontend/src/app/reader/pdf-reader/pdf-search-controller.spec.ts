import { signal } from '@angular/core';
import { FindState } from 'ngx-extended-pdf-viewer';
import { PdfReaderTimers } from './pdf-reader-timers';
import { PdfSearchController } from './pdf-search-controller';

describe('PdfSearchController', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function setup(textCap: 'unknown' | 'available' | 'unavailable' = 'available') {
    const timers = new PdfReaderTimers();
    const textCapability = signal<'unknown' | 'available' | 'unavailable'>(textCap);
    const pdfSearch = {
      find: vi.fn(() => [Promise.resolve(2)]),
      findNext: vi.fn(() => true),
      findPrevious: vi.fn(() => true),
    };
    let doc: object | null = { id: 'doc-1' };

    const controller = new PdfSearchController({
      pdfSearch: pdfSearch as any,
      textCapability,
      timers,
      getPdfDoc: () => doc,
    });
    controller.pdfSearchReady.set(true);

    return {
      timers,
      textCapability,
      pdfSearch,
      controller,
      setDoc: (next: object | null) => {
        doc = next;
      },
    };
  }

  it('cancels source passage polling immediately on reset()', async () => {
    const { timers, pdfSearch, controller } = setup();
    controller.pdfSearchReady.set(false);

    controller.highlightSourcePassage('one two three four five six');
    expect(timers.hasPending()).toBe(true);

    controller.reset();
    timers.cancelAll();
    expect(timers.hasPending()).toBe(false);

    controller.pdfSearchReady.set(true);
    await vi.advanceTimersByTimeAsync(2000);
    expect(pdfSearch.find).not.toHaveBeenCalled();
  });

  it('handles find state transitions and clears active queries cleanly', async () => {
    const { pdfSearch, controller } = setup();

    controller.search('philosophy');
    expect(controller.searchState().status).toBe('searching');

    controller.onFindState(FindState.NOT_FOUND);
    expect(controller.searchState()).toEqual({ status: 'not-found', current: 0, total: 0 });

    controller.onMatchesCount({ current: 1, total: 4 } as any);
    expect(controller.searchState()).toEqual({ status: 'ready', current: 1, total: 4 });

    controller.nextResult();
    controller.previousResult();
    expect(pdfSearch.findNext).toHaveBeenCalledTimes(1);
    expect(pdfSearch.findPrevious).toHaveBeenCalledTimes(1);

    controller.clear();
    expect(pdfSearch.find).toHaveBeenLastCalledWith('', {
      highlightAll: false,
      dontScrollIntoView: true,
    });
    expect(controller.searchState()).toEqual({ status: 'idle', current: 0, total: 0 });
  });
});
