import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { ButtonComponent } from '../ui/button/button.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { NoteComposerComponent } from '../ui/note-composer/note-composer.component';
import { NoteFormatPipe } from '../ui/pipes/note-format.pipe';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { PenSwatchesComponent } from '../ui/pen-swatches/pen-swatches.component';
import { QuoteBlockComponent } from '../ui/quote-block/quote-block.component';
import { TopicRowComponent } from '../ui/topic-row/topic-row.component';
import type { Note } from '../core/dtos/note.dtos';
import type { TopicDto } from '../core/services/topics.service';
import { HighlightColour } from './highlight-colours';
import type { MenuPlacement } from './reader-selection-surface.component';

/**
 * A saved highlight opened from the page: its passage, its note, its pen and
 * what can be done with it. Anchored at the mark on a fine-pointer desktop and
 * docked at the top on phones (#1344), exactly as the selection surface is.
 *
 * The card was the shell's companion block to the selection menu (issue
 * #1607). The shell still owns which highlight is open and the note's save;
 * this component owns the layout and reports every intent as an output.
 */
@Component({
  selector: 'app-reader-highlight-card',
  standalone: true,
  imports: [
    ButtonComponent,
    IconButtonComponent,
    NoteComposerComponent,
    NoteFormatPipe,
    NostosIconComponent,
    PenSwatchesComponent,
    QuoteBlockComponent,
    TopicRowComponent,
    NgTemplateOutlet,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './reader-highlight-card.component.html',
  styleUrls: ['./reader-highlight-card.component.css', './reader-floating-surface.css'],
  host: { style: 'display: contents' },
})
export class ReaderHighlightCard {
  /** The opened highlight's note; null closes the card. */
  readonly note = input<Note | null>(null);
  readonly hasNote = input(false);
  /** The note's own words, without the lines its topic pills are. */
  readonly words = input('');
  readonly topics = input<readonly string[]>([]);
  /** Topics by lower-cased name, for [[ ]] links in the note. */
  readonly topicMap = input<ReadonlyMap<string, TopicDto> | null>(null);
  readonly pen = input<HighlightColour>('sage');
  readonly preview = input('');
  /** Where the anchored card sits, or null for the docked sheet. */
  readonly placement = input<MenuPlacement | null>(null);

  readonly editing = input(false);
  readonly noteDraft = input('');
  readonly noteDraftTopics = input<readonly string[]>([]);
  readonly noteDraftSaving = input(false);
  /** Save would change the note: adding, editing or clearing it. */
  readonly noteChanged = input(false);
  /** Save would leave the highlight with no note: Save reads "Remove note". */
  readonly noteEmpty = input(false);
  readonly askNostosAvailable = input(false);

  readonly closed = output<void>();
  readonly editNote = output<void>();
  readonly cancelEdit = output<void>();
  readonly saveNote = output<void>();
  readonly noteWordsChange = output<string>();
  readonly noteTopicsChange = output<readonly string[]>();
  readonly colourChosen = output<HighlightColour>();
  readonly copy = output<void>();
  readonly askNostos = output<void>();
  readonly showInNotes = output<void>();
  readonly deleteHighlight = output<void>();
}
