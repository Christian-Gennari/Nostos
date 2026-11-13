import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { editionChoices } from '../../book-detail/edition-switch-options';
import { BOOK_FORMAT_LABEL, bookFormatConcept, type BookFormatConcept, UNKNOWN_AUTHOR } from '../../core/book-format';
import { Note, NoteSearchHit, isPassage } from '../../core/dtos/note.dtos';
import { Book as BookDto } from '../../core/services/books.service';
import { TopicDto } from '../../core/services/topics.service';
import { quotePen } from '../../reader/highlight-colours';
import { ButtonComponent } from '../../ui/button/button.component';
import { ChoiceRowComponent } from '../../ui/choice-row/choice-row.component';
import { DropdownComponent, type DropdownOption } from '../../ui/dropdown/dropdown.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import { InputDirective } from '../../ui/form-control/form-control.directive';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';
import { NOSTOS_MENU } from '../../ui/menu/menu.component';
import { NoteItemComponent } from '../../ui/note-item/note-item.component';
import { PassageViewerRequest } from '../../ui/passage-viewer/passage-viewer.service';
import { QuoteBlockComponent } from '../../ui/quote-block/quote-block.component';
import { SearchFieldComponent } from '../../ui/search-field/search-field.component';
import { TopicChipComponent } from '../../ui/topic-chip/topic-chip.component';
import { NO_TOPICS, NO_TOPICS_MATCH, TopicOptionComponent } from '../../ui/topic-option/topic-option.component';
import { buildReferenceRowMetaMap, formatSourceProvenance } from '../reference-preview';
import { hasMeaningfulNoteContent, hasMeaningfulSelectedText, sourceHumanLabel } from '../source-insertion.helpers';
import { sourceKey } from '../writing-touches.helpers';

export type ReferenceLoadState = 'idle' | 'loading' | 'loaded' | 'error';
export type ReferenceScrollKey = 'writing' | 'topics' | 'topicNotes' | 'books' | 'bookNotes';

