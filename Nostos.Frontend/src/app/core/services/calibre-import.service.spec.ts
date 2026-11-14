import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import {
  CalibreImportDecision,
  CalibreImportService,
  CalibreStatus,
} from './calibre-import.service';

const STATUS: CalibreStatus = {
  configured: true,
  available: true,
  import: {
    status: 'idle',
    startedAt: null,
    finishedAt: null,
    total: 0,
    completed: 0,
    current: null,
    batchId: null,
    results: [],
    error: null,
  },
};

describe('CalibreImportService', () => {
  let service: CalibreImportService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(CalibreImportService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('opens the dialog plainly, or on a dropped zip', () => {
    expect(service.isOpen()).toBe(false);
    expect(service.pendingFile()).toBeNull();

    service.open();
    expect(service.isOpen()).toBe(true);

    const file = new File(['zip'], 'library.zip');
    service.openWithFile(file);
    expect(service.pendingFile()).toBe(file);
    expect(service.isOpen()).toBe(true);

    service.close();
    expect(service.isOpen()).toBe(false);
  });

  it('reads the library status', () => {
    let seen: CalibreStatus | undefined;
    service.status().subscribe((status) => (seen = status));

    const request = http.expectOne('/api/calibre/');
    expect(request.request.method).toBe('GET');
    request.flush(STATUS);

    expect(seen).toEqual(STATUS);
  });

  it('previews the library without writing anything', () => {
    service.preview().subscribe();

    const request = http.expectOne('/api/calibre/preview');
    expect(request.request.method).toBe('GET');
    request.flush({ source: 'folder', summary: {}, books: [], skipped: [], customColumns: [], warnings: [] });
  });

  it('starts an import with the owner’s decisions', () => {
    const decisions: CalibreImportDecision[] = [
      { sourceKey: 'calibre:1', formats: ['EPUB'], createCollections: true },
      { sourceKey: 'calibre:2' },
    ];
    service.start(decisions).subscribe();

    const request = http.expectOne('/api/calibre/import');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ decisions });
    request.flush(STATUS.import);
  });

  it('reads the running import and the recent batches', () => {
    service.progress().subscribe();
    const progress = http.expectOne('/api/calibre/import');
    expect(progress.request.method).toBe('GET');
    progress.flush(STATUS.import);

    service.recent().subscribe();
    const recent = http.expectOne('/api/calibre/batches');
    expect(recent.request.method).toBe('GET');
    recent.flush([]);
  });

  it('undoes one batch by id', () => {
    service.undo('batch 1').subscribe();

    const request = http.expectOne('/api/calibre/batches/batch 1');
    expect(request.request.method).toBe('DELETE');
    request.flush({ removed: 3, kept: 1 });
  });

  it('finalises a staged upload by session id', () => {
    service.completeUpload('session-1').subscribe();

    const request = http.expectOne('/api/calibre/upload/session-1/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBeNull();
    request.flush({});
  });

  it('passes a refusal through to the caller', () => {
    let status: number | undefined;
    service.start([]).subscribe({
      error: (error: { status: number }) => (status = error.status),
    });

    http.expectOne('/api/calibre/import').flush(
      { code: 'calibre_import_failed' },
      { status: 500, statusText: 'Server Error' },
    );

    expect(status).toBe(500);
  });
});
