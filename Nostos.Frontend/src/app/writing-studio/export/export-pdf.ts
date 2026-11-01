import { STUDIO_FONTS } from '../page-settings/studio-preferences.service';
import { escapeHtml, renderExportBody } from './export-html';
import type { ExportDocument } from './export-model';

/**
 * PDF through a print layout (issue #1167). The resolved document is laid out
 * for paper in a hidden frame and printed with the browser's own print
 * dialog, where "Save as PDF" makes the file. Nothing is sent anywhere.
 *
 * Browsers can't float a note to the foot of its page (CSS `float: footnote`
 * is only in print engines), so a writing with notes is paginated in script by
 * Paged.js (#1306), which runs inside the frame and loads only for such a
 * writing. That is limited to Chromium-based browsers on purpose: measured on
 * Paged.js 0.4.3 with a 212-page book, Chromium put 300 of 300 notes on their
 * page with no text lost or repeated, while Firefox repeated the text and the
 * note on two pages for 30 of the 300. Safari is unmeasured. Every other
 * browser, and any layout Paged.js can't finish, gets notes at the end of each
 * chapter, numbered through the whole document.
 *
 * Page numbers come from the page's bottom margin (`@page @bottom-center`),
 * which Chromium-based browsers print. Where a browser doesn't, its own print
 * dialog's "Headers and footers" option numbers the pages.
 */

export interface PrintLayoutOptions {
  /** `@font-face` rules for the writing's font, ready to paste into the layout. */
  fontFaces?: string;
  /** `page` needs Paged.js to run on the layout; `chapter`, the default, prints as it is. */
  notes?: 'chapter' | 'page';
}

/** Tall images stop at this share of the text block, so one never has a page to itself and a caption after it. */
const IMAGE_MAX_HEIGHT = 0.8;

/** Copied from the package by `angular.json`, and fetched only when a writing has notes. */
const PAGINATOR_SCRIPT = 'assets/pagedjs/paged.polyfill.min.js';

/** Well past a long book (212 pages took 4 seconds); after this the notes go at the end of each chapter. */
const PAGINATION_TIMEOUT_MS = 60_000;

/** Chrome, Edge and the other Chromium-based browsers. Chrome on iPhone is WebKit, and says `CriOS`. */
export function paginatesPageNotes(userAgent: string): boolean {
  return /Chrom(?:e|ium)\/\d/.test(userAgent);
}

export function renderPrintLayout(doc: ExportDocument, options: PrintLayoutOptions = {}): string {
  const font = STUDIO_FONTS.find((f) => f.value === doc.setup.font)?.stack ?? 'Georgia, serif';
  const paper = doc.setup.paper === 'letter' ? { name: 'letter', heightMm: 279.4 } : { name: 'A4', heightMm: 297 };
  const margin = doc.setup.marginMm;
  const textHeightMm = paper.heightMm - 2 * margin;
  const chapters = doc.sections.filter((section) => section.type === 'chapter').length;
  const notes = options.notes ?? 'chapter';
  return `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>${escapeHtml(doc.title)}</title>
<style>
${options.fontFaces ?? ''}
@page {
  size: ${paper.name};
  margin: ${margin}mm;
  @bottom-center { content: counter(page); font-family: ${font}; font-size: 9pt; color: #444; }${notes === 'page' ? '\n  @footnote { border-top: 0.5pt solid #444; padding-top: 2mm; }' : ''}
}
html { font-family: ${font}; font-size: 12pt; line-height: ${doc.setup.lineSpacing}; color: #000; background: #fff; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
body { margin: 0; }
p { margin: 0 0 0.6em; orphans: 3; widows: 3; }
h1, h2, h3, h4, h5, h6 { line-height: 1.2; margin: 1.2em 0 0.5em; break-after: avoid; page-break-after: avoid; break-inside: avoid; page-break-inside: avoid; }
h1 { font-size: 1.6em; } h2 { font-size: 1.3em; } h3 { font-size: 1.15em; } h4, h5, h6 { font-size: 1em; } h5, h6 { font-style: italic; }
.keep { break-inside: avoid; page-break-inside: avoid; }
.keep > :last-child { margin-bottom: 0.6em; }
section > :first-child, .keep:first-child > :first-child { margin-top: 0; }
.chapter:not(:first-child):not(.follows-group), .group:not(:first-child):not(.follows-group)${chapters > 1 ? ', .bibliography' : ''} { break-before: page; page-break-before: always; }
blockquote { margin: 0 2em 0.6em; }
ul, ol { margin: 0 0 0.6em; padding-inline-start: 1.6em; }
li > p { margin-bottom: 0.3em; }
pre { white-space: pre-wrap; font-size: 0.85em; line-height: 1.35; background: #f2f2f2; padding: 0.6em 0.8em; break-inside: avoid; page-break-inside: avoid; }
code { font-family: ui-monospace, 'Courier New', monospace; font-size: 0.9em; }
table { border-collapse: collapse; margin: 0 0 0.8em; width: 100%; font-size: 0.95em; line-height: 1.3; }
thead { display: table-header-group; }
tr, img { break-inside: avoid; page-break-inside: avoid; }
td, th { border: 0.5pt solid #888; padding: 0.25em 0.5em; vertical-align: top; text-align: start; }
img { max-width: 100%; max-height: ${Math.round(textHeightMm * IMAGE_MAX_HEIGHT)}mm; object-fit: contain; }
p:has(> img:only-child) { break-inside: avoid; page-break-inside: avoid; text-align: center; }
a { color: inherit; text-decoration: none; }
sup { line-height: 0; font-size: 0.7em; }
hr { border: 0; text-align: center; margin: 1em 0; }
hr::after { content: '* * *'; }
.footnotes { font-size: 0.85em; line-height: 1.35; margin-top: 1.5em; }
.footnotes h2, .footnotes h3 { font-size: 1.1em; }
.footnotes ol { padding-inline-start: 2em; }
.footnotes li { margin-bottom: 0.25em; break-inside: avoid; page-break-inside: avoid; }
.footnotes a[aria-label] { display: none; }
.bibliography p { padding-inline-start: 2em; text-indent: -2em; }
${
  notes === 'page'
    ? `.page-note { float: footnote; font-size: 0.85em; line-height: 1.35; }
::footnote-call { font-size: 0.7em; line-height: 0; vertical-align: super; }
::footnote-marker { font-size: 0.85em; }`
    : ''
}
</style>
</head>
<body>
${renderExportBody(doc, { notes, keepHeadings: true })}
</body>
</html>
`;
}

