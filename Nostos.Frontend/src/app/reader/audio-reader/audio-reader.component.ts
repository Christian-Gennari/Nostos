import {
  Component,
  ElementRef,
  Injector,
  ViewChild,
  afterNextRender,
  inject,
  input,
  effect,
  signal,
  computed,
  OnDestroy,
  HostListener,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Howl } from 'howler';
import { Subject, Subscription } from 'rxjs';
import { sampleTime, filter } from 'rxjs/operators';
import { BooksService } from '../../core/services/books.service';
import { AssistantContextService } from '../../ui/assistant/assistant-context.service';
import { IReader, ReaderProgress, TocItem } from '../reader.interface';
import { Book } from '../../core/dtos/book.dtos';

import { BloomArtDirective } from '../../ui/bloom-art/bloom-art.directive';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';

@Component({
  selector: 'app-audio-reader',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    NostosIconComponent,
    BloomArtDirective,
    IconButtonComponent,
    LoadingIndicatorComponent,
  ],
  templateUrl: './audio-reader.component.html',
  styleUrl: './audio-reader.component.css',
})
export class AudioReader implements OnDestroy, IReader {
  bookId = input.required<string>();
  // The already-loaded book is passed in by the reader shell so this component
  // never issues a second GET /api/books/{id} (issue #7).
  book = input<Book | null>(null);

  private booksService = inject(BooksService);
  private assistantContext = inject(AssistantContextService);

  /**
   * Published to the assistant context registry (issue #261): the in-app audio
   * timestamp and its chapter. Additive only.
   */
  private unregisterAssistantContext: (() => void) | null = null;

  // IReader Interface
  toc = signal<TocItem[]>([]);
  progress = signal<ReaderProgress>({ label: '0:00', percentage: 0 });
  currentLocationTarget = computed(() => {
    const time = this.currentTime();
    const toc = this.toc();
    let activeTarget: string | number | null = null;
    let maxStart = -1;
    for (const item of toc) {
      const start = typeof item.target === 'number' ? item.target : parseFloat(item.target);
      if (!isNaN(start) && start <= time && start > maxStart) {
        maxStart = start;
        activeTarget = item.target;
      }
    }
    return activeTarget;
  });

  /** The chapter label the current position falls in, for the assistant context. */
  private assistantChapterLabel(): string | null {
    const target = this.currentLocationTarget();
    if (target === null) return null;
    const find = (items: TocItem[]): string | null => {
      for (const item of items) {
        if (item.target === target) return item.label;
        if (item.children) {
          const nested = find(item.children);
          if (nested) return nested;
        }
      }
      return null;
    };
    return find(this.toc());
  }

  // Player State
  player: Howl | null = null;
  isPlaying = signal(false);
  currentTime = signal(0);
  duration = signal(0);
  currentRate = signal(1);
  // The single playback menu holds speed and sleep (issue #227 §1/§3), so there is
  // one pill, one chevron, one focus path and one outside-click rule.
  // True while Howl is initializing (between `new Howl` and the onload callback),
  // so the UI can show a visible loading state instead of a dead-looking player.
  loading = signal(true);
  loadError = signal<string | null>(null);

  // --- Sleep timer (issue #47) ---
  // Armed preset in minutes; null means the timer is off. Session-only state:
  // never persisted — a browser reload clears it.
  sleepTimerMinutes = signal<number | null>(null);
  // Wall-clock deadline (epoch ms) for the armed countdown. The timer counts
  // real time, not play time: pausing playback does NOT pause the countdown
  // (standard sleep-timer behavior — it's a clock, not a play-time quota).
  sleepDeadline = signal<number | null>(null);
  // Whole seconds remaining, refreshed once per second while armed.
  sleepRemainingSeconds = signal(0);
  // Brief status message shown when the timer expires.
  sleepStatusMessage = signal<string | null>(null);
  // --- Playback menu: speed and sleep timer share ONE control (issue #227 §1/§3).
  playbackMenuOpen = signal(false);
  sleepPresets = [15, 30, 45, 60];
  // Sleep-at-end-of-chapter (issue #209). The chapter starts are already in the
  // component from the file metadata, so this needs no clock: it watches the
  // position reach the next chapter start and pauses there.
  sleepAtChapterEnd = signal(false);
  private chapterEndTarget = 0;
  private sleepTimerInterval: any = null;
  private sleepStatusTimeout: any = null;

