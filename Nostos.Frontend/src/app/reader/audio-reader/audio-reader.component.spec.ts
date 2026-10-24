import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { AudioReader, PLAYING_SAVE_INTERVAL_MS, parseTimeString } from './audio-reader.component';
import { BooksService } from '../../core/services/books.service';
import { Book } from '../../core/dtos/book.dtos';
import { AUDIO_PLAYER_FACTORY, PlaylistCallbacks, PlaylistTrack } from './track-playlist-player';

// A fake player so specs can drive the player lifecycle (onload / onloaderror)
// without real media loading. `config` holds the callbacks the reader passed.
const playerState = { instances: [] as any[] };

const FAKE_PLAYER = {
  provide: AUDIO_PLAYER_FACTORY,
  useValue: (tracks: PlaylistTrack[], config: PlaylistCallbacks) => {
    const instance = {
      config,
      tracks,
      unload: vi.fn(),
      duration: vi.fn(() => 7200),
      seek: vi.fn(() => 0),
      playing: vi.fn(() => false),
      play: vi.fn(),
      pause: vi.fn(),
      rate: vi.fn(),
    };
    playerState.instances.push(instance);
    return instance;
  },
};

function mockPlayer(component: AudioReader): any {
  // Read what the component actually received from the factory; the player
  // reference is the production seam.
  expect(component.player).toBeDefined();
  return component.player as any;
}

describe('parseTimeString (issue #6)', () => {
  it('parses M:SS', () => {
    expect(parseTimeString('1:30')).toBe(90);
    expect(parseTimeString('0:00')).toBe(0);
    expect(parseTimeString('0:05')).toBe(5);
    expect(parseTimeString('12:34')).toBe(754);
  });

  it('parses H:MM:SS', () => {
    expect(parseTimeString('1:05:20')).toBe(3920);
    expect(parseTimeString('0:00:00')).toBe(0);
    expect(parseTimeString('0:01:00')).toBe(60);
  });

  it('accepts single-digit segments', () => {
    expect(parseTimeString('5:3')).toBe(303);
    expect(parseTimeString('1:2:3')).toBe(3723);
  });

  it('trims surrounding whitespace', () => {
    expect(parseTimeString('  1:30  ')).toBe(90);
  });

  it('returns null for empty / whitespace input', () => {
    expect(parseTimeString('')).toBeNull();
    expect(parseTimeString('   ')).toBeNull();
  });

  it('returns null for non-numeric or partial input', () => {
    expect(parseTimeString('abc')).toBeNull();
    expect(parseTimeString('1:')).toBeNull();
    expect(parseTimeString(':30')).toBeNull();
    expect(parseTimeString('1:30:00:00')).toBeNull();
    expect(parseTimeString('1:2:3:4')).toBeNull();
  });

  it('returns null for negative input', () => {
    expect(parseTimeString('-1:00')).toBeNull();
  });

  it('returns null for null / undefined input', () => {
    expect(parseTimeString(null as unknown as string)).toBeNull();
    expect(parseTimeString(undefined as unknown as string)).toBeNull();
  });

  it('returns permissive totals (caller clamps to duration)', () => {
    // Plan: 99:99 -> 6039s, then clamp to duration(). Parser does not reject.
    expect(parseTimeString('99:99')).toBe(6039);
    expect(parseTimeString('1:60')).toBe(120);
    expect(parseTimeString('1:00:99')).toBe(3699);
    expect(parseTimeString('1:99:00')).toBe(9540);
  });

  it('handles large hour values without overflow (caller clamps to duration)', () => {
    expect(parseTimeString('99:00:00')).toBe(356400);
  });
});

describe('AudioReader jump-to-timestamp (issue #6)', () => {
  let fixture: ComponentFixture<AudioReader>;
  let component: AudioReader;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AudioReader],
      providers: [FAKE_PLAYER],
    }).compileComponents();

    fixture = TestBed.createComponent(AudioReader);
    component = fixture.componentInstance;
    // bookId is a required input; provide a placeholder so the constructor effect
    // does not throw before our individual tests can set up state.
    fixture.componentRef.setInput('bookId', 'test-book-id');
  });

  it('applies a queued source timestamp after audio metadata is available', () => {
    fixture.detectChanges();
    const player = mockPlayer(component);

    component.goToSource({ type: 'audio', audioTime: 53 });
    expect(component.currentTime()).toBe(0);
    player.config.onload();

    expect(player.seek).toHaveBeenCalledWith(53);
    expect(component.currentTime()).toBe(53);
  });

  it('does not enter edit mode when duration is zero', () => {
    expect(component.duration()).toBe(0);
    component.startEditingTime();
    expect(component.isEditingTime()).toBe(false);
  });

  it('cancels cleanly without seeking', () => {
    component.duration.set(600);
    component.startEditingTime();
    expect(component.isEditingTime()).toBe(true);
    component.cancelTimeEdit();
    expect(component.isEditingTime()).toBe(false);
  });

  it('is idempotent on commit when not editing (no double-blur seek)', () => {
    component.duration.set(600);
    expect(component.isEditingTime()).toBe(false);
    component.commitTimeEdit('5:00');
    // isEditingTime was never set true; commit must be a no-op.
    expect(component.isEditingTime()).toBe(false);
  });

  it('cancels silently on malformed input and exits edit mode', () => {
    component.duration.set(600);
    component.startEditingTime();
    component.commitTimeEdit('not-a-time');
    expect(component.isEditingTime()).toBe(false);
  });

  it('clamps a value greater than duration via commitTimeEdit (no direct seek past end)', () => {
    component.duration.set(600);
    // Spy on goToTime so we can assert that commitTimeEdit invoked it with the
    // clamped value, not the raw 99:99 (=6039s) value.
    const seen: number[] = [];
    const original = (component as unknown as { goToTime: (n: number) => void }).goToTime;
    (component as unknown as { goToTime: (n: number) => void }).goToTime = (n: number) => {
      seen.push(n);
    };
    try {
      component.startEditingTime();
      component.commitTimeEdit('99:99');
    } finally {
      (component as unknown as { goToTime: (n: number) => void }).goToTime = original;
    }
    expect(seen).toEqual([600]);
    expect(component.isEditingTime()).toBe(false);
  });
});

