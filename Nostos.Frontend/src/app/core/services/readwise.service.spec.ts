import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ReadwiseService } from './readwise.service';
import type { HighlightImportDecision } from './highlight-import.service';

const STATUS = {
  connected: true,
  lastImportedAtUtc: '2026-08-01T08:00:00Z',
  importedThroughUtc: '2026-08-01T07:00:00Z',
};

describe('ReadwiseService', () => {
  let service: ReadwiseService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ReadwiseService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads whether the library is connected', () => {
    service.status().subscribe();

    const request = http.expectOne('/api/notes/imports/readwise');
    expect(request.request.method).toBe('GET');
    request.flush(STATUS);
  });

  it('sends the token to the server only', () => {
    service.connect('rw-token').subscribe();

    const request = http.expectOne('/api/notes/imports/readwise/connect');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'rw-token' });
    request.flush(STATUS);
  });

  it('disconnects the library', () => {
    service.disconnect().subscribe();

    const request = http.expectOne('/api/notes/imports/readwise');
    expect(request.request.method).toBe('DELETE');
    request.flush({ ...STATUS, connected: false });
  });

  it('asks the server to fetch and propose the export', () => {
    service.preview().subscribe();

    const request = http.expectOne('/api/notes/imports/readwise/preview');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({});
    request.flush({ source: 'readwise', books: [] });
  });

  it('commits decisions as one undo batch', () => {
    const decisions: HighlightImportDecision[] = [{ sourceKey: 'rw:1', bookId: 'b1' }];
    service.commit(decisions, 'batch-1').subscribe();

    const request = http.expectOne('/api/notes/imports/readwise/commit');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ decisions, batchId: 'batch-1' });
    request.flush({ batchId: 'batch-1', source: 'readwise', books: [] });
  });

  it('sends a null batch id when the import is not next to an existing batch', () => {
    service.commit([]).subscribe();

    const request = http.expectOne('/api/notes/imports/readwise/commit');
    expect(request.request.body).toEqual({ decisions: [], batchId: null });
    request.flush({ batchId: null, source: 'readwise', books: [] });
  });

  it('passes a rejected token through to the caller', () => {
    let status: number | undefined;
    service.connect('bad').subscribe({
      error: (error: { status: number }) => (status = error.status),
    });

    http.expectOne('/api/notes/imports/readwise/connect').flush(
      { code: 'readwise_invalid_token' },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(status).toBe(400);
  });
});
