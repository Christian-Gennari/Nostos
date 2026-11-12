import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import { NotesService } from '../core/services/notes.service';
import { ButtonComponent } from '../ui/button/button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';
import { BRAIN_WIDE_QUERY, isBrainWideViewport } from '../ui/breakpoints';
import { BrainNoteActionsService } from './brain-note-actions.service';
import { BrainNoteDetailComponent } from './brain-note-detail.component';
import { BrainNoteDialogsComponent } from './brain-note-dialogs.component';
import { BrainStateService } from './brain-state.service';
import { BrainTopicSuggestionsService } from './brain-topic-suggestions.service';

/**
 * One note on its own: where a link to a note lands (the command palette,
 * Ask Nostos). On a phone it is the full page; on a wide screen the note
 * belongs in the Brain's detail pane, so the same address opens the list and
 * selects it there (issue #1531).
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
    BrainNoteDetailComponent,
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

  private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
  readonly noteId = computed(() => this.params().get('noteId'));
  readonly note = signal<NoteSearchHit | null>(null);
  readonly failed = signal(false);

  /** The wide Brain shows this note in its pane instead of on a page of its own. */
  private readonly wideMedia = typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia(BRAIN_WIDE_QUERY)
    : null;
  readonly wide = signal(isBrainWideViewport());
  private redirectedFor: string | null = null;

  constructor() {
    this.state.ensureLoaded();
    this.noteActions.attach({
      replace: (note) => this.note.set(note),
      remove: () => void this.router.navigate(['/brain']),
    });

    if (this.wideMedia) {
      const onChange = (event: MediaQueryListEvent) => this.wide.set(event.matches);
      this.wideMedia.addEventListener('change', onChange);
      inject(DestroyRef).onDestroy(() => this.wideMedia?.removeEventListener('change', onChange));
    }

    effect(() => {
      const id = this.noteId();
      const wide = this.wide();
      // A topic rename rewrites the note's references: read it again.
      this.state.topicsRewritten();
      untracked(() => {
        if (id && wide) {
          // One canonical wide address: the note selected in the All notes
          // list. Replace, so Back does not bounce between the two routes.
          if (this.redirectedFor !== id) {
            this.redirectedFor = id;
            void this.router.navigate(['/brain'], { queryParams: { note: id }, replaceUrl: true });
          }
          return;
        }
        this.load(id);
      });
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

  /** A related note opened from this page: read it at its own address. */
  openNote(id: string): void {
    void this.router.navigate(['/brain/notes', id]);
  }

  moreFromBook(note: NoteSearchHit): void {
    void this.router.navigate(['/brain'], { queryParams: { book: note.workId ?? note.bookId } });
  }

  openDrawer(): void {
    this.state.drawerOpen.set(true);
  }
}
