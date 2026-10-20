import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { BookDetail } from './book-detail.component';
import { BookDetailStore } from './book-detail.store';
import { Book } from '../core/dtos/book.dtos';
import { bookNotes, brainReads, notesPage } from './book-detail.testing';
import { clampPaperPage, paperPagePercent, readPaperPage, writePaperPage } from './paper-page-progress';

describe('paper page progress (issue #1091)', () => {
  it('reads only a paper location', () => {
    expect(readPaperPage('{"page":143}')).toBe(143);
    expect(readPaperPage(writePaperPage(7))).toBe(7);
    expect(readPaperPage('{"pageNumber":3}')).toBeNull();
    expect(readPaperPage('{"page":0}')).toBeNull();
    expect(readPaperPage('epubcfi(/6/4)')).toBeNull();
    expect(readPaperPage('1234.5')).toBeNull();
    expect(readPaperPage(null)).toBeNull();
  });

  it('clamps to 1..pageCount, and to 1.. without one', () => {
    expect(clampPaperPage(0, 880)).toBe(1);
    expect(clampPaperPage(-4, 880)).toBe(1);
    expect(clampPaperPage(900, 880)).toBe(880);
    expect(clampPaperPage(12.7, 880)).toBe(12);
    expect(clampPaperPage(5000, null)).toBe(5000);
  });

  it('derives the percentage by the PDF reader rule, and 1% without a page count', () => {
    expect(paperPagePercent(143, 880)).toBe(16);
    expect(paperPagePercent(880, 880)).toBe(100);
    expect(paperPagePercent(1, 880)).toBe(1);
    expect(paperPagePercent(50, null)).toBe(1);
  });
});

const paperBook: Book = {
  id: 'b1',
  title: 'After Virtue',
  subtitle: null,
  author: 'Alasdair MacIntyre',
  editor: null,
  translator: null,
  narrator: null,
  description: null,
  type: 'physical',
  edition: null,
  asin: null,
  duration: null,
  isbn: null,
  publisher: null,
  placeOfPublication: null,
  publishedDate: null,
  pageCount: 880,
  language: null,
  categories: null,
  series: null,
  volumeNumber: null,
  createdAt: '2026-08-01T08:00:00+02:00',
  hasFile: false,
  fileName: null,
  coverUrl: null,
  collectionIds: [],
  lastLocation: null,
  progressPercent: 0,
  lastReadAt: null,
  rating: 0,
  isFavorite: false,
  personalReview: null,
  finishedAt: null,
};