  // --- Jump-to-timestamp (issue #6) ---
  isEditingTime = signal(false);
  timeInputValue = signal('');

  @ViewChild('timeInput') set timeInputRef(el: ElementRef<HTMLInputElement> | undefined) {
    if (el) {
      el.nativeElement.focus();
      el.nativeElement.select();
    }
  }

  @ViewChild('timeButton') timeButtonRef?: ElementRef<HTMLButtonElement>;

  private injector = inject(Injector);

  private restoreFocusToTimeButton(): void {
    afterNextRender(() => {
      this.timeButtonRef?.nativeElement.focus();
    }, { injector: this.injector });
  }

  // Playback speeds (issue #227 §2): a short preset list for the coarse choice,
  // with ±0.05 stepping in the same menu for the fine one. Remembered
  // reader-wide — a speed is a habit, not a property of one audiobook.
  availableRates = [0.8, 1, 1.25, 1.5, 2];
  private static readonly RATE_KEY = 'nostos.audio-rate';
  private static readonly RATE_STEP = 0.05;
  private static readonly RATE_MIN = 0.5;
  private static readonly RATE_MAX = 3;

  private progressSubject = new Subject<{ timestamp: number; percent: number }>();
  private progressSubscription!: Subscription;
  private progressTimer: any;
  private isInitialized = false;
  private onVisibilityChange = this.handleVisibilityChange.bind(this);
  private onBeforeUnload = this.saveProgressImmediately.bind(this);
  private onPageHide = this.saveProgressImmediately.bind(this);

  constructor() {
    this.unregisterAssistantContext = this.assistantContext.register(
      () => ({
        readerType: 'audio',
        audioTimestamp: this.currentTime(),
        audioChapter: this.assistantChapterLabel(),
      }),
      { explicit: true },
    );

    this.progressSubscription = this.progressSubject.pipe(
      sampleTime(2000),
      filter(() => this.isInitialized),
    ).subscribe((data) => {
      this.booksService
        .updateProgress(this.bookId(), data.timestamp.toString(), data.percent)
        .subscribe({
          error: (err) => console.error('Failed to save progress:', err),
        });
    });

    document.addEventListener('visibilitychange', this.onVisibilityChange);
    window.addEventListener('beforeunload', this.onBeforeUnload);
    window.addEventListener('pagehide', this.onPageHide);

    effect(() => {
      const id = this.bookId();
      if (id) {
        // Init Player
        this.initPlayer(id);
      }
    });

    // Build the table of contents from the book passed in by the reader shell.
    // No second GET /api/books/{id} (issue #7).
    effect(() => {
      const book = this.book();
      if (!book) return;

      if (book.chapters && book.chapters.length > 0) {
        this.toc.set(
          book.chapters.map((c) => ({
            label: c.title,
            target: c.startTime, // Target is the timestamp in seconds
            children: [],
          })),
        );
      }
    });
  }

  // --- IReader Methods ---
  next() {
    this.skip(15);
  }
  previous() {
    this.skip(-15);
  }
  zoomIn() {}
  zoomOut() {}

  goTo(target: string | number) {
    const time = typeof target === 'string' ? parseFloat(target) : target;
    if (!isNaN(time)) this.goToTime(time);
  }

  getCurrentLocation(): string {
    return this.currentTime().toString();
  }

  // --- Audio Logic ---
  initPlayer(id: string) {
    if (this.player) this.player.unload();

    this.loading.set(true);
    this.loadError.set(null);
    // Show the remembered speed immediately; applied to Howl on load.
    this.currentRate.set(this.restoreSavedRate());

    const src = `/api/books/${id}/file`;
    this.player = new Howl({
      src: [src],
      html5: true,
      format: ['mp3', 'm4a', 'm4b'],
      onload: () => {
        this.loading.set(false);
        this.duration.set(this.player?.duration() || 0);
        this.player?.rate(this.currentRate());
        this.restoreProgress();
        this.updateMediaSessionMetadata();
      },
      onloaderror: () => {
        // Loading failed; drop the loading state so the UI can surface the error
        // instead of appearing stuck forever.
        this.loading.set(false);
        this.loadError.set('Unable to load audio.');
      },
      onplay: () => {
        this.isPlaying.set(true);
        this.startProgressTracking();
        this.updateMediaSessionPlaybackState('playing');
      },
      onpause: () => {
        this.isPlaying.set(false);
        this.stopProgressTracking();
        this.saveProgressImmediately();
        this.updateMediaSessionPlaybackState('paused');
      },
      onend: () => {
        this.isPlaying.set(false);
        this.stopProgressTracking();
        this.currentTime.set(this.duration());
        this.updateProgressState();
        this.updateMediaSessionPlaybackState('none');
      },
    });
  }

