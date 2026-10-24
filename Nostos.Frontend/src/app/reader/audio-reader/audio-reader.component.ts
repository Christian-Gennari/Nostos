import {
  Component,
  ElementRef,
  Injector,
  ViewChild,
  afterNextRender,
  inject,
  input,
  output,
  effect,
  signal,
  computed,
  untracked,
  OnDestroy,
  HostListener,
  ChangeDetectionStrategy,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subject, Subscription } from 'rxjs';
import { sampleTime, filter } from 'rxjs/operators';
import { BooksService } from '../../core/services/books.service';
import { AssistantContextService } from '../../ui/assistant/assistant-context.service';
import { IReader, ReaderProgress, ReaderSourceTarget, TocItem } from '../reader.interface';
import { Book } from '../../core/dtos/book.dtos';
import { AUDIO_PLAYER_FACTORY, AudioPlayer, PlaylistTrack } from './track-playlist-player';

import { BloomArtDirective } from '../../ui/bloom-art/bloom-art.directive';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';

import { ButtonComponent } from '../../ui/button/button.component';
import { NOSTOS_MENU } from '../../ui/menu/menu.component';
import { PlaybackSpeedMenuComponent } from '../../ui/playback-speed-menu/playback-speed-menu.component';
import { RangeDirective } from '../../ui/range/range.directive';
import { PassageDraft, PassageMark } from '../passage/passage-mark';
import { formatAudioTime } from '../../core/dtos/note.dtos';
import { PASSAGE_CLIP_PLAY_EVENT } from '../../ui/passage-viewer/clip-player';
import { coverImageUrl } from '../../core/cover-url';

/** How often a playing audiobook saves its position (see the constructor). */
export const PLAYING_SAVE_INTERVAL_MS = 10_000;

@Component({
  selector: 'app-audio-reader',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    NostosIconComponent,
    BloomArtDirective,
    LoadingIndicatorComponent,
    ButtonComponent,
    PlaybackSpeedMenuComponent,
    ...NOSTOS_MENU,
    RangeDirective,
  ],
  templateUrl: './audio-reader.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './audio-reader.component.css',
})
export class AudioReader implements OnDestroy, IReader {
  bookId = input.required<string>();
  // The already-loaded book is passed in by the reader shell so this component
  // never issues a second GET /api/books/{id} (issue #7).
  book = input<Book | null>(null);
  /** The cover under its version, so the browser can keep it between visits. */
  readonly coverSrc = computed(() => coverImageUrl(this.book()));

  // The chapter line asks the shell for its Contents panel instead of carrying
  // a second chapter list inside the player (issue #904).
  contentsRequested = output<void>();

  private booksService = inject(BooksService);
  private assistantContext = inject(AssistantContextService);

  /**
   * Published to the assistant context registry (issue #261): the in-app audio
   * timestamp and its chapter. Additive only.
   */
  private unregisterAssistantContext: (() => void) | null = null;
  private pendingSourceTime: number | null = null;

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

  /**
   * The title of the chapter the current position falls in, or null before the
   * first chapter start and for a book without chapters. The one source for the
   * player's chapter line, the assistant context and Media Session (issue #904).
   */
  currentChapter = computed<string | null>(() => {
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
  });

  // Player State
  // One audio element plays the book: a multi-track book as its tracks in
  // order, a single-file one as a list of one. Seconds across the whole book.
  player: AudioPlayer | null = null;
  private readonly createPlayer = inject(AUDIO_PLAYER_FACTORY);
  isPlaying = signal(false);
  currentTime = signal(0);
  duration = signal(0);
  currentRate = signal(1);
  // True while the player is initializing (until its onload callback),
  // so the UI can show a visible loading state instead of a dead-looking player.
  loading = signal(true);
  loadError = signal<string | null>(null);
  // A dropped connection (issue #1058): the audio is being reloaded, or waits
  // for the connection to come back, before the dead-end error is shown.
  reconnecting = signal(false);

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
  // Speed is the shared playback-speed menu (issue #1200); the sleep timer is
  // a nostos-menu beside it. Both are CDK menus: Escape, outside clicks and
  // focus return are theirs, not this component's.
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
  readonly availableRates = [0.8, 1, 1.25, 1.5, 2];
  readonly rateStep = 0.05;
  readonly rateMin = 0.5;
  readonly rateMax = 3;
  private static readonly RATE_KEY = 'nostos.audio-rate';
  /** A reported position under this many seconds, while the reader knows better, is a lost one. */
  private static readonly LOST_POSITION_MAX = 1;
  /** Only a real place in the book is protected; the first seconds have nothing to lose. */
  private static readonly LOST_POSITION_MIN_KNOWN = 5;
  /** Waits before each automatic reload after the audio stopped loading mid-book. */
  static readonly RELOAD_DELAYS_MS = [2_000, 5_000, 15_000];

