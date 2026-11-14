import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { ButtonComponent } from '../ui/button/button.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { NoteComposerComponent } from '../ui/note-composer/note-composer.component';
import { NoteItemComponent, type NoteItemEdit } from '../ui/note-item/note-item.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { PenSwatchesComponent } from '../ui/pen-swatches/pen-swatches.component';
import type { PassageViewerRequest } from '../ui/passage-viewer/passage-viewer.service';
import { BrainNoteActionsService } from '../brain/brain-note-actions.service';
import { BrainStateService } from '../brain/brain-state.service';
import { BrainTopicSuggestionsService } from '../brain/brain-topic-suggestions.service';
import { HighlightColour } from './highlight-colours';
import { Note, NoteSearchHit, noteAudioSeconds } from '../core/dtos/note.dtos';

/** The notes panel's rows: each note, and the same note as its item shows it. */
export type ReaderNoteRow = { note: Note; item: NoteSearchHit };

/**
 * Notes & highlights for the open book: the highlighter pens, a quick note, the
 * list, and the notes made in the book's other editions, kept apart.
 *
 * The drawer was the largest block of the shell's template (issue #1607). It is
 * now a component with signal inputs and outputs; the Brain's own note actions
 * and the topic suggestions it already used are injected, because both are
 * provided at shell scope and are the same instances the shell attaches its
 * page repaint to.
 */
@Component({
  selector: 'app-reader-notes-panel',
  standalone: true,
  imports: [
    ButtonComponent,
    EmptyStateComponent,
    IconButtonComponent,
    NoteComposerComponent,
    NoteItemComponent,
    NostosIconComponent,
    PenSwatchesComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './reader-notes-panel.component.html',
  styleUrl: './reader-notes-panel.component.css',
  host: { style: 'display: contents' },
})
export class ReaderNotesPanel {
  private readonly brain = inject(BrainStateService);
  readonly noteActions = inject(BrainNoteActionsService);
  readonly suggestions = inject(BrainTopicSuggestionsService);

  readonly open = input(false);
  /** The pen the reader has chosen; the dots are the same for the whole book. */
  readonly highlightColour = input<HighlightColour>('sage');
  /** The book's format: audiobooks carry a note's timestamp, and no pens. */
  readonly fileType = input<'pdf' | 'epub' | 'audio' | null>(null);
  readonly rows = input<readonly ReaderNoteRow[]>([]);
  readonly notesFailed = input(false);
  /** The note just pointed at from a highlight card, briefly marked. */
  readonly flashedNoteId = input<string | null>(null);
  /** The notes made in the book's other editions. */
  readonly otherEditionNotes = input<readonly NoteSearchHit[]>([]);
  /** "4 from the audiobook", or null when there are none. */
  readonly otherNotesLabel = input<string | null>(null);
  readonly otherNotesOpen = input(false);
  /** The passage viewer, opened from this reader's notes, turns this book. */
  readonly passageViewerOptions = input<Omit<PassageViewerRequest, 'noteId'> | null>(null);

  readonly quickNoteContent = input('');
  readonly quickNoteTopics = input<readonly string[]>([]);
  readonly quickNoteSaving = input(false);
  readonly quickNoteReady = input(false);
  /** The audio moment the draft will be saved at, or null. */
  readonly quickNotePinnedTime = input<string | null>(null);

  readonly closed = output<void>();
  readonly colourChosen = output<HighlightColour>();
  readonly retry = output<void>();
  readonly quickNoteWordsChange = output<string>();
  readonly quickNoteTopicsChange = output<readonly string[]>();
  readonly quickNoteSave = output<void>();
  readonly otherNotesOpenChange = output<boolean>();

  readonly edit = output<NoteItemEdit>();
  readonly delete = output<string>();
  readonly jump = output<Note>();

  readonly topics = this.brain.topics;
  readonly topicMap = this.brain.lookup;

  /** The time to show on a note's card: audiobooks only. */
  noteAudioTime(note: Note): number | null {
    return this.fileType() === 'audio' ? noteAudioSeconds(note) : null;
  }
}
