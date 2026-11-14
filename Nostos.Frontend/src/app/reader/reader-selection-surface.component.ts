import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MenuComponent, MenuItemComponent, MenuTriggerDirective } from '../ui/menu/menu.component';
import { ButtonComponent } from '../ui/button/button.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { NoteComposerComponent } from '../ui/note-composer/note-composer.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { PenSwatchesComponent } from '../ui/pen-swatches/pen-swatches.component';
import { QuoteBlockComponent } from '../ui/quote-block/quote-block.component';
import { HighlightColour } from './highlight-colours';

/** Viewport placement of a menu anchored at text; null docks the surface. */
export type MenuPlacement = { left: number; width: number; top: number | null; bottom: number | null };

/**
 * One set of selection decisions (#650), placed two ways: anchored at the
 * marked text on a fine-pointer desktop, docked at the top edge on phones and
 * touch-first devices (#1344). The quote, the pens, the utilities, the note
 * field and the commit actions are the whole surface.
 *
 * This was the shell template's largest interactive block (issue #1607). The
 * decisions stay the shell's: it is the only owner of the pending capture and
 * of the reader's commit, so every action leaves as an output while the draft
 * words, its topics and the open field come back in as inputs.
 */
@Component({
  selector: 'app-reader-selection-surface',
  standalone: true,
  imports: [
    ButtonComponent,
    IconButtonComponent,
    MenuComponent,
    MenuItemComponent,
    MenuTriggerDirective,
    NoteComposerComponent,
    NostosIconComponent,
    PenSwatchesComponent,
    QuoteBlockComponent,
    NgTemplateOutlet,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './reader-selection-surface.component.html',
  styleUrls: ['./reader-selection-surface.component.css', './reader-floating-surface.css'],
  host: { style: 'display: contents' },
})
export class ReaderSelectionSurface {
  /** Where the anchored menu sits, or null for the docked bar. */
  readonly placement = input<MenuPlacement | null>(null);
  readonly preview = input('');
  readonly highlightColour = input<HighlightColour>('sage');
  readonly saving = input(false);
  readonly fileType = input<'pdf' | 'epub' | 'audio' | null>(null);

  readonly noteDraftOpen = input(false);
  readonly noteDraft = input('');
  readonly noteDraftTopics = input<readonly string[]>([]);
  readonly noteDraftReady = input(false);

  /** The reader can mark passages from a selection. */
  readonly canMarkPassage = input(false);
  /** A passage end is already waiting for this one. */
  readonly passageMarkActive = input(false);
  readonly askNostosAvailable = input(false);

  readonly cancelled = output<void>();
  readonly copy = output<void>();
  readonly markPassage = output<void>();
  readonly askNostos = output<void>();
  readonly highlight = output<void>();
  readonly addNote = output<void>();
  readonly saveNote = output<void>();
  readonly noteWordsChange = output<string>();
  readonly noteTopicsChange = output<readonly string[]>();
  readonly colourChosen = output<HighlightColour>();
}
