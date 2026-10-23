import { exportFilename, MAX_FILENAME_BYTES } from './export-filename';
import { renderExportHtml } from './export-html';
import type { ExportBlock, ExportDocument, ExportInline } from './export-model';
import { exportChapters, ancestorFolders, type ExportTreeItem } from './export-order';
import { defaultPaper, normalizePageSetup, pageSetupFromStudio } from './export-page-setup';
import { splitMarkdown } from './export-parse';
import { IN_TEXT, NAMES, NOTES, chapter, renderer, resolve } from './export.testing';
import { exportBlockedReason, orderChapters } from './export.service';
import { DEFAULT_STUDIO_PREFERENCES } from '../page-settings/studio-preferences.service';

/** The document as Markdown-like text, so expectations read like the Studio's. */
function text(doc: ExportDocument): string {
  const parts: string[] = [];
  for (const section of doc.sections) {
    if (section.type === 'group') parts.push(`${'#'.repeat(section.depth)} [${section.title}]`);
    else parts.push(...section.blocks.map(blockText));
  }
  for (const note of doc.footnotes) parts.push(`[^${note.number}]: ${inlines(note.children)}`);
  if (doc.bibliography) parts.push(`## ${doc.bibliography.heading}`, ...doc.bibliography.entries.map((e) => inlines(e.children)));
  return parts.join('\n\n');
}

function blockText(block: ExportBlock): string {
  switch (block.type) {
    case 'heading':
      return `${'#'.repeat(block.level)} ${inlines(block.children)}`;
    case 'paragraph':
      return inlines(block.children);
    case 'blockquote':
      return block.blocks.map((b) => blockText(b).split('\n').map((l) => `> ${l}`).join('\n')).join('\n>\n');
    case 'list':
      return block.items.map((item, i) => `${block.ordered ? `${block.start + i}.` : '-'} ${item.blocks.map(blockText).join(' ')}`).join('\n');
    case 'table':
      return [block.header, ...block.rows].map((row) => `| ${row.map(inlines).join(' | ')} |`).join('\n');
    case 'code':
      return '```\n' + block.text + '\n```';
    case 'rule':
      return '---';
  }
}

function inlines(list: readonly ExportInline[]): string {
  return list
    .map((inline) => {
      switch (inline.type) {
        case 'text':
          return inline.text;
        case 'emphasis':
          return `*${inlines(inline.children)}*`;
        case 'strong':
          return `**${inlines(inline.children)}**`;
        case 'strike':
          return `~~${inlines(inline.children)}~~`;
        case 'code':
          return `\`${inline.text}\``;
        case 'link':
          return `[${inlines(inline.children)}](${inline.href})`;
        case 'image':
          return `![${inline.alt}](${inline.src})`;
        case 'break':
          return '\n';
        case 'footnoteRef':
          return `[^${inline.number}]`;
      }
    })
    .join('');
}