  restoreProgress() {
    // The book (with its lastLocation) is passed in by the reader shell —
    // no second GET /api/books/{id} (issue #7). If the book has not arrived
    // yet, do nothing; the shell only renders this component once it has.
    const book = this.book();
    if (book?.lastLocation) {
      const timestamp = parseFloat(book.lastLocation);
      if (!isNaN(timestamp)) this.goToTime(timestamp);
    }
    this.updateProgressState();
    this.isInitialized = true;
  }

  togglePlay() {
    this.player?.playing() ? this.player.pause() : this.player?.play();
  }

  seek(event: any) {
    const time = parseFloat(event.target.value);
    this.goToTime(time);
  }

  skip(seconds: number) {
    if (!this.player) return;
    const current = this.player.seek() as number;
    const newTime = Math.max(0, Math.min(current + seconds, this.duration()));
    this.goToTime(newTime);
  }

  goToTime(seconds: number) {
    if (this.player) {
      this.player.seek(seconds);
      this.currentTime.set(seconds);
      // Re-arm BEFORE the progress update: that update is what checks the boundary,
      // and a jump is user intent, not "the position reached the chapter".
      this.rearmChapterSleepTarget();
      this.updateProgressState();
      this.updateMediaSessionPositionState();
    }
  }

  // Speed Logic
  toggleRate() {
    const current = this.currentRate();
    const currentIndex = this.availableRates.indexOf(current);

    // Calculate next index, wrapping around to the start
    const nextIndex = (currentIndex + 1) % this.availableRates.length;

    // Set the new rate
    this.setRate(this.availableRates[nextIndex]);
  }

  /** Fine step: the presets are the coarse choice, this is the nudge next to them. */
  nudgeRate(delta: number) {
    const stepped =
      Math.round((this.currentRate() + delta) / AudioReader.RATE_STEP) * AudioReader.RATE_STEP;
    this.setRate(this.clampRate(stepped));
  }

  private clampRate(rate: number): number {
    const clamped = Math.min(AudioReader.RATE_MAX, Math.max(AudioReader.RATE_MIN, rate));
    return parseFloat(clamped.toFixed(2));
  }

  /** Display form: the app's editorial copy uses the multiplication sign. */
  formatRate(rate: number): string {
    return `${rate}×`;
  }

  setRate(rate: number) {
    this.player?.rate(rate);
    this.currentRate.set(rate);
    try {
      localStorage.setItem(AudioReader.RATE_KEY, String(rate));
    } catch {
      // Private-mode storage can throw — playback speed still applies for the session.
    }
    this.updateMediaSessionPositionState();
  }

  /**
   * Reader-wide since issue #227 §2. A per-book value written before this change
   * is adopted once (same migration shape the EPUB typography key used), so
   * nobody loses the speed they had chosen on the book they are listening to.
   */
  private restoreSavedRate(): number {
    const read = (key: string): number | null => {
      const raw = localStorage.getItem(key);
      if (raw == null) return null;
      const parsed = parseFloat(raw);
      return isNaN(parsed) ? null : parsed;
    };
    try {
      const own = read(AudioReader.RATE_KEY);
      if (own != null) return this.clampRate(own);

      const perBook = read(`nostos.audio-rate.${this.bookId()}`);
      if (perBook != null) {
        const adopted = this.clampRate(perBook);
        localStorage.setItem(AudioReader.RATE_KEY, String(adopted));
        return adopted;
      }
    } catch {
      // Storage unreadable — fall back to 1x.
    }
    return 1;
  }

  // --- Chapter skip (TOC-driven, ±15s skip stays on the inner buttons) ---
  private sortedChapterStarts(): number[] {
    return this.toc()
      .map((item) => (typeof item.target === 'number' ? item.target : parseFloat(item.target)))
      .filter((start) => !isNaN(start))
      .sort((a, b) => a - b);
  }

  hasChapters = computed(() => this.sortedChapterStarts().length > 0);

