import { Component, DestroyRef, inject } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';

import {
  READER_PROGRESS_DEBOUNCE_MS,
  ReaderProgressReporter,
  type ReaderProgressReporterOptions,
} from './reader-progress-reporter';

interface Report {
  page: number;
}

@Component({ selector: 'app-progress-reporter-host', standalone: true, template: '' })
class ProgressReporterHost {
  readonly destroyRef = inject(DestroyRef);
}

/**
 * The debounced reading-progress save both readers share (issue #1610): one
 * write a second after the last page turn, nothing while the opening display
 * is still settling, and no write outliving the reader.
 */
describe('ReaderProgressReporter', () => {
  let fixture: ComponentFixture<ProgressReporterHost>;
  let destroyRef: DestroyRef;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ imports: [ProgressReporterHost] });
    fixture = TestBed.createComponent(ProgressReporterHost);
    destroyRef = fixture.componentInstance.destroyRef;
  });

  afterEach(() => {
    fixture.destroy();
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  function reporter(
    write: (report: Report) => Observable<unknown>,
    overrides: Partial<ReaderProgressReporterOptions<Report>> = {},
  ): ReaderProgressReporter<Report> {
    return new ReaderProgressReporter<Report>({
      destroyRef,
      isReady: () => true,
      isSamePosition: (previous, current) => previous.page === current.page,
      write,
      ...overrides,
    });
  }

  it('writes the last place once, a second after the reader stopped moving', () => {
    const write = vi.fn(() => of(null));
    const progress = reporter(write);

    progress.report({ page: 1 });
    vi.advanceTimersByTime(300);
    progress.report({ page: 2 });
    vi.advanceTimersByTime(300);
    progress.report({ page: 3 });

    expect(write).not.toHaveBeenCalled();
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS - 1);
    expect(write).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);

    expect(write).toHaveBeenCalledTimes(1);
    expect(write).toHaveBeenCalledWith({ page: 3 });
  });

  it('drops a place reported while writes are closed, rather than saving it later', () => {
    const write = vi.fn(() => of(null));
    let ready = false;
    const progress = reporter(write, { isReady: () => ready });

    progress.report({ page: 1 });
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS * 2);
    expect(write).not.toHaveBeenCalled();

    ready = true;
    progress.report({ page: 2 });
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS);
    expect(write).toHaveBeenCalledTimes(1);
    expect(write).toHaveBeenCalledWith({ page: 2 });
  });

  it('does not write the same place twice', () => {
    const write = vi.fn(() => of(null));
    const progress = reporter(write);

    progress.report({ page: 4 });
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS);
    progress.report({ page: 4 });
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS);

    expect(write).toHaveBeenCalledTimes(1);
  });

  it('cancels the write in flight when a newer place arrives (EPUB)', () => {
    let cancelled = false;
    const write = vi.fn((report: Report) =>
      report.page === 1
        ? new Observable<unknown>(() => () => {
            cancelled = true;
          })
        : of(null),
    );
    const progress = reporter(write, { concurrency: 'switch' });

    progress.report({ page: 1 });
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS);
    expect(cancelled).toBe(false);

    progress.report({ page: 2 });
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS);

    expect(cancelled).toBe(true);
    expect(write).toHaveBeenLastCalledWith({ page: 2 });
  });

  it('lets a write in flight finish when a newer place arrives (PDF)', () => {
    let cancelled = false;
    const write = vi.fn((report: Report) =>
      report.page === 1
        ? new Observable<unknown>(() => () => {
            cancelled = true;
          })
        : of(null),
    );
    const progress = reporter(write, { concurrency: 'merge' });

    progress.report({ page: 1 });
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS);
    progress.report({ page: 2 });
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS);

    expect(cancelled).toBe(false);
    expect(write.mock.calls.map(([report]) => report.page)).toEqual([1, 2]);
  });

  it('keeps writing after a failed save, and reports the failure', () => {
    const write = vi
      .fn<(report: Report) => Observable<unknown>>()
      .mockReturnValueOnce(throwError(() => new Error('offline')))
      .mockReturnValue(of(null));
    const onWriteError = vi.fn();
    const progress = reporter(write, { onWriteError });

    progress.report({ page: 1 });
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS);
    expect(onWriteError).toHaveBeenCalledTimes(1);

    progress.report({ page: 2 });
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS);
    expect(write).toHaveBeenCalledTimes(2);
  });

  it('stops a pending write when disposed, and writes nothing after', () => {
    const write = vi.fn(() => of(null));
    const progress = reporter(write);

    progress.report({ page: 1 });
    progress.dispose();
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS * 2);

    expect(write).not.toHaveBeenCalled();
  });

  it('stops a pending write when the reader is destroyed', () => {
    const write = vi.fn(() => of(null));
    const progress = reporter(write);

    progress.report({ page: 1 });
    fixture.destroy();
    vi.advanceTimersByTime(READER_PROGRESS_DEBOUNCE_MS * 2);

    expect(write).not.toHaveBeenCalled();
  });
});
