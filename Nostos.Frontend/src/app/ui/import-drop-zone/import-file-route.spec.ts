import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  classifyImportFile,
  csvHeader,
  looksLikeGoodreads,
  looksLikeReadwise,
  looksLikeStorygraph,
  routeImportFiles,
  type ImportProbe,
} from './import-file-route';

const GOODREADS_HEADER =
  'Book Id,Title,Author,Author l-f,Additional Authors,ISBN,ISBN13,My Rating,Average Rating,Publisher,' +
  'Date Read,Bookshelves,Exclusive Shelf,My Review,Read Count';
const STORYGRAPH_HEADER =
  'Title,Authors,Contributors,ISBN/UID,Format,Read Status,Date Added,Last Date Read,Star Rating,Review,Tags';
const READWISE_HEADER = 'Highlight,Book Title,Book Author,Amazon Book ID,Note,Tags,Location Type,Location';

function file(content: string, name: string): File {
  return new File([content], name);
}

function binary(name: string): File {
  return new File([new Uint8Array([0x53, 0x51, 0x4c, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74])], name);
}

const archive = () => new File([new Uint8Array([0x50, 0x4b, 0x03, 0x04])], 'library.nostos');

const noProbe = vi.fn(async (): Promise<ImportProbe> => ({ flow: null, message: null }));

describe('import file routing', () => {
  beforeEach(() => vi.clearAllMocks());

  it('sends every highlight source to the highlight flow by extension and content', async () => {
    const files = [
      binary('KoboReader.sqlite'),
      file('return { annotations = {} }', 'metadata.lua'),
      file('Pride and Prejudice\n- Your Highlight on page 1', 'My Clippings.txt'),
      binary('AEAnnotation_v10312011_1727_local.sqlite'),
      file(`${READWISE_HEADER}\n\"A line\",A Book,An Author`, 'readwise-export.csv'),
    ];

    for (const file of files) {
      const route = await routeImportFiles([file], noProbe);
      expect(route, file.name).toMatchObject({ kind: 'highlights', files: [file] });
    }
    expect(noProbe).not.toHaveBeenCalled();
  });

  it('tells Goodreads and StoryGraph apart from a Readwise CSV by the header row', async () => {
    const goodreads = file(`${GOODREADS_HEADER}\n101,A Book,An Author`, 'goodreads_library_export.csv');
    const storygraph = file(`${STORYGRAPH_HEADER}\nA Book,An Author,,9780000000019,paperback,read`, 'storygraph_export.csv');
    const readwise = file(`${READWISE_HEADER}\n\"A line\",A Book,An Author`, 'readwise-export.csv');

    expect(await classifyImportFile(goodreads)).toBe('reading-history');
    expect(await classifyImportFile(storygraph)).toBe('reading-history');
    expect(await classifyImportFile(readwise)).toBe('highlights');
    expect(noProbe).not.toHaveBeenCalled();

    expect(await routeImportFiles([goodreads], noProbe)).toMatchObject({
      kind: 'reading-history',
      file: goodreads,
    });
    expect(await routeImportFiles([storygraph], noProbe)).toMatchObject({
      kind: 'reading-history',
      file: storygraph,
    });
  });

  it('routes a .nostos archive to the whole-library flow', async () => {
    const file = archive();
    expect(await routeImportFiles([file], noProbe)).toEqual({ kind: 'archive', file });
  });

  it('refuses a .nostos file that is not an archive', async () => {
    const file = new File(['not a zip'], 'library.nostos');
    const route = await routeImportFiles([file], noProbe);
    expect(route).toMatchObject({ kind: 'unknown' });
    expect(route.kind === 'unknown' && route.message).toContain('not a Nostos archive');
  });

  it('sends several highlight files as one import', async () => {
    const first = file('return { annotations = {} }', 'metadata.lua');
    const second = file('return { annotations = {} }', 'metadata.multiline.lua');
    expect(await routeImportFiles([first, second], noProbe)).toEqual({
      kind: 'highlights',
      files: [first, second],
    });
  });

  it('keeps Apple Books -wal and -shm companions with its databases', async () => {
    const annotations = binary('AEAnnotation_v10312011_1727_local.sqlite');
    const library = binary('BKLibrary-1-091020131601.sqlite');
    const wal = binary('AEAnnotation_v10312011_1727_local.sqlite-wal');
    expect(await routeImportFiles([annotations, library, wal], noProbe)).toEqual({
      kind: 'highlights',
      files: [annotations, library, wal],
    });
    expect(noProbe).not.toHaveBeenCalled();
  });

  it('asks for one reading history at a time rather than importing a guess', async () => {
    const history = file(`${GOODREADS_HEADER}\n101,A Book,An Author`, 'goodreads_library_export.csv');
    const kobo = binary('KoboReader.sqlite');
    const route = await routeImportFiles([history, kobo], noProbe);
    expect(route).toMatchObject({ kind: 'unknown' });
    expect(route.kind === 'unknown' && route.message).toContain('one reading history at a time');
  });

  it('falls back to the server for an unknown file and uses its answer', async () => {
    const file = new File(['%PDF-1.7'], 'book.pdf');
    const probe = vi.fn(async (): Promise<ImportProbe> => ({ flow: 'reading-history', message: null }));
    expect(await routeImportFiles([file], probe)).toMatchObject({ kind: 'reading-history', file });
    expect(probe).toHaveBeenCalledWith(file);
  });

  it('keeps the server refusal for the supported-sources message', async () => {
    const file = new File(['%PDF-1.7'], 'book.pdf');
    const probe = vi.fn(async (): Promise<ImportProbe> => ({
      flow: null,
      message: 'This file is not one Nostos can import highlights from.',
    }));
    const route = await routeImportFiles([file], probe);
    expect(route.kind).toBe('unknown');
    expect(route.kind === 'unknown' && route.serverMessage).toContain('not one Nostos can import');
  });

  it('refuses an empty file before asking anyone', async () => {
    const probe = vi.fn(async (): Promise<ImportProbe> => ({ flow: 'highlights', message: null }));
    expect(await routeImportFiles([new File([], 'empty.csv')], probe)).toMatchObject({ kind: 'unknown' });
    expect(probe).not.toHaveBeenCalled();
  });

  it('reads a quoted header row as a CSV, not on a comma inside a title', () => {
    expect(csvHeader('\uFEFF"Book Id","Title, with comma",Author\n1,x,y')).toEqual([
      'Book Id',
      'Title, with comma',
      'Author',
    ]);
    expect(looksLikeGoodreads(['Book Id', 'Exclusive Shelf', 'My Rating'])).toBe(true);
    expect(looksLikeStorygraph(['Read Status', 'Star Rating', 'ISBN/UID'])).toBe(true);
    expect(looksLikeReadwise(['Highlight', 'Book Title'])).toBe(true);
    expect(looksLikeReadwise(['Title', 'Author'])).toBe(false);
  });
});