describe('BookDetail paper page stepper (issue #1091)', () => {
  let fixture: ComponentFixture<BookDetail>;
  let httpMock: HttpTestingController;

  async function setup(initial: Book) {
    fixture = TestBed.createComponent(BookDetail);
    await fixture.whenStable();
    fixture.detectChanges();
    httpMock.expectOne('/api/books/b1').flush(initial);
    httpMock.expectOne(bookNotes).flush(notesPage([]));
    httpMock.expectOne('/api/collections').flush([]);
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    fixture.detectChanges();
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  }

  const el = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLElement | null;
  const pageInput = () => el('.page-stepper input') as HTMLInputElement;
  const plus = () => el('.page-stepper button[aria-label="Next page"]') as HTMLButtonElement;
  const minus = () => el('.page-stepper button[aria-label="Previous page"]') as HTMLButtonElement;
  const statusPill = () => el('.status-dropdown .nostos-dropdown__trigger')!.textContent ?? '';
  const progressWrites = () => httpMock.match((r) => r.method === 'PUT' && r.url === '/api/books/b1/progress');

  function typePage(value: string) {
    const input = pageInput();
    input.value = value;
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    input.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BookDetail],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: 'b1' })) } },
      ],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.useRealTimers();
    httpMock.match(brainReads).forEach((request) => !request.cancelled && request.flush([]));
    httpMock.verify();
  });

  it('is shown only for a paper book', async () => {
    await setup({ ...paperBook, type: 'ebook' });
    expect(el('.page-stepper')).toBeNull();
  });

  it('typing page 143 of 880 shows 16% and saves {"page":143} once', async () => {
    await setup(paperBook);
    const group = el('.page-stepper')!;
    expect(group.getAttribute('role')).toBe('group');
    expect(group.getAttribute('aria-label')).toBe('Current page');
    expect(pageInput().getAttribute('aria-label')).toBe('Current page');
    expect(group.textContent).toContain('of 880');

    typePage('143');
    expect(pageInput().value).toBe('143');
    expect(statusPill()).toContain('Reading');
    expect(statusPill()).toContain('16%');
    expect(progressWrites()).toHaveLength(0);

    vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);
    const [write] = progressWrites();
    expect(write.request.body).toEqual({ location: '{"page":143}', percentage: 16 });
    write.flush({});
  });

  it('a burst of +/− taps is one write with the final page', async () => {
    await setup({ ...paperBook, lastLocation: '{"page":10}', progressPercent: 1 });
    for (let i = 0; i < 5; i++) {
      plus().click();
      vi.advanceTimersByTime(100);
    }
    minus().click();
    fixture.detectChanges();
    expect(pageInput().value).toBe('14');

    vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);
    const writes = progressWrites();
    expect(writes).toHaveLength(1);
    expect(writes[0].request.body).toEqual({ location: '{"page":14}', percentage: 1 });
    writes[0].flush({});
  });

  it('clamps typed pages, and ignores an empty or zero entry', async () => {
    await setup({ ...paperBook, lastLocation: '{"page":10}', progressPercent: 1 });
    typePage('0');
    expect(pageInput().value).toBe('10');
    typePage('');
    expect(pageInput().value).toBe('10');
    vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);
    expect(progressWrites()).toHaveLength(0);

    typePage('5000');
    expect(pageInput().value).toBe('880');
    expect(statusPill()).toContain('Finished');
    vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);
    const [write] = progressWrites();
    expect(write.request.body).toEqual({ location: '{"page":880}', percentage: 100 });
    write.flush({});
    expect(plus().disabled).toBe(true);
  });

  it('a lower page after the last un-finishes the book', async () => {
    await setup({
      ...paperBook,
      lastLocation: '{"page":880}',
      progressPercent: 100,
      finishedAt: '2026-10-01T08:00:00Z',
    });
    expect(statusPill()).toContain('Finished');
    minus().click();
    fixture.detectChanges();
    expect(statusPill()).toContain('Reading');
    expect(statusPill()).toContain('99%');
    vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);
    progressWrites()[0].flush({});
  });

  it('without a page count shows "page N", no percentage, and a quiet hint', async () => {
    await setup({ ...paperBook, pageCount: null });
    expect(el('.page-stepper')!.textContent).not.toContain('of');
    expect(el('.page-stepper-hint')).toBeNull();

    typePage('42');
    expect(statusPill()).toContain('Reading');
    expect(statusPill()).not.toContain('%');
    expect(el('.page-stepper-hint')!.textContent).toContain('page count');

    vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);
    const [write] = progressWrites();
    expect(write.request.body).toEqual({ location: '{"page":42}', percentage: 1 });
    write.flush({});
  });

  it('the first + on an unstarted book opens it at page 1', async () => {
    await setup(paperBook);
    expect(minus().disabled).toBe(true);
    plus().click();
    fixture.detectChanges();
    expect(pageInput().value).toBe('1');
    vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);
    progressWrites()[0].flush({});
  });

  it('a page set just before leaving is still saved', async () => {
    await setup(paperBook);
    typePage('200');
    fixture.destroy();
    const [write] = progressWrites();
    expect(write.request.body).toEqual({ location: '{"page":200}', percentage: 22 });
    write.flush({});
  });
});
