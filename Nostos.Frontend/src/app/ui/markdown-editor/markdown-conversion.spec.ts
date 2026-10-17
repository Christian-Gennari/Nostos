import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import Image from '@tiptap/extension-image';
import { TableKit } from '@tiptap/extension-table';

import { htmlToMarkdown, markdownToHtml } from './markdown-conversion';
import { buildNoteMarkdown, buildQuoteMarkdown, buildReferenceMarkdown } from '../../writing-studio/source-insertion.helpers';

/**
 * Round-trip safety for the Studio editor (issue #1037).
 *
 * A writing goes Markdown -> HTML -> editor document -> HTML -> Markdown on
 * every open-and-save. The editor here is configured like the Studio's, so a
 * shape that loses text in this test loses it in somebody's draft.
 */
function throughEditor(markdown: string): string {
  const editor = new Editor({
    element: document.createElement('div'),
    extensions: [
      StarterKit.configure({ heading: { levels: [1, 2, 3] }, underline: false }),
      Image,
      TableKit.configure({ table: { resizable: false } }),
    ],
    content: markdownToHtml(markdown),
  });
  const out = htmlToMarkdown(editor.getHTML());
  editor.destroy();
  return out;
}

/** The words of a text, so a check can say "nothing was lost". */
function words(text: string): string[] {
  return (text.match(/[\p{L}\p{N}]+/gu) ?? []).map((w) => w.toLowerCase());
}

const quoteSource = {
  bookTitle: 'The World as Will and Representation',
  selectedText: 'Every act of the will is an act of the body.',
  content: 'On striving.',
  sourceAnchorKind: 'pdf_page',
  sourceAnchorValue: '12',
  anchorVerified: true,
};

const CORPUS: Record<string, string> = {
  headings: '# Title\n\n## Section\n\n### Subsection\n\nBody text.',
  emphasis: 'Plain, **bold**, *italic* and `code` in one line.',
  escapedMarkers: 'A literal \\*star\\* and an \\_underscore\\_ stay text.',
  links: 'See [the essay](https://example.com/essay) for more.',
  image: '![A cover](https://example.com/cover.jpg)',
  bulletList: '- one\n- two\n    - nested\n- three',
  orderedList: '1. first\n2. second\n3. third',
  blockquote: '> A quoted line.\n>\n> A second paragraph in the quote.',
  codeBlock: '```js\nconst x = 1;\n```',
  rule: 'Above\n\n---\n\nBelow',
  table: '| Name | Year |\n| --- | --- |\n| Ethics | 1677 |\n| Either/Or | 1843 |',
  unicode: 'Søren Kierkegaard — «Enten – Eller» — 日本語のテキスト.',
  quoteInsertion: buildQuoteMarkdown(quoteSource)!,
  noteInsertion: buildNoteMarkdown(quoteSource)!,
  referenceInsertion: buildReferenceMarkdown(quoteSource)!,
};

describe('Markdown round trip through the Studio editor', () => {
  for (const [name, markdown] of Object.entries(CORPUS)) {
    it(`keeps every word of "${name}"`, () => {
      expect(words(throughEditor(markdown))).toEqual(words(markdown));
    });

    it(`is stable after the first save of "${name}"`, () => {
      const once = throughEditor(markdown);
      expect(throughEditor(once)).toBe(once);
    });
  }

  it('keeps a tight list tight, exactly as TinyMCE saved it', () => {
    const tight = '1.  **Ricoeur**, _Oneself as Another_\n2.  **Murdoch** — short\n\n*   one\n*   two';
    expect(throughEditor(tight)).toBe(tight);
  });

  it('keeps a list item with two paragraphs as two paragraphs', () => {
    const out = throughEditor('*   first para\n    \n    second para\n*   next');
    expect(out).toContain('first para\n    \n    second para');
  });

  it('saves a table as a GFM pipe table, not as loose cell text', () => {
    const out = throughEditor(CORPUS['table']);
    expect(out).toContain('| Name | Year |');
    expect(out).toMatch(/\| --- \| --- \|/);
    expect(out).toContain('| Either/Or | 1843 |');
  });

  it('escapes a pipe typed inside a table cell', () => {
    const out = throughEditor('| A | B |\n| --- | --- |\n| x \\| y | z |');
    expect(out).toContain('x \\| y');
  });

  it('keeps the words of underlined HTML, which Markdown cannot keep as underline', () => {
    expect(htmlToMarkdown('<p>a <u>under</u> b</p>')).toBe('a under b');
  });

  it('keeps headings, lists and quotes as their Markdown form', () => {
    const out = throughEditor('## Heading\n\n- item\n\n> quote');
    expect(out).toContain('## Heading');
    expect(out).toMatch(/^\*\s+item$/m);
    expect(out).toContain('> quote');
  });
});
