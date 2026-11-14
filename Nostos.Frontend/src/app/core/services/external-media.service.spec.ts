import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ExternalMediaService } from './external-media.service';

const STATUS = {
  configured: true,
  mounted: true,
  linkedBooks: 4,
  scan: {
    status: 'idle' as const,
    startedAt: null,
    finishedAt: null,
    examined: 0,
    total: 0,
    error: null,
  },
};

describe('ExternalMediaService', () => {
  let service: ExternalMediaService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ExternalMediaService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the folder status', () => {
    service.status().subscribe();

    const request = http.expectOne('/api/external-media');
    expect(request.request.method).toBe('GET');
    request.flush(STATUS);
  });

  it('starts a scan', () => {
    service.startScan().subscribe();

    const request = http.expectOne('/api/external-media/scan');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBeNull();
    request.flush(STATUS.scan);
  });

  it('reads a page of the scan with its offset and limit', () => {
    service.scan().subscribe();

    const request = http.expectOne((candidate) => candidate.url === '/api/external-media/scan');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('offset')).toBe('0');
    expect(request.request.params.get('limit')).toBe('200');
    expect(request.request.params.has('status')).toBe(false);
    request.flush({ scan: STATUS.scan, summary: null, items: [], matchingItems: 0, unsupportedSamples: [], missing: [] });
  });

  it('filters the scan by status, at a chosen page', () => {
    service.scan('review', 40, 20).subscribe();

    const request = http.expectOne((candidate) => candidate.url === '/api/external-media/scan');
    expect(request.request.params.get('status')).toBe('review');
    expect(request.request.params.get('offset')).toBe('40');
    expect(request.request.params.get('limit')).toBe('20');
    request.flush({ scan: STATUS.scan, summary: null, items: [], matchingItems: 0, unsupportedSamples: [], missing: [] });
  });

  it('starts an import of the chosen books and reads its progress', () => {
    const body = { allReady: true, except: ['a'], keys: ['b'] };
    service.startImport(body).subscribe();

    const start = http.expectOne('/api/external-media/import');
    expect(start.request.method).toBe('POST');
    expect(start.request.body).toEqual(body);
    start.flush({
      status: 'importing',
      startedAt: null,
      finishedAt: null,
      total: 2,
      completed: 0,
      current: null,
      outcomes: [],
    });

    service.importState().subscribe();
    const state = http.expectOne('/api/external-media/import');
    expect(state.request.method).toBe('GET');
    state.flush({
      status: 'done',
      startedAt: null,
      finishedAt: null,
      total: 2,
      completed: 2,
      current: null,
      outcomes: [],
    });
  });

  it('reads one book’s link and file status', () => {
    service.book('b1').subscribe();

    const request = http.expectOne('/api/external-media/books/b1');
    expect(request.request.method).toBe('GET');
    request.flush({ linked: true, files: 3, unavailableFiles: 1 });
  });

  it('passes a server that has no folder feature through to the caller', () => {
    let status: number | undefined;
    service.status().subscribe({
      error: (error: { status: number }) => (status = error.status),
    });

    http.expectOne('/api/external-media').flush(
      { message: 'Not found' },
      { status: 404, statusText: 'Not Found' },
    );

    expect(status).toBe(404);
  });
});
