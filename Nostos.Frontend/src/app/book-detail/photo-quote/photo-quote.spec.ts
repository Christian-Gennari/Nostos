import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { BookDetail } from '../book-detail.component';
import { Book } from '../../core/dtos/book.dtos';
import { bookNotes, brainReads, notesPage } from '../book-detail.testing';
import { OcrWord, PageOcrService } from './page-ocr';
import { PhotoQuote, PhotoQuoteModal } from './photo-quote-modal.component';

const words: OcrWord[] = [
  { text: 'They', confidence: 95, paragraph: 0, line: 0 },
  { text: 'waited', confidence: 95, paragraph: 0, line: 0 },
  { text: 'by', confidence: 95, paragraph: 0, line: 0 },
  { text: 'the', confidence: 95, paragraph: 0, line: 0 },
  { text: 'fjord.', confidence: 41, paragraph: 0, line: 0 },
  { text: 'Nobody', confidence: 95, paragraph: 1, line: 1 },
  { text: 'came.', confidence: 95, paragraph: 1, line: 1 },
];

class FakeOcr {
  calls: { language: string }[] = [];
  result: Promise<OcrWord[]> = Promise.resolve(words);
  read(_photo: Blob, language: string, progress: (fraction: number) => void) {
    this.calls.push({ language });
    progress(0.5);
    return this.result;
  }
}

const photo = new Blob(['jpeg'], { type: 'image/jpeg' });

describe('PhotoQuoteModal (issue #1292)', () => {
  let fixture: ComponentFixture<PhotoQuoteModal>;
  let ocr: FakeOcr;
  let saved: PhotoQuote[];

  const el = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLElement | null;
  const word = (index: number) => el(`[data-word="${index}"]`)!;

  function tap(index: number) {
    word(index).dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerType: 'touch' }));
    word(index).dispatchEvent(new PointerEvent('pointerup', { bubbles: true, pointerType: 'touch' }));
    fixture.detectChanges();
  }

  async function open(page: number | null = 143) {
    ocr = new FakeOcr();
    await TestBed.configureTestingModule({
      imports: [PhotoQuoteModal],
      providers: [{ provide: PageOcrService, useValue: ocr }],
    }).compileComponents();
    fixture = TestBed.createComponent(PhotoQuoteModal);
    fixture.componentRef.setInput('language', 'swe');
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('photo', photo);
    saved = [];
    fixture.componentInstance.save.subscribe((quote) => saved.push(quote));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('reads the photo in the book language and shows its words', async () => {
    await open();
    expect(ocr.calls).toEqual([{ language: 'swe' }]);
    expect(el('[data-testid="photo-quote-page"]')!.textContent!.replace(/\s+/g, ' ')).toContain('They waited by the fjord.');
    expect(word(4).classList).toContain('is-unsure');
    expect(word(0).classList).not.toContain('is-unsure');
  });

  it('tapping the first and last word selects the quote between them', async () => {
    await open();
    tap(1);
    tap(6);
    expect(word(3).classList).toContain('is-selected');
    expect(word(0).classList).not.toContain('is-selected');
    const text = el('[data-testid="photo-quote-text"]') as HTMLTextAreaElement;
    await fixture.whenStable();
    expect(text.value).toBe('waited by the fjord.\n\nNobody came.');
  });

  it('saves the corrected text, the comment and the page', async () => {
    await open(143);
    tap(0);
    tap(4);
    fixture.componentInstance.quote.set('They waited by the fjord.');
    fixture.componentInstance.comment.set('The waiting again');
    fixture.detectChanges();
    expect((el('[data-testid="photo-quote-page-number"]') as HTMLInputElement).value).toBe('143');

    (el('[data-testid="photo-quote-save"]') as HTMLButtonElement).click();

    expect(saved).toEqual([{ text: 'They waited by the fjord.', comment: 'The waiting again', page: 143 }]);
  });

  it('cannot save before a quote is selected', async () => {
    await open();
    expect((el('[data-testid="photo-quote-save"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('says so when no text could be read', async () => {
    ocr = new FakeOcr();
    ocr.result = Promise.resolve([]);
    await TestBed.configureTestingModule({
      imports: [PhotoQuoteModal],
      providers: [{ provide: PageOcrService, useValue: ocr }],
    }).compileComponents();
    fixture = TestBed.createComponent(PhotoQuoteModal);
    fixture.componentRef.setInput('photo', photo);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No text could be read from that photo');
  });
});

const paperBook = {
  id: 'b1',
  title: 'The Valley Road',
  type: 'physical',
  pageCount: 320,
  language: 'sv',
  lastLocation: '{"page":143}',
  progressPercent: 44,
  hasFile: false,
  collectionIds: [],
} as unknown as Book;

describe('BookDetail photo quote (issue #1292)', () => {
  let fixture: ComponentFixture<BookDetail>;
  let httpMock: HttpTestingController;

  const el = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLElement | null;

  async function setup(initial: Book) {
    await TestBed.configureTestingModule({
      imports: [BookDetail],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: 'b1' })) } },
        { provide: PageOcrService, useValue: new FakeOcr() },
      ],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(BookDetail);
    await fixture.whenStable();
    fixture.detectChanges();
    httpMock.expectOne('/api/books/b1').flush(initial);
    httpMock.expectOne(bookNotes).flush(notesPage([]));
    httpMock.expectOne('/api/collections').flush([]);
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    fixture.detectChanges();
  }

  afterEach(() => {
    httpMock.match(brainReads).forEach((request) => !request.cancelled && request.flush([]));
    httpMock.verify();
  });

  it('is offered only on a paper book', async () => {
    await setup({ ...paperBook, type: 'ebook', hasFile: true, fileName: 'x.epub' } as Book);
    expect(el('[data-testid="photo-quote-button"]')).toBeNull();
  });

  it('saves the quote as a highlight on the paper book at the page, from a photo', async () => {
    await setup(paperBook);
    expect(el('[data-testid="photo-quote-button"]')).not.toBeNull();
    const input = el('[data-testid="photo-quote-input"]') as HTMLInputElement;
    expect(input.getAttribute('accept')).toBe('image/*');
    expect(input.getAttribute('capture')).toBe('environment');

    fixture.componentInstance.photoQuotePhoto.set(photo);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.componentInstance.photoQuoteLanguage()).toBe('swe');

    fixture.componentInstance.savePhotoQuote({ text: 'They waited by the fjord.', comment: '', page: 143 });

    const post = httpMock.expectOne((r) => r.method === 'POST' && r.url === '/api/books/b1/notes');
    expect(post.request.body).toEqual({
      content: '',
      selectedText: 'They waited by the fjord.',
      captureSource: 'photo',
      sourceAnchorKind: 'physical_page',
      sourceAnchorValue: '143',
      anchorVerified: false,
    });
    post.flush({ id: 'n1' });
    httpMock.expectOne(bookNotes).flush(notesPage([]));
    fixture.detectChanges();
    expect(fixture.componentInstance.photoQuotePhoto()).toBeNull();
  });
});