  nextChapter() {
    const starts = this.sortedChapterStarts();
    if (starts.length === 0) return;
    const now = this.currentTime();
    const next = starts.find((start) => start > now + 2);
    this.goToTime(next ?? this.duration());
  }

  prevChapter() {
    const starts = this.sortedChapterStarts();
    if (starts.length === 0) return;
    const now = this.currentTime();
    const prev = [...starts].reverse().find((start) => start < now - 2);
    this.goToTime(prev ?? starts[0]);
  }

  // Dropdown — one menu now holds speed and sleep (issue #227 §1/§3)
  toggleDropdown() {
    this.playbackMenuOpen.update(v => !v);
  }

  closeDropdown() {
    this.playbackMenuOpen.set(false);
  }

  selectRate(rate: number) {
    this.setRate(rate);
    this.closeDropdown();
  }

  // --- Sleep Timer (issue #47) ---
  // Label for the timer control: the remaining mm:ss while armed, 'Off' otherwise.
  sleepLabel = computed(() => {
    if (this.sleepAtChapterEnd()) return 'Chapter end';
    const minutes = this.sleepTimerMinutes();
    if (minutes == null) return 'Off';
    return this.formatTime(this.sleepRemainingSeconds());
  });

  /** True whenever any sleep mode is armed, so the pill reports its state. */
  sleepArmed = computed(() => this.sleepAtChapterEnd() || this.sleepTimerMinutes() !== null);

  /**
   * Arm the end-of-chapter sleep (issue #209): pause when the position reaches the
   * next chapter start. Mutually exclusive with the minute presets, and falls back
   * to the end of the media on the last chapter, where pausing is harmless.
   */
  selectSleepAtChapterEnd(): void {
    this.closePlaybackMenu();
    this.sleepStatusMessage.set(null);
    this.disarmSleepTimer();
    this.chapterEndTarget = this.nextChapterStart(this.currentTime());
    this.sleepAtChapterEnd.set(true);
  }

  private nextChapterStart(afterSeconds: number): number {
    const starts = this.sortedChapterStarts();
    return starts.find((start) => start > afterSeconds + 1) ?? this.duration();
  }

  /**
   * A seek is not "reaching" a chapter: if the position jumps past the armed
   * target, the target moves to the next chapter start instead of firing.
   */
  private rearmChapterSleepTarget(): void {
    if (this.sleepAtChapterEnd()) this.chapterEndTarget = this.nextChapterStart(this.currentTime());
  }

  private checkChapterSleep(now: number): void {
    if (!this.sleepAtChapterEnd()) return;
    if (this.chapterEndTarget <= 0 || now < this.chapterEndTarget) return;
    this.sleepAtChapterEnd.set(false);
    this.chapterEndTarget = 0;
    // Same contract as the minute timer: the sleep timer stops the book, not the app.
    this.player?.pause();
    this.sleepStatusMessage.set('Chapter finished. Playback paused.');
    this.sleepStatusTimeout = window.setTimeout(() => {
      this.sleepStatusMessage.set(null);
    }, 5000);
  }

  toggleSleepMenu() {
    this.playbackMenuOpen.update((v) => !v);
  }

  closePlaybackMenu() {
    this.playbackMenuOpen.set(false);
  }

  selectSleepTimer(minutes: number | null) {
    this.closePlaybackMenu();
    this.sleepStatusMessage.set(null);
    // Either preset replaces whatever was armed, including the chapter mode.
    this.disarmSleepTimer();
    if (minutes == null) {
      return;
    }
    // Arming (or re-arming with another preset) always restarts the countdown
    // from the current wall-clock time.
    this.sleepTimerMinutes.set(minutes);
    this.sleepDeadline.set(Date.now() + minutes * 60_000);
    this.sleepRemainingSeconds.set(minutes * 60);
    this.startSleepTimerCountdown();
  }

  private disarmSleepTimer() {
    this.stopSleepTimerCountdown();
    this.sleepTimerMinutes.set(null);
    this.sleepDeadline.set(null);
    this.sleepRemainingSeconds.set(0);
    this.sleepAtChapterEnd.set(false);
    this.chapterEndTarget = 0;
  }

  private startSleepTimerCountdown() {
    this.stopSleepTimerCountdown();
    this.sleepTimerInterval = setInterval(() => this.tickSleepTimer(), 1000);
  }

  private stopSleepTimerCountdown() {
    if (this.sleepTimerInterval != null) {
      clearInterval(this.sleepTimerInterval);
      this.sleepTimerInterval = null;
    }
  }

