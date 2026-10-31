import apaCsl from '../../../../public/csl/styles/apa.csl' with { loader: 'text' };
import chicagoNotesCsl from '../../../../public/csl/styles/chicago-notes-bibliography.csl' with { loader: 'text' };
import enUs from '../../../../public/csl/locales/locales-en-US.xml' with { loader: 'text' };
import enGb from '../../../../public/csl/locales/locales-en-GB.xml' with { loader: 'text' };

import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import type { Book } from '../../core/dtos/book.dtos';
import type { WritingDto } from '../../core/dtos/writing.dtos';
import { BooksService } from '../../core/services/books.service';
import { WritingsService } from '../../core/services/writings.service';
import type { ExportDocument, ExportInline, PageSetup } from './export-model';
import { inlineText } from './export-model';
import { ExportService, citedBookIds } from './export.service';

const schopenhauer = {
  id: 'b1',
  title: 'The World as Will and Representation',
  subtitle: null,
  author: 'Arthur Schopenhauer',
  editor: null,
  translator: 'E. F. J. Payne',
  publisher: 'Dover',
  placeOfPublication: 'New York',
  publishedDate: '1851',
  edition: null,
} as unknown as Book;

const items: WritingDto[] = [
  { id: 'thesis', name: 'Thesis', type: 'Folder', parentId: null, updatedAt: '' },
  { id: 'one', name: 'One', type: 'Document', parentId: 'thesis', updatedAt: '' },
  { id: 'two', name: 'Two', type: 'Document', parentId: 'thesis', updatedAt: '' },
];

const contents: Record<string, string> = {
  one: 'The will [(Schopenhauer, 1851, p. 42)](nostos:cite/b1?p=42&as=reference) and [(Lost, 1900)](nostos:cite/gone?as=reference).',
  two: [
    'Again [(Schopenhauer, 1851, p. 42)](nostos:cite/b1?p=42&as=reference).',
    '## References',
    '[Schopenhauer, A. Old entry.](nostos:cite/b1?as=bib)',
  ].join('\n\n'),
};

const SETUP: PageSetup = { paper: 'a4', marginMm: 25.4, font: 'newsreader', lineSpacing: 1.5, citationStyle: 'chicago-notes' };

function words(inlines: readonly ExportInline[]): string {
  return inlines
    .map((inline) => (inline.type === 'emphasis' ? `*${words(inline.children)}*` : inline.type === 'footnoteRef' ? `[^${inline.number}]` : inlineText([inline])))
    .join('');
}

function body(doc: ExportDocument): string[] {
  return doc.sections.flatMap((section) => (section.type === 'chapter' ? section.blocks.map((b) => (b.type === 'paragraph' ? words(b.children) : b.type)) : []));
}

