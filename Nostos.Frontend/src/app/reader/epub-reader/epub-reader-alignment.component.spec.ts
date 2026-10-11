import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { EMPTY, Subject } from 'rxjs';

import { EpubReader } from './epub-reader.component';
import { AlignmentService } from '../../core/services/alignment.service';
import { LocateAudioResult } from '../../core/dtos/alignment.dtos';
import { Book } from '../../core/dtos/book.dtos';
import { NotesService } from '../../core/services/notes.service';
import { BooksService } from '../../core/services/books.service';

// What the reader "sees" on the page; the real extraction has its own spec.
let visibleExcerpt: string | null = null;

describe('EpubReader continue listening', () => {
  let fixture: ComponentFixture<EpubReader>;
  let reader: EpubReader;
  let response: Subject<LocateAudioResult>;

  const alignment = {
    locateAudio: vi.fn(),
  };

  const router = {
    navigate: vi.fn(() => Promise.resolve(true)),
    events: EMPTY,
    url: '/read/ebook-1',
  };

  const books = {
    getLocations: vi.fn(),
    saveLocations: vi.fn(),
    updateProgress: vi.fn(),
    get: vi.fn(),
  };

  const notes = {
    list: vi.fn(),
    create: vi.fn(),
  };

  const epub: Book = {
    id: 'epub-1',
    type: 'ebook',
    title: 'Example Book',
    hasFile: true,
    fileName: 'example.epub',
    otherEditions: [{
      id: 'audio-2',
      type: 'audiobook',
      format: 'm4b',
      progressPercent: 0,
      hasFile: true,
    }],
  } as Book;

  beforeEach(async () => {
    vi.clearAllMocks();

    response = new Subject<LocateAudioResult>();
    alignment.locateAudio.mockReturnValue(response.asObservable());
    visibleExcerpt = 'The evening was quiet. The last light rested on the mountains.';

    await TestBed.configureTestingModule({
      imports: [EpubReader],
      providers: [
        { provide: AlignmentService, useValue: alignment },
        { provide: Router, useValue: router },
        { provide: NotesService, useValue: notes },
        { provide: BooksService, useValue: books },
      ],
    }).compileComponents();

    // Do not invoke ngOnInit: this suite tests the alignment interaction,
    // not the EPUB renderer's opening/restore lifecycle.
    fixture = TestBed.createComponent(EpubReader);
    reader = fixture.componentInstance;
    (reader as unknown as { alignmentVisibleExcerpt: () => string | null }).alignmentVisibleExcerpt =
      () => visibleExcerpt;

    fixture.componentRef.setInput('bookId', 'epub-1');
    fixture.componentRef.setInput('book', epub);

    reader.loading.set(false);

    // Give the action a mounted fake rendition. The visible-text helper
    // has its own tests for the actual iframe/range geometry.
    (reader as any).rendition = {
      getContents: () => [{ document: document }],
    };

    vi.spyOn(reader as any, 'readingViewRect')
      .mockReturnValue(new DOMRect(0, 0, 600, 400));
  });

  afterEach(() => {
    fixture.destroy();
  });

  it('hides the action when there is no audiobook edition', () => {
    fixture.componentRef.setInput('book', {
      ...epub,
      otherEditions: [],
    });

    expect(reader.alignmentTarget()).toBeNull();
    expect(reader.canContinueListening()).toBe(false);

    reader.continueListening();

    expect(alignment.locateAudio).not.toHaveBeenCalled();
  });

  it('hides editions without media', () => {
    fixture.componentRef.setInput('book', {
      ...epub,
      otherEditions: [{
        ...epub.otherEditions![0],
        hasFile: false,
      }],
    });

    expect(reader.canContinueListening()).toBe(false);
  });

  it('submits the visible text and ignores a second click', () => {
    reader.continueListening();
    reader.continueListening();

    expect(reader.alignmentBusy()).toBe(true);
    expect(alignment.locateAudio).toHaveBeenCalledTimes(1);
    expect(alignment.locateAudio).toHaveBeenCalledWith(
      'epub-1',
      {
        targetBookId: 'audio-2',
        excerpt:
          'The evening was quiet. The last light rested on the mountains.',
      },
    );

  });

  it('does not submit when the visible page has no text', () => {
    visibleExcerpt = null;

    reader.continueListening();

    expect(alignment.locateAudio).not.toHaveBeenCalled();
    expect(reader.alignmentBusy()).toBe(false);
    expect(reader.alignmentMessage()).toContain(
      'No readable text is visible',
    );
  });

  it('opens the audiobook at the exact located time', () => {
    reader.continueListening();

    response.next({
      located: true,
      positionSeconds: 3721.5,
      score: 0.97,
      probes: 4,
      anchorStored: true,
    });

    expect(reader.alignmentBusy()).toBe(false);
    expect(router.navigate).toHaveBeenCalledWith(
      ['/read', 'audio-2'],
      { queryParams: { sourceTime: 3721.5 } },
    );
  });

  it('offers an approximation but does not navigate automatically', () => {
    reader.continueListening();

    response.next({
      located: false,
      reason: 'no_confident_match',
      approximatePositionSeconds: 3600,
      probes: 5,
    });

    expect(reader.alignmentDeclined()).toBe(true);
    expect(reader.alignmentApproximatePosition()).toBe(3600);
    expect(reader.alignmentMessage()).toContain(
      'exact sentence could not be found',
    );
    expect(router.navigate).not.toHaveBeenCalled();

    reader.jumpToApproximateAudio();

    expect(router.navigate).toHaveBeenCalledWith(
      ['/read', 'audio-2'],
      { queryParams: { sourceTime: 3600 } },
    );
  });

  it('preserves a valid approximation of zero seconds', () => {
    reader.continueListening();

    response.next({
      located: false,
      reason: 'no_confident_match',
      approximatePositionSeconds: 0,
      probes: 2,
    });

    expect(reader.alignmentApproximatePosition()).toBe(0);

    reader.jumpToApproximateAudio();

    expect(router.navigate).toHaveBeenCalledWith(
      ['/read', 'audio-2'],
      { queryParams: { sourceTime: 0 } },
    );
  });

  it('offers opening at saved progress when no approximation exists', () => {
    reader.continueListening();

    response.next({
      located: false,
      reason: 'insufficient_evidence',
      approximatePositionSeconds: null,
      probes: 3,
    });

    expect(reader.alignmentDeclined()).toBe(true);
    expect(reader.alignmentApproximatePosition()).toBeNull();
    expect(router.navigate).not.toHaveBeenCalled();

    reader.openAudiobookAnyway();

    expect(router.navigate).toHaveBeenCalledWith(
      ['/read', 'audio-2'],
      { queryParams: {} },
    );
  });

  it('hides the action on a typed transcription configuration error', () => {
    reader.continueListening();

    response.error(new HttpErrorResponse({
      status: 503,
      error: {
        title: 'alignment_transcription_not_configured',
        detail: 'Not configured',
      },
    }));

    expect(reader.alignmentBusy()).toBe(false);
    expect(reader.alignmentUnavailable()).toBe(true);
    expect(reader.canContinueListening()).toBe(false);
    expect(reader.alignmentMessage()).toContain('Check Settings');

    reader.continueListening();
    expect(alignment.locateAudio).toHaveBeenCalledTimes(1);
  });

  it('leaves the action usable after an ordinary error', () => {
    reader.continueListening();

    response.error(new HttpErrorResponse({
      status: 504,
      error: { title: 'alignment_operation_timeout' },
    }));

    expect(reader.alignmentBusy()).toBe(false);
    expect(reader.alignmentUnavailable()).toBe(false);
    expect(reader.canContinueListening()).toBe(true);
    expect(reader.alignmentMessage()).toContain('try again');

    response = new Subject<LocateAudioResult>();
    alignment.locateAudio.mockReturnValue(response.asObservable());

    reader.continueListening();

    expect(alignment.locateAudio).toHaveBeenCalledTimes(2);
  });

  it('does not treat rate limiting as unavailable configuration', () => {
    reader.continueListening();

    response.error(new HttpErrorResponse({
      status: 429,
      error: { title: 'ai_usage_limit_reached' },
    }));

    expect(reader.alignmentUnavailable()).toBe(false);
    expect(reader.alignmentMessage()).toContain(
      'temporarily limited',
    );
  });

  it('rejects an invalid successful position rather than navigating', () => {
    reader.continueListening();

    response.next({
      located: true,
      positionSeconds: Number.NaN,
      score: 0.9,
      probes: 2,
      anchorStored: false,
    });

    expect(router.navigate).not.toHaveBeenCalled();
    expect(reader.alignmentMessage()).toContain(
      'audio position is unavailable',
    );
  });

  it('ignores an old response after a different book becomes active', () => {
    reader.continueListening();

    fixture.componentRef.setInput('bookId', 'epub-3');

    response.next({
      located: true,
      positionSeconds: 123,
      score: 0.95,
      probes: 2,
      anchorStored: true,
    });

    expect(router.navigate).not.toHaveBeenCalled();
  });
});