  private tickSleepTimer() {
    const deadline = this.sleepDeadline();
    if (deadline == null) return;
    const now = Date.now();
    this.sleepRemainingSeconds.set(Math.max(0, Math.ceil((deadline - now) / 1000)));
    if (now >= deadline) {
      this.expireSleepTimer();
    }
  }

  private expireSleepTimer() {
    this.stopSleepTimerCountdown();
    this.sleepTimerMinutes.set(null);
    this.sleepDeadline.set(null);
    this.sleepRemainingSeconds.set(0);
    // Sleep timers stop the book, not the clock.
    this.player?.pause();
    this.sleepStatusMessage.set('Sleep timer finished. Playback paused.');
    this.sleepStatusTimeout = window.setTimeout(() => {
      this.sleepStatusMessage.set(null);
    }, 5000);
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent) {
    if (!this.playbackMenuOpen()) return;
    const target = event.target as HTMLElement;
    if (!target.closest('.playback-selector')) {
      this.closePlaybackMenu();
    }
  }

  @HostListener('document:keydown.escape')
  onKeydownEscape() {
    if (this.playbackMenuOpen()) {
      this.closePlaybackMenu();
    }
  }

  startProgressTracking() {
    this.stopProgressTracking();
    this.progressTimer = setInterval(() => {
      const seek = (this.player?.seek() as number) || 0;
      this.currentTime.set(seek);
      this.updateProgressState();
    }, 1000);
  }

  stopProgressTracking() {
    if (this.progressTimer) clearInterval(this.progressTimer);
  }

  private updateProgressState() {
    const now = this.currentTime();
    const total = this.duration();
    const percent = total > 0 ? Math.floor((now / total) * 100) : 0;
    const labels = this.timeLabels();
    const label = `${labels.current} / ${labels.total}`;
    this.progress.set({ label, percentage: percent });
    this.progressSubject.next({ timestamp: now, percent });
    // The tick is also the sleep timer's watch (issue #209): no second clock.
    this.checkChapterSleep(now);
  }

  /**
   * One format per media (issue #227 §4): hours appear only when the media has
   * them, so the two ends of the pair cannot disagree — this used to print
   * `0:00 / 15:59:00` for the same audiobook.
   */
  timeLabels = computed(() => {
    const total = this.duration();
    const withHours = total >= 3600;
    return {
      current: this.formatTime(this.currentTime(), withHours),
      total: this.formatTime(total, withHours),
    };
  });

