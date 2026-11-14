import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { ButtonComponent } from '../ui/button/button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { ModalShell } from '../ui/modal-shell/modal-shell.component';
import { NoteComposerComponent } from '../ui/note-composer/note-composer.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { QuoteBlockComponent } from '../ui/quote-block/quote-block.component';
import { formatAudioTime, passageSizeLabel } from '../core/dtos/note.dtos';
import type { PassageDraft } from './passage/passage-mark';

/**
 * Save a passage (issue #1014): both ends of it are marked, then this sheet
 * names it and keeps it, with an optional note.
 *
 * It was the tail of the shell's template with its title, note and topic
 * signals living on the shell (issue #1607). Now the sheet owns its layout and
 * the nudge maths for a clip's ends; the shell keeps the draft and the save,
 * and hears about every edit through outputs, so the signals its specs and
 * handlers use are still the shell's.
 */
@Component({
  selector: 'app-reader-passage-sheet',
  standalone: true,
  imports: [
    ButtonComponent,
    InputDirective,
    ModalShell,
    NoteComposerComponent,
    NostosIconComponent,
    QuoteBlockComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './reader-passage-sheet.component.html',
  styleUrl: './reader-passage-sheet.component.css',
})
export class ReaderPassageSheet {
  readonly isOpen = input(false);
  readonly draft = input<PassageDraft | null>(null);
  readonly building = input(false);
  readonly buildFailed = input(false);
  readonly saving = input(false);
  readonly title = input('');
  /** The optional note's words. */
  readonly note = input('');
  /** The optional note's pill topics. */
  readonly topics = input<readonly string[]>([]);

  readonly closed = output<void>();
  readonly save = output<void>();
  readonly titleChange = output<string>();
  readonly noteChange = output<string>();
  readonly topicsChange = output<readonly string[]>();
  /** A clip's end moved by a step: the sheet reports the new draft. */
  readonly draftChange = output<PassageDraft>();

  /** The draft's size as its card will show it: words, pages or a time range. */
  readonly draftSize = computed(() => {
    const draft = this.draft();
    return draft
      ? passageSizeLabel({
          kind: 'passage',
          sourceAnchorKind: draft.anchorKind,
          sourceAnchorValue: draft.start,
          passageEnd: draft.end,
          passageWordCount: draft.words,
        })
      : '';
  });

  readonly audioTime = formatAudioTime;

  /** A clip's ends move by 15 seconds on the save sheet, never past each other. */
  nudge(which: 'start' | 'end', delta: number): void {
    const draft = this.draft();
    if (!draft || draft.anchorKind !== 'audio_timestamp') return;
    let start = Number(draft.start);
    let end = Number(draft.end);
    if (which === 'start') start = Math.min(end - 1, Math.max(0, start + delta));
    else end = Math.min(draft.limit ?? Infinity, Math.max(start + 1, end + delta));
    this.draftChange.emit({
      ...draft,
      start: String(start),
      end: String(end),
      opening: formatAudioTime(start),
      closing: formatAudioTime(end),
    });
  }

  canSave(): boolean {
    return !!this.draft() && !!this.title().trim() && !this.saving();
  }
}
