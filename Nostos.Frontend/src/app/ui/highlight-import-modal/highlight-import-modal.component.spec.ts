import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { of, throwError } from 'rxjs';

import { HighlightImportModal } from './highlight-import-modal.component';
import {
  HighlightImportBook,
  HighlightImportService,
} from '../../core/services/highlight-import.service';

const book = (overrides: Partial<HighlightImportBook>): HighlightImportBook => ({
  status: 'matched',
  bookId: 'b1',
  bookTitle: 'Synthetic Book',
  sourceTitle: 'Synthetic Book',
  sourceAuthor: 'Ada Reader',
  annotationCount: 0,
  importedCount: 0,
  duplicateCount: 0,
  skippedCount: 0,
  message: null,
  ...overrides,
});

describe('HighlightImportModal', () => {
  let fixture: ComponentFixture<HighlightImportModal>;
  let component: HighlightImportModal;
  let service: HighlightImportService;
  let router: Router;

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const file = (name: string) => new File(['x'], name);

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HighlightImportModal],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(HighlightImportModal);
    component = fixture.componentInstance;
    service = TestBed.inject(HighlightImportService);
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true as never);
  });

  it('renders nothing until the service opens it', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.modal-card')).toBeNull();

    service.open();
    fixture.detectChanges();
    expect(text()).toContain('Import highlights');
    expect(fixture.nativeElement.querySelector('[data-testid="highlight-import-input"]')).not.toBeNull();
  });

  it('detects the e-reader from the file name', () => {
    expect(service.detectSource('KoboReader.sqlite')).toBe('kobo');
    expect(service.detectSource('metadata.epub.lua')).toBe('koreader');
    expect(service.detectSource('notes.txt')).toBeNull();
  });

  it('reports imported, already-imported and unplaced books separately', async () => {
    vi.spyOn(service, 'import').mockReturnValue(
      of([
        book({ annotationCount: 5, importedCount: 3, duplicateCount: 2 }),
        book({
          status: 'unmatched',
          bookId: null,
          bookTitle: null,
          sourceTitle: 'Not In The Library',
          annotationCount: 4,
          skippedCount: 4,
        }),
      ]),
    );
    service.open();

    await component.importFiles([file('KoboReader.sqlite')]);
    fixture.detectChanges();

    expect(component.stage()).toBe('result');
    expect(component.headline()).toBe('3 highlights imported');
    expect(component.summary()).toBe('From 1 book. 2 more were already in Nostos.');
    expect(text()).toContain('3 new · 2 already imported');
    expect(text()).toContain('Not In The Library');
    expect(text()).toContain('4 highlights belong to books Nostos could not place');
  });

  it('says so plainly when a re-import adds nothing', async () => {
    vi.spyOn(service, 'import').mockReturnValue(
      of([book({ annotationCount: 2, duplicateCount: 2 })]),
    );
    service.open();

    await component.importFiles([file('KoboReader.sqlite')]);

    expect(component.headline()).toBe('Already up to date');
    expect(component.summary()).toBe('All 2 highlights found are already in Nostos.');
  });

  it('keeps going past a file that fails and shows the server reason', async () => {
    vi.spyOn(service, 'import')
      .mockReturnValueOnce(
        throwError(
          () => new HttpErrorResponse({ status: 400, error: { error: 'Not a Kobo database.' } }),
        ),
      )
      .mockReturnValueOnce(of([book({ annotationCount: 1, importedCount: 1 })]));
    service.open();

    await component.importFiles([file('broken.sqlite'), file('metadata.epub.lua'), file('cover.jpg')]);
    fixture.detectChanges();

    expect(component.failures()).toEqual([
      { fileName: 'broken.sqlite', message: 'Not a Kobo database.' },
      { fileName: 'cover.jpg', message: 'Not a Kobo database or a KOReader metadata file.' },
    ]);
    expect(component.headline()).toBe('1 highlight imported');
    expect(text()).toContain('Not a Kobo database.');
  });

  it('cannot be closed while an import is running', () => {
    service.open();
    component.stage.set('importing');

    component.close();

    expect(service.isOpen()).toBe(true);
  });

  it('opens a matched book and closes the dialog', async () => {
    vi.spyOn(service, 'import').mockReturnValue(of([book({ importedCount: 1 })]));
    service.open();
    await component.importFiles([file('KoboReader.sqlite')]);

    component.openBook(component.matched()[0]);

    expect(router.navigate).toHaveBeenCalledWith(['/library', 'b1']);
    expect(service.isOpen()).toBe(false);
  });

  it('posts a Kobo database to the Kobo endpoint and a sidecar to the KOReader one', () => {
    const http = TestBed.inject(HttpTestingController);

    service.import(file('KoboReader.sqlite'), 'kobo').subscribe((books) => {
      expect(books).toEqual([]);
    });
    http.expectOne('/api/notes/import/kobo').flush({ books: [] });

    service.import(file('metadata.epub.lua'), 'koreader').subscribe((books) => {
      expect(books[0].sourceTitle).toBe('metadata.epub.lua');
      expect(books[0].status).toBe('unmatched');
    });
    http.expectOne('/api/notes/import/koreader').flush({
      status: 'unmatched',
      bookId: null,
      bookTitle: null,
      annotationCount: 2,
      importedCount: 0,
      duplicateCount: 0,
      skippedCount: 2,
      message: 'No library book matched.',
    });
    http.verify();
  });
});
