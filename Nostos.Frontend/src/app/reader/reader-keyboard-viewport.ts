/**
 * The on-screen keyboard while a book is open (#1270).
 *
 * `index.html` asks for `interactive-widget=resizes-content`, so on Android
 * Chrome the keyboard shrinks the layout viewport and with it the reader. An
 * EPUB re-paginates into the shorter box and re-displays its last seen
 * location, which in a long section is pages away; closing the keyboard does
 * it again. While the reader is mounted the keyboard overlays the page instead
 * (`resizes-visual`, what iOS Safari always does), and the docked selection
 * bar is lifted above it from `visualViewport`. Other screens keep the
 * document's own setting: it is put back when the reader goes.
 */

const INTERACTIVE_WIDGET = /(^|,)\s*interactive-widget\s*=\s*[^,]*/;

/** The viewport meta content with its `interactive-widget` set to `value`. */
export function withInteractiveWidget(content: string, value: string): string {
  if (INTERACTIVE_WIDGET.test(content)) {
    return content.replace(INTERACTIVE_WIDGET, (_match, lead: string) => `${lead}${lead ? ' ' : ''}interactive-widget=${value}`);
  }
  const trimmed = content.trim();
  return trimmed ? `${trimmed}, interactive-widget=${value}` : `interactive-widget=${value}`;
}

/**
 * How much of the layout viewport's bottom the keyboard covers, in whole
 * pixels: the layout height less the visible viewport's bottom edge. Zero
 * with no keyboard, and never negative (a pinch zoom can scroll the visual
 * viewport past the layout bottom by a fraction).
 */
export function keyboardInset(
  innerHeight: number,
  visual: { height: number; offsetTop: number } | null | undefined,
): number {
  if (!visual) return 0;
  const covered = innerHeight - (visual.height + visual.offsetTop);
  return covered > 0 ? Math.round(covered) : 0;
}

/**
 * While the reader is mounted: let the keyboard overlay the page, and keep
 * `--reader-keyboard-inset` on `host` at the height it covers. Returns the
 * teardown that restores the viewport meta and stops listening.
 */
export function overlayKeyboardWhileReading(host: HTMLElement, win: Window = window): () => void {
  const doc = win.document;
  const meta = doc.querySelector<HTMLMetaElement>('meta[name="viewport"]');
  const original = meta?.getAttribute('content') ?? null;
  if (meta && original !== null) {
    meta.setAttribute('content', withInteractiveWidget(original, 'resizes-visual'));
  }

  const visual = win.visualViewport ?? null;
  const update = () => {
    host.style.setProperty('--reader-keyboard-inset', `${keyboardInset(win.innerHeight, visual)}px`);
  };
  update();
  visual?.addEventListener('resize', update);
  visual?.addEventListener('scroll', update);

  return () => {
    visual?.removeEventListener('resize', update);
    visual?.removeEventListener('scroll', update);
    host.style.removeProperty('--reader-keyboard-inset');
    if (meta && original !== null) meta.setAttribute('content', original);
  };
}
