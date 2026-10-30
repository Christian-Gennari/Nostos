import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import { NotesService } from '../core/services/notes.service';
import { ButtonComponent } from '../ui/button/button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';
import { NoteItemComponent } from '../ui/note-item/note-item.component';
import { BrainNoteActionsService } from './brain-note-actions.service';
import { BrainNoteDialogsComponent } from './brain-note-dialogs.component';
import { BrainStateService } from './brain-state.service';
import { BrainTopicSuggestionsService } from './brain-topic-suggestions.service';

/**
 * One note on its own: where a link to a note lands (the command palette,
 * Ask Nostos). The same card and actions as every list.
 */
@Component({
  selector: 'app-brain-note-page',
  standalone: true,
  imports: [
    RouterLink,
    ButtonComponent,
    NostosIconComponent,
    LoadingIndicatorComponent,
    EmptyStateComponent,
    NoteItemComponent,
    BrainNoteDialogsComponent,
  ],
  providers: [BrainNoteActionsService, BrainTopicSuggestionsService],
  templateUrl: './brain-note.page.html',
  styleUrls: ['./brain-note.page.css', './brain-touch.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainNotePage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly notesService = inject(NotesService);

  readonly state = inject(BrainStateService);
  readonly noteActions = inject(BrainNoteActionsService);
  readonly suggestions = inject(BrainTopicSuggestionsService);

  private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
  readonly noteId = computed(() => this.params().get('noteId'));
  readonly note = signal<NoteSearchHit | null>(null);
  readonly failed = signal(false);

  constructor() {
    this.state.ensureLoaded();
    this.noteActions.attach({
      replace: (note) => this.note.set(note),
      remove: () => void this.router.navigate(['/brain']),
    });
    effect(() => {
      const id = this.noteId();
      // A topic rename rewrites the note's references: read it again.
      this.state.topicsRewritten();
      untracked(() => this.load(id));
    });
  }

  /** "Try again" after the note failed to load. */
  retry(): void {
    this.load(this.noteId());
  }

  private load(id: string | null): void {
    this.note.set(null);
    this.failed.set(false);
    if (!id) return;
    this.notesService.get(id).subscribe({
      next: (note) => {
        if (this.noteId() === id) this.note.set(note);
      },
      error: () => {
        if (this.noteId() === id) this.failed.set(true);
      },
    });
  }

  moreFromBook(note: NoteSearchHit): void {
    void this.router.navigate(['/brain'], { queryParams: { book: note.workId ?? note.bookId } });
  }

  openDrawer(): void {
    this.state.drawerOpen.set(true);
  }
}