@Component({
  selector: 'app-studio-reference-drawer',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ButtonComponent,
    IconButtonComponent,
    ChoiceRowComponent,
    DropdownComponent,
    InputDirective,
    NostosIconComponent,
    NoteItemComponent,
    NOSTOS_MENU,
    QuoteBlockComponent,
    SearchFieldComponent,
    TopicChipComponent,
    TopicOptionComponent,
    EmptyStateComponent,
    LoadingIndicatorComponent,
  ],
  templateUrl: './studio-reference-drawer.component.html',
  styleUrl: './studio-reference-drawer.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StudioReferenceDrawerComponent {
  readonly isOpen = input(false);
  readonly railLabel = input('Show reference');
  readonly referenceMode = input<'writing' | 'library'>('writing');
  readonly activeSidebarTab = input<'brain' | 'notes'>('brain');
  readonly hasActiveItem = input(false);
  readonly editorWordCount = input<number | null>(null);

  readonly inspectedSource = input<Note | null>(null);
  readonly inspectedSourceItem = input<NoteSearchHit | null>(null);
  readonly inspectionReturnLabel = input('');
  readonly topicMap = input<Map<string, TopicDto>>(new Map());
  readonly passageViewerOptions = input<Omit<PassageViewerRequest, 'noteId'> | null>(null);

  readonly keptSourcesState = input<ReferenceLoadState>('idle');
  readonly keptNotes = input<readonly Note[]>([]);
  readonly citedOnlyNotes = input<readonly Note[]>([]);
  readonly removedCitedCount = input(0);
  readonly touchedTopics = input<readonly string[]>([]);
  readonly keptNoteIds = input<ReadonlySet<string>>(new Set());
  readonly keepingNoteIds = input<ReadonlySet<string>>(new Set());
  readonly citedIdSet = input<ReadonlySet<string>>(new Set());
  readonly sourceLookup = input<ReadonlyMap<string, NoteSearchHit>>(new Map());
  readonly topicIdsByName = input<ReadonlyMap<string, string>>(new Map());

  readonly brainQuery = input('');
  readonly topicIndexState = input<ReferenceLoadState>('loading');
  readonly filteredTopics = input<readonly TopicDto[]>([]);
  readonly selectedTopicId = input<string | null>(null);
  readonly selectedTopicNotes = input<readonly Note[]>([]);
  readonly topicNotesState = input<ReferenceLoadState>('idle');

  readonly bookQuery = input('');
  readonly bookIndexState = input<ReferenceLoadState>('loading');
  readonly filteredBooks = input<readonly BookDto[]>([]);
  readonly selectedBookId = input<string | null>(null);
  readonly selectedEditionOptions = input<readonly DropdownOption[]>([]);
  readonly selectedBookNotes = input<readonly Note[]>([]);
  readonly bookNotesState = input<ReferenceLoadState>('idle');

  readonly collapseRail = output<void>();
  readonly referenceModeChanged = output<'writing' | 'library'>();
  readonly libraryTabChanged = output<'brain' | 'notes'>();
  readonly brainQueryChanged = output<string>();
  readonly bookQueryChanged = output<string>();
  readonly retryKeptSources = output<void>();
  readonly retryTopicIndex = output<void>();
  readonly retryTopicNotes = output<void>();
  readonly retryBookIndex = output<void>();
  readonly retryBookNotes = output<void>();
  readonly topicSelected = output<string>();
  readonly backToTopics = output<void>();
  readonly bookSelected = output<string>();
  readonly backToBooks = output<void>();
  readonly sourceInspected = output<Note>();
  readonly inspectedSourceClosed = output<void>();
  readonly sourceOpened = output<Note>();
  readonly passageRead = output<Note>();
  readonly quoteInserted = output<Note>();
  readonly noteInserted = output<Note>();
  readonly referenceInserted = output<Note>();
  readonly noteKept = output<string>();
  readonly keptSourceRemoved = output<string>();
  readonly topicActivated = output<string>();
  readonly scrollCaptured = output<{ key: ReferenceScrollKey; scrollTop: number }>();

  protected readonly quotePen = quotePen;
  protected readonly noTopicsMatch = NO_TOPICS_MATCH;
  protected readonly noTopics = NO_TOPICS;
  protected readonly formatLabel = BOOK_FORMAT_LABEL;
  protected readonly unknownAuthor = UNKNOWN_AUTHOR;

  readonly keptNotesMeta = computed(() => buildReferenceRowMetaMap(this.keptNotes()));
  readonly citedOnlyNotesMeta = computed(() => buildReferenceRowMetaMap(this.citedOnlyNotes()));
  readonly selectedTopicNotesMeta = computed(() => buildReferenceRowMetaMap(this.selectedTopicNotes()));
  readonly selectedBookNotesMeta = computed(() => buildReferenceRowMetaMap(this.selectedBookNotes()));

  readonly inspectedSourceProvenance = computed(() => {
    const source = this.inspectedSource();
    return source ? formatSourceProvenance(source) : '';
  });

  readonly backToTopicsAction = (): void => this.backToTopics.emit();
  readonly backToBooksAction = (): void => this.backToBooks.emit();

  protected captureReferenceScroll(event: Event): void {
    const element = event.target as HTMLElement;
    const key = element.dataset['referenceScroll'] as ReferenceScrollKey | undefined;
    if (key) {
      this.scrollCaptured.emit({ key, scrollTop: element.scrollTop });
    }
  }

  protected onKeepClick(noteId: string, event: Event): void {
    event.stopPropagation();
    this.noteKept.emit(noteId);
  }

  protected onRemoveKeptClick(noteId: string, event: Event): void {
    event.stopPropagation();
    this.keptSourceRemoved.emit(noteId);
  }

  protected isCited(noteId: string): boolean {
    return this.citedIdSet().has(sourceKey(noteId));
  }

  protected citedTopics(noteId: string): string[] {
    return this.isCited(noteId) ? (this.sourceLookup().get(sourceKey(noteId))?.topicNames ?? []) : [];
  }

  protected topicIdFor(name: string): string | null {
    return this.topicIdsByName().get(name.toLocaleLowerCase()) ?? null;
  }

  protected keepActionLabel(noteId: string): string {
    if (!this.hasActiveItem()) return 'Open a document to keep or insert sources';
    if (this.keepingNoteIds().has(noteId)) return 'Keeping in this document';
    if (this.keptNoteIds().has(noteId)) {
      return 'Already kept in this document; no change on click';
    }
    return 'Keep in this document';
  }

  protected sourceActionDisabledReason(): string | null {
    if (!this.hasActiveItem()) return 'Open a document to keep or insert sources';
    if (this.editorWordCount() === null) return 'The editor is not ready for insertion yet';
    return null;
  }

  protected isPassageSource(source: Note): boolean {
    return isPassage(source);
  }

  protected canInsertQuote(source: Note): boolean {
    if (isPassage(source)) return false;
    return hasMeaningfulSelectedText(source) && !!sourceHumanLabel(source);
  }

  protected canInsertNote(source: Note): boolean {
    return hasMeaningfulNoteContent(source) && !!sourceHumanLabel(source);
  }

  protected canInsertReference(source: Note): boolean {
    return !!sourceHumanLabel(source);
  }

  protected passageRowTitle(note: Note): string | null {
    return isPassage(note) ? note.passageTitle?.trim() || 'Passage' : null;
  }

  protected bookEditions(book: BookDto) {
    return editionChoices(book);
  }

  protected bookFormat(book: BookDto): BookFormatConcept {
    return bookFormatConcept(book.type, book.fileName);
  }
}
