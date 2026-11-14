import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import type { ReaderProgress } from './reader.interface';
import { ReaderTimers } from './reader-timers';

/**
 * What the EPUB/PDF progress bar measures, reader-wide (#1269). Presentation
 * only: the saved position stays book-relative.
 */
export type ProgressScope = 'book' | 'chapter';

export const PROGRESS_SCOPE_KEY = 'nostos.reader-progress-scope';

/** How long "Chapter progress" / "Book progress" stays in the label slot after a flip. */
export const PROGRESS_SCOPE_FLASH_MS = 1200;

function readStoredProgressScope(): ProgressScope {
  try {
    return localStorage.getItem(PROGRESS_SCOPE_KEY) === 'chapter' ? 'chapter' : 'book';
  } catch {
    return 'book';
  }
}

/**
 * The pager's book/chapter choice and the brief label that confirms a flip.
 *
 * It was a dozen lines inside the shell: a signal, four computed labels, a
 * timer and the cleanup for it. Left there, the flash timer was one of the
 * handles `ngOnDestroy` had to remember (issue #1607). It is now a shell-scoped
 * service, and the timer belongs to {@link ReaderTimers}.
 */
@Injectable()
export class ReaderProgressScope {
  private readonly timers = inject(ReaderTimers);
  private readonly destroyRef = inject(DestroyRef);

  /** The reader's own progress report; connected once the shell has one. */
  private readonly state = signal<ReaderProgress | null>(null);

  /** The chosen scope, remembered for every book on this device. */
  readonly scope = signal<ProgressScope>(readStoredProgressScope());
  /** "Chapter progress" / "Book progress", briefly, after a flip. */
  readonly flash = signal<string | null>(null);
  private flashTimer: ReturnType<typeof setTimeout> | null = null;

  /** Chapter is offered only when the reader can place one. */
  readonly chapterAvailable = computed(() => this.state()?.chapterPercentage != null);

  /** Chapter only when chosen AND available; otherwise silently the book bar. */
  readonly effective = computed<ProgressScope>(() =>
    this.scope() === 'chapter' && this.chapterAvailable() ? 'chapter' : 'book',
  );

  readonly fillPercent = computed(() => {
    const state = this.state();
    if (!state) return 0;
    return this.effective() === 'chapter'
      ? (state.chapterPercentage ?? 0)
      : state.percentage;
  });

  readonly toggleLabel = computed(() =>
    this.effective() === 'chapter' ? 'Show book progress' : 'Show chapter progress',
  );

  readonly title = computed(() =>
    this.effective() === 'chapter'
      ? 'Chapter progress: tap for book progress'
      : 'Book progress: tap for chapter progress',
  );

  readonly label = computed(() => {
    const flash = this.flash();
    if (flash) return flash;
    const state = this.state();
    if (!state) return '';
    if (this.effective() === 'chapter') {
      return `${state.chapterPercentage}% of ${state.chapterLabel || 'chapter'}`;
    }
    return state.label ?? '';
  });

  readonly tooltip = computed(() => this.state()?.tooltip ?? '');

  constructor() {
    this.destroyRef.onDestroy(() => this.timers.clear(this.flashTimer));
  }

  /** The shell pushes the mounted reader's report in as the page turns. */
  update(state: ReaderProgress | null | undefined): void {
    this.state.set(state ?? null);
  }

  toggle(): void {
    if (!this.chapterAvailable()) return;
    const next: ProgressScope = this.effective() === 'chapter' ? 'book' : 'chapter';
    this.scope.set(next);
    try {
      localStorage.setItem(PROGRESS_SCOPE_KEY, next);
    } catch {
      // The choice still applies for this session.
    }
    this.timers.clear(this.flashTimer);
    this.flash.set(next === 'chapter' ? 'Chapter progress' : 'Book progress');
    this.flashTimer = this.timers.after(PROGRESS_SCOPE_FLASH_MS, () => {
      this.flashTimer = null;
      this.flash.set(null);
    });
  }
}
