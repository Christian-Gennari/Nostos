import { STUDIO_FONTS } from '../page-settings/studio-preferences.service';
import { escapeHtml, renderExportBody } from './export-html';
import type { ExportDocument } from './export-model';

/**
 * PDF through a print layout (issue #1167). The resolved document is laid out
 * for paper in a hidden frame and printed with the browser's own print
 * dialog, where "Save as PDF" makes the file. Nothing is sent anywhere.
 *
 * Footnotes are endnotes at the end of each chapter, numbered through the
 * whole document. Browsers can't yet float a note to the foot of its page
 * (CSS `float: footnote` is only in print engines), and a polyfill that
 * paginates in script would differ between browsers, which is the opposite of
 * what a print layout is for.
 *
 * Page numbers come from the page's bottom margin (`@page @bottom-center`),
 * which Chromium-based browsers print. Where a browser doesn't, its own print
 * dialog's "Headers and footers" option numbers the pages.
 */

export interface PrintLayoutOptions {
  /** `@font-face` rules for the writing's font, ready to paste into the layout. */
  fontFaces?: string;
}

/** Tall images stop at this share of the text block, so one never has a page to itself and a caption after it. */
const IMAGE_MAX_HEIGHT = 0.8;

export function renderPrintLayout(doc: ExportDocument, options: PrintLayoutOptions = {}): string {
  const font = STUDIO_FONTS.find((f) => f.value === doc.setup.font)?.stack ?? 'Georgia, serif';
  const paper = doc.setup.paper === 'letter' ? { name: 'letter', heightMm: 279.4 } : { name: 'A4', heightMm: 297 };
  const margin = doc.setup.marginMm;
  const textHeightMm = paper.heightMm - 2 * margin;
  const chapters = doc.sections.filter((section) => section.type === 'chapter').length;
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
  @bottom-center { content: counter(page); font-family: ${font}; font-size: 9pt; color: #444; }
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
</style>
</head>
<body>
${renderExportBody(doc, { notes: 'chapter', keepHeadings: true })}
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
export async function printExport(doc: ExportDocument, title: string, timeoutMs = 10_000): Promise<void> {
  cleanUp();
  const stack = STUDIO_FONTS.find((f) => f.value === doc.setup.font)?.stack ?? 'Georgia, serif';
  const family = firstFamily(stack);
  const faces = fontFacesFor(family);
  const layout = renderPrintLayout(doc, { fontFaces: faces.css }).replace(
    '</head>',
    `${faces.links.map((href) => `<link rel="stylesheet" href="${escapeHtml(href)}">`).join('\n')}\n</head>`,
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

  const loaded = new Promise<void>((resolve) => frame.addEventListener('load', () => resolve(), { once: true }));
  frame.srcdoc = layout;
  document.body.appendChild(frame);
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

  // The PDF is named from the title in most browsers' "Save as PDF".
  document.title = title;
  win.addEventListener('afterprint', () => setTimeout(cleanUp, 0), { once: true });
  win.focus();
  win.print();
  // Browsers whose print() doesn't wait for the dialog still fire afterprint; this is the backstop.
  setTimeout(() => {
    if (current?.frame === frame) document.title = current.restoreTitle;
  }, 1000);
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
