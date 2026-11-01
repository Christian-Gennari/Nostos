import { HttpContext } from '@angular/common/http';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import {
  LIBRARY_REVISIONS_HEADER,
  LIBRARY_WRITE_HANDLED,
  LibraryChangesService,
} from './library-changes.service';
import { libraryWriteInterceptor } from './library-write.interceptor';

describe('libraryWriteInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let noteOwnWrite: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    noteOwnWrite = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([libraryWriteInterceptor])),
        provideHttpClientTesting(),
        { provide: LibraryChangesService, useValue: { noteOwnWrite } },
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  it('hands the revisions a handled write committed to the change feed', () => {
    http
      .put('/api/books/1', {}, { context: new HttpContext().set(LIBRARY_WRITE_HANDLED, true) })
      .subscribe();

    backend.expectOne('/api/books/1').flush({}, { headers: { [LIBRARY_REVISIONS_HEADER]: '12,13' } });

    expect(noteOwnWrite).toHaveBeenCalledWith('12,13');
  });

  it('leaves a write that did not opt in alone, so its announcement still re-reads', () => {
    http.put('/api/books/1', {}).subscribe();

    backend.expectOne('/api/books/1').flush({}, { headers: { [LIBRARY_REVISIONS_HEADER]: '12' } });

    expect(noteOwnWrite).not.toHaveBeenCalled();
  });

  it('records nothing for a write that failed', () => {
    http
      .put('/api/books/1', {}, { context: new HttpContext().set(LIBRARY_WRITE_HANDLED, true) })
      .subscribe({ error: () => undefined });

    backend.expectOne('/api/books/1').flush('no', { status: 500, statusText: 'Server Error' });

    expect(noteOwnWrite).not.toHaveBeenCalled();
  });
});
