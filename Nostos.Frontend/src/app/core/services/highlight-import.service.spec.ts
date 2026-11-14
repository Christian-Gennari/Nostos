import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import {
  APPLE_BOOKS_FOLDER,
  HIGHLIGHT_IMPORT_SOURCE_LABEL,
  HighlightImportDecision,
  HighlightImportService,
  appleBooksFiles,
  groupHighlightImportFiles,
} from './highlight-import.service';

const file = (name: string) => new File([name], name);

describe('Apple Books file grouping (issue #1357)', () => {
  it('keeps only the two databases and their -wal and -shm files', () => {
    const files = [
      file('AEAnnotation_v10312011_1712345678901.sqlite'),
      file('BKLibrary_v10312011_1712345678901.sqlite'),
      file('AEAnnotation_v10312011_1712345678901.sqlite-wal'),
      file('BKLibrary_v10312011_1712345678901.sqlite-shm'),
      file('cover.jpg'),
      file('Books.plist'),
    ];

    expect(appleBooksFiles(files).map((f) => f.name)).toEqual([
      'AEAnnotation_v10312011_1712345678901.sqlite',
      'BKLibrary_v10312011_1712345678901.sqlite',
      'AEAnnotation_v10312011_1712345678901.sqlite-wal',
      'BKLibrary_v10312011_1712345678901.sqlite-shm',
    ]);
  });

  it('matches the database names whatever their case', () => {
    const files = [file('aeannotation_1.SQLITE'), file('bklibrary_1.SqLite-WAL')];

    expect(appleBooksFiles(files)).toEqual(files);
  });

  it('sends the highlights database first, with its companions, and every other file alone', () => {
    const annotations = file('AEAnnotation_1.sqlite');
    const library = file('BKLibrary_1.sqlite');
    const wal = file('AEAnnotation_1.sqlite-wal');
    const other = file('kobo.sqlite');

    expect(groupHighlightImportFiles([other, annotations, library, wal])).toEqual([
      { file: annotations, companions: [library, wal] },
      { file: other, companions: [] },
    ]);
  });

  it('leads with the library database when the highlights one is absent', () => {
    const library = file('BKLibrary_1.sqlite');
    const shm = file('BKLibrary_1.sqlite-shm');
    const other = file('readwise.csv');

    expect(groupHighlightImportFiles([other, library, shm])).toEqual([
      { file: library, companions: [shm] },
      { file: other, companions: [] },
    ]);
  });

  it('sends every file on its own when no Apple Books database is chosen', () => {
    const files = [file('kobo.sqlite'), file('AEAnnotation_1.sqlite-wal')];

    expect(groupHighlightImportFiles(files)).toEqual([
      { file: files[0], companions: [] },
      { file: files[1], companions: [] },
    ]);
  });

  it('leaves a file that is not an Apple database its own upload', () => {
    const annotations = file('AEAnnotation_1.sqlite');
    const other = file('Libby.json');

    expect(groupHighlightImportFiles([annotations, other])[0]).toEqual({
      file: annotations,
      companions: [],
    });
  });

  it('names the folder where Books keeps its databases', () => {
    expect(APPLE_BOOKS_FOLDER).toBe(
      '~/Library/Containers/com.apple.iBooksX/Data/Documents',
    );
  });

  it('labels every source the server can report', () => {
    expect(HIGHLIGHT_IMPORT_SOURCE_LABEL).toEqual({
      kobo: 'Kobo',
      koreader: 'KOReader',
      kindle: 'Kindle',
      readwise: 'Readwise',
      applebooks: 'Apple Books',
    });
  });
});

describe('HighlightImportService', () => {
  let service: HighlightImportService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(HighlightImportService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('opens on the requested step, or on files a drop zone handed over', () => {
    expect(service.isOpen()).toBe(false);
    expect(service.startOn()).toBe('all');

    service.open('readwise');
    expect(service.isOpen()).toBe(true);
    expect(service.startOn()).toBe('readwise');

    const files = [file('export.csv')];
    service.openWithFiles(files);
    expect(service.startOn()).toBe('all');
    expect(service.pendingFiles()).toEqual(files);

    service.close();
    expect(service.isOpen()).toBe(false);
  });

  it('previews a file and its companions as one upload', () => {
    service.preview(file('AEAnnotation_1.sqlite'), [
      file('BKLibrary_1.sqlite'),
      file('AEAnnotation_1.sqlite-wal'),
    ]).subscribe();

    const request = http.expectOne('/api/notes/imports/preview');
    expect(request.request.method).toBe('POST');
    const body = request.request.body as FormData;
    expect((body.get('file') as File).name).toBe('AEAnnotation_1.sqlite');
    expect(body.getAll('companion').map((entry) => (entry as File).name)).toEqual([
      'BKLibrary_1.sqlite',
      'AEAnnotation_1.sqlite-wal',
    ]);
    request.flush({ source: 'applebooks', books: [] });
  });

  it('commits the decisions beside the file, as one undo batch when known', () => {
    const decisions: HighlightImportDecision[] = [
      { sourceKey: 'apple:1', bookId: 'b1' },
      { sourceKey: 'apple:2', create: true, excludedHighlights: [1, 3] },
    ];
    service.commit(file('notes.csv'), decisions, 'batch-7', [file('companion.csv')]).subscribe();

    const request = http.expectOne('/api/notes/imports/commit');
    expect(request.request.method).toBe('POST');
    const body = request.request.body as FormData;
    expect((body.get('file') as File).name).toBe('notes.csv');
    expect((body.get('companion') as File).name).toBe('companion.csv');
    expect(JSON.parse(body.get('decisions') as string)).toEqual(decisions);
    expect(body.get('batchId')).toBe('batch-7');
    request.flush({ batchId: 'batch-7', source: 'kobo', books: [] });
  });

  it('leaves the batch id out when there is none', () => {
    service.commit(file('notes.csv'), []).subscribe();

    const body = http.expectOne('/api/notes/imports/commit').request.body as FormData;
    expect(body.has('batchId')).toBe(false);
    expect(body.get('decisions')).toBe('[]');
  });

  it('reads the recent imports, and the newest without an undo batch chosen', () => {
    service.recent().subscribe();
    const recent = http.expectOne('/api/notes/imports/batches');
    expect(recent.request.method).toBe('GET');
    recent.flush([]);

    service.starterPassages().subscribe();
    const latest = http.expectOne('/api/notes/imports/starter');
    expect(latest.request.params.keys()).toEqual([]);
    latest.flush([]);
  });

  it('asks for the passages of the chosen imports', () => {
    service.starterPassages(['a', 'b']).subscribe();

    const request = http.expectOne((candidate) => candidate.url === '/api/notes/imports/starter');
    expect(request.request.params.get('batchIds')).toBe('a,b');
    request.flush([]);
  });

  it('undoes one import by batch id', () => {
    service.undo('batch-7').subscribe();

    const request = http.expectOne('/api/notes/imports/batches/batch-7');
    expect(request.request.method).toBe('DELETE');
    request.flush({ removed: 4 });
  });

  it('passes a refused preview through to the caller', () => {
    let status: number | undefined;
    service.preview(file('x.csv')).subscribe({
      error: (error: { status: number }) => (status = error.status),
    });

    http.expectOne('/api/notes/imports/preview').flush(
      { code: 'import_unreadable' },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(status).toBe(400);
  });
});
