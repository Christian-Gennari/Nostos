// Emulates PdfReader.visibleExcerpt(): first 280 chars of a page's pdf.js text.
import { getDocument } from 'pdfjs-dist/legacy/build/pdf.mjs';
import fs from 'node:fs';
const [,, file, countArg, out] = process.argv;
const doc = await getDocument({ data: new Uint8Array(fs.readFileSync(file)), verbosity: 0 }).promise;
const count = Number(countArg), excerpts = [], pages = [];
// Body pages only: skip front and back matter.
const pageText = async (n) => {
  const content = await (await doc.getPage(n)).getTextContent();
  return content.items.map((it) => (typeof it.str === 'string' ? it.str : '')).join(' ').replace(/\s+/g, ' ').trim();
};
for (let i = 0; i < count; i++) {
  const n = Math.round(doc.numPages * (0.15 + 0.7 * i / Math.max(1, count - 1)));
  // As PdfReader.visibleExcerpt(): a page with under 100 characters is spoken for by the next page with text, up to two on.
  let text = await pageText(n), used = n;
  for (let next = n; next <= Math.min(doc.numPages, n + 2); next++) {
    const candidate = await pageText(next);
    if (candidate.length >= 100) { text = candidate; used = next; break; }
  }
  excerpts.push(text.slice(0, 280)); pages.push(used === n ? n : `${n}->${used}`);
}
fs.writeFileSync(out, JSON.stringify(excerpts, null, 1));
console.log(doc.numPages, 'pages; sampled', pages.join(','));
