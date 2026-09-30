import { Injector } from '@angular/core';
import { Rendition, Contents } from 'epubjs';
import { of, throwError } from 'rxjs';
import type { Mock } from 'vitest';
import { EpubAnnotationManager, isMouseContextMenu } from './epub-annotation-manager';

/**
 * Mobile native-callout suppression + completed selection capture (issues #16, #304).
 * Mirrors the "Minimal Section A specs" from the expert design: 13 specs.
 */

const CFI = 'epubcfi(/6/4!/4/2/1:0)';

function makeContents(doc: Document = document): Contents {
  return {
    document: doc,
    window: window,
    cfiFromRange: vi.fn(() => CFI),
  } as unknown as Contents;
}

function createRendition() {
  const registeredContents: Contents[] = [];
  const views: Array<{ index: number; pane: { removeMark: ReturnType<typeof vi.fn> } }> = [
    { index: 0, pane: { removeMark: vi.fn() } },
  ];
  /**
   * epub.js's `rendition.views()` is a `Views` COLLECTION, not an array:
   * `all()` is its array accessor, and the object is not iterable. This double
   * has to keep that shape. Stubbing it as a plain array is exactly how the
   * `for…of` regression in `removeAnnotation()` stayed green while every
   * highlight save threw in the browser (issue #225 §1.1).
   */
  const viewsCollection = {
    length: views.length,
    all: vi.fn(() => views),
    forEach: (cb: (view: (typeof views)[number]) => void) => views.forEach(cb),
    get: (i: number) => views[i],
  };
  const annotations = {
    highlight: vi.fn((cfiRange: string) => ({
      type: 'highlight',
      cfiRange,
      sectionIndex: 0,
      mark: { element: document.createElement('span') },
    })),
    add: vi.fn(),
    remove: vi.fn(),
  };
  const rendition = {
    hooks: { content: { register: vi.fn() } },
    on: vi.fn(),
    off: vi.fn(),
    annotations,
    getContents: vi.fn(() => [...registeredContents]),
    views: vi.fn(() => viewsCollection),
    getRange: vi.fn(),
  };
  return { rendition, annotations, views, viewsCollection, registeredContents };
}

/** Selects the given text in the test document via the real jsdom Selection. */
function selectText(text: string) {
  document.body.textContent = text;
  const range = document.createRange();
  range.selectNodeContents(document.body);
  const selection = window.getSelection()!;
  selection.removeAllRanges();
  selection.addRange(range);
}

function selectWhitespaceOnly() {
  document.body.textContent = '   ';
  const range = document.createRange();
  range.selectNodeContents(document.body);
  const selection = window.getSelection()!;
  selection.removeAllRanges();
  selection.addRange(range);
}

function collapseSelection() {
  window.getSelection()?.collapse(document.body, 0);
}

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

