import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { CdkConnectedOverlay, CdkOverlayOrigin, type ConnectedPosition } from '@angular/cdk/overlay';

import { isPassage, passageSizeLabel, type NoteSearchHit } from '../../core/dtos/note.dtos';
import { noteSourceLink } from '../../core/note-source';
import type { TopicDto } from '../../core/services/topics.service';
import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HIGHLIGHT_COLOURS,
  highlightFillRef,
  noteHighlightColour,
  type HighlightColour,
} from '../../reader/highlight-colours';
import { NOSTOS_MENU } from '../menu/menu.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { ButtonComponent } from '../button/button.component';
import { CheckboxDirective } from '../check/check.directive';
import { InputDirective, TextareaDirective } from '../form-control/form-control.directive';
import { TopicInputComponent } from '../topic-input.component/topic-input.component';
import { NoteFormatPipe } from '../pipes/note-format.pipe';
import { NoteCaptureDetailsComponent } from '../note-capture-details/note-capture-details.component';
import { PassageViewerService } from '../passage-viewer/passage-viewer.service';
import {
  TopicPickerPopoverComponent,
  type TopicSuggestionState,
} from '../topic-picker-popover/topic-picker-popover.component';
import { noteLocationLabel, thoughtForDisplay, topicLookup } from './note-item.helpers';
import type { Note } from '../../core/dtos/note.dtos';

/** What an edit changed. Fields that did not change are absent. */
export interface NoteItemEdit {
  id: string;
  content: string;
  selectedText?: string;
  highlightColour?: HighlightColour;
  passageTitle?: string;
}

/** Above this many characters a quote is cut to a few lines, with "Show all". */
const LONG_QUOTE = 420;
/** Above this many characters the note's own words are cut too. */
const LONG_THOUGHT = 600;

const PICKER_POSITIONS: ConnectedPosition[] = [
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 6 },
  { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -6 },
  { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 6 },
];

/**
 * One note, the same everywhere the Brain shows it: the quote, your own words,
 * its topics (open one, remove one, add one), where it comes from, and a menu
 * for the rest. A passage shows its title and opening, and is read in the
 * passage viewer.
 *
 * The card owns no data. It says what the reader asked for through outputs
 * and shows whatever note it is given back, so a list can save, undo and
 * replace a note in one place.
 */
@Component({
  selector: 'app-note-item',
  standalone: true,
  imports: [
    DatePipe,
    FormsModule,
    RouterLink,
    CdkConnectedOverlay,
    CdkOverlayOrigin,
    ...NOSTOS_MENU,
    NostosIconComponent,
    IconButtonComponent,
    ButtonComponent,
    CheckboxDirective,
    InputDirective,
    TextareaDirective,
    TopicInputComponent,
    NoteFormatPipe,
    NoteCaptureDetailsComponent,
    TopicPickerPopoverComponent,
  ],
  templateUrl: './note-item.component.html',
  styleUrls: ['./note-item.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'note-item',
    '[class.is-selected]': 'selected()',
    '[class.is-busy]': 'busy()',
    '[attr.data-note-id]': 'note().id',
  },
})
export class NoteItemComponent {
  readonly note = input.required<NoteSearchHit>();
  /** Every topic, for the picker. */
  readonly topics = input<readonly TopicDto[]>([]);
  /**
   * The same topics by lower-cased name, built ONCE by the list that renders
   * the cards (see {@link topicLookup}); turns chips and inline `[[links]]`
   * into links. Built here from `topics` only when no list passes it.
   */
  readonly topicMap = input<ReadonlyMap<string, TopicDto> | null>(null);
  /** False where a heading above already names the book. */
  readonly showBook = input(true);
  /** Offer "More from this book" (not when the list is already that book). */
  readonly showMoreFromBook = input(true);
  /** A topic change for this note is in flight: its topic controls wait. */
  readonly busy = input(false);
  /** Offer "Select" in the menu (a list where several notes can be chosen). */
  readonly canSelect = input(true);
  readonly selectable = input(false);
  readonly selected = input(false);
  /** AI suggestions for this note's picker, already loaded by the caller. */
  readonly suggestions = input<readonly string[]>([]);
  readonly suggestionState = input<TopicSuggestionState>('off');

