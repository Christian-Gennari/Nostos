import JSZip from 'jszip';
import { marked } from 'marked';

import { buildDocx, docxFontName } from './export-docx';
import { Packer } from 'docx';
import { EXPORT_FORMATS } from './export-formats';
import { embedImages, type EmbeddedImage } from './export-images';
import { chapterTitle, footnotesIn, imagesIn, isRightToLeft } from './export-layout';
import { escapeText, renderExportMarkdown } from './export-markdown';
import type { ExportDocument, ExportInline, ExportSection } from './export-model';
import { renderPrintLayout } from './export-pdf';
import { copyRichText, renderExportPlainText, renderExportRichText } from './export-rich-text';
import { IN_TEXT, NOTES, SETUP, chapter, renderer, resolve } from './export.testing';

const t = (text: string): ExportInline => ({ type: 'text', text });

/** A test essay with every kind of block the model has. */
function essay(overrides: Partial<ExportDocument> = {}): ExportDocument {
  return {
    title: 'Essay',
    setup: { ...SETUP },
    citationStyle: { id: 'chicago-notes', label: 'Chicago (notes)', kind: 'note' },
    sections: [
      {
        type: 'chapter',
        writingId: 'essay',
        title: 'Essay',
        depth: 0,
        blocks: [
          { type: 'heading', level: 1, children: [t('On '), { type: 'emphasis', children: [t('Candide')] }] },
          { type: 'paragraph', children: [t('Voltaire mocks optimism.'), { type: 'footnoteRef', number: 1 }, t(' See the '), { type: 'link', href: 'https://example.org/a b', children: [t('notes')] }, t('.')] },
          { type: 'blockquote', blocks: [{ type: 'paragraph', children: [t('All is for the best.'), { type: 'footnoteRef', number: 2 }] }] },
          {
            type: 'list',
            ordered: true,
            start: 3,
            items: [
              { checked: null, blocks: [{ type: 'paragraph', children: [t('Third')] }, { type: 'list', ordered: false, start: 1, items: [{ checked: null, blocks: [{ type: 'paragraph', children: [t('Nested')] }] }] }] },
              { checked: null, blocks: [{ type: 'paragraph', children: [t('Fourth')] }] },
            ],
          },
          { type: 'list', ordered: false, start: 1, items: [{ checked: true, blocks: [{ type: 'paragraph', children: [t('Done')] }] }] },
          {
            type: 'table',
            align: ['left', 'right'],
            header: [[t('Book')], [t('Year')]],
            rows: [[[t('Candide | a tale')], [t('1759')]]],
          },
          { type: 'code', language: 'js', text: 'const best = "all";\n' },
          { type: 'rule' },
          { type: 'paragraph', children: [{ type: 'image', src: 'https://example.org/map.png', alt: 'Map', title: null }] },
        ],
      },
    ],
    footnotes: [
      { number: 1, children: [t('Voltaire, '), { type: 'emphasis', children: [t('Candide')] }, t(' (Paris, 1759), see '), { type: 'link', href: 'https://example.org/c', children: [t('the text')] }, t('.')] },
      { number: 2, children: [t('Ibid., 3.')] },
    ],
    bibliography: { heading: 'Bibliography', entries: [{ book: 'b1', children: [t('Voltaire. '), { type: 'emphasis', children: [t('Candide')] }, t('. Paris, 1759.')] }] },
    warnings: [],
    words: 20,
    ...overrides,
  };
}

function folder(): ExportDocument {
  const chapterSection = (id: string, title: string, depth: number, blocks: Extract<ExportSection, { type: 'chapter' }>['blocks']): ExportSection => ({ type: 'chapter', writingId: id, title, depth, blocks });
  return essay({
    title: 'Thesis',
    sections: [
      chapterSection('one', 'One', 0, [{ type: 'heading', level: 1, children: [t('Chapter one')] }, { type: 'paragraph', children: [t('First.'), { type: 'footnoteRef', number: 1 }] }]),
      { type: 'group', folderId: 'part', title: 'Part two', depth: 1 },
      chapterSection('two', 'Two', 1, [{ type: 'paragraph', children: [t('Second, no heading.'), { type: 'footnoteRef', number: 2 }] }]),
      chapterSection('three', 'Three', 1, [{ type: 'paragraph', children: [t('Third.')] }]),
    ],
  });
}