  formatTime(seconds: number, withHours = false): string {
    if (!seconds || isNaN(seconds)) return withHours ? '0:00:00' : '0:00';
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = Math.floor(seconds % 60);
    if (withHours) return `${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  // --- Jump-to-timestamp (issue #6) ---
  startEditingTime(): void {
    if (this.duration() <= 0) return;
    this.timeInputValue.set(this.timeLabels().current);
    this.isEditingTime.set(true);
  }

  commitTimeEdit(value: string): void {
    if (!this.isEditingTime()) return;
    const seconds = parseTimeString(value);
    this.isEditingTime.set(false);
    if (seconds == null) {
      this.restoreFocusToTimeButton();
      return;
    }
    const clamped = Math.max(0, Math.min(seconds, this.duration()));
    this.goToTime(clamped);
    this.restoreFocusToTimeButton();
  }

  cancelTimeEdit(): void {
    if (!this.isEditingTime()) return;
    this.isEditingTime.set(false);
    this.restoreFocusToTimeButton();
  }

  removeHighlight(_identifier: string): void {
    // No-op: Audio reader does not support highlights
  }

  commitHighlight(): void {
    // No-op: Audio reader does not support highlights
  }

  discardHighlight(): void {
    // No-op: Audio reader does not support highlights
  }

  // --- Visibility Change: Save on hidden, resync on visible ---
  private handleVisibilityChange() {
    if (!this.player) return;

    if (document.visibilityState === 'hidden') {
      this.saveProgressImmediately();
      return;
    }

    const actualTime = this.player.seek() as number;
    const actuallyPlaying = this.player.playing();

    this.currentTime.set(actualTime);
    this.isPlaying.set(actuallyPlaying);
    this.updateProgressState();

    if (actuallyPlaying) {
      this.startProgressTracking();
    }

    this.updateMediaSessionPositionState();
  }

  private saveProgressImmediately() {
    if (!this.player || !this.isInitialized) return;
    const seek = this.player.seek() as number;
    if (seek > 0) {
      this.booksService.updateProgress(
        this.bookId(),
        seek.toString(),
        this.duration() > 0 ? Math.floor((seek / this.duration()) * 100) : 0,
      ).subscribe();
    }
  }

  // --- Media Session API: Sync with Android notification controls ---
  private updateMediaSessionMetadata() {
    if (!('mediaSession' in navigator)) return;
    const book = this.book();
    if (!book) return;

    navigator.mediaSession.metadata = new MediaMetadata({
      title: book.title,
      artist: book.author || book.narrator || undefined,
      album: book.series || undefined,
      artwork: book.coverUrl ? [{ src: book.coverUrl, sizes: '512x512', type: 'image/jpeg' }] : [],
    });

    // Register action handlers so notification controls sync back to our component
    navigator.mediaSession.setActionHandler('play', () => {
      this.player?.play();
    });

    navigator.mediaSession.setActionHandler('pause', () => {
      this.player?.pause();
    });

    navigator.mediaSession.setActionHandler('seekbackward', (details) => {
      this.skip(-(details.seekOffset || 15));
    });

    navigator.mediaSession.setActionHandler('seekforward', (details) => {
      this.skip(details.seekOffset || 15);
    });

    navigator.mediaSession.setActionHandler('seekto', (details) => {
      if (details.seekTime != null) {
        this.goToTime(details.seekTime!);
      }
    });
  }

  private updateMediaSessionPlaybackState(state: MediaSessionPlaybackState) {
    if (!('mediaSession' in navigator)) return;
    navigator.mediaSession.playbackState = state;
    this.updateMediaSessionPositionState();
  }

  private updateMediaSessionPositionState() {
    if (!('mediaSession' in navigator) || !this.player) return;
    try {
      navigator.mediaSession.setPositionState({
        duration: this.duration(),
        playbackRate: this.currentRate(),
        position: Math.min(this.currentTime(), this.duration()),
      });
    } catch {
      // setPositionState can throw if values are invalid (e.g., duration=0)
    }
  }

  ngOnDestroy() {
    this.unregisterAssistantContext?.();
    this.unregisterAssistantContext = null;
    this.saveProgressImmediately();
    this.stopProgressTracking();
    this.progressSubscription?.unsubscribe();
    this.progressSubject.complete();
    // Sleep timer: never leak the countdown interval or the status-message timeout.
    this.stopSleepTimerCountdown();
    if (this.sleepStatusTimeout != null) {
      clearTimeout(this.sleepStatusTimeout);
      this.sleepStatusTimeout = null;
    }
    document.removeEventListener('visibilitychange', this.onVisibilityChange);
    window.removeEventListener('beforeunload', this.onBeforeUnload);
    window.removeEventListener('pagehide', this.onPageHide);

    // Clean up Media Session
    if ('mediaSession' in navigator) {
      navigator.mediaSession.metadata = null;
      navigator.mediaSession.playbackState = 'none';
      for (const action of [
        'play',
        'pause',
        'seekbackward',
        'seekforward',
        'seekto',
      ] as MediaSessionAction[]) {
        try {
          navigator.mediaSession.setActionHandler(action, null);
        } catch {}
      }
    }

    this.player?.unload();
  }
}

/**
 * Parse a time string in `[H:]MM:SS` format into total seconds.
 * Accepts 1 or 2 digits per segment, trims surrounding whitespace.
 * Each segment is treated as a raw integer — out-of-range minute/second
 * values are NOT rejected here; the caller clamps the result to a
 * maximum (e.g. `duration()`). Returns null for any malformed input
 * (empty, negative, extra segments, non-digits).
 */
export function parseTimeString(input: string): number | null {
  const trimmed = (input ?? '').trim();
  if (!trimmed) return null;
  const match = /^(\d{1,2})(?::(\d{1,2}))(?::(\d{1,2}))?$/.exec(trimmed);
  if (!match) return null;
  const hasHours = match[3] != null;
  const hours = hasHours ? parseInt(match[1], 10) : 0;
  const minutes = hasHours ? parseInt(match[2], 10) : parseInt(match[1], 10);
  const seconds = hasHours ? parseInt(match[3], 10) : parseInt(match[2], 10);
  return hours * 3600 + minutes * 60 + seconds;
}
