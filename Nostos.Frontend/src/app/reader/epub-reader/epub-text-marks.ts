// Nostos.Frontend/src/app/reader/epub-reader/epub-text-marks.ts
import { Contents, EpubCFI } from 'epubjs';
import type { Theme } from '../../core/services/theme.service';
import { HIGHLIGHT_COLOURS, HighlightColour, resolveHighlightFill } from '../highlight-colours';

/**
 * Highlights, search hits and reading marks painted by the page's own text,
 * through the CSS Custom Highlight API.
 *
 * epub.js draws a mark as SVG rectangles in a pane laid over the iframe: it
 * measures the range once and redraws only when the iframe changes size. Any
 * reflow that keeps the frame's size — a web font finishing its load, a text
 * size or typeface change, an image above loading late — re-breaks the lines
 * under rectangles that stay where they were, so a highlight sits on the wrong
 * words until a reload measures it again.
 *
 * A registered `Highlight` is a set of live DOM Ranges the browser paints as
 * part of laying out the text, so it follows every reflow by construction:
 * there is no measurement to go stale and nothing to redraw.
 */

/** Which layer a mark belongs to: a saved or offered pen, or one of the reader's own marks. */
export type TextMarkLayer =
  | { kind: 'pen'; colour: HighlightColour; noted?: boolean }
  | { kind: 'search' }
  | { kind: 'landing' }
  | { kind: 'read-aloud' };

/** A painted mark. Removed by identity, so two marks at one CFI never collide. */
export interface TextMark {
  readonly cfi: string;
  readonly name: string;
  readonly sectionIndex: number;
}

/** Window shape the API needs; absent in jsdom and in browsers that predate it. */
interface HighlightWindow {
  CSS?: { highlights?: Map<string, unknown> };
  Highlight?: new (...ranges: Range[]) => { priority: number };
}

/**
 * Whether this browser can paint marks into the text (Chrome 105, Safari 17.2,
 * Firefox 140). Without it the reader keeps epub.js's overlay.
 */
export function textMarksSupported(win: unknown = typeof window === 'undefined' ? undefined : window): boolean {
  const w = win as HighlightWindow | undefined;
  return !!w && typeof w.Highlight === 'function' && !!w.CSS?.highlights;
}

const NAME_PREFIX = 'nostos-mark-';

/** The registry name of a layer, also the `::highlight()` argument. */
export function textMarkName(layer: TextMarkLayer): string {
  if (layer.kind !== 'pen') return `${NAME_PREFIX}${layer.kind}`;
  return `${NAME_PREFIX}pen-${layer.colour}${layer.noted ? '-noted' : ''}`;
}

/** Later layers paint over earlier ones where they overlap: the reader's own marks stay visible on a highlight. */
const PRIORITY: Record<TextMarkLayer['kind'], number> = {
  pen: 0,
  'read-aloud': 1,
  landing: 2,
  search: 3,
};

/** The light fills from styles.css, for a document whose tokens cannot be read (unit tests). */
const PEN_FALLBACK: Record<HighlightColour, string> = {
  sage: '#b7e4cf',
  amber: '#ffda00',
  blue: '#bcd9f2',
  rose: '#f7c6d9',
};

const ACCENT_FALLBACK: Record<Theme, string> = { light: '#5B5E66', dark: '#a9cfc2' };

/**
 * A colour at an opacity. Hex is what the tokens hold; anything else is mixed,
 * which every browser with the Highlight API also supports.
 */
function withAlpha(colour: string, alpha: number): string {
  if (alpha >= 1) return colour;
  if (/^#[0-9a-f]{6}$/i.test(colour)) {
    return colour + Math.round(alpha * 255).toString(16).padStart(2, '0');
  }
  return `color-mix(in srgb, ${colour} ${Math.round(alpha * 100)}%, transparent)`;
}

function readAccent(theme: Theme): string {
  try {
    const value = getComputedStyle(document.documentElement).getPropertyValue('--color-accent').trim();
    if (value) return value;
  } catch {
    // Unreadable styles — fall through.
  }
  return ACCENT_FALLBACK[theme];
}

/**
 * The `::highlight()` rules for one theme. Pure apart from reading the app's
 * tokens. On paper a pen is drawn at full strength under dark ink, like a real
 * highlighter; on the dark page the same pastel at full strength would sit
 * under pale text and drown it, so it is laid down as a tint.
 */