describe('ExportService (issue #1164): real writings, books and citation styles', () => {
  const files: Record<string, string> = {
    '/csl/styles/apa.csl': apaCsl,
    '/csl/styles/chicago-notes-bibliography.csl': chicagoNotesCsl,
    '/csl/locales/locales-en-US.xml': enUs,
    '/csl/locales/locales-en-GB.xml': enGb,
  };
  let realFetch: typeof fetch;
  let service: ExportService;
  let writings: {
    get: ReturnType<typeof vi.fn>;
    listSources: ReturnType<typeof vi.fn>;
    getPageSetup: ReturnType<typeof vi.fn>;
    savePageSetup: ReturnType<typeof vi.fn>;
  };

  beforeAll(() => {
    realFetch = globalThis.fetch;
    globalThis.fetch = (async (url: string) => {
      const text = files[String(url)];
      return { ok: text !== undefined, text: async () => text } as Response;
    }) as typeof fetch;
  });

  afterAll(() => {
    globalThis.fetch = realFetch;
  });

  beforeEach(() => {
    writings = {
      get: vi.fn((id: string) => of({ id, name: id, content: contents[id] ?? '', updatedAt: '' })),
      listSources: vi.fn(() => of([])),
      getPageSetup: vi.fn(() => of(null)),
      savePageSetup: vi.fn((_id: string, dto: unknown) => of(dto)),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: WritingsService, useValue: writings },
        { provide: BooksService, useValue: { get: vi.fn((id: string) => (id === 'b1' ? of(schopenhauer) : throwError(() => new Error('gone')))) } },
      ],
    });
    service = TestBed.inject(ExportService);
  });

  it('exports a folder in Chicago notes: one full note, the short form in a later chapter, one bibliography', async () => {
    const source = await service.load(items, 'thesis');
    const doc = await service.resolve(source, SETUP);

    expect(body(doc)).toEqual(['The will[^1] and[^2].', 'Again[^3].']);
    expect(doc.footnotes.map((note) => words(note.children))).toEqual([
      'Arthur Schopenhauer, *The World as Will and Representation*, trans. E. F. J. Payne (Dover, 1851), 42.',
      '(Lost, 1900)',
      'Schopenhauer, *The World as Will and Representation*, 42.',
    ]);
    expect(doc.bibliography?.heading).toBe('Bibliography');
    expect(doc.bibliography?.entries.map((entry) => words(entry.children))).toEqual([
      'Schopenhauer, Arthur. *The World as Will and Representation*. Translated by E. F. J. Payne. Dover, 1851.',
    ]);
    expect(doc.warnings).toEqual([expect.objectContaining({ kind: 'citation', chapter: 'One', message: '(Lost, 1900): book no longer in your library, kept as written' })]);
  });

  it('gives a writing with citations and no list its bibliography, in each kind of style (#1252)', async () => {
    const source = await service.load(items, 'one');

    const notes = await service.resolve(source, SETUP);
    expect(notes.footnotes).toHaveLength(2);
    expect(notes.bibliography?.heading).toBe('Bibliography');
    expect(notes.bibliography?.entries.map((entry) => words(entry.children))).toEqual([
      'Schopenhauer, Arthur. *The World as Will and Representation*. Translated by E. F. J. Payne. Dover, 1851.',
    ]);

    const apa = await service.resolve(source, { ...SETUP, citationStyle: 'apa' });
    expect(apa.bibliography?.heading).toBe('References');
    expect(apa.bibliography?.entries.map((entry) => entry.book)).toEqual(['b1']);

    const house = await service.resolve(source, { ...SETUP, citationStyle: 'nostos' });
    expect(house.bibliography?.heading).toBe('Bibliography');
    expect(house.bibliography?.entries.map((entry) => entry.book)).toEqual(['b1']);
  });

  it('writes the same folder again in APA: citations back in the sentence, “References”', async () => {
    const source = await service.load(items, 'thesis');
    const doc = await service.resolve(source, { ...SETUP, citationStyle: 'apa' });

    expect(body(doc)).toEqual(['The will (Schopenhauer, 1851, p. 42) and (Lost, 1900).', 'Again (Schopenhauer, 1851, p. 42).']);
    expect(doc.footnotes).toEqual([]);
    expect(doc.bibliography?.heading).toBe('References');
    expect(doc.citationStyle).toEqual({ id: 'apa', label: 'APA 7', kind: 'in-text' });
  });

  it('exports the open writing as it is now, ahead of its autosave', async () => {
    const source = await service.load(items, 'one', new Map([['one', 'Fresh words.']]));
    expect(source.chapters.map((c) => c.markdown)).toEqual(['Fresh words.']);
    expect(writings.get).not.toHaveBeenCalled();
  });

  it('reads a saved page setup with distrust, and has none until one is saved', async () => {
    expect(await service.savedSetup('one', SETUP)).toBeNull();
    writings.getPageSetup.mockReturnValueOnce(of({ paper: 'letter', marginMm: 38.1, font: 'nope', lineSpacing: 2, citationStyle: 'mla' }));
    expect(await service.savedSetup('one', SETUP)).toEqual({ ...SETUP, paper: 'letter', marginMm: 38.1, lineSpacing: 2, citationStyle: 'mla' });

    await service.saveSetup('thesis', SETUP);
    expect(writings.savePageSetup).toHaveBeenCalledWith('thesis', SETUP);
  });

  it('finds the books a writing cites, bibliography entries included', () => {
    expect(citedBookIds(contents['two'] + ' [x](nostos:cite/../bad)')).toEqual(['b1']);
  });
});
