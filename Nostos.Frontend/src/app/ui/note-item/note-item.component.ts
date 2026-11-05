import {
  ChangeDetectionStrategy,
  Component,
  afterNextRender,
  computed,
  effect,
  ElementRef,
  inject,
  Injector,
  untracked,
  input,
  output,
  signal,
} from '@angular/core';
import { NostosDatePipe } from '../pipes/nostos-date.pipe';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { formatAudioTime, isPassage, passageSizeLabel, type NoteSearchHit } from '../../core/dtos/note.dtos';
import { noteSourceLink } from '../../core/note-source';
import type { TopicDto } from '../../core/services/topics.service';
import {
  DEFAULT_HIGHLIGHT_COLOUR,
  noteHighlightColour,
  quotePen,
  type HighlightColour,
} from '../../reader/highlight-colours';
import { NOSTOS_MENU } from '../menu/menu.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { ButtonComponent } from '../button/button.component';
import { CheckboxDirective } from '../check/check.directive';
import { InputDirective, TextareaDirective } from '../form-control/form-control.directive';
import { NoteComposerComponent, draftChanged, draftForNote, draftToSave } from '../note-composer/note-composer.component';
import { TopicRowComponent } from '../topic-row/topic-row.component';
import { PenSwatchesComponent } from '../pen-swatches/pen-swatches.component';
import { QuoteBlockComponent } from '../quote-block/quote-block.component';
import { NoteFormatPipe } from '../pipes/note-format.pipe';
import { NoteCaptureDetailsComponent } from '../note-capture-details/note-capture-details.component';
import { PassageViewerService, type PassageViewerRequest } from '../passage-viewer/passage-viewer.service';
import type { TopicSuggestionState } from '../topic-picker-popover/topic-picker-popover.component';
import { inlineTopicHref, noteLocationLabel, thoughtForDisplay, topicLookup } from './note-item.helpers';
import type { Note } from '../../core/dtos/note.dtos';
import { UNKNOWN_BOOK, formatOfConcept } from '../../core/book-format';


/** What an edit changed. Fields that did not change are absent. */
export interface NoteItemEdit {
  id: string;
  content: string;
  selectedText?: string;
  highlightColour?: HighlightColour;
  passageTitle?: string;
  /** The note's pill topics; with them, `content` is the words only (issue #1446). */
  topics?: string[];
}

/** Above this many characters a quote is cut to a few lines, with "Show all". */
const LONG_QUOTE = 420;
/** Above this many characters the note's own words are cut too. */
const LONG_THOUGHT = 600;

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
/** A writing that cites a note: enough to name it and open it. */
export interface CitingWriting {
  id: string;
  name: string;
}

