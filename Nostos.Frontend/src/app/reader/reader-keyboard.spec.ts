import {
  isInteractiveTarget,
  isTypingTarget,
  pageActionForKey,
  shouldSuppressTouchClick,
  surfaceActionForPoint,
  swipePageAction,
  touchClickSuppressionForMovement,
} from './reader-keyboard';

/**
 * The page-key binding is shared by the shell (document-level) and the EPUB
 * reader (inside the contents iframe), so its rules are asserted once here.
 * Issue #225 §1.5: the reader had no keyboard navigation at all, despite
 * `_docs/reader-system.md` claiming arrow-key paging.
 */
describe('reader keyboard bindings (issue #225 §1.5)', () => {
  it('maps the conventional page keys', () => {
    expect(pageActionForKey({ key: 'ArrowRight' })).toBe('next');
    expect(pageActionForKey({ key: 'PageDown' })).toBe('next');
    expect(pageActionForKey({ key: 'ArrowLeft' })).toBe('previous');
    expect(pageActionForKey({ key: 'PageUp' })).toBe('previous');
  });

  it('uses Space to advance and Shift+Space to go back', () => {
    expect(pageActionForKey({ key: ' ', shiftKey: false })).toBe('next');
    expect(pageActionForKey({ key: ' ', shiftKey: true })).toBe('previous');
    // Older/edge browsers still report the legacy key name.
    expect(pageActionForKey({ key: 'Spacebar' })).toBe('next');
  });

  it('ignores every other key', () => {
    for (const key of ['a', 'Enter', 'Escape', 'Home', 'End', 'Tab', 'F1']) {
      expect(pageActionForKey({ key })).toBeNull();
    }
  });

  it('leaves Space to interactive controls instead of paging', () => {
    const button = document.createElement('button');
    const icon = document.createElement('span');
    button.appendChild(icon);
    expect(isInteractiveTarget(button)).toBe(true);
    expect(isInteractiveTarget(icon)).toBe(true);

    const link = document.createElement('a');
    link.href = '/library';
    expect(isInteractiveTarget(link)).toBe(true);

    for (const tag of ['label', 'summary', 'audio', 'video'] as const) {
      expect(isInteractiveTarget(document.createElement(tag))).toBe(true);
    }

    const authored = document.createElement('div');
    authored.setAttribute('onclick', 'void 0');
    expect(isInteractiveTarget(authored)).toBe(true);
    expect(isInteractiveTarget(document.createElement('div'))).toBe(false);
  });

  it('treats text-entry surfaces as typing targets', () => {
    expect(isTypingTarget(document.createElement('input'))).toBe(true);
    expect(isTypingTarget(document.createElement('textarea'))).toBe(true);
    expect(isTypingTarget(document.createElement('select'))).toBe(true);

    const editable = document.createElement('div');
    Object.defineProperty(editable, 'isContentEditable', { value: true });
    expect(isTypingTarget(editable)).toBe(true);

    expect(isTypingTarget(document.createElement('button'))).toBe(false);
    expect(isTypingTarget(document.createElement('div'))).toBe(false);
    expect(isTypingTarget(null)).toBe(false);
  });

  it('gives selection and authored controls priority over immersive surface taps', () => {
    const link = document.createElement('a');
    link.href = '/chapter';

    expect(surfaceActionForPoint({
      target: document.createElement('p'),
      selectedText: 'selected words',
      clientX: 195,
      width: 390,
      coarsePointer: true,
      edgePaging: true,
    })).toBeNull();

    expect(surfaceActionForPoint({
      target: link,
      clientX: 195,
      width: 390,
      coarsePointer: true,
      edgePaging: true,
    })).toBeNull();
  });

  it('uses coarse edge taps for paging and the centre for chrome', () => {
    const p = document.createElement('p');
    expect(surfaceActionForPoint({
      target: p,
      clientX: 20,
      width: 390,
      coarsePointer: true,
      edgePaging: true,
    })).toBe('previous');
    expect(surfaceActionForPoint({
      target: p,
      clientX: 370,
      width: 390,
      coarsePointer: true,
      edgePaging: true,
    })).toBe('next');
    expect(surfaceActionForPoint({
      target: p,
      clientX: 195,
      width: 390,
      coarsePointer: true,
      edgePaging: true,
    })).toBe('toggle-chrome');
    expect(surfaceActionForPoint({
      target: p,
      clientX: 20,
      width: 390,
      coarsePointer: false,
      edgePaging: true,
    })).toBe('toggle-chrome');
  });

  it('suppresses only the synthetic click near a moved touch, never a later unrelated tap', () => {
    const suppression = touchClickSuppressionForMovement({
      startX: 300,
      startY: 200,
      endX: 160,
      endY: 205,
      nowMs: 1_000,
    });
    expect(suppression).not.toBeNull();
    expect(shouldSuppressTouchClick(suppression, 162, 207, 1_100)).toBe(true);
    expect(shouldSuppressTouchClick(suppression, 20, 20, 1_100)).toBe(false);
    expect(shouldSuppressTouchClick(suppression, 162, 207, 1_800)).toBe(false);

    expect(touchClickSuppressionForMovement({
      startX: 100,
      startY: 100,
      endX: 105,
      endY: 104,
      nowMs: 1_000,
    })).toBeNull();
  });

  it('accepts only deliberate horizontal swipes as page turns', () => {
    const p = document.createElement('p');
    expect(swipePageAction({
      target: p, startX: 320, startY: 200, endX: 180, endY: 210, durationMs: 260,
    })).toBe('next');
    expect(swipePageAction({
      target: p, startX: 120, startY: 200, endX: 250, endY: 205, durationMs: 300,
    })).toBe('previous');
    expect(swipePageAction({
      target: p, startX: 200, startY: 100, endX: 210, endY: 240, durationMs: 220,
    })).toBeNull();
    expect(swipePageAction({
      target: p, selectedText: 'hold', startX: 320, startY: 200, endX: 180, endY: 210, durationMs: 260,
    })).toBeNull();
  });
});


describe('PDF.js focusable reading surface', () => {
  function layer() {
    const surface = document.createElement('div');
    surface.className = 'textLayer';
    surface.tabIndex = 0;
    const text = document.createElement('span');
    surface.append(text);
    return { surface, text };
  }

  it('allows a neutral tap and paged swipe on the text layer', () => {
    const { text } = layer();
    expect(isInteractiveTarget(text)).toBe(true);
    expect(surfaceActionForPoint({
      target: text, focusableSurface: '.textLayer', clientX: 195,
      width: 390, coarsePointer: true, edgePaging: false,
    })).toBe('toggle-chrome');
    expect(swipePageAction({
      target: text, focusableSurface: '.textLayer', startX: 280,
      startY: 200, endX: 100, endY: 210, durationMs: 200,
    })).toBe('next');
  });

  it('preserves selection, links, and nested focusable controls', () => {
    const { surface, text } = layer();
    const point = { focusableSurface: '.textLayer', clientX: 195,
      width: 390, coarsePointer: true, edgePaging: false };
    expect(surfaceActionForPoint({ ...point, target: text, selectedText: 'a passage' })).toBeNull();
    const link = document.createElement('a');
    link.href = 'https://example.com';
    surface.append(link);
    expect(surfaceActionForPoint({ ...point, target: link })).toBeNull();
    const control = document.createElement('div');
    control.tabIndex = 0;
    surface.append(control);
    expect(surfaceActionForPoint({ ...point, target: control })).toBeNull();
  });
});
