// Nostos.Frontend/src/app/ui/note-card.component/note-card.component.ts
import { Component, EventEmitter, Input, Output, TemplateRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

export interface NoteSourceNavigation {
  commands: Array<string | number>;
  queryParams: Record<string, string | number> | null;
  label: 'Return to passage' | 'Open book';
}

import { Note } from '../../core/dtos/note.dtos';
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

@Component({
  selector: 'app-note-card',
  standalone: true,
  imports: [
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
  @Input() actionsTemplate: TemplateRef<unknown> | null = null;
  @Input() actionsContext: Record<string, unknown> = {};

  // Updated Output signature to match UpdateNoteDto
  // `highlightColour` is present only when the pen was changed in this edit.
  @Output() update = new EventEmitter<{
    id: string;
    content: string;
    selectedText?: string;
    highlightColour?: HighlightColour;
  }>();
  @Output() delete = new EventEmitter<string>();
  @Output() topicClick = new EventEmitter<string>();
  @Output() quoteClick = new EventEmitter<void>();
  @Output() cardClick = new EventEmitter<void>();

  isEditing = false;
  editContent = '';
  editSelectedText = '';
  /** The pen picked in the editor; a highlight's colour is changed here (#861). */
  editColour: HighlightColour = noteHighlightColour({});
  readonly highlightColours = HIGHLIGHT_COLOURS;

  /**
   * The pen to show on the card, or null when there is none to show: a note
   * that is not a highlight, or one from a source that does not carry the
   * field (search hits, Writing Studio sources). Guessing amber there would
   * mislabel a recoloured highlight, so an unknown pen shows nothing. A
   * highlight whose pen is known to be unrecorded (null) is amber.
   */
  get penColour(): HighlightColourOption | null {
    if (!this.note.selectedText || this.note.highlightColour === undefined) return null;
    const colour = noteHighlightColour(this.note);
    return HIGHLIGHT_COLOURS.find((option) => option.id === colour) ?? null;
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
  get shouldShowExpandBtn(): boolean {
    if (this.isEditing) return false;

    // A quote is the reason the note exists and is set as the card's hero, so it
    // is not truncated merely for existing: collapsing a normal quotation buries
    // the very thing the reader came for. A quotation long enough to swamp the
    // feed is the exception — it collapses, and the hero survives because
    // expanding it is one click away.
    const totalLength =
      (this.note.selectedText?.length || 0) + (this.note.content?.length || 0);

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