async function unzip(blob: Blob): Promise<JSZip> {
  return JSZip.loadAsync(await blob.arrayBuffer());
}

async function docxXml(doc: ExportDocument, images: ReadonlyMap<string, EmbeddedImage> = new Map()): Promise<{ zip: JSZip; document: string; footnotes: string; styles: string }> {
  const zip = await unzip(await Packer.toBlob(buildDocx(doc, { images })));
  return {
    zip,
    document: await zip.file('word/document.xml')!.async('string'),
    footnotes: await zip.file('word/footnotes.xml')!.async('string'),
    styles: await zip.file('word/styles.xml')!.async('string'),
  };
}

/** A 1×1 PNG. */
const PNG = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='), (c) => c.charCodeAt(0));

describe('export layout shared by every format (issues #1165–#1167)', () => {
  it('titles a folder’s chapter only when it doesn’t open with a heading', () => {
    const doc = folder();
    const chapters = doc.sections.filter((s): s is Extract<ExportSection, { type: 'chapter' }> => s.type === 'chapter');
    expect(chapterTitle(doc, chapters[0])).toBeNull();
    expect(chapterTitle(doc, chapters[1])).toEqual({ level: 2, text: 'Two' });
    // A single writing prints exactly as written.
    const single = essay({ sections: [{ ...chapters[1], depth: 0 }] });
    expect(chapterTitle(single, chapters[1])).toBeNull();
  });

  it('finds notes and images in reading order, each once', () => {
    const doc = essay();
    expect(footnotesIn((doc.sections[0] as Extract<ExportSection, { type: 'chapter' }>).blocks)).toEqual([1, 2]);
    expect([...imagesIn(doc)]).toEqual([['https://example.org/map.png', 'Map']]);
  });

  it('reads the direction from the first strong letter', () => {
    expect(isRightToLeft([t('«שלום» world')])).toBe(true);
    expect(isRightToLeft([t('1. مرحبا')])).toBe(true);
    expect(isRightToLeft([t('Hello مرحبا')])).toBe(false);
  });

  it('offers DOCX, PDF, Markdown, rich text and the web page, DOCX first', () => {
    expect(EXPORT_FORMATS.map((f) => [f.id, f.action, f.paged])).toEqual([
      ['docx', 'download', true],
      ['pdf', 'print', true],
      ['markdown', 'download', false],
      ['rich-text', 'copy', false],
      ['html', 'download', false],
    ]);
  });
});