function makeBook(overrides: Partial<Book> = {}): Book {
  return {
    id: 'book-1',
    title: 'The Iliad',
    subtitle: null,
    author: 'Homer',
    editor: null,
    translator: null,
    narrator: null,
    description: null,
    type: 'audiobook',
    edition: null,
    asin: null,
    duration: null,
    isbn: null,
    publisher: null,
    placeOfPublication: null,
    publishedDate: null,
    pageCount: null,
    language: null,
    categories: null,
    series: null,
    volumeNumber: null,
    createdAt: '2026-01-01T00:00:00Z',
    hasFile: true,
    fileName: 'iliad.m4b',
    coverUrl: null,
    collectionIds: [],
    lastLocation: null,
    progressPercent: 0,
    lastReadAt: null,
    rating: 0,
    isFavorite: false,
    personalReview: null,
    finishedAt: null,
    ...overrides,
  } as Book;
}

describe('AudioReader single-fetch + restore + loading state (issue #7)', () => {
  let fixture: ComponentFixture<AudioReader>;
  let component: AudioReader;
  const booksServiceMock = {
    get: vi.fn(),
    updateProgress: vi.fn(() => of(null)),
  };

  beforeEach(async () => {
    playerState.instances.length = 0;
    booksServiceMock.get.mockReset();
    booksServiceMock.updateProgress.mockReset();

    await TestBed.configureTestingModule({
      imports: [AudioReader],
      providers: [{ provide: BooksService, useValue: booksServiceMock }, FAKE_PLAYER],
    }).compileComponents();

    fixture = TestBed.createComponent(AudioReader);
    component = fixture.componentInstance;
  });

  function render(book: Book) {
    fixture.componentRef.setInput('bookId', book.id);
    fixture.componentRef.setInput('book', book);
    fixture.detectChanges();
  }

  function captureGoToTime(): { seen: number[]; restore: () => void } {
    const seen: number[] = [];
    const original = (component as unknown as { goToTime: (n: number) => void }).goToTime;
    (component as unknown as { goToTime: (n: number) => void }).goToTime = (n: number) => {
      seen.push(n);
    };
    return { seen, restore: () => ((component as unknown as { goToTime: (n: number) => void }).goToTime = original) };
  }

  it('restores playback from the passed-in lastLocation without a second book fetch', () => {
    render(makeBook({ lastLocation: '3721.5' }));

    const { seen, restore } = captureGoToTime();
    try {
      component.restoreProgress();
    } finally {
      restore();
    }

    expect(seen).toEqual([3721.5]);
    expect(booksServiceMock.get).not.toHaveBeenCalled();
  });

  it('does not seek when lastLocation is missing', () => {
    render(makeBook({ lastLocation: null }));

    const { seen, restore } = captureGoToTime();
    try {
      component.restoreProgress();
    } finally {
      restore();
    }

    expect(seen).toEqual([]);
    expect(booksServiceMock.get).not.toHaveBeenCalled();
  });

  it('does not seek when lastLocation is not a valid timestamp', () => {
    render(makeBook({ lastLocation: 'not-a-timestamp' }));

    const { seen, restore } = captureGoToTime();
    try {
      component.restoreProgress();
    } finally {
      restore();
    }

    expect(seen).toEqual([]);
    expect(booksServiceMock.get).not.toHaveBeenCalled();
  });

  it('builds the table of contents from the passed-in book chapters without a fetch', () => {
    render(
      makeBook({
        chapters: [
          { title: 'Book One', startTime: 0 },
          { title: 'Book Two', startTime: 3661 },
        ],
      }),
    );

    expect(component.toc()).toEqual([
      { label: 'Book One', target: 0, children: [] },
      { label: 'Book Two', target: 3661, children: [] },
    ]);
    expect(booksServiceMock.get).not.toHaveBeenCalled();
  });

  it('plays a single-file book as a playlist of one, its length read from the file (issue #1161)', () => {
    const book = makeBook();
    render(book);

    expect(mockPlayer(component).tracks).toEqual([{ url: `/api/books/${book.id}/file`, duration: 0 }]);
  });

  it('shows the loading state while the player initializes and clears it on load', () => {
    render(makeBook());

    const fakePlayer = mockPlayer(component);
    expect(fakePlayer).toBeDefined();
    expect(component.loading()).toBe(true);
    const loading = fixture.nativeElement.querySelector('[data-testid="audio-loading"]') as HTMLElement;
    expect(loading.textContent).toContain('Loading audio');
    expect(loading.querySelector('nostos-loading-indicator')).not.toBeNull();
    expect(loading.querySelector('.audio-loading-icon')).toBeNull();

    fakePlayer.config.onload();
    fixture.detectChanges();

    expect(component.loading()).toBe(false);
    expect(component.duration()).toBe(7200);
    expect(fixture.nativeElement.textContent).not.toContain('Loading audio');
  });

  it('clears the loading state and surfaces an error on load failure', () => {
    render(makeBook());

    const fakePlayer = mockPlayer(component);
    expect(fakePlayer).toBeDefined();
    expect(component.loading()).toBe(true);

    fakePlayer.config.onloaderror();
    fixture.detectChanges();

    expect(component.loading()).toBe(false);
    expect(component.loadError()).toBe('Unable to load audio.');
    expect(fixture.nativeElement.textContent).toContain('Unable to load audio');
  });
});

