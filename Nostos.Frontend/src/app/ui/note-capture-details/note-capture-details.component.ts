import { Component, DestroyRef, effect, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Note, NoteSearchHit } from '../../core/dtos/note.dtos';
import { NotesService } from '../../core/services/notes.service';
import { ButtonComponent } from '../button/button.component';

/** Original wording is canonical note data, available without an assistant session. */
@Component({
  selector: 'app-note-capture-details',
  imports: [ButtonComponent],
  templateUrl: './note-capture-details.component.html',
  styleUrl: './note-capture-details.component.css',
})
export class NoteCaptureDetailsComponent {
  readonly note = input.required<NoteSearchHit>();
  readonly restored = output<Note>();
  readonly original = signal<string | null>(null);
  readonly expanded = signal(false);
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
      this.expanded.set(false);
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

  toggleOriginal(): void {
    if (this.loading() || this.restoring()) return;
    if (this.expanded()) { this.expanded.set(false); return; }
    this.expanded.set(true);
    if (this.original() !== null) return;
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
