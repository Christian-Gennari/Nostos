import { marked } from 'marked';
import { Note } from '../core/dtos/note.dtos';
import {
  ReferencePreviewPipe,
  buildReferenceRowMetaMap,
  clearReferencePreviewCache,
  computeReferencePreview,
  formatSourceRowLabel,
} from './reference-preview';

describe('reference-preview', () => {
  beforeEach(() => {
    clearReferencePreviewCache();
    vi.restoreAllMocks();
  });

  it('renders readable Markdown text with wikilinks resolved to their names', () => {
    const note = {
      id: 'preview-note',
      bookId: 'book-1',
      content: '## [[S2 Topic 01 — Attention and the Reader]]\n\n**A note without** _markup_ and [[Attention]].',
      createdAt: '2026-08-01T10:00:00Z',
    } as Note;

    const preview = computeReferencePreview(note);
    expect(preview).toBe('S2 Topic 01 — Attention and the Reader A note without markup and Attention.');
    expect(preview).not.toMatch(/[\[\]_*`#|]/);
  });

  it('memoizes parsed output across repeated calls and only reparses when content changes', () => {
    const parseSpy = vi.spyOn(marked, 'parse');
    const note: Note = {
      id: 'n-memo',
      bookId: 'b-1',
      bookTitle: 'Book One',
      content: '**Memoized** _markdown_ content',
      createdAt: '2026-08-01T10:00:00Z',
    };

    const first = computeReferencePreview(note);
    const second = computeReferencePreview(note);
    const label = formatSourceRowLabel(note);

    expect(first).toBe('Memoized markdown content');
    expect(second).toBe(first);
    expect(label).toBe('Inspect source from Book One: Memoized markdown content');
    expect(parseSpy).toHaveBeenCalledTimes(1);

    // Mutating note content invalidates the ref cache entry cleanly.
    note.content = '**Updated** markdown content';
    expect(computeReferencePreview(note)).toBe('Updated markdown content');
    expect(parseSpy).toHaveBeenCalledTimes(2);
  });

  it('precomputes row metadata map once per note batch', () => {
    const parseSpy = vi.spyOn(marked, 'parse');
    const notes: Note[] = [
      {
        id: 'n-1',
        bookId: 'b-1',
        bookTitle: 'Walden',
        selectedText: 'First *selected* line',
        content: 'Commentary',
        sourceAnchorKind: 'pdf_page',
        sourceAnchorValue: '12',
        anchorVerified: true,
        createdAt: '2026-08-01T10:00:00Z',
      },
      {
        id: 'n-2',
        bookId: 'b-1',
        bookTitle: 'Walden',
        content: 'Second **note** line',
        createdAt: '2026-08-01T10:00:00Z',
      },
    ];

    const map = buildReferenceRowMetaMap(notes);
    expect(map.get('n-1')).toEqual({
      preview: 'First selected line',
      provenance: 'Walden · p. 12',
      rowLabel: 'Inspect source from Walden · p. 12: First selected line',
      hasQuotePreview: true,
    });
    expect(map.get('n-2')).toEqual({
      preview: 'Second note line',
      provenance: 'Walden',
      rowLabel: 'Inspect source from Walden: Second note line',
      hasQuotePreview: false,
    });
    expect(parseSpy).toHaveBeenCalledTimes(2);
  });

  it('provides a pure ReferencePreviewPipe wrapper', () => {
    const pipe = new ReferencePreviewPipe();
    expect(pipe.transform(null)).toBe('');
    expect(
      pipe.transform({
        content: '**Hello** [[World]]',
      }),
    ).toBe('Hello World');
  });
});