describe('AudioReader audio quick wins (speeds, chapters, persisted rate)', () => {
  let fixture: ComponentFixture<AudioReader>;
  let component: AudioReader;
  const booksServiceMock = {
    get: vi.fn(),
    updateProgress: vi.fn(() => of(null)),
  };

  beforeEach(async () => {
    playerState.instances.length = 0;
    booksServiceMock.get.mockReset();
    booksServiceMock.updateProgress.mockReset();
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [AudioReader],
      providers: [{ provide: BooksService, useValue: booksServiceMock }, FAKE_PLAYER],
    }).compileComponents();

    fixture = TestBed.createComponent(AudioReader);
    component = fixture.componentInstance;
  });

  it('offers a short preset list, with a fine step beside it', () => {
    // Issue #227 §2: nine entries was too much choice for a once-a-session control.
    expect(component.availableRates.length).toBeLessThanOrEqual(6);
    expect(component.availableRates).toEqual([0.8, 1, 1.25, 1.5, 2]);

    component.setRate(1);
    component.nudgeRate(0.05);
    expect(component.currentRate()).toBe(1.05);
    component.nudgeRate(-0.05);
    expect(component.currentRate()).toBe(1);
  });

  it('clamps the fine step to the supported range', () => {
    component.setRate(3);
    component.nudgeRate(0.05);
    expect(component.currentRate()).toBe(3);

    component.setRate(0.5);
    component.nudgeRate(-0.05);
    expect(component.currentRate()).toBe(0.5);
  });

  it('remembers the chosen speed reader-wide, not per book', () => {
    fixture.componentRef.setInput('bookId', 'book-42');
    fixture.detectChanges();

    component.selectRate(2);
    expect(component.currentRate()).toBe(2);
    expect(localStorage.getItem('nostos.audio-rate')).toBe('2');
    expect(localStorage.getItem('nostos.audio-rate.book-42')).toBeNull();
  });

  it('restores the remembered speed on init', () => {
    localStorage.setItem('nostos.audio-rate', '1.75');
    fixture.componentRef.setInput('bookId', 'book-7');
    fixture.detectChanges();

    expect(component.currentRate()).toBe(1.75);

    // Applied to the player once the audio loads.
    mockPlayer(component).config.onload();
    expect(mockPlayer(component).rate).toHaveBeenCalledWith(1.75);
  });

  it('adopts a speed that was saved per book before this change', () => {
    localStorage.setItem('nostos.audio-rate.book-9', '1.5');
    fixture.componentRef.setInput('bookId', 'book-9');
    fixture.detectChanges();

    expect(component.currentRate()).toBe(1.5);
    expect(localStorage.getItem('nostos.audio-rate')).toBe('1.5');
  });

  it('keeps the transport at three controls and leaves chapters to the list', () => {
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.componentRef.setInput('book', makeBook());
    fixture.detectChanges();

    const transport = fixture.nativeElement.querySelectorAll('.main-controls button');
    expect(transport.length).toBe(3);
    // The chapter list (shell panel) and the scrubber are the chapter navigation now.
    expect(fixture.nativeElement.querySelector('[data-testid="audio-prev-chapter"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="audio-next-chapter"]')).toBeNull();
    expect(component.hasChapters()).toBe(false);
  });

  it('gives the play/pause transport a state-aware accessible name', () => {
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.componentRef.setInput('book', makeBook());
    fixture.detectChanges();

    const play = fixture.nativeElement.querySelector('.play-btn') as HTMLButtonElement;
    expect(play.getAttribute('aria-label')).toBe('Play');

    component.isPlaying.set(true);
    fixture.detectChanges();
    expect(play.getAttribute('aria-label')).toBe('Pause');
  });

  it('uses one time format for both ends of the pair (issue #227 §4)', () => {
    component.duration.set(15 * 3600 + 59 * 60);
    component.currentTime.set(0);
    expect(component.timeLabels()).toEqual({ current: '0:00:00', total: '15:59:00' });

    // A media without hours keeps m:ss on both sides.
    component.duration.set(1800);
    component.currentTime.set(90);
    expect(component.timeLabels()).toEqual({ current: '1:30', total: '30:00' });
  });

  it('draws the scrubber with the shared slider, filled to the position (issue #1199)', () => {
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.componentRef.setInput('book', makeBook());
    fixture.detectChanges();
    component.duration.set(200);
    component.currentTime.set(50);
    fixture.detectChanges();

    const scrubber = fixture.nativeElement.querySelector('[data-testid="audio-scrubber"]') as HTMLInputElement;
    expect(scrubber.classList).toContain('nostos-range');
    expect(scrubber.getAttribute('aria-label')).toBe('Playback position');
    expect(scrubber.style.getPropertyValue('--range-fill')).toBe('25%');
  });

  it('writes the multiplication sign, not the letter x', () => {
    expect(component.formatRate(1.25)).toBe('1.25×');
  });

  it('jumps between chapters with next/prev', () => {
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.componentRef.setInput(
      'book',
      makeBook({
        chapters: [
          { title: 'One', startTime: 0 },
          { title: 'Two', startTime: 600 },
          { title: 'Three', startTime: 1200 },
        ],
      }),
    );
    fixture.detectChanges();

    const seen: number[] = [];
    const original = (component as unknown as { goToTime: (n: number) => void }).goToTime;
    (component as unknown as { goToTime: (n: number) => void }).goToTime = (n: number) => {
      seen.push(n);
    };
    try {
      component.currentTime.set(610);
      component.nextChapter();
      component.prevChapter();
    } finally {
      (component as unknown as { goToTime: (n: number) => void }).goToTime = original;
    }
    expect(seen).toEqual([1200, 600]);
  });

});

