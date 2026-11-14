/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

import { DestroyRef, Injectable, WritableSignal, computed, inject, signal } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { Subscription, firstValueFrom } from 'rxjs';

import { formatByteProgress } from '../core/format';
import { Book as BookModel, BooksService } from '../core/services/books.service';
import {
  BookUploadPhase,
  BookUploadService,
  rejectedFileMessage,
} from '../core/services/book-upload.service';
import { describeUploadFailure } from '../core/uploads/upload-error-copy';

/**
 * The shell facts an upload run needs but does not own. The modal binds these
 * once, before any run starts.
 */
export interface AddBookUploadHooks {
  /** The shell's single mutation guard, shared with create/update. */
  isSubmitting: WritableSignal<boolean>;
  selectedFile(): File | null;
  /** Two or more audio files chosen together: the tracks of one audiobook. */
  trackFiles(): File[];
  /** The server refused the file: drop the chosen file so another can be picked. */
  onFileRejected(): void;
  /** The file finished landing; the shell may upload the cover and finish. */
  onFileUploaded(book: BookModel): void;
  /** The server refused the file and the row the shell had just created was removed. */
  onDiscardedBook(): void;
  /** Stop was carried out; the shell closes and asks for a library refresh. */
  onStopped(): void;
}

/**
 * The Add book file run, kept out of the modal shell (#1639).
 *
 * It owns everything that happens between "a book row exists" and "the file
 * landed": which upload path runs, the visible progress, the stop question's
 * work, the rejected-file recovery, and the `beforeunload` guard. Two things
 * stay with the shell because they are not the run's: the chosen files (the
 * form owns them) and the close/discard hooks that change the library.
 *
 * Every subscription and timer is torn down with the modal, and every late
 * callback is checked against a generation counter so a stopped or reopened
 * run can never paint onto the fresh form (#1275).
 */
@Injectable()
export class AddBookUploadSession {
  private readonly books = inject(BooksService);
  /** Resumable uploads outlive this modal, so they are owned by a root service. */
  private readonly bookUploads = inject(BookUploadService);
  private readonly destroyRef = inject(DestroyRef);

  readonly progress = signal<number | null>(null);
  /** Which visible stage the resumable path is in; null on the legacy path. */
  readonly phase = signal<BookUploadPhase | null>(null);
  readonly error = signal<string | null>(null);
  /** Existing row kept after a failed file upload so retry never creates another book. */
  readonly book = signal<BookModel | null>(null);
  /** True while the chosen Stop is being carried out (pause in flight). */
  readonly stopping = signal(false);

  /** Bytes behind the percentage, so the upload line can say how much is left. */
  private readonly byteProgress = signal<{ loaded: number; total: number } | null>(null);

  /**
   * The focused upload view: the file's own run, whether it is moving or has
   * failed and can be retried. Never the ordinary form (#1275).
   */
  readonly viewOpen = computed(
    () => this.progress() !== null || (this.book() !== null && this.error() !== null),
  );