export function textMarkCss(theme: Theme): string {
  const penAlpha = theme === 'dark' ? 0.35 : 1;
  const accent = readAccent(theme);
  const underline =
    `text-decoration-line:underline;text-decoration-color:${accent};` +
    'text-decoration-thickness:2px;text-underline-offset:0.18em;';

  // A highlight that carries a note is the same pen with a dotted rule under
  // it, so a reader can tell which marks have something to open.
  const noteRule =
    `text-decoration-line:underline;text-decoration-style:dotted;text-decoration-color:${accent};` +
    'text-decoration-thickness:2px;text-underline-offset:0.22em;';
  const pens = HIGHLIGHT_COLOURS.map(({ id }) => {
    const fill = `background-color:${withAlpha(resolveHighlightFill(id, PEN_FALLBACK[id]), penAlpha)};`;
    return (
      `::highlight(${textMarkName({ kind: 'pen', colour: id })}){${fill}}` +
      `::highlight(${textMarkName({ kind: 'pen', colour: id, noted: true })}){${fill}${noteRule}}`
    );
  }).join('');

  return (
    pens +
    `::highlight(${textMarkName({ kind: 'search' })}){${underline}}` +
    `::highlight(${textMarkName({ kind: 'landing' })}){${underline}}` +
    `::highlight(${textMarkName({ kind: 'read-aloud' })}){background-color:${withAlpha(accent, 0.14)};${underline}}`
  );
}

const STYLE_ID = 'nostos-text-marks';

export class EpubTextMarks {
  private readonly marks = new Set<TextMark>();
  /** Ranges resolved per document, so repainting one layer does not re-walk every CFI. */
  private readonly ranges = new WeakMap<Document, Map<string, Range | null>>();
  private theme: Theme = 'light';

  /** `rendered` lists the sections currently on screen. */
  constructor(private readonly rendered: () => Contents[]) {}

  add(cfi: string, layer: TextMarkLayer): TextMark {
    const mark: TextMark = { cfi, name: textMarkName(layer), sectionIndex: sectionOf(cfi) };
    this.marks.add(mark);
    this.repaint(mark.name);
    return mark;
  }

  remove(mark: TextMark | null | undefined): void {
    if (!mark || !this.marks.delete(mark)) return;
    this.repaint(mark.name);
  }

  /**
   * Prepare a newly rendered section: its stylesheet and every mark that falls
   * in it. Called from `rendition.hooks.content`, before the section is shown.
   */
  attach(contents: Contents): void {
    this.writeStyle(contents.document);
    this.paint(contents, null);
  }

  /** The page changed theme: the tint each pen is laid down at changes with it. */
  setTheme(theme: Theme): void {
    if (theme === this.theme) return;
    this.theme = theme;
    for (const contents of this.rendered()) this.writeStyle(contents.document);
  }

  clear(): void {
    const names = new Set([...this.marks].map((mark) => mark.name));
    this.marks.clear();
    for (const name of names) this.repaint(name);
  }

  private repaint(name: string): void {
    for (const contents of this.rendered()) this.paint(contents, name);
  }

  /** Rebuild one layer (or, with `null`, every layer) in one section. */
  private paint(contents: Contents, only: string | null): void {
    const win = contents?.window as unknown as HighlightWindow | undefined;
    const registry = win?.CSS?.highlights;
    const HighlightCtor = win?.Highlight;
    if (!registry || !HighlightCtor || !contents.document) return;

    const byName = new Map<string, Range[]>();
    if (only) byName.set(only, []);
    for (const mark of this.marks) {
      if (only && mark.name !== only) continue;
      if (mark.sectionIndex !== contents.sectionIndex) continue;
      const range = this.rangeFor(contents, mark.cfi);
      if (!range) continue;
      const list = byName.get(mark.name) ?? [];
      list.push(range);
      byName.set(mark.name, list);
    }

    for (const [name, list] of byName) {
      if (list.length === 0) {
        registry.delete(name);
        continue;
      }
      const highlight = new HighlightCtor(...list);
      highlight.priority = PRIORITY[layerKind(name)];
      registry.set(name, highlight);
    }
  }

  private rangeFor(contents: Contents, cfi: string): Range | null {
    let cache = this.ranges.get(contents.document);
    if (!cache) {
      cache = new Map();
      this.ranges.set(contents.document, cache);
    }
    if (!cache.has(cfi)) {
      let range: Range | null = null;
      try {
        const resolved = contents.range(cfi);
        range = resolved && !resolved.collapsed ? resolved : null;
      } catch {
        // A CFI this edition's markup cannot resolve paints nothing rather than throwing.
      }
      cache.set(cfi, range);
    }
    return cache.get(cfi) ?? null;
  }

  private writeStyle(doc: Document | undefined): void {
    try {
      if (!doc?.head) return;
      let style = doc.getElementById(STYLE_ID);
      if (!style) {
        style = doc.createElement('style');
        style.id = STYLE_ID;
        doc.head.appendChild(style);
      }
      style.textContent = textMarkCss(this.theme);
    } catch {
      // A section mid-teardown has no head to write to — skip it.
    }
  }
}

/** The spine position a CFI points into; -1 when it cannot be parsed. */
export function sectionOf(cfi: string): number {
  try {
    return new EpubCFI(cfi).spinePos;
  } catch {
    return -1;
  }
}

function layerKind(name: string): TextMarkLayer['kind'] {
  const rest = name.slice(NAME_PREFIX.length);
  return rest.startsWith('pen-') ? 'pen' : (rest as TextMarkLayer['kind']);
}
