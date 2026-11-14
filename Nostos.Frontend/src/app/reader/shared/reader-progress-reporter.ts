import { DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, Subject, Subscription } from 'rxjs';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  filter,
  mergeMap,
  switchMap,
} from 'rxjs/operators';

/**
 * How long the reader waits after the last page turn before saving the
 * position. One second is the cadence both readers have always used: long
 * enough that a flick through ten pages writes once, short enough that closing
 * the book a moment after reading still keeps the place.
 */
export const READER_PROGRESS_DEBOUNCE_MS = 1000;

export interface ReaderProgressReporterOptions<T> {
  /** Ends the pipeline with the reader; no write can outlive it. */
  destroyRef: DestroyRef;
  /**
   * Whether a position may be written yet. Both readers keep this closed until
   * their opening display (and a saved-position restore) has settled, so the
   * first page laid out can never overwrite the reader's real place.
   */
  isReady: () => boolean;
  /**
   * Whether two reports name the same place. A report equal to the previous one
   * is not sent again, so a re-layout that lands on the same page writes nothing.
   */
  isSamePosition: (previous: T, current: T) => boolean;
  /**
   * The API call for one report. It carries the reader's own guards: a report
   * for a book that is no longer open must resolve to `EMPTY`, and a request
   * still in flight when the book changes is cancelled by the reader, not here.
   */
  write: (report: T) => Observable<unknown>;
  /**
   * `switch` (EPUB) cancels a write still in flight when the next position
   * arrives; `merge` (PDF) lets it finish and writes the newer place after it.
   * Both readers behaved this way before the pipeline was shared, and the
   * difference was never deliberate: it is kept so no save changes hands.
   */
  concurrency?: 'switch' | 'merge';
  /** A failed write is logged and the pipeline stays open for the next page. */
  onWriteError?: (error: unknown) => void;
}

/**
 * The debounced reading-progress save both page readers run: report a place as
 * often as the reader likes, and only the last one is written, once, a second
 * after the reader stops.
 *
 * The readers disagreed in the details (EPUB compared the whole report and
 * could cancel its own write, PDF compared the location alone and never
 * cancelled) while agreeing on the shape: a subject, a write barrier, a
 * one-second debounce, a duplicate filter and teardown with the reader. Those
 * parts live here; the barriers and the API calls stay with the reader that
 * knows what its position means (issue #1610).
 */
export class ReaderProgressReporter<T> {
  private readonly reports = new Subject<T>();
  private readonly subscription: Subscription;

  constructor(private readonly options: ReaderProgressReporterOptions<T>) {
    const send = (report: T) => this.send(report);
    const writeOne = this.options.concurrency === 'merge' ? mergeMap(send) : switchMap(send);
    this.subscription = this.reports
      .pipe(
        filter(() => this.options.isReady()),
        debounceTime(READER_PROGRESS_DEBOUNCE_MS),
        distinctUntilChanged(this.options.isSamePosition),
        writeOne,
        takeUntilDestroyed(this.options.destroyRef),
      )
      .subscribe();
  }

  /** A place the reader has moved to. Safe to call on every page turn. */
  report(report: T): void {
    this.reports.next(report);
  }

  /** Stop writing; the reader is going away. */
  dispose(): void {
    this.subscription.unsubscribe();
    this.reports.complete();
  }

  private send(report: T): Observable<unknown> {
    return this.options.write(report).pipe(
      catchError((error) => {
        // A failed save must not close the stream: the next page still writes.
        if (this.options.onWriteError) this.options.onWriteError(error);
        return EMPTY;
      }),
    );
  }
}