@Component({
  selector: 'app-note-item',
  standalone: true,
  imports: [
    NostosDatePipe,
    FormsModule,
    RouterLink,
    ...NOSTOS_MENU,
    NostosIconComponent,
    IconButtonComponent,
    ButtonComponent,
    CheckboxDirective,
    InputDirective,
    TextareaDirective,
    NoteComposerComponent,
    TopicRowComponent,
    NoteFormatPipe,
    NoteCaptureDetailsComponent,
    PenSwatchesComponent,
    QuoteBlockComponent,
  ],
  templateUrl: './note-item.component.html',
  styleUrls: ['./note-item.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'note-item',
    '[class.is-selected]': 'selected()',
    '[class.is-busy]': 'busy()',
    '[class.is-read-only]': 'readOnly()',
    '[attr.data-note-id]': 'note().id',
  },
})
export class NoteItemComponent {
  protected readonly unknownBook = UNKNOWN_BOOK;
  readonly note = input.required<NoteSearchHit>();
  /** Every topic, for the picker. */
  readonly topics = input<readonly TopicDto[]>([]);
  /**
   * The same topics by lower-cased name, built ONCE by the list that renders
   * the cards (see {@link topicLookup}); turns chips and inline `[[links]]`
   * into links. Built here from `topics` only when no list passes it.
   */
  readonly topicMap = input<ReadonlyMap<string, TopicDto> | null>(null);
  /**
   * The writings that cite this note (issue #1162), newest edited first. When
   * there are any, the meta line says "Cited in N writings" as a quiet button
   * whose menu opens each one in the Studio.
   */
  readonly citedWritings = input<readonly CitingWriting[]>([]);
  /** False where a heading above already names the book. */
  readonly showBook = input(true);
  /**
   * The note is shown inside its own open book (the reader's notes panel):
   * "Open in book" turns into "Show in book" and asks for `jumpRequested`
   * instead of opening the reader again.
   */
  readonly inBook = input(false);
  /**
   * The playback time an audiobook note was taken at, in seconds (issue #906).
   * Shown at the start of the meta line; activating it asks for `jumpRequested`.
   * Null shows nothing.
   */
  readonly audioTime = input<number | null>(null);
  /**
   * How the passage viewer opened from this note behaves (issue #1014). The
   * reader passes its own "Open in book", which turns the open book.
   */
  readonly passageViewerOptions = input<Omit<PassageViewerRequest, 'noteId'> | null>(null);
  /**
   * Shows the note only: no menu, no topic chips or controls (its `[[links]]`
   * stay in its words), no capture details. For a
   * surface that offers its own actions around the note (the Studio's
   * inspected source).
   */
  readonly readOnly = input(false);
  /** False where the surface names the note's source itself: no meta line. */
  readonly showMeta = input(true);
  /**
   * Names the edition the note was made in (its format glyph and label) beside
   * the note's location. For a list that mixes the notes of several editions of
   * one book; off by default, so a single-edition list stays as it was.
   */
  readonly showFormat = input(false);
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
  /** The picker closed (a choice, Escape, a click elsewhere): stop any suggestions. */
  readonly pickerClosed = output<void>();
  readonly edited = output<NoteItemEdit>();
  readonly keepWithWriting = output<void>();
  readonly moreFromBook = output<void>();
  readonly selectRequested = output<void>();
  readonly selectedChange = output<boolean>();
  readonly deleteRequested = output<void>();
  readonly restored = output<Note>();
  /** With `inBook` or an `audioTime`: go to the note's place in the open book. */
  readonly jumpRequested = output<void>();

  private readonly passageViewer = inject(PassageViewerService);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  /** Opens a writing that cites this note in the Studio. */
  openWriting(writing: CitingWriting): void {
    void this.router.navigate(['/studio'], { queryParams: { writingId: writing.id } });
  }

  readonly expanded = signal(false);
  readonly editing = signal(false);
  /** A saved edit is out; the form stays until the note comes back changed. */
  readonly saving = signal(false);
  private savingNote: object | null = null;

  /**
   * An edit leaves the form open while it saves. When the change settles
   * (`busy` falls) the form closes if the note was replaced; if it was not,
   * the save failed and the words stay in the form to try again.
   */
  constructor() {
    // Keyboard focus follows the edit: into the words when the form opens, and
    // back to the note's "More actions" button when it closes (that button is
    // not in the page while the form is, so focus would fall to <body>).
    let wasEditing = false;
    effect(() => {
      const editing = this.editing();
      untracked(() => {
        if (editing === wasEditing) return;
        wasEditing = editing;
        afterNextRender(
          () => {
            const host = this.host.nativeElement;
            const target = editing
              ? (host.querySelector('app-topic-input textarea') ?? host.querySelector('form input, form textarea'))
              : host.querySelector('.note-item-more');
            (target as HTMLElement | null)?.focus();
          },
          { injector: this.injector },
        );
      });
    });
    effect(() => {
      const busy = this.busy();
      const note = this.note();
      untracked(() => {
        if (busy || !this.saving()) return;
        if (note !== this.savingNote) this.editing.set(false);
        this.saving.set(false);
      });
    });
  }