  // Recovery from a dropped connection (issue #1058).
  private hasLoaded = false;
  private reloadAttempts = 0;
  private reloadTimer: ReturnType<typeof setTimeout> | null = null;
  private resumeAt: number | null = null;
  private resumeAfterReload = false;
  private onOnline = this.handleOnline.bind(this);

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
        audioChapter: this.currentChapter(),
      }),
      { explicit: true },
    );

    // While audio plays, the position is saved at most every ten seconds.
    // Pause, seek, hiding the tab, leaving the reader and unloading the page
    // each save at once, so this only bounds what a crash or a dropped
    // connection can lose; every save is a database write (and, on Cloud, a
    // tenant lookup), so it is not done every two seconds.
    this.progressSubscription = this.progressSubject.pipe(
      sampleTime(PLAYING_SAVE_INTERVAL_MS),
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
    window.addEventListener('online', this.onOnline);

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

    // The lock screen follows the chapter (issue #904). `currentChapter` is
    // re-evaluated on every tick but only notifies when the title changes, so
    // this writes once per chapter, not once per second.
    effect(() => {
      const chapter = this.currentChapter();
      if (!this.mediaSessionActive || chapter === this.mediaSessionChapter) return;
      untracked(() => this.writeMediaSessionMetadata(chapter));
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
  initPlayer(id: string, recovering = false) {
    if (this.player) this.player.unload();

    if (!recovering) this.resetRecovery();
    this.loading.set(true);
    this.loadError.set(null);
    // Show the remembered speed immediately; applied to the player on load.
    this.currentRate.set(this.restoreSavedRate());

    // Read without tracking: this runs inside the effect keyed on the book id,
    // and a refreshed book object (a saved position, say) must not restart
    // playback.
    const tracks = untracked(() => this.book())?.tracks ?? [];
    const callbacks = {
      onload: () => {
        this.loading.set(false);
        this.duration.set(this.player?.duration() || 0);
        this.player?.rate(this.currentRate());
        const resumeAt = this.resumeAt;
        const resumePlaying = this.resumeAfterReload;
        this.hasLoaded = true;
        this.resetRecovery();
        if (resumeAt !== null) {
          // Back after a drop: the place the listener was at, not the saved one,
          // which can be up to a save interval older.
          this.player?.seek(resumeAt);
          this.currentTime.set(resumeAt);
          this.updateProgressState();
        } else {
          this.restoreProgress();
        }
        if (this.pendingSourceTime !== null) {
          const sourceTime = this.pendingSourceTime;
          this.pendingSourceTime = null;
          this.goToTime(Math.min(sourceTime, this.duration()));
        }
        this.updateMediaSessionMetadata();
        if (resumePlaying) this.play();
      },
      onloaderror: () => this.handleLoadError(),
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
    };

    // A single file's length is read from the file itself once it loads.
    const playlist: PlaylistTrack[] =
      tracks.length > 0
        ? tracks.map((track) => ({
            url: `/api/books/${id}/tracks/${track.number}`,
            duration: track.duration,
          }))
        : [{ url: `/api/books/${id}/file`, duration: 0 }];
    this.player = this.createPlayer(playlist, callbacks);
  }

  /**
   * The audio stopped loading (issue #1058). A stream that dies mid-book is
   * first requested again in place by the player itself (issue #1161); this
   * runs when that fails too, or the book never loaded. A dead stream fires no
   * pause, so the player is marked paused here. After the book has played once, the
   * failure is most likely the connection: the audio is reloaded a few times
   * with growing waits, and at once when the browser reports it is back online.
   * While offline nothing is tried until then. Only when that runs out, or the
   * book never loaded while online, does the dead-end error show.
   */
  private handleLoadError() {
    if (this.isPlaying()) this.resumeAfterReload = true;
    if (this.hasLoaded && this.resumeAt === null) this.resumeAt = this.currentTime();
    this.isPlaying.set(false);
    this.stopProgressTracking();
    this.updateMediaSessionPlaybackState('paused');
    this.loading.set(false);
    this.clearReloadTimer();

    const offline = navigator.onLine === false;
    const canRetry = this.hasLoaded && this.reloadAttempts < AudioReader.RELOAD_DELAYS_MS.length;
    if (offline || canRetry) {
      this.reconnecting.set(true);
      this.loadError.set(null);
      if (offline) return;
      const delay = AudioReader.RELOAD_DELAYS_MS[this.reloadAttempts++];
      this.reloadTimer = setTimeout(() => this.reloadAudio(), delay);
      return;
    }

    this.reconnecting.set(false);
    this.loadError.set('Unable to load audio.');
  }

  private handleOnline() {
    if (!this.reconnecting() && !this.loadError()) return;
    this.retryLoad();
  }

  /** The Retry action, and play while the audio is down: reload now, with a fresh set of attempts. */
  retryLoad() {
    this.reloadAttempts = 0;
    this.reloadAudio();
  }

  private reloadAudio() {
    this.clearReloadTimer();
    const id = this.bookId();
    if (!id) return;
    this.reconnecting.set(true);
    this.initPlayer(id, true);
  }

  private clearReloadTimer() {
    if (this.reloadTimer !== null) {
      clearTimeout(this.reloadTimer);
      this.reloadTimer = null;
    }
  }

  private resetRecovery() {
    this.clearReloadTimer();
    this.reloadAttempts = 0;
    this.resumeAt = null;
    this.resumeAfterReload = false;
    this.reconnecting.set(false);
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
    this.player?.playing() ? this.player.pause() : this.play();
  }

  /** Every play request goes through here, so a lost position is restored before sound starts. */
  private play() {
    if (!this.player) return;
    if (this.reconnecting() || this.loadError()) {
      // The element is dead: play means "try again, and keep playing once it's back".
      this.resumeAfterReload = true;
      this.retryLoad();
      return;
    }
    this.recoverLostPosition();
    this.player.play();
  }

  /**
   * The player's own position in book seconds, or null when it has none to give.
   */
  private readPlayerPosition(): number | null {
    const position = this.player?.seek();
    return typeof position === 'number' && Number.isFinite(position) && position >= 0 ? position : null;
  }

  /**
   * A phone can drop a paused media element while the tab is in the background
   * and hand it back at 0:00. Adopting that position — on return to the tab, on
   * the next tick, or on play from the lock screen — once overwrote a listener's
   * saved place 60% into a book. The reader's own position is the truth: a jump
   * to the start that nobody asked for is the player losing its place, so the
   * player is put back instead. A user's seek to 0 goes through goToTime, which
   * moves the reader's position first, so it is never mistaken for a loss.
   * Returns the position the reader should show.
   */
  private recoverLostPosition(): number | null {
    const actual = this.readPlayerPosition();
    const known = this.currentTime();
    const lost =
      (actual === null || actual < AudioReader.LOST_POSITION_MAX) &&
      known > AudioReader.LOST_POSITION_MIN_KNOWN &&
      // Past the end the player legitimately starts over from the beginning.
      known < this.duration() - 1;
    if (!lost) return actual;
    this.player?.seek(known);
    return known;
  }

  /**
   * Pauses, saves progress, and returns the real media position on the book's
   * whole timeline, for continuing in another edition. Null when nothing has
   * been played yet or the audio is not ready.
   */
  handoffPosition(): number | null {
    const player = this.player;
    if (!player || this.loading() || this.loadError()) {
      // The element is gone or still loading, but the reader knows its place.
      const known = this.currentTime();
      return Number.isFinite(known) && known > 0 ? known : null;
    }

    // Prefer the real media position to the UI's once-per-second snapshot,
    // unless a phone handed the paused element back at 0:00: then the
    // reader's own position is the truth, as it is for play.
    const position = this.recoverLostPosition();
    if (position === null || position <= 0) return null;

    player.pause();
    this.currentTime.set(position);
    this.updateProgressState();
    this.saveProgressImmediately();
    return position;
  }

  seek(event: any) {
    const time = parseFloat(event.target.value);
    this.goToTime(time);
  }

  skip(seconds: number) {
    if (!this.player) return;
    const current = this.readPlayerPosition() ?? this.currentTime();
    const newTime = Math.max(0, Math.min(current + seconds, this.duration()));
    this.goToTime(newTime);
  }

  goToSource(target: ReaderSourceTarget): void {
    if (target.type !== 'audio' || target.audioTime === undefined ||
        !Number.isFinite(target.audioTime) || target.audioTime < 0) return;
    if (!this.player || this.loading()) {
      this.pendingSourceTime = target.audioTime;
      return;
    }
    this.goToTime(Math.min(target.audioTime, this.duration()));
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
      // A jump is the listener's choice, so it is saved at once, 0:00 included
      // (the periodic save runs only every PLAYING_SAVE_INTERVAL_MS). Restoring
      // the saved place on open happens before initialisation and is not saved.
      if (this.isInitialized) this.savePosition(seconds);
    }
  }

  /** Saves `seconds` as the place, even 0:00: only for positions the listener chose. */
  private savePosition(seconds: number) {
    const duration = this.duration();
    this.booksService
      .updateProgress(this.bookId(), seconds.toString(), duration > 0 ? Math.floor((seconds / duration) * 100) : 0)
      .subscribe({ error: (err) => console.error('Failed to save progress:', err) });
  }

  // Speed Logic
  private clampRate(rate: number): number {
    const clamped = Math.min(this.rateMax, Math.max(this.rateMin, rate));
    return parseFloat(clamped.toFixed(2));
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

  selectSleepTimer(minutes: number | null) {
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

  startProgressTracking() {
    this.stopProgressTracking();
    this.progressTimer = setInterval(() => {
      const position = this.recoverLostPosition();
      if (position === null) return;
      this.currentTime.set(position);
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

  // --- Passages (issue #1019): a clip between two times ---

  /** A clip started playing in the passage viewer: the book makes way. */
  @HostListener('window:' + PASSAGE_CLIP_PLAY_EVENT)
  onPassageClipPlay(): void {
    if (this.player?.playing()) this.player.pause();
  }

  /** The time playing now, as one end of a clip. */
  passageMarkHere(): PassageMark | null {
    if (this.duration() <= 0) return null;
    const time = Math.round(this.currentTime() * 10) / 10;
    return {
      format: 'audio',
      start: String(time),
      end: String(time),
      excerpt: '',
      label: this.currentChapter() ?? formatAudioTime(time),
    };
  }

  /**
   * The clip between two marks, whichever came first. Null when they are the
   * same moment: a clip needs a length.
   */
  async buildPassage(a: PassageMark, b: PassageMark): Promise<PassageDraft | null> {
    if (a.format !== 'audio' || b.format !== 'audio') return null;
    const times = [a.start, a.end, b.start, b.end].map(Number).filter(Number.isFinite);
    if (times.length !== 4) return null;
    const start = Math.max(0, Math.min(...times));
    const end = Math.min(this.duration() || Infinity, Math.max(...times));
    if (end - start < 1) return null;
    return {
      anchorKind: 'audio_timestamp',
      start: String(start),
      end: String(end),
      title: this.chapterTitleAt(start) ?? `Clip at ${formatAudioTime(start)}`,
      html: null,
      text: null,
      words: 0,
      opening: formatAudioTime(start),
      closing: formatAudioTime(end),
      limit: this.duration() || undefined,
    };
  }

  /** The chapter a time falls in. */
  private chapterTitleAt(seconds: number): string | null {
    let best: { label: string; at: number } | null = null;
    const visit = (items: TocItem[]) => {
      for (const item of items) {
        const at = Number(item.target);
        if (Number.isFinite(at) && at <= seconds + 0.5 && (!best || at >= best.at)) best = { label: item.label, at };
        visit(item.children ?? []);
      }
    };
    visit(this.toc());
    return (best as { label: string } | null)?.label.trim() || null;
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

    const actualTime = this.recoverLostPosition();
    const actuallyPlaying = this.player.playing();

    if (actualTime !== null) this.currentTime.set(actualTime);
    this.isPlaying.set(actuallyPlaying);
    this.updateProgressState();

    if (actuallyPlaying) {
      this.startProgressTracking();
    }

    this.updateMediaSessionPositionState();
  }

  private saveProgressImmediately() {
    if (!this.player || !this.isInitialized) return;
    const seek = this.readPlayerPosition() ?? 0;
    if (seek > 0) {
      this.booksService.updateProgress(
        this.bookId(),
        seek.toString(),
        this.duration() > 0 ? Math.floor((seek / this.duration()) * 100) : 0,
      ).subscribe();
    }
  }

  // --- Media Session API: Sync with Android notification controls ---
  // False until the audio has loaded; the chapter last written to the lock screen.
  private mediaSessionActive = false;
  private mediaSessionChapter: string | null = null;

  /**
   * With a chapter, the lock screen reads chapter / author / book; without one it
   * keeps the book / author / series it always had.
   */
  private writeMediaSessionMetadata(chapter: string | null) {
    if (!('mediaSession' in navigator)) return;
    const book = this.book();
    if (!book) return;

    this.mediaSessionChapter = chapter;
    navigator.mediaSession.metadata = new MediaMetadata({
      title: chapter ?? book.title,
      artist: book.author || book.narrator || undefined,
      album: chapter ? book.title : book.series || undefined,
      artwork: book.coverUrl ? [{ src: coverImageUrl(book)!, sizes: '512x512', type: 'image/jpeg' }] : [],
    });
  }

  private updateMediaSessionMetadata() {
    if (!('mediaSession' in navigator)) return;
    if (!this.book()) return;

    this.writeMediaSessionMetadata(this.currentChapter());
    this.mediaSessionActive = true;

    // Register action handlers so notification controls sync back to our component
    navigator.mediaSession.setActionHandler('play', () => {
      this.play();
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
    window.removeEventListener('online', this.onOnline);
    this.clearReloadTimer();

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
