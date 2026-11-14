import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import {
  READING_HISTORY_SOURCE_EXPORT_HINT,
  READING_HISTORY_SOURCE_LABEL,
  ReadingHistoryImportDecision,
  ReadingHistoryImportService,
} from './reading-history-import.service';

describe('ReadingHistoryImportService', () => {
  let service: ReadingHistoryImportService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ReadingHistoryImportService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('names and hints for both services the server reads', () => {
    expect(READING_HISTORY_SOURCE_LABEL).toEqual({
      goodreads: 'Goodreads',
      storygraph: 'StoryGraph',
    });
    expect(READING_HISTORY_SOURCE_EXPORT_HINT['goodreads']).toContain('Import and export');
    expect(READING_HISTORY_SOURCE_EXPORT_HINT['storygraph']).toContain('Export');
  });

  it('opens plainly, or on a file a drop zone handed over', () => {
    expect(service.isOpen()).toBe(false);

    service.open();
    expect(service.isOpen()).toBe(true);

    const file = new File(['csv'], 'goodreads_library_export.csv');
    service.openWithFile(file);
    expect(service.pendingFile()).toBe(file);
    expect(service.isOpen()).toBe(true);

    service.close();
    expect(service.isOpen()).toBe(false);
  });

  it('previews the export without writing anything', () => {
    service.preview(new File(['csv'], 'goodreads_library_export.csv')).subscribe();

    const request = http.expectOne('/api/books/imports/preview');
    expect(request.request.method).toBe('POST');
    const body = request.request.body as FormData;
    expect((body.get('file') as File).name).toBe('goodreads_library_export.csv');
    request.flush({ source: 'goodreads', books: [] });
  });

  it('commits the rows the owner answered', () => {
    const decisions: ReadingHistoryImportDecision[] = [
      { sourceKey: 'goodreads:1', bookId: 'b1' },
      { sourceKey: 'goodreads:2', create: true, mapShelves: true },
    ];
    service.commit(new File(['csv'], 'export.csv'), decisions).subscribe();

    const request = http.expectOne('/api/books/imports/commit');
    expect(request.request.method).toBe('POST');
    const body = request.request.body as FormData;
    expect((body.get('file') as File).name).toBe('export.csv');
    expect(JSON.parse(body.get('decisions') as string)).toEqual(decisions);
    request.flush({ batchId: 'batch-1', source: 'goodreads', books: [] });
  });

  it('reads the recent imports and undoes one', () => {
    service.recent().subscribe();
    const recent = http.expectOne('/api/books/imports/batches');
    expect(recent.request.method).toBe('GET');
    recent.flush([]);

    service.undo('batch-1').subscribe();
    const undo = http.expectOne('/api/books/imports/batches/batch-1');
    expect(undo.request.method).toBe('DELETE');
    undo.flush({ removed: 2 });
  });

  it('passes a refused commit through to the caller', () => {
    let status: number | undefined;
    service.commit(new File(['csv'], 'export.csv'), []).subscribe({
      error: (error: { status: number }) => (status = error.status),
    });

    http.expectOne('/api/books/imports/commit').flush(
      { code: 'import_unreadable' },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(status).toBe(400);
  });
});