  /** The edit's words and pill topics (the composer's draft). */
  readonly editWords = signal('');
  readonly editTopics = signal<readonly string[]>([]);
  readonly editQuote = signal('');
  readonly editTitle = signal('');
  readonly editColour = signal<HighlightColour>(DEFAULT_HIGHLIGHT_COLOUR);

  readonly isPassage = computed(() => isPassage(this.note()));
  readonly passageSize = computed(() => passageSizeLabel(this.note()));
  readonly quote = computed(() => this.note().selectedText?.trim() ?? '');
  /**
   * The note's words. Where the topics are listed as chips, lines holding only
   * `[[Topic]]` references are left out; a read-only note has no chips, so it
   * keeps them, as links.
   */
  readonly thought = computed(() =>
    this.readOnly() ? (this.note().content ?? '').trim() : thoughtForDisplay(this.note().content));
  readonly topicNames = computed(() => this.note().topicNames ?? []);
  readonly location = computed(() => noteLocationLabel(this.note()));
  /** The edition's format, when `showFormat` is on and the note says it. */
  readonly editionFormat = computed(() => (this.showFormat() ? formatOfConcept(this.note().bookFormat) : null));

  readonly lookup = computed(() => this.topicMap() ?? topicLookup(this.topics()));

  /** A highlight (or passage) has a pen that can be shown and changed. */
  readonly hasPen = computed(() => !!this.note().selectedText || this.isPassage());

  /** The pen the quote's rail is drawn in; null keeps the neutral rail. */
  readonly pen = computed(() => quotePen(this.note()));

  readonly long = computed(() =>
    !this.isPassage() && (this.quote().length > LONG_QUOTE || this.thought().length > LONG_THOUGHT));

  readonly source = computed(() => noteSourceLink(this.note()));

  readonly audioTimeLabel = computed(() => {
    const seconds = this.audioTime();
    return seconds === null ? null : formatAudioTime(seconds);
  });

  readPassage(): void {
    this.passageViewer.open(this.note().id, this.passageViewerOptions() ?? {});
  }

  /**
   * An inline `[[link]]` in the note's words opens in the app rather than with
   * a page load, at the address the link itself names, so a plain click and a
   * modified click (new tab) go to the same place.
   */
  onThoughtClick(event: MouseEvent): void {
    const href = inlineTopicHref(event);
    if (!href) return;
    event.preventDefault();
    void this.router.navigateByUrl(href);
  }

  startEdit(): void {
    const note = this.note();
    const draft = draftForNote(note.content, this.topicNames());
    this.editWords.set(draft.words);
    this.editTopics.set(draft.topics);
    this.editQuote.set(note.selectedText ?? '');
    this.editTitle.set(note.passageTitle ?? '');
    this.editColour.set(noteHighlightColour(note));
    this.editing.set(true);
  }

  cancelEdit(): void {
    if (this.saving()) return;
    this.editing.set(false);
  }

  /**
   * Escape leaves the edit and nothing else: the note sits inside a reader or
   * Brain drawer that would otherwise close under it. A control that already
   * used the key (a topic list closing) leaves the edit open.
   */
  onEditEscape(event: Event): void {
    if (event.defaultPrevented) return;
    event.preventDefault();
    event.stopPropagation();
    this.cancelEdit();
  }

  saveEdit(): void {
    const note = this.note();
    const draft = draftToSave({ words: this.editWords(), topics: [...this.editTopics()] });
    const edit: NoteItemEdit = { id: note.id, content: draft.words, topics: draft.topics };
    let changed = draftChanged(draft, draftForNote(note.content, this.topicNames()));

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

    if (!changed) {
      this.editing.set(false);
      return;
    }
    if (this.saving()) return;
    this.savingNote = note;
    this.saving.set(true);
    this.edited.emit(edit);
  }

  toggleSelected(checked: boolean): void {
    this.selectedChange.emit(checked);
  }
}