describe('Markdown download (issue #1165)', () => {
  it('leaves no nostos: address in a writing that uses every citation role, in either placement', async () => {
    for (const [markdown, placement] of [
      [IN_TEXT, 'in-text'],
      [NOTES, 'note'],
      [IN_TEXT, 'note'],
      [NOTES, 'house'],
    ] as const) {
      const doc = await resolve(markdown, placement);
      const md = renderExportMarkdown(doc);
      expect(md).not.toContain('nostos:');
      expect(renderExportRichText(doc).html).not.toContain('nostos:');
      expect(renderExportRichText(doc).text).not.toContain('nostos:');
      expect(renderPrintLayout(doc)).not.toContain('nostos:');
    }
  });

  it('writes CommonMark with [^n] notes, GFM tables and a fenced code block', () => {
    const md = renderExportMarkdown(essay());
    expect(md).toContain('# On *Candide*');
    expect(md).toContain('Voltaire mocks optimism.[^1] See the [notes](https://example.org/a%20b).');
    expect(md).toContain('> All is for the best.[^2]');
    expect(md).toContain('3. Third\n   - Nested\n4. Fourth');
    expect(md).toContain('- [x] Done');
    expect(md).toContain('| Book | Year |\n| :--- | ---: |\n| Candide \\| a tale | 1759 |');
    expect(md).toContain('```js\nconst best = "all";\n```');
    expect(md).toContain('![Map](https://example.org/map.png)');
    expect(md).toContain('## Bibliography\n\nVoltaire. *Candide*. Paris, 1759.');
    expect(md).toContain('[^1]: Voltaire, *Candide* (Paris, 1759), see [the text](https://example.org/c).');
    expect(md.endsWith('[^2]: Ibid., 3.\n')).toBe(true);
  });

  it('escapes Markdown characters that are words, so they read back as the same words', () => {
    const words = 'A title written *Candide* [with brackets], 2 * 3 < 4 _under_ `tick` ~strike~ &amp; | pipe';
    const doc = essay({
      sections: [
        {
          type: 'chapter',
          writingId: 'x',
          title: 'X',
          depth: 0,
          blocks: [
            { type: 'paragraph', children: [t(words)] },
            { type: 'paragraph', children: [t('# not a heading')] },
            { type: 'paragraph', children: [t('1. not a list')] },
            { type: 'paragraph', children: [t('- not a bullet')] },
            { type: 'paragraph', children: [t('> not a quote')] },
            { type: 'paragraph', children: [t('    not code')] },
          ],
        },
      ],
      footnotes: [],
      bibliography: null,
    });
    const md = renderExportMarkdown(doc);
    expect(md).toContain('\\*Candide\\* \\[with brackets\\]');
    const html = marked.parse(md, { async: false, gfm: true }) as string;
    const reader = document.createElement('div');
    reader.innerHTML = html;
    const paragraphs = [...reader.querySelectorAll('p')].map((p) => p.textContent);
    expect(paragraphs).toEqual([words, '# not a heading', '1. not a list', '- not a bullet', '> not a quote', '    not code']);
    expect(reader.querySelector('em, strong, code, del, h1, ol, ul, blockquote, pre')).toBeNull();
  });

  it('keeps emphasis when its words start or end with a space', () => {
    const doc = essay({
      sections: [{ type: 'chapter', writingId: 'x', title: 'X', depth: 0, blocks: [{ type: 'paragraph', children: [t('a'), { type: 'emphasis', children: [t(' b ')] }, t('c')] }] }],
      footnotes: [],
      bibliography: null,
    });
    expect(renderExportMarkdown(doc)).toBe('a *b* c\n');
  });

  it('gives a combined folder its group headings and untitled chapters their names', () => {
    const md = renderExportMarkdown(folder());
    expect(md).toContain('# Chapter one\n\nFirst.[^1]\n\n# Part two\n\n## Two\n\nSecond, no heading.[^2]\n\n## Three\n\nThird.');
  });

  it('escapes a literal escape character', () => {
    expect(escapeText('C:\\path')).toBe('C:\\\\path');
  });
});

describe('copy as rich text (issue #1165)', () => {
  it('puts headings, emphasis, quotes, a real table, endnotes and the bibliography on the clipboard', () => {
    const { html, text } = renderExportRichText(essay());
    const view = document.createElement('div');
    view.innerHTML = html;
    expect(view.querySelector('h1')?.innerHTML).toBe('On <em>Candide</em>');
    expect(view.querySelector('blockquote p')?.textContent).toBe('All is for the best.2');
    expect(view.querySelectorAll('table thead th')).toHaveLength(2);
    expect(view.querySelector('table tbody td')?.textContent).toBe('Candide | a tale');
    expect(view.querySelector('sup a')?.textContent).toBe('1');
    const notes = view.querySelector('.footnotes')!;
    expect(notes.querySelector('h2')?.textContent).toBe('Notes');
    expect(notes.querySelector('li em')?.textContent).toBe('Candide');
    expect(view.querySelector('.bibliography h2')?.textContent).toBe('Bibliography');
    // Notes come before the bibliography, as in a printed book.
    expect(html.indexOf('class="footnotes"')).toBeLessThan(html.indexOf('class="bibliography"'));

    expect(text).toContain('Voltaire mocks optimism.[1] See the notes.');
    expect(text).toContain('Notes\n\n1. Voltaire, Candide (Paris, 1759), see the text.\n2. Ibid., 3.');
    expect(text).toContain('3. Third\n  • Nested\n4. Fourth');
    expect(text).toContain('Book\tYear\nCandide | a tale\t1759');
  });

  it('copies a folder as the combined document', () => {
    const { html, text } = renderExportRichText(folder());
    expect(html.indexOf('Chapter one')).toBeLessThan(html.indexOf('Part two'));
    expect(html).toContain('<h2 dir="auto">Two</h2>');
    expect(text).toContain('Chapter one\n\nFirst.[1]\n\nPart two\n\nTwo\n\nSecond, no heading.[2]\n\nThree\n\nThird.');
  });

  it('writes HTML and plain text through ClipboardItem when the browser has it', async () => {
    const write = vi.fn(async (_items: unknown[]) => undefined);
    const had = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    const hadItem = (globalThis as { ClipboardItem?: unknown }).ClipboardItem;
    class Item {
      constructor(readonly parts: Record<string, Blob>) {}
    }
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { write } });
    (globalThis as { ClipboardItem?: unknown }).ClipboardItem = Item;
    try {
      await copyRichText({ html: '<p>Hi</p>', text: 'Hi' });
      const item = write.mock.calls[0][0][0] as Item;
      expect(Object.keys(item.parts)).toEqual(['text/html', 'text/plain']);
      expect(await item.parts['text/plain'].text()).toBe('Hi');
    } finally {
      if (had) Object.defineProperty(navigator, 'clipboard', had);
      else delete (navigator as { clipboard?: unknown }).clipboard;
      (globalThis as { ClipboardItem?: unknown }).ClipboardItem = hadItem;
    }
  });

  it('says NotAllowedError when neither way of copying is allowed', async () => {
    const had = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    const exec = (document as { execCommand?: unknown }).execCommand;
    (document as { execCommand?: unknown }).execCommand = () => false;
    try {
      await expect(copyRichText({ html: '<p>Hi</p>', text: 'Hi' })).rejects.toMatchObject({ name: 'NotAllowedError' });
    } finally {
      if (had) Object.defineProperty(navigator, 'clipboard', had);
      else delete (navigator as { clipboard?: unknown }).clipboard;
      (document as { execCommand?: unknown }).execCommand = exec;
    }
  });

  it('plain text of a writing with no notes has no Notes heading', () => {
    expect(renderExportPlainText(essay({ footnotes: [], bibliography: null }))).not.toContain('Notes');
  });
});