describe('EpubAnnotationManager mobile highlight mode (issue #16)', () => {
  let manager: EpubAnnotationManager;
  let rendition: ReturnType<typeof createRendition>['rendition'];
  let annotations: ReturnType<typeof createRendition>['annotations'];
  let views: ReturnType<typeof createRendition>['views'];
  let viewsCollection: ReturnType<typeof createRendition>['viewsCollection'];
  let registeredContents: ReturnType<typeof createRendition>['registeredContents'];
  let notesService: { create: Mock<() => unknown> };
  let onNoteCreated: Mock<() => void>;
  let onCommitFailed: Mock<() => void>;
  let onSelectionCaptured: Mock<(text: string) => void>;

  beforeEach(() => {
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
      cb(0);
      return 1;
    });

    const r = createRendition();
    rendition = r.rendition;
    annotations = r.annotations;
    views = r.views;
    viewsCollection = r.viewsCollection;
    registeredContents = r.registeredContents;

    notesService = { create: vi.fn(() => of({ id: 'n1' })) };
    onNoteCreated = vi.fn();
    onCommitFailed = vi.fn();
    onSelectionCaptured = vi.fn();

    manager = new EpubAnnotationManager(
      rendition as unknown as Rendition,
      'book-1',
      { get: () => notesService } as unknown as Injector,
      onNoteCreated,
      onCommitFailed,
    );
    manager.setOnSelectionCaptured(onSelectionCaptured);
  });

  afterEach(() => {
    manager.destroy();
    window.getSelection()?.removeAllRanges();
    document.body.innerHTML = '';
    vi.unstubAllGlobals();
  });

  it('adds nostos-highlight-mode to every current iframe body when mode turns on', () => {
    const docA = document.implementation.createHTMLDocument('doc-a');
    const docB = document.implementation.createHTMLDocument('doc-b');
    manager.registerContents(makeContents(docA));
    manager.registerContents(makeContents(docB));
    registeredContents.push(makeContents(docA), makeContents(docB));

    expect(docA.body!.classList.contains('nostos-highlight-mode')).toBe(false);
    expect(docB.body!.classList.contains('nostos-highlight-mode')).toBe(false);

    manager.setHighlightMode(true);

    expect(docA.body!.classList.contains('nostos-highlight-mode')).toBe(true);
    expect(docB.body!.classList.contains('nostos-highlight-mode')).toBe(true);
  });

  it('applies the active mode to a newly rendered contents document', () => {
    manager.setHighlightMode(true);

    const doc = document.implementation.createHTMLDocument('doc-new');
    manager.registerContents(makeContents(doc));

    expect(doc.body!.classList.contains('nostos-highlight-mode')).toBe(true);
  });

  it('removes nostos-highlight-mode when mode turns off', () => {
    const doc = document.implementation.createHTMLDocument('doc');
    const contents = makeContents(doc);
    manager.registerContents(contents);
    registeredContents.push(contents);
    manager.setHighlightMode(true);
    expect(doc.body!.classList.contains('nostos-highlight-mode')).toBe(true);

    manager.setHighlightMode(false);

    expect(doc.body!.classList.contains('nostos-highlight-mode')).toBe(false);
  });

  it('prevents contextmenu only while highlight mode is on', () => {
    const doc = document.implementation.createHTMLDocument('doc');
    manager.registerContents(makeContents(doc));

    const fire = () => {
      const event = new Event('contextmenu', { cancelable: true });
      doc.dispatchEvent(event);
      return event;
    };

    expect(fire().defaultPrevented).toBe(false);

    manager.setHighlightMode(true);
    expect(fire().defaultPrevented).toBe(true);

    manager.setHighlightMode(false);
    expect(fire().defaultPrevented).toBe(false);
  });

  it('waits for mouseup before capturing the final drag range (issue #304)', async () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());

    const text = 'Some meaningful text across multiple words';
    document.body.textContent = text;
    const textNode = document.body.firstChild!;
    const selection = window.getSelection()!;
    const range = document.createRange();
    range.setStart(textNode, 0);
    range.setEnd(textNode, 4);
    selection.removeAllRanges();
    selection.addRange(range);

    // A real drag produces selectionchange repeatedly while the range grows.
    // Neither the first partial range nor a later extension may be captured.
    document.dispatchEvent(new Event('selectionchange'));
    await flush();
    expect(annotations.highlight).not.toHaveBeenCalled();
    expect(onSelectionCaptured).not.toHaveBeenCalled();

    range.setEnd(textNode, text.length);
    selection.removeAllRanges();
    selection.addRange(range);
    document.dispatchEvent(new Event('selectionchange'));
    await flush();

    expect(annotations.highlight).not.toHaveBeenCalled();
    expect(selection.toString()).toBe(text);

    // The completed desktop gesture is the point at which Nostos may turn the
    // native range into a pending highlight and clear the browser selection.
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();

    expect(annotations.highlight).toHaveBeenCalledTimes(1);
    expect(onSelectionCaptured).toHaveBeenCalledWith(text, null);
    expect(window.getSelection()?.rangeCount).toBe(0);
  });

  it('captures a non-collapsed iframe selection as one pending highlight', async () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());

    selectText('Some meaningful text');
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();

    expect(annotations.highlight).toHaveBeenCalledTimes(1);
    expect(annotations.highlight).toHaveBeenCalledWith(
      CFI,
      { nostosPending: true },
      undefined,
      'epubjs-hl-pending',
      { fill: expect.any(String) },
    );
    expect(onSelectionCaptured).toHaveBeenCalledWith('Some meaningful text', null);
  });

  it('captures via touchend fallback when epub.js selected never fires', async () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());

    selectText('Fallback text');
    document.dispatchEvent(new Event('touchend'));
    await flush();

    expect(annotations.highlight).toHaveBeenCalledTimes(1);
    expect(onSelectionCaptured).toHaveBeenCalledWith('Fallback text', null);
  });

  it('deduplicates fallback capture against a later epub.js selected event', async () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());
    manager.init();

    selectText('Dedup text');
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();
    expect(annotations.highlight).toHaveBeenCalledTimes(1);

    // epub.js finally fires `selected` for the same selection.
    selectText('Dedup text');
    const selectedHandler = rendition.on.mock.calls.find(([type]) => type === 'selected')?.[1] as (
      cfiRange: string,
      contents: Contents,
    ) => void;
    selectedHandler(CFI, makeContents());

    expect(annotations.highlight).toHaveBeenCalledTimes(1);
    expect(onSelectionCaptured).toHaveBeenCalledTimes(1);
  });

  it('ignores collapsed and whitespace-only selections', async () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());

    selectText('Word');
    collapseSelection();
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();

    selectWhitespaceOnly();
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();

    expect(annotations.highlight).not.toHaveBeenCalled();
    expect(onSelectionCaptured).not.toHaveBeenCalled();
  });

  it('clears native ranges immediately after capturing', async () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());

    selectText('Temp annotation text');
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();

    expect(annotations.highlight).toHaveBeenCalledTimes(1);
    expect(window.getSelection()?.rangeCount).toBe(0);
  });

  it('cancel removes only the temporary annotation', async () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());

    selectText('Cancel me');
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();
    expect(annotations.highlight).toHaveBeenCalledTimes(1);

    manager.discardHighlight();

    expect(views[0].pane.removeMark).toHaveBeenCalledTimes(1);
    expect(annotations.add).not.toHaveBeenCalled();
    expect(onNoteCreated).not.toHaveBeenCalled();

    // Pending state was cleared: a new selection can be captured again.
    selectText('New selection');
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();
    expect(annotations.highlight).toHaveBeenCalledTimes(2);
  });

  it('successful save adds one persisted annotation and removes the temporary one', async () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());

    selectText('Save me');
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();
    expect(annotations.highlight).toHaveBeenCalledTimes(1);

    const result = await manager.commitHighlight();

    expect(result).toBe(true);
    // Permanent annotation is added first, then the temporary is removed by object.
    expect(annotations.add).toHaveBeenCalledTimes(1);
    expect(annotations.add).toHaveBeenCalledWith(
      'highlight',
      CFI,
      {},
      undefined,
      undefined,
      { fill: expect.any(String) },
    );
    expect(annotations.add.mock.invocationCallOrder[0]).toBeLessThan(
      views[0].pane.removeMark.mock.invocationCallOrder[0],
    );
    expect(views[0].pane.removeMark).toHaveBeenCalledTimes(1);
    expect(notesService.create).toHaveBeenCalledWith('book-1', {
      content: '',
      cfiRange: CFI,
      selectedText: 'Save me',
    });
    expect(onNoteCreated).toHaveBeenCalledTimes(1);
    expect(manager.highlights()).toEqual([CFI]);
  });

  it('failed save retains the pending highlight and its visual feedback', async () => {
    notesService.create.mockReturnValue(throwError(() => new Error('api down')));
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());

    selectText('Keep me');
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();
    expect(annotations.highlight).toHaveBeenCalledTimes(1);

    const result = await manager.commitHighlight();

    expect(result).toBe(false);
    expect(onCommitFailed).toHaveBeenCalledTimes(1);
    expect(onNoteCreated).not.toHaveBeenCalled();
    // No permanent annotation, temporary annotation still attached.
    expect(annotations.add).not.toHaveBeenCalled();
    expect(views[0].pane.removeMark).not.toHaveBeenCalled();

    // Pending state survived: cancel still removes the temporary annotation.
    manager.discardHighlight();
    expect(views[0].pane.removeMark).toHaveBeenCalledTimes(1);
  });

  it('destroy removes document listeners and the temporary annotation', async () => {
    const removeListenerSpy = vi.spyOn(document, 'removeEventListener');
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());
    manager.init();

    selectText('Destroy me');
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();
    expect(views[0].pane.removeMark).not.toHaveBeenCalled();

    manager.destroy();

    expect(views[0].pane.removeMark).toHaveBeenCalledTimes(1);
    // contextmenu + mouseup + touchend listeners are removed.
    // Every listener registerContents adds is removed, by name.
    expect(removeListenerSpy.mock.calls.map((call) => call[0]).sort()).toEqual(
      ['contextmenu', 'mousedown', 'mousemove', 'mouseup', 'touchend'],
    );
    expect(rendition.off).toHaveBeenCalledWith('selected', expect.any(Function));
  });

  /**
   * Regression, issue #225 §1.1. The double used to be a plain ARRAY, so
   * `for…of` over `views()` passed here and threw `TypeError: … is not
   * iterable` in the browser: the note was persisted, but the save bar stayed
   * open, the notes panel went stale and the pending mark was never removed.
   */
  it('removes the pending mark through the Views collection, not by iterating it', async () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());

    selectText('Persist me');
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();

    const result = await manager.commitHighlight();

    expect(result).toBe(true);
    expect(viewsCollection.all).toHaveBeenCalled();
    expect(views[0].pane.removeMark).toHaveBeenCalledTimes(1);
    expect(onNoteCreated).toHaveBeenCalledTimes(1);
  });

  it('still commits when the rendition exposes no Views.all() accessor', async () => {
    // A future epub.js may shape the collection differently. The removal is
    // skipped, but the save must still complete and report success — a throw
    // here is what left the confirmation bar stuck.
    (rendition as unknown as { views: () => unknown }).views = () => ({});

    manager.setHighlightMode(true);
    manager.registerContents(makeContents());

    selectText('No accessor');
    document.dispatchEvent(new MouseEvent('mouseup'));
    await flush();

    const result = await manager.commitHighlight();

    expect(result).toBe(true);
    expect(onNoteCreated).toHaveBeenCalledTimes(1);
  });

  it('takes the highlight fill from --color-highlight (issue #225 §1.6)', async () => {
    const computed = vi
      .spyOn(window, 'getComputedStyle')
      .mockReturnValue({
        getPropertyValue: () => ' #123456 ',
      } as unknown as CSSStyleDeclaration);
    try {
      manager.setHighlightMode(true);
      manager.registerContents(makeContents());
      selectText('Token colour');
      document.dispatchEvent(new MouseEvent('mouseup'));
      await flush();
      await manager.commitHighlight();

      expect(annotations.highlight).toHaveBeenCalledWith(
        CFI,
        { nostosPending: true },
        undefined,
        'epubjs-hl-pending',
        { fill: '#123456' },
      );
      expect(annotations.add).toHaveBeenCalledWith(
        'highlight',
        CFI,
        {},
        undefined,
        undefined,
        { fill: '#123456' },
      );
    } finally {
      computed.mockRestore();
    }
  });

  it('falls back to the token light value when the token cannot be read', async () => {
    const computed = vi
      .spyOn(window, 'getComputedStyle')
      .mockReturnValue({ getPropertyValue: () => '' } as unknown as CSSStyleDeclaration);
    try {
      manager.setHighlightMode(true);
      manager.registerContents(makeContents());
      selectText('Fallback colour');
      document.dispatchEvent(new MouseEvent('mouseup'));
      await flush();
      await manager.commitHighlight();

      // Never epub.js's raw `yellow` keyword.
      expect(annotations.add).toHaveBeenCalledWith(
        'highlight',
        CFI,
        {},
        undefined,
        undefined,
        { fill: '#ffda00' },
      );
    } finally {
      computed.mockRestore();
    }
  });
});