describe('AudioReader sleep timer (issue #47)', () => {
  let fixture: ComponentFixture<AudioReader>;
  let component: AudioReader;
  const booksServiceMock = {
    get: vi.fn(),
    updateProgress: vi.fn(() => of(null)),
  };

  beforeEach(async () => {
    playerState.instances.length = 0;
    booksServiceMock.get.mockReset();
    booksServiceMock.updateProgress.mockReset();

    await TestBed.configureTestingModule({
      imports: [AudioReader],
      providers: [{ provide: BooksService, useValue: booksServiceMock }, FAKE_PLAYER],
    }).compileComponents();

    fixture = TestBed.createComponent(AudioReader);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('bookId', 'test-book-id');
    fixture.detectChanges();

    // Scope fake timers to this suite only (the other suites use real timers).
    // Fake just the timers the sleep timer uses; leave queueMicrotask real so
    // Angular's signal/effect machinery keeps flushing normally.
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('arms a preset: sets the timer state and shows the remaining time', () => {
    expect(mockPlayer(component)).toBeDefined();

    component.selectSleepTimer(30);

    expect(component.sleepTimerMinutes()).toBe(30);
    expect(component.sleepRemainingSeconds()).toBe(1800);

    fixture.detectChanges();
    const label = fixture.nativeElement.querySelector('[data-testid="sleep-timer-label"]');
    expect(label.textContent).toContain('30:00');

    // The countdown ticks down once per second.
    vi.advanceTimersByTime(60_000);
    expect(component.sleepRemainingSeconds()).toBe(1740);

    fixture.detectChanges();
    expect(label.textContent).toContain('29:00');
  });

  it('pauses playback and clears the armed state when the timer expires', () => {
    const fakePlayer = mockPlayer(component);
    expect(fakePlayer).toBeDefined();

    component.selectSleepTimer(15);
    vi.advanceTimersByTime(15 * 60 * 1000);

    expect(fakePlayer.pause).toHaveBeenCalled();
    expect(component.sleepTimerMinutes()).toBeNull();
    expect(component.sleepDeadline()).toBeNull();
    expect(component.sleepRemainingSeconds()).toBe(0);
    expect(component.sleepStatusMessage()).toBe('Sleep timer finished. Playback paused.');

    fixture.detectChanges();
    const status = fixture.nativeElement.querySelector('[data-testid="sleep-status-message"]');
    expect(status).not.toBeNull();
    expect(status.textContent).toContain('Sleep timer finished');
  });

  it('resets the countdown when the preset is changed while armed', () => {
    component.selectSleepTimer(45);
    expect(component.sleepRemainingSeconds()).toBe(2700);

    vi.advanceTimersByTime(10 * 60 * 1000);
    expect(component.sleepRemainingSeconds()).toBe(2100);

    component.selectSleepTimer(15);
    expect(component.sleepTimerMinutes()).toBe(15);
    expect(component.sleepRemainingSeconds()).toBe(900);

    // The countdown restarted from the new preset, not the old deadline.
    vi.advanceTimersByTime(60_000);
    expect(component.sleepRemainingSeconds()).toBe(840);
  });

  it("disarms immediately when 'Off' is selected", () => {
    const fakePlayer = mockPlayer(component);
    expect(fakePlayer).toBeDefined();

    component.selectSleepTimer(30);
    vi.advanceTimersByTime(2 * 60 * 1000);
    expect(component.sleepRemainingSeconds()).toBe(1680);

    component.selectSleepTimer(null);

    expect(component.sleepTimerMinutes()).toBeNull();
    expect(component.sleepDeadline()).toBeNull();
    expect(component.sleepRemainingSeconds()).toBe(0);
    // Off just disarms — it must not pause playback.
    expect(fakePlayer.pause).not.toHaveBeenCalled();

    // No countdown continues after disarming.
    vi.advanceTimersByTime(5 * 60 * 1000);
    expect(component.sleepTimerMinutes()).toBeNull();
    expect(component.sleepRemainingSeconds()).toBe(0);
  });

  it('clears the countdown interval when the component is destroyed (no leaked timers)', () => {
    component.selectSleepTimer(30);
    const intervalId = (component as unknown as { sleepTimerInterval: number | null }).sleepTimerInterval;
    expect(intervalId).not.toBeNull();

    const clearSpy = vi.spyOn(globalThis, 'clearInterval');
    fixture.destroy();

    expect(clearSpy).toHaveBeenCalledWith(intervalId);

    // Advancing time after destroy must not throw or resurrect any tick.
    expect(() => vi.advanceTimersByTime(10 * 60 * 1000)).not.toThrow();
  });

  it('keeps counting down wall-clock time while playback is paused (wall-clock decision)', () => {
    const fakePlayer = mockPlayer(component);
    expect(fakePlayer).toBeDefined();

    component.selectSleepTimer(30);
    // Simulate the user pausing playback (the player fires onpause).
    fakePlayer.config.onpause();
    expect(component.isPlaying()).toBe(false);

    vi.advanceTimersByTime(5 * 60 * 1000);

    // The countdown kept running despite playback being paused — the timer is
    // a wall-clock sleep timer, not a play-time quota.
    expect(component.sleepTimerMinutes()).toBe(30);
    expect(component.sleepRemainingSeconds()).toBe(1500);
  });

  it('shows a brief status message on expiry and clears it after a few seconds', () => {
    component.selectSleepTimer(15);
    vi.advanceTimersByTime(15 * 60 * 1000);

    expect(component.sleepStatusMessage()).toBe('Sleep timer finished. Playback paused.');

    vi.advanceTimersByTime(5000);
    expect(component.sleepStatusMessage()).toBeNull();
  });

  it('offers the speed presets and the Off/15/30/45/60 presets in one menu', () => {
    expect(component.playbackMenuOpen()).toBe(false);

    component.toggleSleepMenu();
    fixture.detectChanges();

    const menu = fixture.nativeElement.querySelector('.playback-menu') as Element | null;
    expect(menu).not.toBeNull();
    const labels = Array.from(menu!.querySelectorAll('.sleep-option')).map((el: Element) =>
      el.textContent?.trim(),
    );
    expect(labels).toEqual(['Off', '15 min', '30 min', '45 min', '60 min']);
    // Speed lives in the same menu now (issue #227 §3): the presets and the fine step.
    const rateOptions = Array.from(menu!.querySelectorAll('.rate-options [role="radio"]')).map((el: Element) =>
      el.textContent?.trim(),
    );
    expect(rateOptions).toEqual(['0.8×', '1×', '1.25×', '1.5×', '2×']);
    expect(menu!.querySelectorAll('.rate-step').length).toBe(2);

    // Re-selecting closes the menu and arms the chosen preset.
    component.selectSleepTimer(45);
    expect(component.playbackMenuOpen()).toBe(false);
    expect(component.sleepTimerMinutes()).toBe(45);
  });

  it('pauses at the next chapter start instead of a clock deadline (issue #209)', () => {
    fixture.componentRef.setInput(
      'book',
      makeBook({
        chapters: [
          { title: 'One', startTime: 0 },
          { title: 'Two', startTime: 600 },
          { title: 'Three', startTime: 1200 },
        ],
      }),
    );
    fixture.detectChanges();
    const fakePlayer = mockPlayer(component);
    fakePlayer.config.onload();

    component.currentTime.set(610);
    component.selectSleepAtChapterEnd();
    expect(component.sleepAtChapterEnd()).toBe(true);
    expect(component.sleepLabel()).toBe('Chapter end');

    // Inside the chapter nothing happens; the position reaching 1200 does.
    // The progress watch is restarted here so it runs on the fake clock — the
    // component started it during init, before this suite replaced the timers.
    component.startProgressTracking();
    fakePlayer.seek = vi.fn(() => 1200);
    vi.advanceTimersByTime(1000);

    expect(fakePlayer.pause).toHaveBeenCalled();
    expect(component.sleepAtChapterEnd()).toBe(false);
    expect(component.sleepStatusMessage()).toBe('Chapter finished. Playback paused.');
  });

  it('re-arms to the following chapter when the position jumps past the armed one', () => {
    fixture.componentRef.setInput(
      'book',
      makeBook({
        chapters: [
          { title: 'One', startTime: 0 },
          { title: 'Two', startTime: 600 },
          { title: 'Three', startTime: 1200 },
          { title: 'Four', startTime: 1800 },
        ],
      }),
    );
    fixture.detectChanges();
    const fakePlayer = mockPlayer(component);
    fakePlayer.config.onload();

    component.currentTime.set(610);
    component.selectSleepAtChapterEnd();

    // A jump to 1300 skips the 1200 boundary. Seeking is user intent, so it must
    // NOT fire — the armed target moves to the next chapter start instead.
    component.goToTime(1300);
    expect(fakePlayer.pause).not.toHaveBeenCalled();
    expect(component.sleepAtChapterEnd()).toBe(true);

    component.startProgressTracking();
    fakePlayer.seek = vi.fn(() => 1800);
    vi.advanceTimersByTime(1000);
    expect(fakePlayer.pause).toHaveBeenCalled();
  });

  it('stays armed but inert past the last chapter start', () => {
    fixture.componentRef.setInput(
      'book',
      makeBook({ chapters: [{ title: 'One', startTime: 0 }] }),
    );
    fixture.detectChanges();
    const fakePlayer = mockPlayer(component);
    fakePlayer.config.onload();

    component.currentTime.set(100);
    component.selectSleepAtChapterEnd();

    // No chapter follows, so the target is the end of the media; nothing fires
    // mid-chapter and the state stays visible.
    fakePlayer.seek = vi.fn(() => 200);
    vi.advanceTimersByTime(1000);
    expect(fakePlayer.pause).not.toHaveBeenCalled();
    expect(component.sleepLabel()).toBe('Chapter end');
  });

  it('treats the chapter mode and the minute presets as mutually exclusive', () => {
    fixture.componentRef.setInput(
      'book',
      makeBook({ chapters: [{ title: 'One', startTime: 0 }, { title: 'Two', startTime: 600 }] }),
    );
    fixture.detectChanges();

    component.currentTime.set(100);
    component.selectSleepAtChapterEnd();
    expect(component.sleepArmed()).toBe(true);

    component.selectSleepTimer(30);
    expect(component.sleepAtChapterEnd()).toBe(false);
    expect(component.sleepTimerMinutes()).toBe(30);
    expect(component.sleepLabel()).not.toBe('Chapter end');

    component.selectSleepAtChapterEnd();
    expect(component.sleepTimerMinutes()).toBeNull();

    component.selectSleepTimer(null);
    expect(component.sleepArmed()).toBe(false);
    expect(component.sleepLabel()).toBe('Off');
  });

  it('offers Chapter end in the menu only when the file has chapters', () => {
    fixture.componentRef.setInput('book', makeBook());
    fixture.detectChanges();
    component.toggleSleepMenu();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="audio-sleep-chapter-end"]')).toBeNull();

    fixture.componentRef.setInput(
      'book',
      makeBook({ chapters: [{ title: 'One', startTime: 0 }] }),
    );
    fixture.detectChanges();
    expect(
      fixture.nativeElement.querySelector('[data-testid="audio-sleep-chapter-end"]')
    ).not.toBeNull();
  });
});

describe('AudioReader with a multi-track audiobook (issue #835)', () => {
  // The playlist player drives one <audio> element; the test environment has
  // no media stack, so a minimal element stands in for it.
  class StubAudio extends EventTarget {
    static instances: StubAudio[] = [];
    src = '';
    preload = '';
    currentTime = 0;
    paused = true;
    ended = false;
    playbackRate = 1;
    defaultPlaybackRate = 1;
    constructor() {
      super();
      StubAudio.instances.push(this);
    }
    load(): void {}
    play(): Promise<void> {
      this.paused = false;
      this.dispatchEvent(new Event('playing'));
      return Promise.resolve();
    }
    pause(): void {
      this.paused = true;
    }
    removeAttribute(): void {
      this.src = '';
    }
  }

  let fixture: ComponentFixture<AudioReader>;
  let component: AudioReader;

  beforeEach(async () => {
    StubAudio.instances = [];
    playerState.instances.length = 0;
    vi.stubGlobal('Audio', StubAudio);
    await TestBed.configureTestingModule({
      imports: [AudioReader],
      providers: [
        { provide: BooksService, useValue: { updateProgress: vi.fn(() => of(null)) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(AudioReader);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('bookId', 'multi-track-book');
    fixture.componentRef.setInput(
      'book',
      makeBook({
        fileName: null,
        lastLocation: '130',
        chapters: [
          { title: 'One', startTime: 0 },
          { title: 'Two', startTime: 100 },
        ],
        tracks: [
          { number: 1, title: 'One', duration: 100, bytes: 10, contentType: 'audio/mpeg' },
          { number: 2, title: 'Two', duration: 200, bytes: 20, contentType: 'audio/mpeg' },
        ],
      }),
    );
  });

  afterEach(() => {
    fixture.destroy();
    vi.unstubAllGlobals();
  });

  it('plays the tracks through one audio element instead of a single file', () => {
    fixture.detectChanges();

    expect(StubAudio.instances.length).toBe(1);
    expect(StubAudio.instances[0].src).toBe('/api/books/multi-track-book/tracks/1');
    expect(playerState.instances.length).toBe(0);
  });

  it('shows one continuous book and resumes at the saved position in the right track', () => {
    fixture.detectChanges();
    const audio = StubAudio.instances[0];

    audio.dispatchEvent(new Event('loadedmetadata'));

    expect(component.loading()).toBe(false);
    expect(component.duration()).toBe(300);
    // 130 s into the book is 30 s into the second track.
    expect(audio.src).toBe('/api/books/multi-track-book/tracks/2');
    expect(component.currentTime()).toBe(130);

    audio.dispatchEvent(new Event('loadedmetadata'));
    expect(audio.currentTime).toBe(30);
    expect(component.timeLabels()).toEqual({ current: '2:10', total: '5:00' });
  });

  it('does not restart playback when the book object is refreshed', () => {
    fixture.detectChanges();
    const audio = StubAudio.instances[0];
    audio.dispatchEvent(new Event('loadedmetadata'));

    fixture.componentRef.setInput(
      'book',
      makeBook({
        fileName: null,
        lastLocation: '5',
        tracks: [
          { number: 1, title: 'One', duration: 100, bytes: 10, contentType: 'audio/mpeg' },
          { number: 2, title: 'Two', duration: 200, bytes: 20, contentType: 'audio/mpeg' },
        ],
      }),
    );
    fixture.detectChanges();

    expect(StubAudio.instances.length).toBe(1);
    expect(component.currentTime()).toBe(130);
  });
});


describe('AudioReader current chapter (issue #904)', () => {
  let fixture: ComponentFixture<AudioReader>;
  let component: AudioReader;
  // The test environment has no Media Session; the stand-in records every
  // metadata object the component writes.
  let written: MediaMetadataInit[];

  const chapters = [
    { title: 'Book I: The Quarrel', startTime: 0 },
    { title: 'Book II: The Dream', startTime: 600 },
    { title: 'Book III: The Duel', startTime: 1200 },
  ];

  beforeEach(async () => {
    playerState.instances.length = 0;
    written = [];
    vi.stubGlobal(
      'MediaMetadata',
      class {
        constructor(init: MediaMetadataInit) {
          written.push(init);
        }
      },
    );
    Object.defineProperty(navigator, 'mediaSession', {
      configurable: true,
      value: {
        metadata: null,
        playbackState: 'none',
        setActionHandler: vi.fn(),
        setPositionState: vi.fn(),
      },
    });

    await TestBed.configureTestingModule({
      imports: [AudioReader],
      providers: [
        { provide: BooksService, useValue: { updateProgress: vi.fn(() => of(null)) } },
        FAKE_PLAYER,
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(AudioReader);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    fixture.destroy();
    delete (navigator as unknown as { mediaSession?: unknown }).mediaSession;
    vi.unstubAllGlobals();
  });

  function render(book: Book) {
    fixture.componentRef.setInput('bookId', book.id);
    fixture.componentRef.setInput('book', book);
    fixture.detectChanges();
    mockPlayer(component).config.onload();
    fixture.detectChanges();
  }

  function chapterButton(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('[data-testid="audio-current-chapter"]');
  }

  /** One second of playback: the tick reads the position from the player. */
  function tickAt(seconds: number) {
    component.currentTime.set(seconds);
    fixture.detectChanges();
  }

  it('shows the chapter the position falls in and follows it across a boundary', () => {
    render(makeBook({ chapters }));

    expect(chapterButton()!.textContent!.trim()).toBe('Book I: The Quarrel');

    tickAt(599);
    expect(chapterButton()!.textContent!.trim()).toBe('Book I: The Quarrel');

    tickAt(600);
    expect(chapterButton()!.textContent!.trim()).toBe('Book II: The Dream');
  });

  it('follows a seek, whichever control made it', () => {
    render(makeBook({ chapters }));

    component.seek({ target: { value: '1250' } });
    fixture.detectChanges();
    expect(chapterButton()!.textContent!.trim()).toBe('Book III: The Duel');

    component.goTo(600);
    fixture.detectChanges();
    expect(chapterButton()!.textContent!.trim()).toBe('Book II: The Dream');

    component.startEditingTime();
    component.commitTimeEdit('0:30');
    fixture.detectChanges();
    expect(chapterButton()!.textContent!.trim()).toBe('Book I: The Quarrel');
  });

  it('opens on the saved chapter when resuming', () => {
    render(makeBook({ chapters, lastLocation: '1300' }));

    expect(chapterButton()!.textContent!.trim()).toBe('Book III: The Duel');
  });

  it('names the chapter for assistive tech, offers the full title, and is not a live region', () => {
    render(makeBook({ chapters }));

    const button = chapterButton()!;
    expect(button.getAttribute('aria-label')).toBe(
      'Current chapter: Book I: The Quarrel. Open contents',
    );
    expect(button.title).toBe('Book I: The Quarrel');
    const line = fixture.nativeElement.querySelector('.chapter-line') as HTMLElement;
    expect(line.hasAttribute('aria-live')).toBe(false);
    expect(line.getAttribute('role')).toBeNull();
  });

  it('asks the shell for Contents when activated', () => {
    render(makeBook({ chapters }));
    const requested = vi.fn();
    component.contentsRequested.subscribe(requested);

    chapterButton()!.click();

    expect(requested).toHaveBeenCalledTimes(1);
  });

  it('renders no chapter line for a book without chapters', () => {
    render(makeBook({ chapters: [] }));

    expect(fixture.nativeElement.querySelector('.chapter-line')).toBeNull();
    expect(chapterButton()).toBeNull();
    expect(component.currentChapter()).toBeNull();
  });

  it('keeps the line, empty, before the first chapter start', () => {
    render(makeBook({ chapters: [{ title: 'Chapter 1', startTime: 30 }] }));

    expect(fixture.nativeElement.querySelector('.chapter-line')).not.toBeNull();
    expect(chapterButton()).toBeNull();

    tickAt(30);
    expect(chapterButton()!.textContent!.trim()).toBe('Chapter 1');
  });

  it('puts the chapter on the lock screen and rewrites it once per chapter change', () => {
    render(makeBook({ chapters, series: 'Epic Cycle', narrator: 'A. Reader' }));

    expect(written).toEqual([
      { title: 'Book I: The Quarrel', artist: 'Homer', album: 'The Iliad', artwork: [] },
    ]);

    // Ticks inside the chapter write nothing.
    for (const second of [1, 2, 3, 599]) tickAt(second);
    expect(written.length).toBe(1);

    tickAt(600);
    for (const second of [601, 602]) tickAt(second);
    expect(written.length).toBe(2);
    expect(written[1]).toEqual({
      title: 'Book II: The Dream',
      artist: 'Homer',
      album: 'The Iliad',
      artwork: [],
    });
  });

  it('keeps the book metadata on the lock screen for a book without chapters', () => {
    render(makeBook({ chapters: [], series: 'Epic Cycle' }));
    for (const second of [1, 600, 1200]) tickAt(second);

    expect(written).toEqual([
      { title: 'The Iliad', artist: 'Homer', album: 'Epic Cycle', artwork: [] },
    ]);
  });
});

describe('AudioReader keeps its place when the player loses it', () => {
  let fixture: ComponentFixture<AudioReader>;
  let component: AudioReader;
  let fakePlayer: any;
  let visibility: DocumentVisibilityState;
  const booksServiceMock = { updateProgress: vi.fn(() => of(null)) };

  beforeEach(async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
    booksServiceMock.updateProgress.mockClear();
    visibility = 'visible';
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility);

    await TestBed.configureTestingModule({
      imports: [AudioReader],
      providers: [{ provide: BooksService, useValue: booksServiceMock }, FAKE_PLAYER],
    }).compileComponents();

    fixture = TestBed.createComponent(AudioReader);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('bookId', 'book-1');
    // 43 minutes into a two-hour book.
    fixture.componentRef.setInput('book', makeBook({ lastLocation: '2580' }));
    fixture.detectChanges();
    fakePlayer = mockPlayer(component);
    let position = 0;
    fakePlayer.seek = vi.fn((seconds?: number) => {
      if (seconds === undefined) return position;
      position = seconds;
      return fakePlayer;
    });
    fakePlayer.config.onload();
    fakePlayer.resetPosition = () => (position = 0);
    booksServiceMock.updateProgress.mockClear();
  });

  afterEach(() => {
    component.ngOnDestroy();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function savedLocations(): string[] {
    return booksServiceMock.updateProgress.mock.calls.map((call: unknown[]) => call[1] as string);
  }

  function leaveAndReturn() {
    visibility = 'hidden';
    document.dispatchEvent(new Event('visibilitychange'));
    // The phone drops the paused media element while the tab is away.
    fakePlayer.resetPosition();
    visibility = 'visible';
    document.dispatchEvent(new Event('visibilitychange'));
  }

  it('returning to a paused book keeps the place and never saves 0:00', () => {
    leaveAndReturn();
    vi.advanceTimersByTime(5000);

    expect(component.currentTime()).toBe(2580);
    expect(fakePlayer.seek()).toBe(2580);
    expect(savedLocations()).not.toContain('0');
    expect(savedLocations().every((location) => location === '2580')).toBe(true);
  });

  it('play from the lock screen after a reset resumes from the place, not the start', () => {
    fakePlayer.resetPosition();
    component.togglePlay();

    expect(fakePlayer.seek).toHaveBeenCalledWith(2580);
    expect(fakePlayer.play).toHaveBeenCalled();
    expect(fakePlayer.seek()).toBe(2580);
  });

  it('a reset noticed while playing puts the player back', () => {
    fakePlayer.config.onplay();
    fakePlayer.resetPosition();
    vi.advanceTimersByTime(5000);

    expect(component.currentTime()).toBe(2580);
    expect(savedLocations()).not.toContain('0');
  });

  it('saves a playing position every ten seconds, not every tick', () => {
    fakePlayer.config.onplay();
    vi.advanceTimersByTime(30_000);

    expect(booksServiceMock.updateProgress).toHaveBeenCalledTimes(30_000 / PLAYING_SAVE_INTERVAL_MS);
  });

  it('a seek to the start that the listener made is kept and saved', () => {
    component.goToTime(0);
    leaveAndReturn();
    vi.advanceTimersByTime(5000);

    expect(component.currentTime()).toBe(0);
    expect(savedLocations()).toContain('0');
  });

  it('hands off the place, not 0:00, when switching edition after a reset', () => {
    fakePlayer.resetPosition();

    expect(component.handoffPosition()).toBe(2580);
    expect(fakePlayer.seek()).toBe(2580);
    expect(savedLocations()).not.toContain('0');
  });

  it('a finished book plays again from the start', () => {
    component.goToTime(7200);
    fakePlayer.resetPosition();
    component.togglePlay();

    expect(fakePlayer.seek()).toBe(0);
  });
});

describe('AudioReader recovers from a dropped connection (issue #1058)', () => {
  let fixture: ComponentFixture<AudioReader>;
  let component: AudioReader;
  let online: boolean;
  const booksServiceMock = { updateProgress: vi.fn(() => of(null)) };

  /** Gives the current player a working position, as a loaded player has. */
  function withPosition(fakePlayer: any, start = 0): any {
    let position = start;
    fakePlayer.seek = vi.fn((seconds?: number) => {
      if (seconds === undefined) return position;
      position = seconds;
      return fakePlayer;
    });
    return fakePlayer;
  }

  /** Loads the book and starts playback 43 minutes in. */
  function startPlaying(): any {
    const fakePlayer = withPosition(mockPlayer(component));
    fakePlayer.config.onload();
    component.goToTime(2580);
    fakePlayer.config.onplay();
    fixture.detectChanges();
    expect(component.isPlaying()).toBe(true);
    return fakePlayer;
  }

  beforeEach(async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
    booksServiceMock.updateProgress.mockClear();
    online = true;
    vi.spyOn(navigator, 'onLine', 'get').mockImplementation(() => online);

    await TestBed.configureTestingModule({
      imports: [AudioReader],
      providers: [{ provide: BooksService, useValue: booksServiceMock }, FAKE_PLAYER],
    }).compileComponents();

    fixture = TestBed.createComponent(AudioReader);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.componentRef.setInput('book', makeBook({ lastLocation: '60' }));
    fixture.detectChanges();
  });

  afterEach(() => {
    component.ngOnDestroy();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('shows play, not pause, and "Reconnecting…" when the stream dies mid-book', () => {
    const fakePlayer = startPlaying();

    fakePlayer.config.onloaderror();
    fixture.detectChanges();

    expect(component.isPlaying()).toBe(false);
    expect(component.loadError()).toBeNull();
    expect(component.reconnecting()).toBe(true);
    const status = fixture.nativeElement.querySelector('[data-testid="audio-reconnecting"]') as HTMLElement;
    expect(status.textContent).toContain('Reconnecting');
    expect(fixture.nativeElement.querySelector('[data-testid="audio-load-error"]')).toBeNull();
  });

  it('reloads after a wait and resumes playing at the same place', () => {
    const fakePlayer = startPlaying();
    fakePlayer.config.onloaderror();

    vi.advanceTimersByTime(AudioReader.RELOAD_DELAYS_MS[0]);
    const reloaded = withPosition(mockPlayer(component));
    expect(reloaded).not.toBe(fakePlayer);
    expect(fakePlayer.unload).toHaveBeenCalled();

    reloaded.config.onload();
    fixture.detectChanges();

    expect(reloaded.seek()).toBe(2580);
    expect(component.currentTime()).toBe(2580);
    expect(reloaded.play).toHaveBeenCalled();
    expect(component.reconnecting()).toBe(false);
    expect(component.loadError()).toBeNull();
  });

  it('stays paused after the reload when the drop happened while paused', () => {
    const fakePlayer = withPosition(mockPlayer(component));
    fakePlayer.config.onload();
    component.goToTime(2580);

    fakePlayer.config.onloaderror();
    vi.advanceTimersByTime(AudioReader.RELOAD_DELAYS_MS[0]);
    const reloaded = withPosition(mockPlayer(component));
    reloaded.config.onload();

    expect(reloaded.seek()).toBe(2580);
    expect(reloaded.play).not.toHaveBeenCalled();
  });

  it('waits for the connection while offline, then reloads at once when it is back', () => {
    const fakePlayer = startPlaying();
    online = false;
    fakePlayer.config.onloaderror();

    vi.advanceTimersByTime(60_000);
    expect(mockPlayer(component)).toBe(fakePlayer);
    expect(component.reconnecting()).toBe(true);

    online = true;
    window.dispatchEvent(new Event('online'));
    const reloaded = withPosition(mockPlayer(component));
    expect(reloaded).not.toBe(fakePlayer);

    reloaded.config.onload();
    expect(reloaded.seek()).toBe(2580);
    expect(reloaded.play).toHaveBeenCalled();
  });

  it('shows the error with a Retry action once the reloads run out', () => {
    let fakePlayer = startPlaying();
    fakePlayer.config.onloaderror();
    for (const delay of AudioReader.RELOAD_DELAYS_MS) {
      vi.advanceTimersByTime(delay);
      fakePlayer = mockPlayer(component);
      fakePlayer.config.onloaderror();
    }
    fixture.detectChanges();

    expect(component.reconnecting()).toBe(false);
    expect(component.loadError()).toBe('Unable to load audio.');
    const error = fixture.nativeElement.querySelector('[data-testid="audio-load-error"]') as HTMLElement;
    const retry = error.querySelector('button') as HTMLButtonElement;
    expect(retry.textContent).toContain('Retry');

    retry.click();
    const reloaded = withPosition(mockPlayer(component));
    expect(reloaded).not.toBe(fakePlayer);
    reloaded.config.onload();

    // Still at the place the listener was, and still meant to be playing.
    expect(reloaded.seek()).toBe(2580);
    expect(reloaded.play).toHaveBeenCalled();
  });

  it('pressing play while reconnecting reloads now instead of waiting', () => {
    const fakePlayer = withPosition(mockPlayer(component));
    fakePlayer.config.onload();
    fakePlayer.config.onloaderror();

    component.togglePlay();
    const reloaded = withPosition(mockPlayer(component));
    expect(reloaded).not.toBe(fakePlayer);
    reloaded.config.onload();

    expect(reloaded.play).toHaveBeenCalled();
  });

  it('shows the error at once when a book that never loaded fails while online', () => {
    const fakePlayer = mockPlayer(component);
    fakePlayer.config.onloaderror();
    vi.advanceTimersByTime(60_000);

    expect(mockPlayer(component)).toBe(fakePlayer);
    expect(component.reconnecting()).toBe(false);
    expect(component.loadError()).toBe('Unable to load audio.');
  });

  it('stops trying once the reader is closed', () => {
    const fakePlayer = startPlaying();
    fakePlayer.config.onloaderror();
    component.ngOnDestroy();

    vi.advanceTimersByTime(60_000);
    window.dispatchEvent(new Event('online'));

    expect(mockPlayer(component)).toBe(fakePlayer);
  });
});