/** The `@font-face` rules this page knows for a family, with absolute addresses, plus stylesheets it can only link to. */
export function fontFacesFor(family: string, root: Document = document): { css: string; links: string[] } {
  const wanted = family.replace(/^['"]|['"]$/g, '').toLowerCase();
  const rules: string[] = [];
  const links = new Set<string>();
  const visit = (sheet: CSSStyleSheet) => {
    let list: CSSRuleList;
    try {
      list = sheet.cssRules;
    } catch {
      // Another site's stylesheet (Google Fonts): it can't be read, but it can be linked.
      if (sheet.href) links.add(sheet.href);
      return;
    }
    const base = sheet.href ?? root.baseURI;
    for (const rule of Array.from(list)) {
      if (rule instanceof CSSImportRule) {
        if (rule.styleSheet) visit(rule.styleSheet);
        else if (rule.href) links.add(new URL(rule.href, base).href);
      } else if (rule instanceof CSSFontFaceRule) {
        const name = rule.style.getPropertyValue('font-family').trim().replace(/^['"]|['"]$/g, '').toLowerCase();
        if (name === wanted) rules.push(rule.cssText.replace(/url\((['"]?)([^'")]+)\1\)/g, (_match: string, _quote: string, url: string) => `url("${new URL(url, base).href}")`));
      }
    }
  };
  for (const sheet of Array.from(root.styleSheets)) visit(sheet);
  return { css: rules.join('\n'), links: [...links].filter((href) => href.includes(encodeURIComponent(family).replace(/%20/g, '+')) || href.includes(family.replace(/ /g, '+'))) };
}

/** The first family of a CSS font stack. */
export function firstFamily(stack: string): string {
  return (stack.split(',')[0] ?? '').trim().replace(/^['"]|['"]$/g, '');
}

let current: { frame: HTMLIFrameElement; style: HTMLStyleElement; restoreTitle: string } | null = null;

/**
 * Lays the document out in a hidden frame and opens the print dialog on it.
 * Resolves once the dialog has been opened (or the layout couldn't be). The
 * Studio itself never prints: the dialog prints the frame, and while the frame
 * exists the page's own print style hides everything else, for the browsers
 * that print the whole page when a frame asks.
 */
export async function printExport(doc: ExportDocument, title: string, timeoutMs = 10_000, paginationTimeoutMs = PAGINATION_TIMEOUT_MS): Promise<void> {
  cleanUp();
  const stack = STUDIO_FONTS.find((f) => f.value === doc.setup.font)?.stack ?? 'Georgia, serif';
  const family = firstFamily(stack);
  const faces = fontFacesFor(family);
  // Paged.js would fetch and rewrite a linked sheet; the font's addresses work as they are.
  const layout = (notes: 'chapter' | 'page') =>
    renderPrintLayout(doc, { fontFaces: faces.css, notes }).replace(
      '</head>',
      `${faces.links.map((href) => `<link rel="stylesheet" data-pagedjs-ignore href="${escapeHtml(href)}">`).join('\n')}\n</head>`,
    );

  const style = document.createElement('style');
  style.textContent = `@media print { body > :not(.nostos-print-frame) { display: none !important; } .nostos-print-frame { position: static !important; width: 100% !important; height: auto !important; opacity: 1 !important; } }`;
  document.head.appendChild(style);

  const frame = document.createElement('iframe');
  frame.className = 'nostos-print-frame';
  frame.setAttribute('aria-hidden', 'true');
  frame.tabIndex = -1;
  frame.title = title;
  Object.assign(frame.style, { position: 'fixed', right: '0', bottom: '0', width: '1px', height: '1px', border: '0', opacity: '0', pointerEvents: 'none' });
  current = { frame, style, restoreTitle: document.title };

  const open = async (html: string): Promise<{ win: Window; frameDoc: Document }> => {
    const loaded = new Promise<void>((resolve) => frame.addEventListener('load', () => resolve(), { once: true }));
    frame.srcdoc = html;
    if (!frame.isConnected) document.body.appendChild(frame);
    await loaded;

    const win = frame.contentWindow;
    const frameDoc = frame.contentDocument;
    if (!win || !frameDoc) {
      cleanUp();
      throw new Error('The print layout could not be opened.');
    }
    // Every face the text uses, then every image, before the dialog opens: no fallback font, no empty boxes.
    await withTimeout(
      Promise.all([
        ...['400 normal', '400 italic', '700 normal', '700 italic'].map((face) => {
          const [weight, styleName] = face.split(' ');
          return frameDoc.fonts ? frameDoc.fonts.load(`${styleName} ${weight} 12pt "${family}"`).catch(() => []) : [];
        }),
        ...Array.from(frameDoc.images).map((image) => image.decode?.().catch(() => undefined)),
      ]).then(() => frameDoc.fonts?.ready),
      timeoutMs,
    );
    return { win, frameDoc };
  };

  const pageNotes = doc.footnotes.length > 0 && paginatesPageNotes(navigator.userAgent);
  let { win, frameDoc } = await open(layout(pageNotes ? 'page' : 'chapter'));
  if (pageNotes && !(await paginate(win, frameDoc, paginationTimeoutMs))) {
    ({ win, frameDoc } = await open(layout('chapter')));
  }

  // The PDF is named from the title in most browsers' "Save as PDF".
  document.title = title;
  win.addEventListener('afterprint', () => setTimeout(cleanUp, 0), { once: true });
  try {
    win.focus();
    win.print();
  } catch (error) {
    // A browser that refuses to print leaves neither the frame nor the borrowed title behind.
    cleanUp();
    throw error;
  }
  // Browsers whose print() doesn't wait for the dialog still fire afterprint; this is the backstop.
  setTimeout(() => {
    if (current?.frame === frame) document.title = current.restoreTitle;
  }, 1000);
}

interface PagedWindow {
  PagedConfig?: { auto: boolean };
  PagedPolyfill?: { preview(): Promise<unknown> };
}

/**
 * Runs Paged.js on the laid-out frame, inside it: the script measures the
 * `document` it loads in. False when it fails or runs too long, so the caller
 * can fall back to a layout that needs no script.
 */
async function paginate(win: Window, frameDoc: Document, timeoutMs: number): Promise<boolean> {
  const paged = win as Window & PagedWindow;
  paged.PagedConfig = { auto: false };
  const run = (async () => {
    await new Promise<void>((resolve, reject) => {
      const script = frameDoc.createElement('script');
      script.src = new URL(PAGINATOR_SCRIPT, document.baseURI).href;
      script.addEventListener('load', () => resolve(), { once: true });
      script.addEventListener('error', () => reject(new Error('The paginator could not be loaded.')), { once: true });
      frameDoc.head.appendChild(script);
    });
    await paged.PagedPolyfill?.preview();
    return !!paged.PagedPolyfill;
  })();
  try {
    return (await Promise.race([run, new Promise<boolean>((resolve) => setTimeout(() => resolve(false), timeoutMs))])) === true;
  } catch (error) {
    console.warn('Notes go at the end of each chapter: the paginator failed', error);
    return false;
  }
}

function cleanUp(): void {
  if (!current) return;
  current.frame.remove();
  current.style.remove();
  document.title = current.restoreTitle;
  current = null;
}

function withTimeout<T>(work: Promise<T>, ms: number): Promise<T | void> {
  return Promise.race([work, new Promise<void>((resolve) => setTimeout(resolve, ms))]);
}
