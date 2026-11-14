import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { isPassage, type NoteSearchHit } from '../core/dtos/note.dtos';
import { UNKNOWN_BOOK, formatOfConcept } from '../core/book-format';
import { CheckboxDirective } from '../ui/check/check.directive';
import { NostosDatePipe } from '../ui/pipes/nostos-date.pipe';
import { noteLocationLabel } from '../ui/note-item/note-item.helpers';
import { plainNoteText } from './brain-note-detail.helpers';
import { plural } from './brain.helpers';

/**
 * One note as the wide Brain's list shows it (issue #1531): a compact index
 * row rather than the full card, because the note itself is open beside it.
 * It owns no behaviour: the listbox around it handles the keyboard and what a
 * press means, and this row only says which note it is, whether it is open in
 * the pane, and — while notes are being ticked — whether it is ticked.
 */
@Component({
  selector: 'app-brain-note-row',
  standalone: true,
  imports: [CheckboxDirective, NostosDatePipe],
  templateUrl: './brain-note-row.component.html',
  styleUrls: ['./brain-note-row.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'note-row',
    role: 'option',
    'data-note-row': '',
    '[attr.aria-selected]': 'selected()',
    '[attr.tabindex]': 'tabIndex()',
    '[attr.data-note-id]': 'note().id',
  },
})
export class BrainNoteRowComponent {
  readonly note = input.required<NoteSearchHit>();
  /** The note is the one open in the pane (not "ticked"; see `checked`). */
  readonly selected = input(false);
  /** Roving tabindex: 0 for the row the list's Tab lands on, -1 for the rest. */
  readonly tabIndex = input(0);
  /** Notes are being ticked: the row shows a checkbox. */
  readonly selectable = input(false);
  /** Ticked for a bulk action. */
  readonly checked = input(false);

  readonly checkedChange = output<boolean>();

  readonly bookTitle = computed(() => this.note().bookTitle || UNKNOWN_BOOK);
  readonly excerpt = computed(() => {
    const note = this.note();
    const quote = note.selectedText?.trim();
    if (quote) return quote;
    return plainNoteText(note.content) || 'A note with no words yet.';
  });
  /** The quote's words read differently from the reader's own. */
  readonly isWords = computed(() => !this.note().selectedText?.trim());
  readonly foot = computed(() => {
    const note = this.note();
    const where = noteLocationLabel(note) ?? (isPassage(note) ? 'Passage' : 'Note');
    const topics = note.topicNames?.length ?? 0;
    return topics ? `${where} · ${plural(topics, 'topic')}` : where;
  });
  readonly format = computed(() => {
    const note = this.note();
    return (note.editionCount ?? 1) > 1 ? formatOfConcept(note.bookFormat)?.format ?? null : null;
  });

  toggleChecked(checked: boolean): void {
    this.checkedChange.emit(checked);
  }
}