describe('DOCX (issue #1166)', () => {
  it('writes real Word footnotes that keep their italics and links', async () => {
    const { document: body, footnotes, zip } = await docxXml(essay());
    expect(body).toContain('<w:footnoteReference w:id="1"/>');
    expect(body).toContain('<w:footnoteReference w:id="2"/>');
    // No superscript stand-ins in the text.
    expect(body).not.toMatch(/<w:vertAlign w:val="superscript"\/>\s*<\/w:rPr>\s*<w:t[^>]*>1</);
    const note1 = footnotes.slice(footnotes.indexOf('<w:footnote w:id="1"'), footnotes.indexOf('<w:footnote w:id="2"'));
    expect(note1).toMatch(/<w:i\/>[\s\S]*Candide/);
    expect(note1).toContain('<w:hyperlink');
    const rels = await zip.file('word/_rels/footnotes.xml.rels')?.async('string');
    expect(rels).toContain('https://example.org/c');
  });

  it('uses Word’s Heading styles in the writing’s font, and its page setup', async () => {
    const doc = essay({ setup: { paper: 'letter', marginMm: 12.7, font: 'eb-garamond', lineSpacing: 2, citationStyle: 'chicago-notes' } });
    const { document: body, styles } = await docxXml(doc);
    expect(body).toContain('<w:pStyle w:val="Heading1"/>');
    expect(styles).toMatch(/w:styleId="Heading1"[\s\S]*?EB Garamond/);
    expect(styles).toContain('w:ascii="EB Garamond"');
    expect(styles).toMatch(/<w:pPrDefault><w:pPr><w:spacing [^>]*w:line="480"/);
    expect(body).toContain('<w:pgSz w:w="12240" w:h="15840"');
    expect(body).toMatch(/<w:pgMar w:top="720" w:right="720" w:bottom="720" w:left="720"/);
    expect(docxFontName('mono')).toBe('Courier New');
  });

  it('says every line height is a minimum, so LibreOffice neither clips images nor overlaps wrapped headings', async () => {
    const { document: body, styles, footnotes } = await docxXml(folder());
    const spacings = [body, styles, footnotes].join('').match(/<w:spacing [^>]*w:line="[^>]*>/g)!;
    expect(spacings.length).toBeGreaterThan(3);
    for (const spacing of spacings) expect(spacing).toContain('w:lineRule="auto"');
  });

  it('writes block quotes, lists, tables and the bibliography as native elements', async () => {
    const { document: body, zip } = await docxXml(essay());
    // A quote is an indented paragraph.
    expect(body).toMatch(/<w:ind w:left="720" w:right="720"\/>[\s\S]*?All is for the best/);
    // Lists are Word numbering: the ordered list starts at 3, the nested item is one level in.
    const numbering = await zip.file('word/numbering.xml')!.async('string');
    expect(numbering).toMatch(/<w:start w:val="3"\/>/);
    expect(body).toMatch(/<w:ilvl w:val="1"\/>[\s\S]*?Nested/);
    expect(body).toContain('☒ ');
    // A real table, its header row repeated on each page, its rows unsplit.
    expect(body).toContain('<w:tbl>');
    expect(body).toContain('<w:tblHeader/>');
    expect(body).toContain('<w:cantSplit/>');
    expect(body).toContain('<w:jc w:val="right"/>');
    // The bibliography hangs.
    expect(body).toMatch(/<w:ind w:left="720" w:hanging="720"\/>[\s\S]*?Paris, 1759\./);
  });

  it('embeds an image scaled to the text width, and makes one it couldn’t fetch a placeholder', async () => {
    const huge: EmbeddedImage = { data: PNG, width: 4000, height: 1000 };
    const { document: body, zip } = await docxXml(essay(), new Map([['https://example.org/map.png', huge]]));
    expect(Object.keys(zip.files).some((name) => name.startsWith('word/media/'))).toBe(true);
    const extent = /<wp:extent cx="(\d+)" cy="(\d+)"/.exec(body)!;
    // A4 less two 25.4 mm margins is 6.27 in: 5,732,145 EMU, give or take rounding.
    const width = Number(extent[1]);
    expect(width).toBeLessThanOrEqual(5_732_145);
    expect(width).toBeGreaterThan(5_700_000);
    expect(Number(extent[1]) / Number(extent[2])).toBeCloseTo(4, 1);

    const missing = await docxXml(essay());
    expect(missing.document).toContain('[image: Map]');
    expect(Object.keys(missing.zip.files).some((name) => name.startsWith('word/media/'))).toBe(false);
  });

  it('warns before the download about an image the browser can’t fetch, and fetches each once', async () => {
    const fetchImage = vi.fn(async (src: string) => (src.includes('map') ? null : { data: PNG, width: 1, height: 1 }));
    const doc = essay();
    const first = await embedImages(doc, fetchImage);
    const again = await embedImages({ sections: doc.sections, footnotes: [] }, fetchImage);
    expect(again).toBe(first);
    expect(fetchImage).toHaveBeenCalledTimes(1);
    expect(first.warnings).toEqual([
      { kind: 'image', chapter: null, count: 1, message: 'Image “Map”: its site doesn’t allow it to be saved in a file, shown as [image: Map]' },
    ]);
  });

  it('starts each chapter of a folder on a new page, with page numbers in the footer', async () => {
    const { document: body, zip } = await docxXml(folder());
    expect(body.match(/<w:sectPr/g)).toHaveLength(3);
    expect(body.match(/<w:type w:val="nextPage"\/>/g)).toHaveLength(2);
    // The group heading opens the page of the chapter after it.
    expect(body.indexOf('Part two')).toBeGreaterThan(body.indexOf('<w:sectPr'));
    const footers = Object.keys(zip.files).filter((name) => /word\/footer\d+\.xml/.test(name));
    expect(footers.length).toBeGreaterThan(0);
    expect(await zip.file(footers[0])!.async('string')).toContain('PAGE');
  });

  it('keeps right-to-left paragraphs right to left', async () => {
    const doc = essay({ sections: [{ type: 'chapter', writingId: 'x', title: 'X', depth: 0, blocks: [{ type: 'paragraph', children: [t('שלום עולם')] }] }], footnotes: [], bibliography: null });
    const { document: body } = await docxXml(doc);
    expect(body).toContain('<w:bidi/>');
    expect(body).toContain('<w:rtl/>');
    expect(body).toContain('שלום עולם');
  });

  it('is loaded only when a DOCX is made', () => {
    // export-formats reaches the writer through import(), so the Studio's first load doesn't carry it.
    const docx = EXPORT_FORMATS.find((f) => f.id === 'docx')!;
    expect(docx.action === 'download' && docx.render.toString()).toMatch(/\bimport\(|dynamic_import/);
  });
});

describe('PDF print layout (issue #1167)', () => {
  it('lays out the page from the page setup, with page numbers', () => {
    const a4 = renderPrintLayout(essay());
    expect(a4).toMatch(/@page \{\s*size: A4;\s*margin: 25.4mm;\s*@bottom-center \{ content: counter\(page\)/);
    expect(a4).toContain("font-family: Newsreader, Georgia, 'Times New Roman', serif; font-size: 12pt; line-height: 1.5;");
    const letter = renderPrintLayout(essay({ setup: { ...SETUP, paper: 'letter', marginMm: 38.1, lineSpacing: 2 } }));
    expect(letter).toContain('size: letter;');
    expect(letter).toContain('margin: 38.1mm;');
    expect(letter).toContain('line-height: 2;');
  });

  it('puts each chapter’s notes at its end, numbered through the whole document', () => {
    const view = document.createElement('div');
    view.innerHTML = renderPrintLayout(folder()).replace(/[\s\S]*<body>|<\/body>[\s\S]*/g, '');
    const chapters = [...view.querySelectorAll('section.chapter')];
    expect(chapters).toHaveLength(3);
    expect(chapters[0].querySelector('.footnotes ol')?.getAttribute('start')).toBe('1');
    expect(chapters[1].querySelector('.footnotes ol')?.getAttribute('start')).toBe('2');
    expect(chapters[1].querySelector('.footnotes li')?.textContent).toContain('Ibid., 3.');
    expect(chapters[2].querySelector('.footnotes')).toBeNull();
  });

  it('starts each chapter on a new page and never leaves a heading alone at the foot of one', () => {
    const html = renderPrintLayout(folder());
    expect(html).toContain('.chapter:not(:first-child):not(.follows-group), .group:not(:first-child):not(.follows-group), .bibliography { break-before: page;');
    const view = document.createElement('div');
    view.innerHTML = html.replace(/[\s\S]*<body>|<\/body>[\s\S]*/g, '');
    // The chapter after its group heading doesn't take another page.
    expect(view.querySelector('[data-writing="two"]')?.classList).toContain('follows-group');
    // A heading travels with the paragraph after it.
    const keep = view.querySelector('.keep')!;
    expect([...keep.children].map((c) => c.tagName)).toEqual(['H1', 'P']);
    expect(html).toMatch(/h1, h2, h3, h4, h5, h6 \{[^}]*break-after: avoid/);
    expect(html).toMatch(/tr, img \{ break-inside: avoid/);
    expect(html).toContain('thead { display: table-header-group; }');
  });

  it('keeps a long table out of a heading’s group, so it can still break between rows', () => {
    const doc = essay({
      sections: [{ type: 'chapter', writingId: 'x', title: 'X', depth: 0, blocks: [{ type: 'heading', level: 2, children: [t('Data')] }, { type: 'table', align: [null], header: [[t('A')]], rows: [[[t('1')]]] }] }],
    });
    const view = document.createElement('div');
    view.innerHTML = renderPrintLayout(doc).replace(/[\s\S]*<body>|<\/body>[\s\S]*/g, '');
    expect(view.querySelector('.keep table')).toBeNull();
    expect(view.querySelector('.keep h2')).toBeTruthy();
  });
});

describe('the formats with a real citation renderer (issues #1165–#1167)', () => {
  it('a Chicago-notes folder keeps one bibliography and continuous notes in every format', async () => {
    const bib = '## Bibliography\n\n[Voltaire. *Book*. 1992.](nostos:cite/b1?as=bib)';
    const one = chapter(`First[^1].\n\n[^1]: [Voltaire, *Full* (Knopf, 1992).](nostos:cite/b1?as=reference)\n\n${bib}`, 'One');
    const two = chapter(`Again[^1].\n\n[^1]: [V](nostos:cite/b1?as=reference)\n\n${bib}`, 'Two');
    const doc = await resolve([one, two], 'note', { renderer: renderer('note') });
    const md = renderExportMarkdown(doc);
    expect(md).toContain('[^1]: Voltaire, *Full* (Knopf, 1992).');
    expect(md).toContain('[^2]: Voltaire, *Short*.');
    expect(md.match(/## Bibliography/g)).toHaveLength(1);
    const { footnotes } = await docxXml(doc);
    expect(footnotes).toContain('w:id="2"');
  });
});
