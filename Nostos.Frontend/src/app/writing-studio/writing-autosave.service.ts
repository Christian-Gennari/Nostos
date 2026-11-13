import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { WritingContentDto } from '../core/dtos/writing.dtos';
import { ToastService } from '../core/services/toast.service';
import { WritingsService } from '../core/services/writings.service';

export interface SaveConflictState {
  theirs: WritingContentDto;
  title: string;
  content: string;
}

export interface AutosaveHostAdapter {
  getActiveItem: () => WritingContentDto | null;
  setActiveItem: (item: WritingContentDto) => void;
  getTitle: () => string;
  setTitle: (title: string) => void;
  getContent: () => string;
  setContent: (content: string) => void;
  flushPendingEditorChange: () => void;
  onRenamedDuringSave: (id: string, newTitle: string) => void;
}

const AUTOSAVE_DEBOUNCE_MS = 2000;

/**
 * Serialized autosave queue and 409 optimistic-concurrency conflict coordinator
 * for the open Studio writing. Supersedes stale queued UI updates via
 * generation tokens while preserving request ordering on the wire.
 */
@Injectable()
export class WritingAutosaveService {
  private readonly writingsService = inject(WritingsService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly saveStatus = signal<'Saved' | 'Saving…' | 'Unsaved'>('Saved');
  readonly saveConflict = signal<SaveConflictState | null>(null);

  private saveGeneration = 0;
  private saveQueue: Promise<void> = Promise.resolve();
  private pendingSaveCount = 0;
  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.cancelDebounce();
    });
  }

  get pendingCount(): number {
    return this.pendingSaveCount;
  }

  cancelDebounce(): void {
    if (this.debounceTimer !== null) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = null;
    }
  }

  scheduleDebouncedSave(
    item: WritingContentDto,
    title: string,
    content: string,
    adapter: AutosaveHostAdapter,
    delayMs = AUTOSAVE_DEBOUNCE_MS,
  ): void {
    this.cancelDebounce();
    if (this.destroyed) return;

    if (content === item.content && title === item.name) {
      this.saveStatus.set('Saved');
      return;
    }

    this.saveStatus.set('Unsaved');
    const generation = ++this.saveGeneration;

    this.debounceTimer = setTimeout(() => {
      this.debounceTimer = null;
      if (this.destroyed) return;
      void this.queueSave(item, title, content, generation, adapter).catch(() => {
        // Autosave failure is represented by saveStatus. Explicit source
        // navigation handles the same failure with user feedback.
      });
    }, delayMs);
  }

  async flushBeforeNavigation(
    item: WritingContentDto,
    adapter: AutosaveHostAdapter,
  ): Promise<boolean> {
    this.cancelDebounce();

    // Usually one pass. Re-check after each awaited write because the editor
    // remains live: text typed while the request is in flight must be included
    // before teardown can cancel the ordinary debounce.
    for (let attempt = 0; attempt < 4; attempt++) {
      const current = adapter.getActiveItem();
      if (!current || current.id !== item.id) return false;

      // Typing is reported once the writer pauses; what was typed a moment ago is saved too.
      adapter.flushPendingEditorChange();
      const title = adapter.getTitle();
      const content = adapter.getContent();
      const dirty = title !== current.name || content !== current.content;

      // If no delayed/in-flight autosave exists and the document is already
      // persisted, opening a source should not generate a redundant write.
      if (!dirty && this.pendingSaveCount === 0) return true;

      const generation = ++this.saveGeneration;
      try {
        await this.queueSave(current, title, content, generation, adapter);
      } catch {
        if (generation === this.saveGeneration) this.saveStatus.set('Unsaved');
        this.toast.error('Could not save this document. Your draft is still open.');
        return false;
      }

      const saved = adapter.getActiveItem();
      if (
        saved?.id === item.id &&
        adapter.getTitle() === saved.name &&
        adapter.getContent() === saved.content &&
        this.pendingSaveCount === 0
      ) {
        return true;
      }
    }

    // Avoid ever choosing navigation over data safety if the document changes
    // continuously during the flush window.
    this.saveStatus.set('Unsaved');
    this.toast.error('The document changed while opening the source. Try again.');
    return false;
  }

  keepMineAfterConflict(adapter: AutosaveHostAdapter): void {
    const conflict = this.saveConflict();
    const item = adapter.getActiveItem();
    if (!conflict || !item) return;
    this.saveConflict.set(null);
    const generation = ++this.saveGeneration;
    // The text on the page may have moved on while the question was open.
    void this.queueSave(item, adapter.getTitle(), adapter.getContent(), generation, adapter, true).catch(
      () => this.toast.error('Could not save this document. Your draft is still open.'),
    );
  }

  loadNewerAfterConflict(adapter: AutosaveHostAdapter): void {
    const conflict = this.saveConflict();
    if (!conflict) return;
    const mine = adapter.getContent();
    const differs = mine !== conflict.theirs.content;
    this.saveConflict.set(null);
    this.saveGeneration++;
    adapter.setActiveItem(conflict.theirs);
    adapter.setTitle(conflict.theirs.name);
    adapter.setContent(conflict.theirs.content);
    this.saveStatus.set('Saved');
    if (differs) {
      void navigator.clipboard?.writeText(mine).then(
        () => this.toast.info('Your version is on the clipboard'),
        () => undefined,
      );
    }
  }

  queueSave(
    item: WritingContentDto,
    title: string,
    content: string,
    generation: number,
    adapter: AutosaveHostAdapter,
    force = false,
  ): Promise<void> {
    this.pendingSaveCount++;

    const run = async () => {
      if (this.destroyed) return;
      // A newer debounce/flush superseded this request before it began.
      if (generation !== this.saveGeneration) return;

      // Nothing is saved over a newer copy until the writer has chosen.
      if (this.saveConflict()) {
        this.saveStatus.set('Unsaved');
        throw new Error('save-conflict');
      }

      this.saveStatus.set('Saving…');
      try {
        // The version this tab last loaded or saved: an earlier save in the
        // queue may have moved it on since `item` was captured.
        const current = adapter.getActiveItem();
        const baseUpdatedAt = (current?.id === item.id ? current : item).updatedAt;
        const updated = await firstValueFrom(
          this.writingsService
            .update(
              item.id,
              force ? { name: title, content } : { name: title, content, baseUpdatedAt },
            )
            .pipe(takeUntilDestroyed(this.destroyRef)),
        );

        if (this.destroyed) return;
        // An explicit flush may have been queued behind this request. Let that
        // newer write own the UI and final server state.
        if (generation !== this.saveGeneration) return;
        if (adapter.getActiveItem()?.id !== item.id) return;

        adapter.setActiveItem(updated);
        this.saveStatus.set('Saved');
        if (title !== item.name) {
          adapter.onRenamedDuringSave(item.id, title);
        }
      } catch (error) {
        if (!this.destroyed && generation === this.saveGeneration) {
          this.saveStatus.set('Unsaved');
        }
        if (
          !this.destroyed &&
          error instanceof HttpErrorResponse &&
          error.status === 409 &&
          error.error?.id === item.id
        ) {
          // Saved from another tab or device since this copy was loaded.
          this.saveConflict.set({ theirs: error.error as WritingContentDto, title, content });
        }
        throw error;
      }
    };

    const queued = this.saveQueue.catch(() => undefined).then(run);
    this.saveQueue = queued.catch(() => undefined);
    const settlePending = () => {
      this.pendingSaveCount = Math.max(0, this.pendingSaveCount - 1);
    };
    void queued.then(settlePending, settlePending);
    return queued;
  }
}
