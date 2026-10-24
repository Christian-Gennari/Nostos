import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { PortableLibraryService } from './portable-library.service';

describe('PortableLibraryService', () => {
  let service: PortableLibraryService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(PortableLibraryService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('requests the canonical portable export as a progress-reporting blob download', () => {
    service.exportArchive().subscribe();

    const request = http.expectOne('/api/portability/export');
    expect(request.request.method).toBe('GET');
    expect(request.request.responseType).toBe('blob');
    expect(request.request.reportProgress).toBe(true);

    request.flush(new Blob(['portable']));
  });

  it('asks for the notes as Markdown, saying whether writings come too', () => {
    service.exportNotesMarkdown(true).subscribe();
    service.exportNotesMarkdown(false).subscribe();

    const [withWritings, without] = http.match((request) => request.url === '/api/portability/markdown');
    expect(withWritings.request.method).toBe('GET');
    expect(withWritings.request.responseType).toBe('blob');
    expect(withWritings.request.params.get('writings')).toBe('true');
    expect(without.request.params.get('writings')).toBe('false');

    withWritings.flush(new Blob(['zip']));
    without.flush(new Blob(['zip']));
  });
});