  /** Distinguishes this run from an older one whose callbacks arrive late. */
  private generation = 0;
  private activeBookId: string | null = null;
  private stopRequested = false;
  private legacySub: Subscription | null = null;
  private hooks: AddBookUploadHooks | null = null;
  private beforeUnloadAttached = false;
  private destroyed = false;
  private startedAt: number | null = null;
  private finishTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // A closed or destroyed modal must not leave work running.
    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.detachBeforeUnload();
      this.legacySub?.unsubscribe();
      this.legacySub = null;
      this.clearFinishTimer();
    });
  }

  /** Bound once by the modal that owns this session. */
  bind(hooks: AddBookUploadHooks): void {
    this.hooks = hooks;
  }

  /** True when the host advertised the resumable contract for this file run. */
  supportsResumable(): boolean {
    return this.bookUploads.supportsResumable();
  }

  statusText(): string {
    const percent = this.progress() ?? 0;
    const bytes = this.byteProgress();
    const moved = bytes ? formatByteProgress(bytes.loaded, bytes.total) : '';
    switch (this.phase()) {
      case 'preparing':
        return `Preparing file… ${percent}%`;
      case 'finishing':
        return 'Finishing upload…';
      default:
        return `Uploading… ${percent}%${moved ? ` · ${moved}` : ''}`;
    }
  }

  /** The file identity line: the one name, or the count when tracks are several. */
  fileName(): string {
    const tracks = this.hooks?.trackFiles() ?? [];
    return tracks.length > 1
      ? `${tracks.length} audio files`
      : (this.hooks?.selectedFile()?.name ?? '');
  }

  /** Sends the chosen file to the book row that already exists. */
  start(book: BookModel): void {
    const hooks = this.hooks;
    const file = hooks?.selectedFile();
    if (!hooks || !file) {
      hooks?.isSubmitting.set(false);
      this.error.set('Choose a book file before retrying the upload.');
      return;
    }

    const generation = ++this.generation;
    this.activeBookId = book.id;
    this.stopRequested = false;
    this.book.set(book);
    this.error.set(null);
    hooks.isSubmitting.set(true);
    this.startedAt = performance.now();
    this.progress.set(0);
    const tracks = hooks.trackFiles();
    const several = tracks.length > 1;
    const totalBytes = several ? tracks.reduce((sum, t) => sum + t.size, 0) : file.size;
    this.byteProgress.set({ loaded: 0, total: totalBytes });
    this.attachBeforeUnload();

    // The resumable contract carries one file per session, so the parts of
    // one audiobook always go in a single request.
    if (!several && this.bookUploads.supportsResumable()) {
      this.uploadResumable(book, file, generation);
      return;
    }

    const upload$ = several
      ? this.books.uploadTracks(book.id, tracks)
      : this.books.uploadFile(book.id, file);
    this.legacySub = upload$.subscribe({
      next: (event) => {
        if (this.isStale(generation)) return;
        if (event.type === HttpEventType.UploadProgress) {
          const percent = Math.round((event.loaded / (event.total ?? 1)) * 100);
          this.progress.set(percent);
          this.byteProgress.set({
            loaded: event.loaded,
            total: event.total ?? totalBytes,
          });
        }
        if (event.type === HttpEventType.Response) {
          this.finish(book);
        }
      },
      error: (error: unknown) => {
        if (this.isStale(generation)) return;
        this.clearProgress();
        this.hooks?.isSubmitting.set(false);
        this.detachBeforeUnload();
        if (this.discardRejectedBook(book, error)) return;
        this.error.set(describeUploadFailure(error, { bookSaved: true, resumable: false }));
      },
    });
  }

  /** Sends the same file to the same row again; never creates a second book. */
  retry(): void {
    const book = this.book();
    if (!book || this.hooks?.isSubmitting()) return;
    this.start(book);
  }

  /**
   * Stop upload: abort in-flight requests and pause the resumable session
   * through the root service, so the saved row and the same file survive this
   * session. Closing then leaves the library refreshed.
   */
  async stop(): Promise<void> {
    if (this.stopping()) return;
    this.stopping.set(true);
    this.stopRequested = true;
    this.legacySub?.unsubscribe();
    this.legacySub = null;

    const generation = this.generation;
    const bookId = this.activeBookId;
    try {
      if (bookId) await this.bookUploads.stop(bookId);
    } finally {
      if (generation !== this.generation) return;
      this.reset();
      this.hooks?.isSubmitting.set(false);
      this.hooks?.onStopped();
    }
  }

  /** Clears every trace of a file run so a reopened Add book starts clean. */
  reset(): void {
    this.generation += 1;
    this.activeBookId = null;
    this.stopRequested = false;
    this.legacySub?.unsubscribe();
    this.legacySub = null;
    this.detachBeforeUnload();
    this.clearFinishTimer();
    this.stopping.set(false);
    this.clearProgress();
    this.error.set(null);
    this.book.set(null);
    this.startedAt = null;
  }

  private uploadResumable(book: BookModel, file: File, generation: number): void {
    this.phase.set('preparing');
    this.progress.set(0);
    void this.bookUploads
      .upload(book.id, file, {
        onProgress: (progress) => {
          if (this.isStale(generation)) return;
          this.phase.set(progress.phase);
          this.progress.set(progress.percent);
          this.byteProgress.set({
            loaded: progress.movedBytes,
            total: progress.totalBytes,
          });
        },
      })
      .then(() => {
        if (this.isStale(generation)) return;
        this.finish(book);
      })
      .catch((error: unknown) => {
        if (this.isStale(generation)) return;
        this.clearProgress();
        this.hooks?.isSubmitting.set(false);
        this.detachBeforeUnload();
        if (this.discardRejectedBook(book, error)) return;
        this.error.set(describeUploadFailure(error, { bookSaved: true }));
      });
  }

  /**
   * The server read the file and refused it (empty, damaged, wrong kind). A
   * book this form just made would be left without a file, so remove it and
   * send the reader back to the form to choose another file. A book that
   * already had a file is kept as it was.
   */
  private discardRejectedBook(book: BookModel, error: unknown): boolean {
    const message = rejectedFileMessage(error);
    if (!message) return false;
    if (!book.hasFile) {
      // Fire and forget: the row this form just made must go even if the sheet
      // is closed on the failure, so it is a promise rather than a component
      // subscription.
      void firstValueFrom(this.books.delete(book.id))
        .then(() => this.hooks?.onDiscardedBook())
        .catch(() => undefined);
    }
    this.book.set(null);
    this.hooks?.onFileRejected();
    this.error.set(`${message} Choose a different file.`);
    return true;
  }

  private finish(book: BookModel): void {
    const elapsed = performance.now() - (this.startedAt ?? 0);
    this.book.set(null);
    this.error.set(null);
    this.detachBeforeUnload();
    this.clearFinishTimer();
    // Keep the upload view up until finishAdd resets it: clearing the progress
    // first would flash the form back while a cover uploads.
    this.finishTimer = setTimeout(
      () => {
        this.finishTimer = null;
        if (this.destroyed) return;
        this.hooks?.onFileUploaded(book);
      },
      Math.max(0, 1200 - elapsed),
    );
  }

  /** True once this run's callbacks may no longer paint the modal. */
  private isStale(generation: number): boolean {
    return this.destroyed || this.stopRequested || generation !== this.generation;
  }

  private clearProgress(): void {
    this.progress.set(null);
    this.phase.set(null);
    this.byteProgress.set(null);
  }

  private clearFinishTimer(): void {
    if (this.finishTimer !== null) clearTimeout(this.finishTimer);
    this.finishTimer = null;
  }

  /** A refresh or tab close loses a File the browser cannot hand back. */
  private readonly onBeforeUnload = (event: BeforeUnloadEvent): void => {
    if (!this.isRunning()) return;
    event.preventDefault();
    event.returnValue = '';
  };

  /** True while a book row is held and the shell's guard is still up. */
  private isRunning(): boolean {
    return this.book() !== null && this.hooks?.isSubmitting() === true;
  }

  private attachBeforeUnload(): void {
    if (this.beforeUnloadAttached) return;
    window.addEventListener('beforeunload', this.onBeforeUnload);
    this.beforeUnloadAttached = true;
  }

  private detachBeforeUnload(): void {
    if (!this.beforeUnloadAttached) return;
    window.removeEventListener('beforeunload', this.onBeforeUnload);
    this.beforeUnloadAttached = false;
  }
}