/**
 * In-text selection actions (#650): right-click on a selection opens the menu
 * regardless of highlight mode, mouse only; the note text is saved with the
 * mark; the anchor is measured in the page's viewport. Uses the same fakes as
 * the capture suite above so the pipeline under test is the production one.
 */
describe('EpubAnnotationManager selection actions (#650)', () => {
  let manager: EpubAnnotationManager;
  let annotations: ReturnType<typeof createRendition>['annotations'];
  let notesService: { create: Mock<(...args: unknown[]) => unknown> };
  let onSelectionCaptured: Mock<(...args: unknown[]) => void>;

  beforeEach(() => {
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
      cb(0);
      return 1;
    });
    const r = createRendition();
    annotations = r.annotations;
    notesService = { create: vi.fn(() => of({ id: 'n1' })) };
    onSelectionCaptured = vi.fn();
    manager = new EpubAnnotationManager(
      r.rendition as unknown as Rendition,
      'book-1',
      { get: () => notesService } as unknown as Injector,
      vi.fn(),
      vi.fn(),
    );
    manager.setOnSelectionCaptured(onSelectionCaptured);
  });

  afterEach(() => {
    manager.destroy();
    window.getSelection()?.removeAllRanges();
    document.body.innerHTML = '';
    vi.unstubAllGlobals();
  });

  function rightClick(init: { pointerType?: string } = {}): Event {
    const event = new Event('contextmenu', { cancelable: true });
    if (init.pointerType !== undefined) {
      Object.defineProperty(event, 'pointerType', { value: init.pointerType });
    }
    document.dispatchEvent(event);
    return event;
  }

  it('a mouse right-click on a selection captures it without highlight mode and replaces the native menu', () => {
    manager.registerContents(makeContents());
    selectText('A passage worth a note');

    const event = rightClick({ pointerType: 'mouse' });

    expect(event.defaultPrevented).toBe(true);
    expect(annotations.highlight).toHaveBeenCalledTimes(1);
    expect(onSelectionCaptured).toHaveBeenCalledTimes(1);
    expect(onSelectionCaptured.mock.calls[0][0]).toBe('A passage worth a note');
  });

  it('a right-click with nothing selected keeps the native menu', () => {
    manager.registerContents(makeContents());
    collapseSelection();

    const event = rightClick({ pointerType: 'mouse' });

    expect(event.defaultPrevented).toBe(false);
    expect(annotations.highlight).not.toHaveBeenCalled();
  });

  it('a touch long-press contextmenu is left to the platform (#16)', () => {
    manager.registerContents(makeContents());
    selectText('A passage selected by long-press');

    const event = rightClick({ pointerType: 'touch' });

    expect(event.defaultPrevented).toBe(false);
    expect(annotations.highlight).not.toHaveBeenCalled();
    expect(onSelectionCaptured).not.toHaveBeenCalled();
    expect(window.getSelection()?.toString()).toBe('A passage selected by long-press');
  });

  it('a touch-first device never takes over contextmenu, even without pointerType', () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(pointer: coarse)' }));
    manager.registerContents(makeContents());
    selectText('A passage on a phone');

    const event = rightClick();

    expect(event.defaultPrevented).toBe(false);
    expect(annotations.highlight).not.toHaveBeenCalled();
  });

  it('does not double-capture when mouseup already captured in highlight mode', () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());
    selectText('Captured on mouseup first');

    document.dispatchEvent(new Event('mouseup'));
    rightClick({ pointerType: 'mouse' });

    expect(annotations.highlight).toHaveBeenCalledTimes(1);
    expect(onSelectionCaptured).toHaveBeenCalledTimes(1);
  });

  it('highlight-mode completion signals still do nothing while the mode is off', () => {
    manager.registerContents(makeContents());
    selectText('Just reading, not marking');

    document.dispatchEvent(new Event('mouseup'));
    document.dispatchEvent(new Event('touchend'));

    expect(annotations.highlight).not.toHaveBeenCalled();
    expect(window.getSelection()?.toString()).toBe('Just reading, not marking');
  });

  it('saves the typed note with the mark through the same commit', async () => {
    manager.registerContents(makeContents());
    selectText('The passage');
    rightClick({ pointerType: 'mouse' });

    await expect(manager.commitHighlight('My thought about it')).resolves.toBe(true);

    expect(notesService.create).toHaveBeenCalledWith('book-1', {
      content: 'My thought about it',
      cfiRange: CFI,
      selectedText: 'The passage',
    });
    expect(annotations.add).toHaveBeenCalledTimes(1);
  });

  it('a plain highlight still saves empty note content', async () => {
    manager.setHighlightMode(true);
    manager.registerContents(makeContents());
    selectText('Only a mark');
    document.dispatchEvent(new Event('mouseup'));

    await manager.commitHighlight();

    expect(notesService.create).toHaveBeenCalledWith('book-1', expect.objectContaining({ content: '' }));
  });

  it('reports the selection box in the page viewport, offset by the iframe', () => {
    const contents = {
      ...makeContents(),
      window: {
        getSelection: () => ({
          rangeCount: 1,
          isCollapsed: false,
          toString: () => 'Measured text',
          getRangeAt: () => ({
            cloneRange: () => ({}),
            getBoundingClientRect: () => ({ top: 10, bottom: 30, left: 40, right: 140, width: 100, height: 20 }),
          }),
          removeAllRanges: vi.fn(),
        }),
        frameElement: { getBoundingClientRect: () => ({ top: 100, left: 200 }) },
        matchMedia: () => ({ matches: false }),
      },
    } as unknown as Contents;
    manager.registerContents(contents);

    rightClick({ pointerType: 'mouse' });

    expect(onSelectionCaptured).toHaveBeenCalledWith('Measured text', {
      top: 110,
      bottom: 130,
      left: 240,
      right: 340,
    });
  });

  it('isMouseContextMenu accepts mice on fine pointers only', () => {
    const fine = { matchMedia: () => ({ matches: false }) } as unknown as Window;
    const coarse = { matchMedia: () => ({ matches: true }) } as unknown as Window;
    const withType = (pointerType: string) => {
      const event = new Event('contextmenu');
      Object.defineProperty(event, 'pointerType', { value: pointerType });
      return event;
    };

    expect(isMouseContextMenu(withType('mouse'), fine)).toBe(true);
    expect(isMouseContextMenu(new Event('contextmenu'), fine)).toBe(true);
    expect(isMouseContextMenu(withType('touch'), fine)).toBe(false);
    expect(isMouseContextMenu(withType('pen'), fine)).toBe(false);
    expect(isMouseContextMenu(withType('mouse'), coarse)).toBe(false);
  });

  it('ignores the debounced epub.js selected event mid-drag; mouseup captures the full range', () => {
    manager.setHighlightMode(true);
    const contents = makeContents();
    manager.registerContents(contents);
    manager.init();
    const selectedHandler = (manager as unknown as { selectedHandler: (cfi: string, c: Contents) => void })
      .selectedHandler;

    const text = 'The whole dragged passage here';
    document.body.textContent = text;
    const node = document.body.firstChild!;
    const range = document.createRange();
    range.setStart(node, 0);
    range.setEnd(node, 3);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);

    document.dispatchEvent(new MouseEvent('mousedown', { button: 0 }));
    // epub.js's debounce fires while the button is still held: ignored.
    selectedHandler(CFI, contents);
    expect(annotations.highlight).not.toHaveBeenCalled();
    expect(window.getSelection()?.toString()).toBe('The');

    range.setEnd(node, text.length);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);
    document.dispatchEvent(new MouseEvent('mouseup', { button: 0 }));

    expect(annotations.highlight).toHaveBeenCalledTimes(1);
    expect(onSelectionCaptured.mock.calls[0][0]).toBe(text);
  });

  it('a touch selection still captures through the epub.js selected event', () => {
    manager.setHighlightMode(true);
    const contents = makeContents();
    manager.registerContents(contents);
    manager.init();
    const selectedHandler = (manager as unknown as { selectedHandler: (cfi: string, c: Contents) => void })
      .selectedHandler;
    selectText('Selected with the handles');

    selectedHandler(CFI, contents);

    expect(annotations.highlight).toHaveBeenCalledTimes(1);
  });

  it('a drag released outside the iframe cannot leave the drag state stuck', () => {
    manager.setHighlightMode(true);
    const contents = makeContents();
    manager.registerContents(contents);
    manager.init();
    const selectedHandler = (manager as unknown as { selectedHandler: (cfi: string, c: Contents) => void })
      .selectedHandler;
    selectText('Released elsewhere');

    document.dispatchEvent(new MouseEvent('mousedown', { button: 0 }));
    document.dispatchEvent(new MouseEvent('mousemove', { buttons: 0 }));
    selectedHandler(CFI, contents);

    expect(annotations.highlight).toHaveBeenCalledTimes(1);
  });
});
