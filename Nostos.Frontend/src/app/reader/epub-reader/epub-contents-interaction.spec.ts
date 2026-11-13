import type { Contents } from 'epubjs';

import {
  EpubContentsInteractionController,
  PAGE_HOLD_RELEASE_MS,
} from './epub-contents-interaction';

describe('EpubContentsInteractionController', () => {
  function createHarness() {
    const page = document.createElement('div');
    page.id = 'epub-page';
    const scroller = document.createElement('div');
    scroller.className = 'epub-container';
    scroller.scrollLeft = 240;
    scroller.scrollTop = 0;
    page.appendChild(scroller);
    document.body.appendChild(page);

    let rtl = false;
    let assistantSelection: string | null = null;
    const highlightsAt = vi.fn(() => []);
    const next = vi.fn();
    const previous = vi.fn();
    const onSearchRequested = vi.fn();
    const onSurfaceInteracted = vi.fn();
    const onHighlightActivated = vi.fn();

    const controller = new EpubContentsInteractionController({
      pageElement: () => page,
      isRtl: () => rtl,
      assistantSelection: () => assistantSelection,
      highlightsAt,
      next,
      previous,
      onSearchRequested,
      onSurfaceInteracted,
      onHighlightActivated,
    });

    const doc = document.implementation.createHTMLDocument('Chapter');
    doc.body.innerHTML = '<p>Hello world</p><input type="text" />';
    const contents = { document: doc, window: doc.defaultView } as unknown as Contents;

    return {
      page,
      scroller,
      doc,
      contents,
      controller,
      highlightsAt,
      next,
      previous,
      onSearchRequested,
      onSurfaceInteracted,
      onHighlightActivated,
      setRtl: (value: boolean) => {
        rtl = value;
      },
      setAssistantSelection: (value: string | null) => {
        assistantSelection = value;
      },
      cleanupDom: () => page.remove(),
    };
  }

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('registers and clears keyboard listeners cleanly without leaking across documents', () => {
    const h = createHarness();
    try {
      h.controller.registerKeyboard(h.contents);
      expect(h.controller.keyboardDocuments.size).toBe(1);

      // Ctrl+F emits searchRequested.
      const find = new KeyboardEvent('keydown', { key: 'f', ctrlKey: true, bubbles: true, cancelable: true });
      h.doc.dispatchEvent(find);
      expect(find.defaultPrevented).toBe(true);
      expect(h.onSearchRequested).toHaveBeenCalledTimes(1);

      // ArrowRight advances page; RTL reverses it.
      h.doc.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
      expect(h.next).toHaveBeenCalledTimes(1);

      h.setRtl(true);
      h.doc.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
      expect(h.previous).toHaveBeenCalledTimes(1);

      // Typing targets ignore paging keys.
      h.doc.querySelector('input')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
      expect(h.previous).toHaveBeenCalledTimes(1);

      // Clearing detaches the keydown listener.
      h.controller.clearKeyboardListeners();
      expect(h.controller.keyboardDocuments.size).toBe(0);

      h.doc.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
      expect(h.previous).toHaveBeenCalledTimes(1);
    } finally {
      h.controller.destroy();
      h.cleanupDom();
    }
  });

  it('holds and restores the page scroll offset across Android touch-cancel / selection reveals', () => {
    vi.useFakeTimers();
    const h = createHarness();
    try {
      h.controller.registerInteraction(h.contents);

      // Touch start pins the scroller offset.
      h.doc.body.dispatchEvent(
        new TouchEvent('touchstart', {
          bubbles: true,
          touches: [{ clientX: 100, clientY: 100 } as Touch],
        }),
      );

      // Browser tries to scroll the stage while selection handles drag.
      h.scroller.scrollLeft = 412;
      h.scroller.dispatchEvent(new Event('scroll'));
      expect(h.scroller.scrollLeft).toBe(240);

      // touchcancel schedules deferred release; if a selection is active, the hold stays.
      h.setAssistantSelection('selected text');
      h.doc.body.dispatchEvent(new TouchEvent('touchcancel', { bubbles: true }));
      vi.advanceTimersByTime(PAGE_HOLD_RELEASE_MS);

      h.scroller.scrollLeft = 500;
      h.scroller.dispatchEvent(new Event('scroll'));
      expect(h.scroller.scrollLeft).toBe(240);

      // Once selection clears and a new release finishes, scrolling is no longer pinned.
      h.setAssistantSelection(null);
      h.controller.schedulePageHoldRelease();
      vi.advanceTimersByTime(PAGE_HOLD_RELEASE_MS);

      h.scroller.scrollLeft = 500;
      h.scroller.dispatchEvent(new Event('scroll'));
      expect(h.scroller.scrollLeft).toBe(500);
    } finally {
      h.controller.destroy();
      h.cleanupDom();
    }
  });

  it('clears interaction listeners on destroy and detaches click handlers', () => {
    const h = createHarness();
    try {
      h.controller.registerInteraction(h.contents);
      expect(h.controller.interactionDocuments.size).toBe(1);

      h.doc.querySelector('p')!.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: 50, clientY: 50 }));
      expect(h.onSurfaceInteracted).toHaveBeenCalledTimes(1);

      h.controller.clearInteractionListeners();
      expect(h.controller.interactionDocuments.size).toBe(0);

      h.doc.querySelector('p')!.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: 50, clientY: 50 }));
      expect(h.onSurfaceInteracted).toHaveBeenCalledTimes(1);
    } finally {
      h.controller.destroy();
      h.cleanupDom();
    }
  });
});
