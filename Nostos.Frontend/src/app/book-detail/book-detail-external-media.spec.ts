import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { BookDetail } from './book-detail.component';
import { Book } from '../core/dtos/book.dtos';
import { CloudEntryService } from '../core/services/cloud-entry.service';
import { bookNotes, brainReads, notesPage } from './book-detail.testing';

const book = {
  id: 'b1',
  title: 'A Novel',
  author: 'Jane Author',
  type: 'audiobook',
  hasFile: true,
  fileName: 'book.m4b',
  collectionIds: [],
  progressPercent: 0,
  rating: 0,
  isFavorite: false,
  createdAt: '2026-08-01T08:00:00+02:00',
} as unknown as Book;

/** A book whose media is read from the server's linked folder (issue #843). */
describe('BookDetail linked external media', () => {
  let fixture: ComponentFixture<BookDetail>;
  let httpMock: HttpTestingController;
  const supported = signal(true);

  async function setup(external?: { linked: boolean; files: number; unavailableFiles: number }) {
    fixture = TestBed.createComponent(BookDetail);
    await fixture.whenStable();
    fixture.detectChanges();

    httpMock.expectOne('/api/books/b1').flush(book);
    httpMock.expectOne(bookNotes).flush(notesPage([]));
    httpMock.expectOne('/api/collections').flush([]);
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    fixture.detectChanges();

    if (external) {
      httpMock.expectOne('/api/external-media/books/b1').flush(external);
      fixture.detectChanges();
    }
  }

  function deleteDialog(): HTMLElement {
    fixture.componentInstance.openDeleteConfirm();
    fixture.detectChanges();
    return fixture.nativeElement.querySelector('.confirm-modal-card') as HTMLElement;
  }

  beforeEach(async () => {
    supported.set(true);
    await TestBed.configureTestingModule({
      imports: [BookDetail],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: book.id })) } },
        { provide: CloudEntryService, useValue: { supportsExternalMediaLibraries: supported } },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    httpMock.verify();
  });

  it('says the original files are kept when a linked book is removed', async () => {
    await setup({ linked: true, files: 12, unavailableFiles: 0 });

    const dialog = deleteDialog();

    expect(dialog.textContent).toContain('Remove “A Novel” from Nostos?');
    expect(dialog.textContent).toContain(
      'Your original files stay where they are and are never modified or deleted by Nostos.',
    );
    expect(dialog.textContent).toContain('its notes and its reading progress are removed');
    expect(dialog.querySelector('.btn-confirm')?.textContent).toContain('Remove from Nostos');
    expect(fixture.nativeElement.querySelector('[data-testid="external-file-unavailable"]')).toBeNull();
  });

  it('keeps the ordinary wording for a book Nostos stores itself', async () => {
    await setup({ linked: false, files: 0, unavailableFiles: 0 });

    const dialog = deleteDialog();

    expect(dialog.textContent).toContain('Delete “A Novel”?');
    expect(dialog.textContent).toContain('The book file, annotations, and all reading progress');
    expect(dialog.querySelector('.btn-confirm')?.textContent).toContain('Delete permanently');
  });

  it('shows that the external file is unavailable and that notes are kept', async () => {
    await setup({ linked: true, files: 12, unavailableFiles: 12 });

    const notice = fixture.nativeElement.querySelector('[data-testid="external-file-unavailable"]') as HTMLElement;

    expect(notice.textContent).toContain('External file unavailable');
    expect(notice.textContent).toContain('Your notes and progress are kept');
  });

  it('asks nothing of a server that does not report the feature', async () => {
    supported.set(false);
    await setup();

    httpMock.expectNone('/api/external-media/books/b1');
    expect(deleteDialog().textContent).toContain('Delete “A Novel”?');
  });
});