describe('export resolve (issue #1164)', () => {
  it('leaves nothing that only Nostos understands', async () => {
    const doc = await resolve([chapter(IN_TEXT), chapter(NOTES, 'Two')], 'note');
    expect(JSON.stringify(doc)).not.toContain('nostos:');
    expect(JSON.stringify(doc)).not.toContain('[^');
  });

  describe('a citation that cannot be resolved', () => {
    it('keeps the words of a citation whose book was deleted, and lists it', async () => {
      const doc = await resolve('As shown [(Gone, 1990, p. 3)](nostos:cite/gone?p=3&as=reference) and [(Kant, 1992)](nostos:cite/b2).');

      expect(text(doc)).toBe('As shown (Gone, 1990, p. 3) and (Kant, 1992).');
      expect(doc.warnings).toEqual([
        expect.objectContaining({ kind: 'citation', message: '(Gone, 1990, p. 3): book no longer in your library, kept as written' }),
      ]);
    });

    it('keeps its last words in a footnote too, under a note style', async () => {
      const doc = await resolve('Claim [(Gone, 1990)](nostos:cite/gone?as=reference).', 'note');

      expect(text(doc)).toBe('Claim[^1].\n\n[^1]: (Gone, 1990)');
      expect(doc.warnings.map((w) => w.kind)).toEqual(['citation']);
    });

    it('keeps the words of a malformed citation link, and lists it', async () => {
      const doc = await resolve('Odd [(Kant, 1781)](nostos:cite/../etc?p=1) here, and [*a note*](nostos:note/x).');

      expect(text(doc)).toBe('Odd (Kant, 1781) here, and *a note*.');
      expect(doc.warnings).toEqual([expect.objectContaining({ kind: 'citation', message: '(Kant, 1781): citation link couldn’t be read, kept as written' })]);
    });

    it('keeps a deleted book’s bibliography entry as last written, and still writes the rest', async () => {
      const doc = await resolve(
        ['Text [(Kant, 1992)](nostos:cite/b2).', '## References', '[Gone. *Lost*. 1990.](nostos:cite/gone?as=bib)', '[Kant. Old.](nostos:cite/b2?as=bib)'].join('\n\n'),
      );

      expect(doc.bibliography).not.toBeNull();
      expect(doc.bibliography!.entries.map((e) => inlines(e.children))).toEqual(['Kant. *Book*. 1992.', 'Gone. *Lost*. 1990.']);
      expect(doc.warnings.some((w) => w.message === 'Bibliography entry “Gone. Lost. 1990.”: book no longer in your library, kept as written')).toBe(true);
    });
  });

  describe('a different citation style at export', () => {
    it('moves in-text citations into footnotes for a note style, as the Studio does, and names the bibliography', async () => {
      const doc = await resolve(IN_TEXT, 'note');

      expect(text(doc)).toBe(
        [
          'Opening line.',
          '> To be is to do.[^1]',
          'My own thought[^2]',
          'As argued[^3] here.',
          '[^1]: Voltaire, *Full* (Knopf, 1992), 42.',
          '[^2]: Kant, *Full* (Knopf, 1992).',
          '[^3]: Voltaire, *Short*.',
          '## Bibliography',
          'Kant. *Book*. 1992.',
          'Voltaire. *Book*. 1992.',
        ].join('\n\n'),
      );
    });

    it('brings footnotes back into the sentence for an author–date style, and renames the bibliography', async () => {
      const doc = await resolve(NOTES, 'in-text');

      expect(text(doc)).toBe(
        [
          'Opening line.',
          '> To be is to do.\n>\n> (Voltaire, 1992, p. 42)',
          'My own thought (Kant, 1992)',
          'As argued (Voltaire, 1992) here.',
          '## References',
          'Kant. *Book*. 1992.',
          'Voltaire. *Book*. 1992.',
        ].join('\n\n'),
      );
      expect(doc.footnotes).toEqual([]);
    });

    it('re-renders every citation even when the placement stays (a book gained its year)', async () => {
      const doc = await resolve(IN_TEXT.replace(/1992/g, 'n.d.'), 'in-text');
      expect(text(doc)).not.toContain('n.d.');
    });

    it('gives the house format its own line for a note', async () => {
      const doc = await resolve('Thought.[^1]\n\n[^1]: [Kant, *Full* (Knopf, 1992).](nostos:cite/b2?as=note)', 'house');
      expect(text(doc)).toBe('Thought.\n\n*See: Kant*');
    });

    it('keeps a note the writer wrote, with its citation written in the new style', async () => {
      const doc = await resolve('A claim.[^1]\n\n[^1]: Compare [Kant, *Full* (Knopf, 1992).](nostos:cite/b2?as=reference) with Hume.', 'in-text');
      expect(text(doc)).toBe('A claim.[^1]\n\n[^1]: Compare (Kant, 1992) with Hume.');
    });
  });

  describe('note styles across chapters of a folder', () => {
    it('gives a book its full note once in the whole document, and the short form after, numbering notes continuously', async () => {
      // Each chapter, written alone in the Studio, gave Voltaire a full first note.
      const one = chapter('First[^1].\n\n[^1]: [Voltaire, *Full* (Knopf, 1992).](nostos:cite/b1?as=reference)', 'One');
      const two = chapter(
        'Again[^1], and Kant[^2].\n\n[^1]: [Voltaire, *Full* (Knopf, 1992), 9.](nostos:cite/b1?p=9&as=reference)\n\n[^2]: [Kant, *Full* (Knopf, 1992).](nostos:cite/b2?as=reference)',
        'Two',
      );
      const doc = await resolve([one, two], 'note');

      expect(text(doc)).toBe(
        [
          'First[^1].',
          'Again[^2], and Kant[^3].',
          '[^1]: Voltaire, *Full* (Knopf, 1992).',
          '[^2]: Voltaire, *Short*, 9.',
          '[^3]: Kant, *Full* (Knopf, 1992).',
        ].join('\n\n'),
      );
    });

    it('follows the export’s chapter order, not the order the chapters were written in', async () => {
      const one = chapter('A[^1].\n\n[^1]: [V](nostos:cite/b1?as=reference)', 'One');
      const two = chapter('B[^1].\n\n[^1]: [V](nostos:cite/b1?as=reference)', 'Two');
      const doc = await resolve(orderChapters([one, two].map((c) => ({ ...c, id: c.writingId })), ['two', 'one']), 'note');
      expect(doc.footnotes.map((n) => inlines(n.children))).toEqual(['Voltaire, *Full* (Knopf, 1992).', 'Voltaire, *Short*.']);
      expect(doc.sections.map((s) => (s.type === 'chapter' ? s.title : ''))).toEqual(['Two', 'One']);
    });
  });

  describe('the bibliography', () => {
    it('lists a book cited in several chapters once, and keeps a book listed without a live citation', async () => {
      const one = chapter(['Kant [(Kant, 1992)](nostos:cite/b2).', '## References', '[Kant. Old.](nostos:cite/b2?as=bib)', '[Weil. Old.](nostos:cite/b3?as=bib)'].join('\n\n'), 'One');
      const two = chapter(['Kant again [(Kant, 1992)](nostos:cite/b2) and Voltaire [(V)](nostos:cite/b1).', '## References', '[Kant. Old.](nostos:cite/b2?as=bib)'].join('\n\n'), 'Two');
      const doc = await resolve([one, two], 'in-text');

      expect(doc.bibliography).toEqual({
        heading: 'References',
        entries: [
          { book: 'b2', children: [{ type: 'text', text: 'Kant. ' }, { type: 'emphasis', children: [{ type: 'text', text: 'Book' }] }, { type: 'text', text: '. 1992.' }] },
          { book: 'b1', children: expect.anything() },
          { book: 'b3', children: expect.anything() },
        ].sort((a, b) => NAMES[a.book].localeCompare(NAMES[b.book])),
      });
      // The chapters' own lists and their headings are gone; only the one remains.
      expect(text(doc).match(/## References/g)).toHaveLength(1);
      expect(doc.warnings).toEqual([]);
    });

    it('adds the books kept with the writing, as the Studio does', async () => {
      const doc = await resolve('Text.\n\n## Bibliography\n\n[Kant. Old.](nostos:cite/b2?as=bib)', 'in-text', { renderer: renderer('in-text', { kept: ['b3'] }) });
      expect(doc.bibliography!.entries.map((e) => e.book)).toEqual(['b2', 'b3']);
    });

    it('writes none when the writing has none', async () => {
      const doc = await resolve('Kant [(Kant, 1992)](nostos:cite/b2).');
      expect(doc.bibliography).toBeNull();
    });
  });

  describe('footnotes that do not pair', () => {
    it('drops a marker with no note, and says so', async () => {
      const doc = await resolve('One[^1] two[^7].\n\n[^1]: Mine.');
      expect(text(doc)).toBe('One[^1] two.\n\n[^1]: Mine.');
      expect(doc.warnings).toEqual([expect.objectContaining({ kind: 'footnote', message: 'Footnote marker 7: no note, left out' })]);
    });

    it('keeps the writer’s note with no marker as a plain paragraph, and drops the Studio’s', async () => {
      const doc = await resolve('Text.\n\n[^1]: My aside.\n\n[^2]: [Kant, *Full*.](nostos:cite/b2?as=reference)');
      expect(text(doc)).toBe('Text.\n\nMy aside.');
      expect(doc.footnotes).toEqual([]);
      expect(doc.warnings).toEqual([expect.objectContaining({ kind: 'footnote', message: 'Footnote 1: no marker in the text, kept as a paragraph' })]);
    });

    it('gives a note called twice its words at each call', async () => {
      const doc = await resolve('A[^1] and B[^1].\n\n[^1]: Shared.');
      expect(text(doc)).toBe('A[^1] and B[^2].\n\n[^1]: Shared.\n\n[^2]: Shared.');
    });
  });

  describe('a folder', () => {
    const items: ExportTreeItem[] = [
      { id: 'root', name: 'Thesis', type: 'Folder', parentId: null },
      { id: 'intro', name: 'Introduction', type: 'Document', parentId: 'root' },
      { id: 'part2', name: 'Part Two', type: 'Folder', parentId: 'root' },
      { id: 'part1', name: 'Part One', type: 'Folder', parentId: 'root' },
      { id: 'p1c2', name: 'B chapter', type: 'Document', parentId: 'part1' },
      { id: 'p1c1', name: 'A chapter', type: 'Document', parentId: 'part1' },
      { id: 'deep', name: 'Deeper', type: 'Folder', parentId: 'part1' },
      { id: 'deepdoc', name: 'Deep chapter', type: 'Document', parentId: 'deep' },
      { id: 'p2c1', name: 'Only chapter', type: 'Document', parentId: 'part2' },
      { id: 'elsewhere', name: 'Elsewhere', type: 'Document', parentId: null },
    ];

    it('flattens nested folders in Studio tree order: folders first, then documents, each by name', () => {
      expect(exportChapters(items, 'root').map((c) => [c.title, c.groupPath.map((g) => g.title).join('/')])).toEqual([
        ['Deep chapter', 'Part One/Deeper'],
        ['A chapter', 'Part One'],
        ['B chapter', 'Part One'],
        ['Only chapter', 'Part Two'],
        ['Introduction', ''],
      ]);
      expect(exportChapters(items, 'intro')).toEqual([{ id: 'intro', title: 'Introduction', groupPath: [] }]);
      expect(ancestorFolders(items, 'deepdoc').map((f) => f.name)).toEqual(['Deeper', 'Part One', 'Thesis']);
    });

    it('turns folder names into chapter-group headings, once per group', async () => {
      const chapters = exportChapters(items, 'root').map((c) => ({ writingId: c.id, title: c.title, groupPath: c.groupPath, markdown: `Text of ${c.title}.` }));
      const doc = await resolve(chapters);
      expect(text(doc)).toBe(
        [
          '# [Part One]',
          '## [Deeper]',
          'Text of Deep chapter.',
          'Text of A chapter.',
          'Text of B chapter.',
          '# [Part Two]',
          'Text of Only chapter.',
          'Text of Introduction.',
        ].join('\n\n'),
      );
    });

    it('skips empty writings, and cannot be exported when every writing is empty or there is none', async () => {
      const doc = await resolve([chapter('  \n', 'Blank'), chapter('Words.', 'Full')]);
      expect(doc.sections.map((s) => (s.type === 'chapter' ? s.title : ''))).toEqual(['Full']);

      expect(exportBlockedReason({ type: 'Document', chapters: [{ markdown: ' \n\t' }] as never })).toBe('This writing is empty. Write something to export it.');
      expect(exportBlockedReason({ type: 'Folder', chapters: [] })).toBe('This folder has no writings in it yet.');
      expect(exportBlockedReason({ type: 'Folder', chapters: [{ markdown: '' }, { markdown: '\n' }] as never })).toBe('Every writing in this folder is empty.');
      expect(exportBlockedReason({ type: 'Folder', chapters: [{ markdown: '' }, { markdown: 'x' }] as never })).toBeNull();
    });
  });

  describe('images', () => {
    it('links images by URL and turns one that fails to load into a visible placeholder, listed', async () => {
      const checked: string[] = [];
      const doc = await resolve('![A map](/api/books/1/cover) and ![](https://example.com/broken.png) and ![x](javascript:alert(1))', 'in-text', {
        baseUrl: 'https://nostos.example',
        checkImage: async (src) => {
          checked.push(src);
          return !src.includes('broken');
        },
      });

      expect(text(doc)).toBe('![A map](https://nostos.example/api/books/1/cover) and [image: untitled] and [image: x]');
      expect(checked.sort()).toEqual(['https://example.com/broken.png', 'https://nostos.example/api/books/1/cover']);
      expect(doc.warnings.filter((w) => w.kind === 'image')).toHaveLength(2);
      expect(doc.warnings.map((w) => w.message)).toEqual([
        'Image https://example.com/broken.png: couldn’t be loaded, shown as [image: untitled]',
        'Image “x”: couldn’t be loaded, shown as [image: x]',
      ]);
    });
  });

  describe('Unicode and right-to-left text', () => {
    const words = 'مرحبا بالعالم — שלום עולם — 👩🏽‍🔬 café Å ñ 𝔘 — 日本語';

    it('comes through unchanged, in the model and in the web page', async () => {
      const doc = await resolve(`# ${words}\n\n${words} [(Kant, 1992)](nostos:cite/b2).\n\n> ${words}`);
      expect(text(doc)).toBe(`# ${words}\n\n${words} (Kant, 1992).\n\n> ${words}`);
      expect(renderExportHtml(doc)).toContain(`<p dir="auto">${words} (Kant, 1992).</p>`);
      expect(exportFilename(words, 'html')).toBe(`${words}.html`);
    });
  });

  describe('a long writing', () => {
    it('resolves about 100,000 words without holding the page for long', async () => {
      const paragraph = (i: number) =>
        `Paragraph ${i} carries an ordinary amount of prose so that the whole writing is long, with a citation [(Kant, 1992)](nostos:cite/b2?as=reference) in it and a footnote.[^${i}]`;
      const blocks: string[] = [];
      const notes: string[] = [];
      for (let i = 1; i <= 4500; i++) {
        blocks.push(paragraph(i));
        notes.push(`[^${i}]: Note ${i}.`);
      }
      const markdown = [...blocks, ...notes].join('\n\n');
      const gaps: number[] = [];
      let last = performance.now();

      const started = performance.now();
      const doc = await resolve(markdown, 'note', {
        slicing: {
          budgetMs: 12,
          yieldToPage: async () => {
            const now = performance.now();
            gaps.push(now - last);
            await new Promise((r) => setTimeout(r, 0));
            last = performance.now();
          },
        },
      });
      gaps.push(performance.now() - last);

      expect(doc.words).toBeGreaterThan(100_000);
      expect(doc.footnotes).toHaveLength(9000);
      expect(doc.footnotes[1].children).toEqual([{ type: 'text', text: 'Note 1.' }]);
      expect(gaps.length).toBeGreaterThan(10);
      // Each stretch of work is a slice, never the whole job; generous for a busy test machine.
      expect(Math.max(...gaps)).toBeLessThan(Math.max(250, (performance.now() - started) / 4));
      expect(splitMarkdown(markdown).length).toBeGreaterThan(5);
    });

    it('splits only between top-level paragraphs, never in a code fence or a list', () => {
      const markdown = ['x'.repeat(50), '```', 'a', '', 'b', '```', '', '- one', '', '  more', '', 'Next.'].join('\n');
      const pieces = splitMarkdown(markdown, 10);
      expect(pieces.join('\n')).toBe(markdown);
      expect(pieces.some((p) => p.startsWith('b'))).toBe(false);
      expect(pieces.some((p) => p.startsWith('  more'))).toBe(false);
      expect(pieces[pieces.length - 1]).toBe('Next.');
    });

    it('stops when the export is cancelled', async () => {
      const abort = new AbortController();
      abort.abort();
      await expect(resolve('Words.', 'in-text', { slicing: { signal: abort.signal } })).rejects.toThrow();
    });
  });

  it('keeps headings, lists, tables, code and marks', async () => {
    const doc = await resolve(
      ['## Head *it*', '- one\n- **two**', '3. three\n4. four', '| a | b |\n|:-|-:|\n| 1 | ~~2~~ |', '```js\nlet a = 1;\n```', '---', 'Line  \nbreak `code` [site](https://example.com) &amp;'].join('\n\n'),
    );
    expect(text(doc)).toBe(
      ['## Head *it*', '- one\n- **two**', '3. three\n4. four', '| a | b |\n| 1 | ~~2~~ |', '```\nlet a = 1;\n```', '---', 'Line\nbreak `code` [site](https://example.com) &'].join('\n\n'),
    );
  });
});

describe('export file names (issue #1164)', () => {
  it('defaults to the title and keeps it as written where it can', () => {
    expect(exportFilename('My Essay', 'docx')).toBe('My Essay.docx');
    expect(exportFilename('  ', 'pdf')).toBe('Untitled.pdf');
    expect(exportFilename(null, '.md')).toBe('Untitled.md');
  });

  it('replaces characters Windows, macOS or Linux refuse, and trims trailing dots and spaces', () => {
    expect(exportFilename('a<b>c:d"e/f\\g|h?i*j', 'html')).toBe('a b c d e f g h i j.html');
    expect(exportFilename('tab\there\u0000nul', 'html')).toBe('tab here nul.html');
    expect(exportFilename('Ends with dots...  ', 'html')).toBe('Ends with dots.html');
    expect(exportFilename('.hidden', 'html')).toBe('hidden.html');
  });

  it('never names a file after a Windows device', () => {
    for (const name of ['CON', 'prn', 'Aux', 'NUL', 'COM1', 'lpt9', 'con.txt']) {
      expect(exportFilename(name, 'html')).not.toMatch(/^(con|prn|aux|nul|com\d|lpt\d)(\.[a-z]+)?\.html$/i);
    }
    expect(exportFilename('CON', 'html')).toBe('CON_.html');
    expect(exportFilename('Console', 'html')).toBe('Console.html');
  });

  it('removes invisible direction controls that could disguise the extension', () => {
    expect(exportFilename('report‮fdp.exe', 'html')).toBe('reportfdp.exe.html');
  });

  it('fits the strictest length limit, cutting at a whole character', () => {
    const long = 'é'.repeat(300);
    const name = exportFilename(long, 'docx');
    expect(new TextEncoder().encode(name).length).toBeLessThanOrEqual(MAX_FILENAME_BYTES);
    expect(name.endsWith('.docx')).toBe(true);
    const emoji = exportFilename('👩🏽‍🔬'.repeat(40), 'pdf');
    expect(new TextEncoder().encode(emoji).length).toBeLessThanOrEqual(MAX_FILENAME_BYTES);
    // No family member is cut in half.
    expect(emoji.replace(/\.pdf$/, '').length % '👩🏽‍🔬'.length).toBe(0);
  });
});

describe('export page setup (issue #1164)', () => {
  it('starts from the Studio’s preferences, with the paper the region uses', () => {
    expect(pageSetupFromStudio({ ...DEFAULT_STUDIO_PREFERENCES, font: 'eb-garamond', lineSpacing: 2.2, citationStyle: 'mla' }, 'en-US')).toEqual({
      paper: 'letter',
      marginMm: 25.4,
      font: 'eb-garamond',
      lineSpacing: 2,
      citationStyle: 'mla',
    });
    expect(defaultPaper('sv-SE')).toBe('a4');
    expect(defaultPaper('en')).toBe('a4');
    expect(defaultPaper('es-MX')).toBe('letter');
  });

  it('distrusts a saved setup: unknown values take the fallback', () => {
    const fallback = pageSetupFromStudio(DEFAULT_STUDIO_PREFERENCES, 'en-GB');
    expect(normalizePageSetup({ paper: 'tabloid', marginMm: 26, font: 'comic', lineSpacing: 1.4, citationStyle: 'nope' }, fallback)).toEqual({
      ...fallback,
      marginMm: 25.4,
      lineSpacing: 1.5,
    });
    expect(normalizePageSetup(null, fallback)).toEqual(fallback);
  });
});
