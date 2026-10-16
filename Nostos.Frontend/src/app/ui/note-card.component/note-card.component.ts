// Nostos.Frontend/src/app/ui/note-card.component/note-card.component.ts
import { Component, EventEmitter, Input, Output, TemplateRef, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

export interface NoteSourceNavigation {
  commands: Array<string | number>;
  queryParams: Record<string, string | number> | null;
  label: 'Return to passage' | 'Open book';
}

import { Note, formatAudioTime, isPassage, passageSizeLabel } from '../../core/dtos/note.dtos';
import { PassageViewerRequest, PassageViewerService } from '../passage-viewer/passage-viewer.service';
import { InputDirective } from '../form-control/form-control.directive';
import {
  HIGHLIGHT_COLOURS,
  HighlightColour,
  HighlightColourOption,
  noteHighlightColour,
} from '../../reader/highlight-colours';
import { TopicDto } from '../../core/services/topics.service';
import { TopicInputComponent } from '../topic-input.component/topic-input.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { NoteFormatPipe } from '../pipes/note-format.pipe';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { TextareaDirective } from '../form-control/form-control.directive';

@Component({
  selector: 'app-note-card',
  standalone: true,
  imports: [TextareaDirective, InputDirective,
    CommonModule,
    FormsModule,
    NostosIconComponent,
    IconButtonComponent,
    TopicInputComponent,
    NoteFormatPipe,
    RouterLink,
  ],
  templateUrl: './note-card.component.html',
  styleUrls: ['./note-card.component.css'],
})
export class NoteCardComponent {
  @Input({ required: true }) note!: Note;
  @Input() topicMap: Map<string, TopicDto> = new Map();
  @Input() showNavigation = false;
  @Input() showActions = true;
  @Input() showSource = false;
  /** Optional context-specific source destination; null preserves the legacy Library link. */
  @Input() sourceNavigation: NoteSourceNavigation | null = null;
  @Input() showDate = true;
  /**
   * `card` is the boxed card used in lists and panels. `passage` sets the note
   * as running text for a reading column: an upright quotation without marks,
   * the commentary beside it when there is room, no book title in the footer
   * (the column groups passages under their book), and no collapse short of a
   * quotation several pages long.
   */
  @Input() layout: 'card' | 'passage' = 'card';
  /**
   * The playback time an audiobook note was taken at, in seconds (issue #906).
   * Set by the audio reader only; null shows nothing.
   */
  @Input() audioTime: number | null = null;
  /**
   * How the passage viewer opened from this card behaves (issue #1014). The
   * reader passes its own "Open in book", which turns the open book instead of
   * reloading it.
   */
  @Input() passageViewerOptions: Omit<PassageViewerRequest, 'noteId'> | null = null;
  @Input() actionsTemplate: TemplateRef<unknown> | null = null;
  @Input() actionsContext: Record<string, unknown> = {};

  // Updated Output signature to match UpdateNoteDto
  // `highlightColour` is present only when the pen was changed in this edit.
  @Output() update = new EventEmitter<{
    id: string;
    content: string;
    selectedText?: string;
    highlightColour?: HighlightColour;
    /** Present only when a passage's title was changed in this edit. */
    passageTitle?: string;
  }>();
  @Output() delete = new EventEmitter<string>();
  @Output() topicClick = new EventEmitter<string>();
  @Output() quoteClick = new EventEmitter<void>();
  @Output() cardClick = new EventEmitter<void>();

  private readonly passageViewer = inject(PassageViewerService);

  isEditing = false;
  editContent = '';
  editPassageTitle = '';
  editSelectedText = '';
  /** The pen picked in the editor; a highlight's colour is changed here (#861). */
  editColour: HighlightColour = noteHighlightColour({});
  readonly highlightColours = HIGHLIGHT_COLOURS;

  /**
   * The pen the quote rule is drawn in, or null when there is none to show: a note
   * that is not a highlight, or one from a source that does not carry the
   * field (search hits, Writing Studio sources). Guessing amber there would
   * mislabel a recoloured highlight, so an unknown pen keeps the accent rule. A
   * highlight whose pen is known to be unrecorded (null) is amber.
   */
  get penColour(): HighlightColourOption | null {
    if (!this.note.selectedText || this.note.highlightColour === undefined) return null;
    const colour = noteHighlightColour(this.note);
    return HIGHLIGHT_COLOURS.find((option) => option.id === colour) ?? null;
  }

  /** A passage (issue #1014): its own card, read in the passage viewer. */
  get isPassageNote(): boolean {
    return isPassage(this.note);
  }

  get passageSize(): string {
    return passageSizeLabel(this.note);
  }

  readPassage(event?: Event): void {
    event?.stopPropagation();
    this.passageViewer.open(this.note.id, this.passageViewerOptions ?? {});
  }

  get audioTimeLabel(): string | null {
    return this.audioTime === null ? null : formatAudioTime(this.audioTime);
  }

  // Collapse logic
  isExpanded = false;
  readonly CHAR_THRESHOLD = 250;
  // A quoted card has no commentary-collapse path (below), so an unusually long
  // quotation would otherwise expand to its full height — the Combeferre note
  // imported from the vault is 5,871 characters and filled the whole feed. Above
  // this length a quote collapses too, behind the same "Show more" affordance.
  // Set at 1000 characters of total note text: measured against live data it
  // collapses the four oversized imported cards and exactly one pre-existing
  // note, and never a quote a reader would call short.
  readonly QUOTE_COLLAPSE_THRESHOLD = 1000;
  // A reading column has no neighbours for a long passage to distort, so only a
  // quotation of several pages collapses there.
  readonly PASSAGE_COLLAPSE_THRESHOLD = 4000;
  get shouldShowExpandBtn(): boolean {
    if (this.isEditing) return false;
    // A passage shows only its opening; the viewer is where it is read.
    if (this.isPassageNote) return false;

    // A quote is the reason the note exists and is set as the card's hero, so it
    // is not truncated merely for existing: collapsing a normal quotation buries
    // the very thing the reader came for. A quotation long enough to swamp the
    // feed is the exception — it collapses, and the hero survives because
    // expanding it is one click away.
    const totalLength =
      (this.note.selectedText?.length || 0) + (this.note.content?.length || 0);

    if (this.layout === 'passage') return totalLength > this.PASSAGE_COLLAPSE_THRESHOLD;

    if (this.note.selectedText) {
      return totalLength > this.QUOTE_COLLAPSE_THRESHOLD;
    }

    const contentLen = this.note.content?.length || 0;
    return contentLen > this.CHAR_THRESHOLD;
  }

  toggleExpand(event: Event) {
    event.stopPropagation();
    this.isExpanded = !this.isExpanded;
  }

  startEdit(event?: Event) {
    event?.stopPropagation();
    this.editContent = this.note.content;
    this.editSelectedText = this.note.selectedText || ''; // Initialize quote editor
    this.editColour = noteHighlightColour(this.note);
    this.editPassageTitle = this.note.passageTitle ?? '';
    this.isEditing = true;
    this.isExpanded = true;
  }

  cancelEdit(event?: Event) {
    event?.stopPropagation();
    this.isEditing = false;
    this.editContent = '';
    this.editSelectedText = '';
    this.isExpanded = false;
  }

  saveEdit(event?: Event) {
    event?.stopPropagation();
    const cleanContent = this.editContent.trim();
    const cleanQuote = this.editSelectedText.trim();

    if (this.isPassageNote) {
      // A passage's text and ends are fixed; its title, note and pen are not.
      const title = this.editPassageTitle.trim();
      const titleChanged = !!title && title !== (this.note.passageTitle ?? '');
      const penChanged = this.editColour !== noteHighlightColour(this.note);
      if (titleChanged || penChanged || cleanContent !== this.note.content) {
        this.update.emit({
          id: this.note.id,
          content: cleanContent,
          ...(titleChanged ? { passageTitle: title } : {}),
          ...(penChanged ? { highlightColour: this.editColour } : {}),
        });
      }
      this.isEditing = false;
      return;
    }

    // Check if either field changed
    const contentChanged = cleanContent !== this.note.content;
    const quoteChanged = cleanQuote !== (this.note.selectedText || '');

    // Only a highlight has a pen to change; a plain note never sends one.
    const colourChanged =
      !!this.note.selectedText && this.editColour !== noteHighlightColour(this.note);

    if (contentChanged || quoteChanged || colourChanged) {
      this.update.emit({
        id: this.note.id,
        content: cleanContent,
        selectedText: cleanQuote, // Emit the new quote
        ...(colourChanged ? { highlightColour: this.editColour } : {}),
      });
    }
    this.isEditing = false;
    this.isExpanded = false;
  }

  onDelete(event?: Event) {
    event?.stopPropagation();
    this.delete.emit(this.note.id);
  }

  onCardClick(event?: Event) {
    event?.stopPropagation();
    this.cardClick.emit();
  }

  onContentClick(event: MouseEvent) {
    const target = event.target as HTMLElement;
    if (target.classList.contains('topic-tag')) {
      const id = target.getAttribute('data-topic-id');
      if (id) {
        event.preventDefault();
        event.stopPropagation();
        this.topicClick.emit(id);
      }
    }
  }
}
