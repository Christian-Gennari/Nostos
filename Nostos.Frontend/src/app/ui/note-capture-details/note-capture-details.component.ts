import { Component, DestroyRef, effect, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Note, NoteSearchHit } from '../../core/dtos/note.dtos';
import { NotesService } from '../../core/services/notes.service';
import { ButtonComponent } from '../button/button.component';
import { A11yModule } from '@angular/cdk/a11y';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { DialogActionsComponent } from '../dialog-actions/dialog-actions.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';

/** Original wording is canonical note data, available without an assistant session. */
@Component({
  selector: 'app-note-capture-details',
  imports: [ButtonComponent, A11yModule, ModalShell, DialogActionsComponent, IconButtonComponent],
  templateUrl: './note-capture-details.component.html',
  styleUrl: './note-capture-details.component.css',
})
export class NoteCaptureDetailsComponent {
  readonly note = input.required<NoteSearchHit>();
  readonly restored = output<Note>();
  readonly original = signal<string | null>(null);
  readonly originalOpen = signal(false);
  readonly loading = signal(false);
  readonly restoring = signal(false);
  readonly error = signal<string | null>(null);
  readonly restoredHere = signal(false);
  private readonly notes = inject(NotesService);
  private readonly destroyRef = inject(DestroyRef);
  private generation = 0;
  private noteId: string | null = null;

  constructor() {
    effect(() => {
      // A response from a previous note must never populate the next inspector.
      const id = this.note().id;
      if (id === this.noteId) return;
      this.noteId = id;
      this.generation++;
      this.original.set(null);
      this.originalOpen.set(false);
      this.loading.set(false);
      this.restoring.set(false);
      this.error.set(null);
      this.restoredHere.set(false);
    });
  }

  modeLabel(): string {
    switch (this.note().processingMode) {
      case 'light_polish': return 'Light polish';
      case 'clarify': return 'Clarify';
      default: return 'Verbatim';
    }
  }

  closeOriginal(): void {
    if (!this.restoring()) this.originalOpen.set(false);
  }

  openOriginal(): void {
    this.originalOpen.set(true);
    if (this.loading() || this.original() !== null) return;
    const generation = this.generation;
    this.loading.set(true);
    this.error.set(null);
    this.notes.getOriginal(this.note().id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (raw) => {
        if (generation !== this.generation) return;
        this.loading.set(false);
        this.original.set(raw.rawContent);
        if (!raw.rawContent) this.error.set('No original wording is available for this note.');
      },
      error: () => {
        if (generation !== this.generation) return;
        this.loading.set(false);
        this.error.set('Could not load the original wording. Close and reopen it to try again.');
      },
    });
  }

  restoreOriginal(): void {
    if (!this.original() || this.restoring()) return;
    const generation = this.generation;
    this.restoring.set(true);
    this.error.set(null);
    this.notes.restoreOriginal(this.note().id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (note) => {
        if (generation === this.generation) {
          this.restoring.set(false);
          this.restoredHere.set(true);
        }
        // Reconcile the committed note by id even if the user moved on. The
        // parent never substitutes this result for a different inspector note.
        this.restored.emit(note);
      },
      error: () => {
        if (generation !== this.generation) return;
        this.restoring.set(false);
        this.error.set('Could not confirm the restore. Reload this note to check its saved wording.');
      },
    });
  }
}
