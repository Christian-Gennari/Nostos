import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';

import { SwitchEditionDialog } from './switch-edition-dialog.component';
import { AlignmentService } from '../../core/services/alignment.service';
import { LocateAudioResult, LocateTextResult } from '../../core/dtos/alignment.dtos';
import { Book, EditionSummaryDto } from '../../core/dtos/book.dtos';
import { hasSwitched } from './edition-place-keeper';

describe('SwitchEditionDialog', () => {
  let fixture: ComponentFixture<SwitchEditionDialog>;
  let textResponse: Subject<LocateTextResult>;
  let audioResponse: Subject<LocateAudioResult>;

  const alignment = {
    locateText: vi.fn(),
    locateAudio: vi.fn(),
  };
  const router = {
    navigate: vi.fn(() => Promise.resolve(true)),
  };

  const epub: EditionSummaryDto = {
    id: 'epub-1', type: 'ebook', format: 'epub', hasFile: true, progressPercent: 40, fileName: 'book.epub',
  };
  const pdf: EditionSummaryDto = {
    id: 'pdf-1', type: 'ebook', format: 'pdf', hasFile: true, progressPercent: 0, fileName: 'book.pdf',
  };
  const audioA: EditionSummaryDto = {
    id: 'audio-1', type: 'audiobook', hasFile: true, progressPercent: 0, narrator: 'First narrator', duration: '3:14:29',
  };
  const audioB: EditionSummaryDto = {
    id: 'audio-2', type: 'audiobook', hasFile: true, progressPercent: 12, narrator: 'Second narrator',
  };
  const noFile: EditionSummaryDto = {
    id: 'audio-3', type: 'audiobook', hasFile: false, progressPercent: 0,
  };

  const audiobook = (others: EditionSummaryDto[]) =>
    ({ id: 'audio-0', type: 'audiobook', title: 'A Christmas Carol', otherEditions: others }) as unknown as Book;
  const ebook = (others: EditionSummaryDto[]) =>
    ({ id: 'epub-0', type: 'ebook', fileName: 'own.epub', title: 'A Christmas Carol', otherEditions: others }) as unknown as Book;

  function open(book: Book, inputs: { audio?: number | null; excerpt?: string | null } = {}) {
    fixture.componentRef.setInput('book', book);
    fixture.componentRef.setInput('audioPositionSeconds', inputs.audio ?? null);
    fixture.componentRef.setInput('visibleExcerpt', inputs.excerpt ?? null);
    fixture.componentRef.setInput('isOpen', true);
    fixture.detectChanges();
  }

  const rows = () =>
    Array.from(fixture.nativeElement.ownerDocument.querySelectorAll('.switch-row')) as HTMLButtonElement[];
  const text = () => (fixture.nativeElement.ownerDocument.body.textContent ?? '').replace(/\s+/g, ' ');
  const byTestId = (id: string) =>
    fixture.nativeElement.ownerDocument.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function settle() {
    await Promise.resolve();
    await Promise.resolve();
    fixture.detectChanges();
  }

  beforeEach(async () => {
    vi.clearAllMocks();
    textResponse = new Subject<LocateTextResult>();
    audioResponse = new Subject<LocateAudioResult>();
    alignment.locateText.mockReturnValue(textResponse.asObservable());
    alignment.locateAudio.mockReturnValue(audioResponse.asObservable());
    router.navigate.mockImplementation(() => Promise.resolve(true));

    await TestBed.configureTestingModule({
      imports: [SwitchEditionDialog],
      providers: [
        { provide: AlignmentService, useValue: alignment },
        { provide: Router, useValue: router },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(SwitchEditionDialog);
  });

  afterEach(() => fixture.destroy());

  it('lists every other edition that has a file, and only those', () => {
    open(audiobook([epub, pdf, audioB, noFile]), { audio: 180 });

    expect(rows()).toHaveLength(3);
    expect(text()).toContain('Ebook');
    expect(text()).toContain('PDF');
    expect(text()).toContain('Second narrator');
  });

  it('says which promise each row makes', () => {
    open(audiobook([epub, pdf]), { audio: 180 });

    expect(rows()[0].textContent).toContain('Opens at the sentence you’re hearing');
    expect(rows()[1].textContent).toContain('Opens at the page you’re hearing');
  });

  it('offers the edition’s own place beside the same sentence', async () => {
    open(audiobook([epub, pdf]), { audio: 180 });

    // The PDF was never opened, so it has no place of its own to offer.
    expect(byTestId('switch-edition-pdf-where-left')).toBeNull();
    const whereLeft = byTestId('switch-edition-epub-where-left')!;
    expect(whereLeft.textContent).toContain('Or open where you left off · 40%');

    whereLeft.click();
    await settle();

    expect(alignment.locateText).not.toHaveBeenCalled();
    expect(router.navigate).toHaveBeenCalledWith(['/read', 'epub-1'], undefined);
  });

  it('has no second choice when the row already opens where you left off', () => {
    open(audiobook([epub]), { audio: null });

    expect(rows()[0].textContent).toContain('Opens where you left off · 40%');
    expect(byTestId('switch-edition-epub-where-left')).toBeNull();
  });

  it('continues an audiobook in the EPUB at the located sentence', async () => {
    open(audiobook([epub]), { audio: 180.5 });

    rows()[0].click();
    fixture.detectChanges();

    expect(alignment.locateText).toHaveBeenCalledWith('audio-0', {
      positionSeconds: 180.5,
      targetBookId: 'epub-1',
    });
    expect(rows()[0].textContent).toContain('Finding your place…');

    textResponse.next({
      located: true,
      score: 1,
      excerpt: 'upon his own ramparts',
      locator: { resourceHref: 'ch1.xhtml', spineIndex: 1, textOffset: 2938, cfi: null },
    });
    await settle();

    expect(router.navigate).toHaveBeenCalledWith(['/read', 'epub-1'], {
      queryParams: {
        sourceHref: 'ch1.xhtml',
        sourceSpine: 1,
        sourceOffset: 2938,
        sourceExcerpt: 'upon his own ramparts',
        sourceMark: 'sentence',
      },
    });
  });

  it('ignores a second click while it is finding the place', () => {
    open(audiobook([epub, audioB]), { audio: 180 });

    rows()[0].click();
    fixture.detectChanges();
    rows()[0].click();
    rows()[1].click();

    expect(alignment.locateText).toHaveBeenCalledTimes(1);
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('does not navigate when the passage is not found, and offers the saved position', async () => {
    open(audiobook([epub]), { audio: 20 });

    rows()[0].click();
    textResponse.next({ located: false, reason: 'no_match' });
    await settle();

    expect(router.navigate).not.toHaveBeenCalled();
    expect(byTestId('switch-edition-declined')?.textContent).toContain('Could not find this exact place');

    byTestId('switch-edition-open-anyway')!.click();
    expect(router.navigate).toHaveBeenCalledWith(['/read', 'epub-1'], undefined);
  });

  it('continues an EPUB in the chosen audiobook at the located time', async () => {
    open(ebook([audioA, audioB]), { excerpt: '  It was not an agreeable idea.  ' });

    expect(rows()[0].textContent).toContain('Starts where this page begins');

    rows()[1].click();
    expect(alignment.locateAudio).toHaveBeenCalledWith('epub-0', {
      targetBookId: 'audio-2',
      excerpt: 'It was not an agreeable idea.',
    });

    audioResponse.next({ located: true, positionSeconds: 2380.4, probes: 2, score: 1, anchorStored: true });
    await settle();

    expect(router.navigate).toHaveBeenCalledWith(['/read', 'audio-2'], {
      queryParams: { sourceTime: 2380.4 },
    });
  });

  it('remembers a pair once the reader has landed on the same sentence in it', async () => {
    localStorage.clear();
    open(ebook([audioA]), { excerpt: 'It was not an agreeable idea.' });
    expect(hasSwitched('epub-0')).toBe(false);

    rows()[0].click();
    audioResponse.next({ located: true, positionSeconds: 2380.4, probes: 2, score: 1, anchorStored: true });
    await settle();

    expect(hasSwitched('epub-0')).toBe(true);
    expect(hasSwitched('audio-1')).toBe(true);
  });

  it('does not remember a pair opened where it was left', () => {
    localStorage.clear();
    open(audiobook([epub]), { audio: null });

    rows()[0].click();

    expect(hasSwitched('audio-0')).toBe(false);
    expect(hasSwitched('epub-1')).toBe(false);
  });

  it('offers an explicitly approximate jump when only an estimate is available', async () => {
    open(ebook([audioA]), { excerpt: 'Stave three.' });

    rows()[0].click();
    audioResponse.next({
      located: false,
      reason: 'probe_budget_exhausted',
      approximatePositionSeconds: 5400,
      probes: 5,
    });
    await settle();

    expect(router.navigate).not.toHaveBeenCalled();
    byTestId('switch-edition-approximate')!.click();
    expect(router.navigate).toHaveBeenCalledWith(['/read', 'audio-1'], {
      queryParams: { sourceTime: 5400 },
    });
  });

  it('opens an edition where it was left when there is no position to carry over', () => {
    open(audiobook([epub]), { audio: null });

    expect(rows()[0].textContent).toContain('Opens where you left off · 40%');
    rows()[0].click();

    expect(alignment.locateText).not.toHaveBeenCalled();
    expect(router.navigate).toHaveBeenCalledWith(['/read', 'epub-1'], undefined);
  });

  it('continues an audiobook in the PDF at the located page, carrying the passage to mark', async () => {
    open(audiobook([pdf]), { audio: 180 });

    expect(rows()[0].textContent).toContain('Opens at the page you’re hearing');
    rows()[0].click();

    expect(alignment.locateText).toHaveBeenCalledWith('audio-0', {
      positionSeconds: 180,
      targetBookId: 'pdf-1',
    });

    textResponse.next({
      located: true,
      score: 0.96,
      excerpt: 'upon his own ramparts',
      pdf: { pageIndex: 11, pageNumber: 12, pageLabel: '4', textOffset: 640 },
      sourceText: 'in an easterly wind, upon his own ramparts',
    });
    await settle();

    expect(router.navigate).toHaveBeenCalledWith(['/read', 'pdf-1'], {
      queryParams: {
        sourcePage: 12,
        sourcePageLabel: '4',
        sourceExcerpt: 'in an easterly wind, upon his own ramparts',
      },
    });
  });

  it('opens a PDF where it was left when nothing has been played', () => {
    open(audiobook([pdf]), { audio: null });

    rows()[0].click();

    expect(alignment.locateText).not.toHaveBeenCalled();
    expect(router.navigate).toHaveBeenCalledWith(['/read', 'pdf-1'], undefined);
  });

  it('continues a PDF in the audiobook from the top of its page', async () => {
    const pdfBook = { id: 'pdf-0', type: 'ebook', fileName: 'own.pdf', title: 'A Christmas Carol', otherEditions: [audioA] } as unknown as Book;
    open(pdfBook, { excerpt: 'Marley was dead: to begin with.' });

    expect(rows()[0].textContent).toContain('Starts where this page begins');
    rows()[0].click();

    expect(alignment.locateAudio).toHaveBeenCalledWith('pdf-0', {
      targetBookId: 'audio-1',
      excerpt: 'Marley was dead: to begin with.',
    });
  });

  it('opens another text edition where it was left: there is no position to carry between two texts', () => {
    open(ebook([pdf]), { excerpt: 'Marley was dead.' });

    rows()[0].click();

    expect(alignment.locateText).not.toHaveBeenCalled();
    expect(alignment.locateAudio).not.toHaveBeenCalled();
    expect(router.navigate).toHaveBeenCalledWith(['/read', 'pdf-1'], undefined);
  });

  it('falls back to saved positions when transcription is not available', async () => {
    open(audiobook([epub]), { audio: 180 });

    rows()[0].click();
    textResponse.error(
      new HttpErrorResponse({ status: 503, error: { title: 'alignment_transcription_unavailable' } }),
    );
    await settle();

    expect(byTestId('switch-edition-unavailable')).not.toBeNull();
    expect(rows()[0].textContent).toContain('Opens where you left off');

    rows()[0].click();
    expect(alignment.locateText).toHaveBeenCalledTimes(1);
    expect(router.navigate).toHaveBeenCalledWith(['/read', 'epub-1'], undefined);
  });

  it('keeps the row usable after an ordinary failure', async () => {
    open(audiobook([epub]), { audio: 180 });

    rows()[0].click();
    textResponse.error(new HttpErrorResponse({ status: 502, error: { title: 'stt_provider_failed' } }));
    await settle();

    expect(byTestId('switch-edition-error')).not.toBeNull();
    expect(byTestId('switch-edition-unavailable')).toBeNull();
    expect(rows()[0].disabled).toBe(false);
  });

  it('offers a way out when finding the place runs long', async () => {
    vi.useFakeTimers();
    try {
      open(audiobook([epub]), { audio: 180 });
      rows()[0].click();
      fixture.detectChanges();
      expect(byTestId('switch-edition-slow')).toBeNull();

      vi.advanceTimersByTime(SwitchEditionDialog.SlowAfterMs + 1);
      fixture.detectChanges();

      const slow = byTestId('switch-edition-slow')!;
      expect(slow.textContent).toContain('Still looking');

      (slow.querySelector('button') as HTMLButtonElement).click();
      expect(router.navigate).toHaveBeenCalledWith(['/read', 'epub-1'], undefined);

      // The abandoned search must not navigate a second time.
      textResponse.next({
        located: true, score: 1, excerpt: 'x',
        locator: { resourceHref: 'ch1.xhtml', spineIndex: 1, textOffset: 1, cfi: null },
      });
      expect(router.navigate).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('drops a late answer after the dialog was closed', async () => {
    open(audiobook([epub]), { audio: 180 });
    rows()[0].click();

    fixture.componentInstance.close();
    textResponse.next({
      located: true, score: 1, excerpt: 'x',
      locator: { resourceHref: 'ch1.xhtml', spineIndex: 1, textOffset: 1, cfi: null },
    });
    await settle();

    expect(router.navigate).not.toHaveBeenCalled();
  });
});