  readonly topicAdded = output<string>();
  readonly topicRemoved = output<string>();
  /** The picker opened: the caller may start loading suggestions. */
  readonly pickerOpened = output<void>();
  readonly edited = output<NoteItemEdit>();
  readonly keepWithWriting = output<void>();
  readonly moreFromBook = output<void>();
  readonly selectRequested = output<void>();
  readonly selectedChange = output<boolean>();
  readonly deleteRequested = output<void>();
  readonly restored = output<Note>();

  private readonly passageViewer = inject(PassageViewerService);
  private readonly router = inject(Router);

  readonly pickerPositions = PICKER_POSITIONS;
  readonly highlightColours = HIGHLIGHT_COLOURS;

  readonly pickerOpen = signal(false);
  readonly expanded = signal(false);
  readonly editing = signal(false);
  readonly editContent = signal('');
  readonly editQuote = signal('');
  readonly editTitle = signal('');
  readonly editColour = signal<HighlightColour>(DEFAULT_HIGHLIGHT_COLOUR);

  readonly isPassage = computed(() => isPassage(this.note()));
  readonly passageSize = computed(() => passageSizeLabel(this.note()));
  readonly quote = computed(() => this.note().selectedText?.trim() ?? '');
  readonly thought = computed(() => thoughtForDisplay(this.note().content));
  readonly topicNames = computed(() => this.note().topicNames ?? []);
  readonly location = computed(() => noteLocationLabel(this.note()));

  readonly lookup = computed(() => this.topicMap() ?? topicLookup(this.topics()));

  /** The chips: each topic name with its id, when the topic still exists. */
  readonly chips = computed(() =>
    this.topicNames().map((name) => ({ name, id: this.lookup().get(name.trim().toLocaleLowerCase())?.id ?? null })));

  /** A highlight (or passage) has a pen that can be shown and changed. */
  readonly hasPen = computed(() => !!this.note().selectedText || this.isPassage());

  readonly fillRef = highlightFillRef;

  /** The fill the quote's rule is drawn in; null keeps the neutral rule. */
  readonly penFill = computed<string | null>(() => {
    const note = this.note();
    if (!note.selectedText || note.highlightColour === undefined) return null;
    return highlightFillRef(noteHighlightColour(note));
  });

  readonly long = computed(() =>
    !this.isPassage() && (this.quote().length > LONG_QUOTE || this.thought().length > LONG_THOUGHT));

  readonly source = computed(() => noteSourceLink(this.note()));

  openPicker(): void {
    if (this.busy()) return;
    this.pickerOpen.set(true);
    this.pickerOpened.emit();
  }

  closePicker(): void {
    this.pickerOpen.set(false);
  }

  chooseTopic(name: string): void {
    this.pickerOpen.set(false);
    this.topicAdded.emit(name);
  }

  readPassage(): void {
    this.passageViewer.open(this.note().id, {});
  }

  /**
   * An inline `[[link]]` in the note's words opens in the app rather than with
   * a page load, at the address the link itself names, so a plain click and a
   * modified click (new tab) go to the same place.
   */
  onThoughtClick(event: MouseEvent): void {
    const link = (event.target as HTMLElement | null)?.closest<HTMLAnchorElement>('a.topic-tag');
    const href = link?.getAttribute('href');
    if (!href || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    void this.router.navigateByUrl(href);
  }

  startEdit(): void {
    const note = this.note();
    this.pickerOpen.set(false);
    this.editContent.set(note.content ?? '');
    this.editQuote.set(note.selectedText ?? '');
    this.editTitle.set(note.passageTitle ?? '');
    this.editColour.set(noteHighlightColour(note));
    this.editing.set(true);
  }

  cancelEdit(): void {
    this.editing.set(false);
  }

  saveEdit(): void {
    const note = this.note();
    const content = this.editContent().trim();
    const edit: NoteItemEdit = { id: note.id, content };
    let changed = content !== (note.content ?? '').trim();

    if (this.isPassage()) {
      // A passage's text and ends are fixed; its title, words and pen are not.
      const title = this.editTitle().trim();
      if (title && title !== (note.passageTitle ?? '')) {
        edit.passageTitle = title;
        changed = true;
      }
    } else if (note.selectedText) {
      const quote = this.editQuote().trim();
      edit.selectedText = quote;
      if (quote !== (note.selectedText ?? '').trim()) changed = true;
    }

    if (this.hasPen() && this.editColour() !== noteHighlightColour(note)) {
      edit.highlightColour = this.editColour();
      changed = true;
    }

    this.editing.set(false);
    if (changed) this.edited.emit(edit);
  }

  toggleSelected(checked: boolean): void {
    this.selectedChange.emit(checked);
  }
}
