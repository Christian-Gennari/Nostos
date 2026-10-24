import {
  Component,
  inject,
  OnInit,
  AfterViewInit,
  signal,
  effect,
  computed,
  DestroyRef,
  untracked,
  ElementRef,
  ViewChild,
  ChangeDetectionStrategy,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { DragDropModule } from '@angular/cdk/drag-drop';
import { catchError, debounceTime, distinctUntilChanged, firstValueFrom, forkJoin, map, of, switchMap } from 'rxjs';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { marked } from 'marked';

import { WritingsService } from '../core/services/writings.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService, TopicDto, NoteContextDto } from '../core/services/topics.service';
import { BooksService, Book as BookDto } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { RecentItemsService } from '../ui/command-palette/recent-items.service';
import { NoteItemComponent } from '../ui/note-item/note-item.component';
import { noteAsItem } from '../ui/note-item/note-item.helpers';
import { WritingDto, WritingContentDto, WritingSourceDto } from '../core/dtos/writing.dtos';
import { Note, NoteSearchHit, Passage, isPassage } from '../core/dtos/note.dtos';
import { PassageViewerRequest, PassageViewerService } from '../ui/passage-viewer/passage-viewer.service';
import {
  MarkdownEditorComponent,
  MarkdownEditorTransientState,
  SourceSuggestionState,
} from '../ui/markdown-editor/markdown-editor.component';
import { PaletteAnchor, SourcePaletteComponent } from './source-palette/source-palette.component';
import { PaletteChoice } from './source-palette/source-palette.helpers';
import { StudioPageSettingsComponent } from './page-settings/studio-page-settings.component';
import { StudioPreferencesService } from './page-settings/studio-preferences.service';
import { CitableSource, CitationService, citationLocator } from './citations/citation.service';
import { bookToCslItem, missingForStyle, missingPhrase } from './citations/csl-item';
import { CitationRole, citationMarkdown, citedSourceIds } from '../ui/markdown-editor/citation.extension';
import { removedCitedIds, sourceKey, touchedTopics } from './writing-touches.helpers';
import { StyledInsertion, bibliographyMarkdown, styledNote, styledQuote, styledReference } from './citations/styled-insertion';
import { FlatTreeComponent } from '../ui/flat-tree/flat-tree.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { ButtonComponent } from '../ui/button/button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { NO_TOPICS_MATCH } from '../ui/topic-option/topic-option.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { QuoteBlockComponent } from '../ui/quote-block/quote-block.component';
import { TopicChipComponent } from '../ui/topic-chip/topic-chip.component';
import { quotePen } from '../reader/highlight-colours';
import { NOSTOS_MENU } from '../ui/menu/menu.component';
import { ExportDialogComponent } from './export/export-dialog.component';
import { hasExportableContent } from './export/export-resolve';
import { noteWikilinksToText } from '../ui/pipes/note-format.pipe';
import {
  buildNoteMarkdown,
  buildQuoteMarkdown,
  buildReferenceMarkdown,
  hasMeaningfulNoteContent,
  hasMeaningfulSelectedText,
  sourceHumanLabel,
} from './source-insertion.helpers';
import {
  hasStudioSourceReturnState,
  readerReturnOriginState,
  readStudioSourceReturnSnapshot,
  StudioSourceReturnSnapshotV1,
  withStudioSourceReturnState,
  withoutStudioSourceReturnState,
} from '../core/navigation/studio-reader-navigation';
import { SearchFieldComponent } from '../ui/search-field/search-field.component';
import { BOOK_FORMAT_LABEL, bookFormatConcept, UNKNOWN_AUTHOR, type BookFormatConcept } from '../core/book-format';
import { ChoiceRowComponent } from '../ui/choice-row/choice-row.component';

/** The writing the Studio opens on next time. */
const STUDIO_LAST_WRITING_KEY = 'nostos.studio.lastWritingId';
/** Where the caret and scroll were in recently left writings, newest first. */
const STUDIO_CARETS_KEY = 'nostos.studio.carets';
const STUDIO_CARETS_MAX = 50;
const UNTITLED_DOCUMENT = 'Untitled document';
/** Average adult silent-reading pace, for the reading-time stat. */
const READING_WORDS_PER_MINUTE = 238;
const STUDIO_COMPACT_WIDTH = 900;
const STUDIO_COMPACT_HEIGHT = 500;

function isStudioCompactViewport(): boolean {
  return window.innerWidth <= STUDIO_COMPACT_WIDTH || window.innerHeight <= STUDIO_COMPACT_HEIGHT;
}

type ReferenceLoadState = 'idle' | 'loading' | 'loaded' | 'error';
type ReferenceScrollKey = 'writing' | 'topics' | 'topicNotes' | 'books' | 'bookNotes';

function writeStudioValue(key: string, value: string | null): void {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    // A convenience only: the Studio works the same without storage.
  }
}

function readStudioValue(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

/**
 * The name a writing gets from its own first line while it has none of its
 * own: Markdown markers off, at most 80 characters, cut at a word.
 */
export function titleFromMarkdown(markdown: string): string {
  const line = (markdown ?? '')
    .split('\n')
    .map((l) =>
      l
        // A live citation reads as its words; a footnote marker as nothing (#1157).
        .replace(/\[((?:\\.|[^\\\]])*)\]\(nostos:cite\/[^)\s]+\)/g, '$1')
        .replace(/\[\^\d{1,4}\]:?/g, '')
        .replace(/^\s*(#{1,6}\s+|>\s*|[-*+]\s+|\d+[.)]\s+)/, '')
        .replace(/\\([\\*_`[\]])/g, '$1')
        .replace(/[*_`~]/g, '')
        .trim(),
    )
    // A line of only markers or punctuation ("#", "---") names nothing.
    .find((l) => /[\p{L}\p{N}]/u.test(l));
  if (!line) return '';
  if (line.length <= 80) return line;
  const cut = line.slice(0, 80);
  const space = cut.lastIndexOf(' ');
  return (space > 40 ? cut.slice(0, space) : cut).trim() + '…';
}

/** The footnote a styled insertion brings, as the editor takes it. */
function footnoteOf(styled: StyledInsertion | null): { number: number; markdown: string } | null {
  return styled?.footnote ? { number: styled.footnote.number, markdown: styled.footnote.text } : null;
}

interface RememberedCaret {
  id: string;
  state: MarkdownEditorTransientState;
}

function readCarets(): RememberedCaret[] {
  try {
    const parsed = JSON.parse(readStudioValue(STUDIO_CARETS_KEY) ?? '[]') as unknown;
    return Array.isArray(parsed)
      ? parsed.filter((e): e is RememberedCaret => !!e && typeof e.id === 'string' && !!e.state)
      : [];
  } catch {
    return [];
  }
}

/** Bounded: a browser keeps the carets of its 50 most recently left writings. */
function writeCarets(carets: RememberedCaret[]): void {
  writeStudioValue(STUDIO_CARETS_KEY, JSON.stringify(carets.slice(0, STUDIO_CARETS_MAX)));
}


@Component({
  selector: 'app-writing-studio',
  standalone: true,
  imports: [
    ChoiceRowComponent,
    SearchFieldComponent,
    CommonModule,
    FormsModule,
    NostosIconComponent,
    DragDropModule,
    FlatTreeComponent,
    NoteItemComponent,
    MarkdownEditorComponent,
    SourcePaletteComponent,
    StudioPageSettingsComponent,
    IconButtonComponent,
    ButtonComponent,
    InputDirective,
    ConfirmModal,
    NOSTOS_MENU,
    ExportDialogComponent,
    QuoteBlockComponent,
    TopicChipComponent,
  ],
  templateUrl: './writing-studio.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrls: ['./writing-studio.component.css'],
})
export class WritingStudio implements OnInit, AfterViewInit {
  private writingsService = inject(WritingsService);
  private toast = inject(ToastService);
  private topicsService = inject(TopicsService);
  private booksService = inject(BooksService);
  private notesService = inject(NotesService);
  private passageViewer = inject(PassageViewerService);
  private recent = inject(RecentItemsService);
  private route = inject(ActivatedRoute, { optional: true });
  private router = inject(Router, { optional: true });

  private destroyRef = inject(DestroyRef);
  private hostElement = inject(ElementRef<HTMLElement>);
  @ViewChild('editor') private markdownEditor?: MarkdownEditorComponent;

  private writingHandoffSubscription: { unsubscribe(): void } | null = null;
  private requestedWritingId: string | null = null;
  private handledWritingHandoffId: string | null = null;
  private handoffOpenGeneration = 0;
  private writingTreeLoaded = false;

  // Autosaves and explicit "open source" flushes share one serialized lane.
  // A newer generation supersedes stale UI responses, while queue ordering
  // guarantees an older request can never land on the server after the flush.
  private saveGeneration = 0;
  private saveQueue: Promise<void> = Promise.resolve();
  private pendingSaveCount = 0;

  // Reader excursions are represented only by short-lived browser history
  // state. Nothing here is persisted to Writing content or the backend.
  private pendingSourceReturnSnapshot: StudioSourceReturnSnapshotV1 | null = null;
  private pendingInspectedSourceRestore: {
    id: string;
    mode: 'writing' | 'library';
    tab: 'brain' | 'notes';
  } | null = null;

  private readonly initialCompactMode = isStudioCompactViewport();
  isCompact = signal(this.initialCompactMode);
  // The page comes first: both rails start closed. The file list is always a
  // slide-over. Reference sits beside the page on a wide screen, so a source
  // stays in view while it is quoted, and slides over on a compact one. One
  // behaviour, no pin: a choice between the two was a second header button
  // nobody could read.
  showFileSidebar = signal(false);
  showBrainSidebar = signal(false);

  /** A blank page with no writing behind it yet; the first words create one. */
  blankPage = signal(false);
  /** The open writing takes its name from its first line until it is renamed. */
  private autoTitle = false;
  private creatingFromBlank = false;
  /** Which blank page is showing; a new one (New, or leaving) bumps it. */
  private blankGeneration = 0;
  /** What was last typed on each blank page, kept if it is left mid-create. */
  private readonly blankPageTexts = new Map<number, string>();
  /** After a failed create, the next attempt waits until this time. */
  private blankRetryAt = 0;
  /** Nudges the autosave once a blank page has become a writing. */
  private readonly saveNudge = signal(0);

  /** While words are going onto the page, the chrome around it fades away. */
  isTyping = signal(false);
  private lastPointer: { x: number; y: number } | null = null;

  /** Which statistic the corner shows; clicking it cycles them. */
  statMode = signal<'words' | 'characters' | 'reading'>('words');
  editorCharacterCount = signal(0);

  /** The keyboard shortcut sheet. */
  shortcutsOpen = signal(false);
  private compactDrawerCaret: MarkdownEditorTransientState | null = null;

  activeSidebarTab = signal<'brain' | 'notes'>('brain');

  /** Reference surface top-level mode: "For this writing" vs "Library" (issue #491) */
  referenceMode = signal<'writing' | 'library'>('writing');

  /** Kept source notes for the active writing document */
  keptSources = signal<WritingSourceDto[]>([]);
  keptSourcesState = signal<ReferenceLoadState>('idle');

  /** Note IDs currently being kept (in-flight addSource request) to prevent concurrent races */
  keepingNoteIds = signal<Set<string>>(new Set());

  /** Set of kept note IDs for fast lookup */
  keptNoteIds = computed(() => new Set(this.keptSources().map((s) => s.id)));

  /** Source currently being inspected. Browsing never mutates the writing. */
  inspectedSource = signal<Note | null>(null);

  /** The inspected source as its note item shows it. */
  readonly inspectedSourceItem = computed(() => {
    const source = this.inspectedSource();
    return source ? noteAsItem(source) : null;
  });

  /**
   * Kept sources mapped to Note shape for the panel's rows,
   * in addedAt ascending order (chronological accretion).
   */
  keptNotes = computed<Note[]>(() => {
    return [...this.keptSources()]
      .sort((a, b) => new Date(a.addedAt).getTime() - new Date(b.addedAt).getTime())
      .map((s) => ({
        id: s.id,
        bookId: s.bookId,
        content: s.content,
        selectedText: s.selectedText ?? undefined,
        cfiRange: s.cfiRange ?? undefined,
        createdAt: s.createdAt,
        bookTitle: s.bookTitle ?? undefined,
        bookAuthor: s.bookAuthor ?? undefined,
        sourceAnchorKind: s.sourceAnchorKind ?? undefined,
        sourceAnchorValue: s.sourceAnchorValue ?? undefined,
        anchorVerified: s.anchorVerified ?? undefined,
        kind: s.kind,
        passageTitle: s.passageTitle,
        passageEnd: s.passageEnd,
        passageWordCount: s.passageWordCount,
      }));
  });

  rootItems = signal<WritingDto[]>([]);
  activeItem = signal<WritingContentDto | null>(null);

  // --- What the writing touches (issue #1162) ---
  // Worked out from what the writing cites, read live from the editor's
  // document; never stored on the writing, and nothing here adds a topic.

  /** Note ids the open writing cites (lower-case, once each, in order). */
  readonly citedIds = computed(() => (this.activeItem() ? citedSourceIds(this.editorText()) : []));
  private readonly citedIdSet = computed(() => new Set(this.citedIds()));
  /** Kept and cited sources as the server has them now, with their topics. */
  readonly sourceLookup = signal<ReadonlyMap<string, NoteSearchHit>>(new Map());
  /** The ids `sourceLookup` answers for: an id asked about and absent was removed. */
  private readonly sourceLookedUp = signal<ReadonlySet<string>>(new Set());
  /** Every id worth looking up, as one stable key, so typing alone never refetches. */
  private readonly sourceLookupKey = computed(() =>
    [...new Set([...this.keptSources().map((s) => sourceKey(s.id)), ...this.citedIds()])]
      .filter(Boolean)
      .sort()
      .join(','));
  /** The topics the cited sources carry, for the panel's one Touches line. */
  readonly touchedTopics = computed(() => touchedTopics(this.citedIds(), this.sourceLookup()));
  /** Cited sources whose note (or book) was deleted; the citation text stays. */
  readonly removedCitedCount = computed(
    () => removedCitedIds(this.citedIds(), this.sourceLookup(), this.sourceLookedUp()).length);
  /** Sources cited without being kept (a citation pasted from another writing). */
  readonly citedOnlyNotes = computed<Note[]>(() => {
    const kept = new Set(this.keptSources().map((s) => sourceKey(s.id)));
    const lookup = this.sourceLookup();
    return this.citedIds()
      .filter((id) => !kept.has(id) && lookup.has(id))
      .map((id) => hitToNote(lookup.get(id)!));
  });

  /** Topic ids by folded name, so a source's topic pill can open its page. */
  private readonly topicIdsByName = computed(
    () => new Map(this.topics().map((topic) => [topic.name.toLocaleLowerCase(), topic.id])));

  /** The Brain page of a cited source's topic, or null while topics are not loaded. */
  topicIdFor(name: string): string | null {
    return this.topicIdsByName().get(name.toLocaleLowerCase()) ?? null;
  }

  /**
   * A topic pill opens that topic's page in the Brain. The writing is saved
   * first and the Studio's place kept, as opening a source does, so Back
   * returns to the same caret.
   */
  async openTopic(name: string): Promise<void> {
    const id = this.topicIdFor(name);
    if (!this.router || !id) return;
    const active = this.activeItem();
    if (active) {
      if (!(await this.flushActiveWritingBeforeSource(active))) return;
      this.attachSourceReturnSnapshot(this.captureSourceReturnSnapshot(active.id));
    }
    await this.navigateFromStudio(['/brain/topics', id]);
  }

  /** True when the open writing cites this source. */
  isCited(noteId: string): boolean {
    return this.citedIdSet().has(sourceKey(noteId));
  }

  /** A cited source's topics, as small pills under its row; none for an uncited one. */
  citedTopics(noteId: string): string[] {
    return this.isCited(noteId) ? (this.sourceLookup().get(sourceKey(noteId))?.topicNames ?? []) : [];
  }


  editingId = signal<string | null>(null);
  /** Why the last rename was refused, said under the field (issue #1209). */
  readonly renameError = signal<string | null>(null);
  readonly renameBusy = signal(false);

  editorText = signal('');
  editorTitle = signal('');
  saveStatus = signal<'Saved' | 'Saving…' | 'Unsaved'>('Saved');

  // Zen (focus) mode — issue #49. Session-only: never persisted.
  isZen = signal(false);

  /** The page's own settings (issue #1041): type, measure, behaviour. */
  readonly pagePrefs = inject(StudioPreferencesService);
  private readonly citations = inject(CitationService);
  pageSettingsOpen = signal(false);
  /** The export dialog (issue #1164). */
  exportOpen = signal(false);
  /**
   * Why "Export…" is unavailable, shown under it; null when it can open. An
   * empty writing in a folder can still open it, to export the folder.
   */
  readonly exportBlockedReason = computed(() => {
    const item = this.activeItem();
    if (!item) return 'Nothing to export yet';
    if (hasExportableContent(this.editorText())) return null;
    const parent = this.rootItems().find((candidate) => candidate.id === item.id)?.parentId;
    return parent ? null : 'This writing is empty';
  });
  /** Where the settings open: the page, or the Citations list at a book missing details (#1157). */
  readonly pageSettingsStart = signal<{ view: 'main' | 'citations'; book: string | null }>({ view: 'main', book: null });
  /** The books this writing cites, for the Citations list's missing details. */
  readonly citedBookIds = signal<string[]>([]);
  /** The Aa button: the settings popover hangs under it (#1060). */
  @ViewChild('pageSettingsButton', { read: ElementRef }) pageSettingsButton?: ElementRef<HTMLElement>;

  /** Typewriter mode: keep the caret line centered. A page setting now. */
  readonly typewriter = computed(() => this.pagePrefs.preferences().typewriter);

  toggleTypewriter(): void {
    this.pagePrefs.update({ typewriter: !this.typewriter() });
  }

  /** A chosen line length, in characters of the page's own face. */
  readonly textMeasure = computed(() => {
    const width = this.pagePrefs.preferences().textWidth;
    return width === null ? null : `calc(${width}ch + 2 * var(--manuscript-gutter))`;
  });

  /** Opens the export dialog; one layer over the page at a time. */
  openExport(): void {
    if (this.exportBlockedReason()) return;
    this.pageSettingsOpen.set(false);
    this.exportOpen.set(true);
  }

  togglePageSettings(): void {
    const open = !this.pageSettingsOpen();
    if (open) {
      this.pageSettingsStart.set({ view: 'main', book: null });
      this.citedBookIds.set(this.writingBooks());
    }
    // One layer over the page at a time: the settings replace a slide-over.
    if (open && this.drawerOpen()) {
      this.showFileSidebar.set(false);
      if (this.isDrawer('reference')) this.showBrainSidebar.set(false);
    }
    this.pageSettingsOpen.set(open);
    if (!open) this.markdownEditor?.focus();
  }

  /**
   * Closed with Esc: back to the page. A press outside closes it without this,
   * so focus stays where the press went (and no phone keyboard opens).
   */
  closePageSettings(): void {
    if (!this.pageSettingsOpen()) return;
    this.pageSettingsOpen.set(false);
    this.markdownEditor?.focus();
  }

  filesRailLabel(): string {
    return this.showFileSidebar() ? 'Hide files' : 'Show files';
  }

  referenceRailLabel(): string {
    return this.showBrainSidebar() ? 'Hide reference' : 'Show reference';
  }

  toggleFilesRail(): void {
    this.setRailOpen('files', !this.showFileSidebar());
  }

  toggleReferenceRail(): void {
    this.setRailOpen('reference', !this.showBrainSidebar());
  }

  toggleFilesRailFromHeading(): void {
    this.setRailOpen('files', false, true);
  }

  toggleReferenceRailFromHeading(): void {
    this.setRailOpen('reference', false, true);
  }

  openFilesDrawer(): void {
    this.setRailOpen('files', true);
  }

  /** Whether a rail slides over the page (true) or sits beside it (Reference on a wide screen). */
  isDrawer(rail: 'files' | 'reference'): boolean {
    return this.isCompact() || rail === 'files';
  }

  /** Whether an open slide-over is dimming the page. */
  drawerOpen(): boolean {
    return (
      (this.showFileSidebar() && this.isDrawer('files')) ||
      (this.showBrainSidebar() && this.isDrawer('reference'))
    );
  }

  private setRailOpen(
    rail: 'files' | 'reference',
    open: boolean,
    fromHeading = false,
    restoreCaretOnClose = true,
  ): void {
    const isFiles = rail === 'files';

    if (this.isDrawer(rail)) {
      if (open) {
        this.captureCompactDrawerCaret();
        // One slide-over at a time; a Reference column stays.
        if (isFiles && this.isDrawer('reference')) this.showBrainSidebar.set(false);
        if (!isFiles) this.showFileSidebar.set(false);
        if (isFiles) this.showFileSidebar.set(true);
        else this.showBrainSidebar.set(true);
        this.focusDrawerCollapse(rail);
        return;
      }

      if (isFiles) this.showFileSidebar.set(false);
      else this.showBrainSidebar.set(false);
      this.focusHeaderRailToggle(rail);
      if (restoreCaretOnClose) this.restoreCompactDrawerCaret();
      return;
    }

    this.showBrainSidebar.set(open);
    // The heading control is removed with its rail when it collapses, so return
    // focus to the persistent editor-header toggle in that case.
    if (!open && fromHeading) this.focusHeaderRailToggle(rail);
  }

  private focusHeaderRailToggle(rail: 'files' | 'reference'): void {
    const selector = rail === 'files' ? '.files-toggle' : '.reference-toggle';
    const toggle = this.hostElement.nativeElement.querySelector(selector) as HTMLButtonElement | null;
    toggle?.focus();
  }

  private focusDrawerCollapse(rail: 'files' | 'reference'): void {
    const focus = () => {
      const selector = rail === 'files' ? '.files-rail-collapse' : '.reference-rail-collapse';
      const collapse = this.hostElement.nativeElement.querySelector(selector) as HTMLButtonElement | null;
      collapse?.focus();
    };
    if (typeof window.requestAnimationFrame === 'function') window.requestAnimationFrame(focus);
    else window.setTimeout(focus, 0);
  }

  private captureCompactDrawerCaret(): void {
    this.compactDrawerCaret = this.markdownEditor?.captureTransientState() ?? null;
  }

  private restoreCompactDrawerCaret(): void {
    const state = this.compactDrawerCaret;
    this.compactDrawerCaret = null;
    if (!state || !this.activeItem() || !this.markdownEditor) return;
    void this.markdownEditor.restoreTransientState(state, this.editorText());
  }

  /** Element that had focus when zen was entered; restored on exit. */
  private zenFocusReturn: HTMLElement | null = null;

  wordCount = computed(() => {
    const text = this.editorText().trim();
    return text ? text.split(/\s+/).length : 0;
  });

  /**
   * Word count reported by the editor (authoritative for the rendered
   * document). Falls back to the markdown-derived count until
   * the editor boots and emits its first count.
   */
  editorWordCount = signal<number | null>(null);
  displayWordCount = computed(() => this.editorWordCount() ?? this.wordCount());

  // Brain / Topics State
  brainQuery = signal('');
  topics = signal<TopicDto[]>([]);
  topicIndexState = signal<ReferenceLoadState>('loading');
  selectedTopicId = signal<string | null>(null);
  selectedTopicNotes = signal<Note[]>([]);
  topicNotesState = signal<ReferenceLoadState>('idle');

  // Books / Notes State
  books = signal<BookDto[]>([]);
  protected readonly formatLabel = BOOK_FORMAT_LABEL;
  protected readonly unknownAuthor = UNKNOWN_AUTHOR;
  protected bookFormat(book: BookDto): BookFormatConcept {
    return bookFormatConcept(book.type, book.fileName);
  }
  bookIndexState = signal<ReferenceLoadState>('loading');
  bookQuery = signal('');
  selectedBookId = signal<string | null>(null);
  selectedBookNotes = signal<Note[]>([]);
  bookNotesState = signal<ReferenceLoadState>('idle');

  /** Scroll positions belong to the parent surface and survive its child view. */
  private referenceScrollPositions: Record<ReferenceScrollKey, number> = {
    writing: 0,
    topics: 0,
    topicNotes: 0,
    books: 0,
    bookNotes: 0,
  };

  /**
   * Label for the surface an inspected source drills down from.
   * Inspection is a temporary child state: closing it returns to the exact
   * kept-source / topic / book context rather than to a generic Library root.
   */
  inspectionReturnLabel = computed(() => {
    if (this.referenceMode() === 'writing') return 'For this writing';

    if (this.activeSidebarTab() === 'brain') {
      const topicId = this.selectedTopicId();
      return (
        this.topics().find((topic) => topic.id === topicId)?.name ??
        'Topic notes'
      );
    }

    const bookId = this.selectedBookId();
    return this.books().find((book) => book.id === bookId)?.title ?? 'Book notes';
  });

  // Computed Map for highlighting topics in notes
  topicMap = computed(() => {
    const map = new Map<string, TopicDto>();
    for (const c of this.topics()) {
      map.set(c.name.toLowerCase(), c);
    }
    return map;
  });

  filteredTopics = computed(() => {
    const q = this.brainQuery().toLowerCase();
    const list = this.topics();
    if (!q) return list;
    return list.filter((c) => c.name.toLowerCase().includes(q));
  });

  filteredBooks = computed(() => {
    const q = this.bookQuery().toLowerCase();
    const list = this.books();
    if (!q) return list;
    return list.filter(
      (b) => b.title.toLowerCase().includes(q) || (b.author && b.author.toLowerCase().includes(q)),
    );
  });

  constructor() {
    // The kept and cited sources, with their topics, once the ids settle.
    toObservable(this.sourceLookupKey)
      .pipe(
        debounceTime(300),
        distinctUntilChanged(),
        switchMap((key) => {
          const ids = key ? key.split(',') : [];
          if (!ids.length) return of({ ids, hits: [] as NoteSearchHit[] });
          const chunks: string[][] = [];
          for (let i = 0; i < ids.length; i += 200) chunks.push(ids.slice(i, i + 200));
          return forkJoin(chunks.map((chunk) => this.notesService.getMany(chunk))).pipe(
            map((pages) => ({ ids, hits: pages.flat() })),
            // A failed lookup shows nothing rather than calling sources removed.
            catchError(() => of(null)),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((answer) => {
        if (!answer) return;
        this.sourceLookup.set(new Map(answer.hits.map((hit) => [sourceKey(hit.id), hit])));
        this.sourceLookedUp.set(new Set(answer.ids));
      });
    const onResize = () => {
      const compact = isStudioCompactViewport();

      if (compact === this.isCompact()) return;

      // The file list is a slide-over in both modes, so crossing the
      // breakpoint leaves it as it is (a first layout can report a narrow
      // viewport and then widen; that must not shut a drawer just opened).
      // Only an open Reference column changes kind: it closes on a compact
      // screen rather than turning into a slide-over that covers the page.
      const referenceWasColumn = !this.isCompact() && this.showBrainSidebar();
      this.isCompact.set(compact);
      if (compact) {
        if (referenceWasColumn) this.showBrainSidebar.set(false);
      } else {
        // A drawer still open keeps the caret it will hand back on closing.
        if (!this.drawerOpen()) this.compactDrawerCaret = null;
      }
    };

    window.addEventListener('resize', onResize);
    this.destroyRef.onDestroy(() => window.removeEventListener('resize', onResize));

    // Zen mode: Esc exits focus mode; listener removed on destroy.
    window.addEventListener('keydown', this.onKeyDown);
    window.addEventListener('pointermove', this.onPointerMove, { passive: true });
    window.addEventListener('pointerdown', this.onPointerDown, { passive: true });
    window.addEventListener('pagehide', this.rememberCaret);
    this.destroyRef.onDestroy(() => {
      this.rememberCaret();
      window.removeEventListener('keydown', this.onKeyDown);
      window.removeEventListener('pointermove', this.onPointerMove);
      window.removeEventListener('pointerdown', this.onPointerDown);
      window.removeEventListener('pagehide', this.rememberCaret);
      document.body.classList.remove('nostos-studio-typing');
      this.writingHandoffSubscription?.unsubscribe();
      // Never leave the body class behind if the component is torn down mid-zen.
      document.body.classList.remove('nostos-zen');
    });

    effect((onCleanup) => {
      const text = this.editorText();
      const title = this.editorTitle();
      this.saveNudge();
      const item = untracked(() => this.activeItem());

      if (!item) return;

      if (text === item.content && title === item.name) {
        this.saveStatus.set('Saved');
        return;
      }

      this.saveStatus.set('Unsaved');
      const generation = ++this.saveGeneration;

      const timer = setTimeout(() => {
        void this.queueWritingSave(item, title, text, generation).catch(() => {
          // Autosave failure is represented by saveStatus. Explicit source
          // navigation handles the same failure with user feedback below.
        });
      }, 2000);

      onCleanup(() => clearTimeout(timer));
    });
  }

  ngOnInit() {
    this.prepareSourceReturnRestore();
    this.watchWritingHandoff();
    this.loadTree();
    this.loadBrain();
    this.loadBooks();

    if (this.isCompact()) {
      this.showFileSidebar.set(false);
      this.showBrainSidebar.set(false);
    }
  }

  ngAfterViewInit(): void {
    this.tryRestoreEditorSourceReturn();
  }

  closeSidebars() {
    if (!this.drawerOpen()) return;
    const returnTo: 'files' | 'reference' =
      this.showFileSidebar() && this.isDrawer('files') ? 'files' : 'reference';
    this.showFileSidebar.set(false);
    if (this.isDrawer('reference')) this.showBrainSidebar.set(false);
    this.focusHeaderRailToggle(returnTo);
    this.restoreCompactDrawerCaret();
  }

  // --- The page first (issue #1040) ---

  /** The editor reported new Markdown: keep it, name an untitled writing, fade the chrome. */
  onEditorChange(markdown: string): void {
    this.editorText.set(markdown);
    this.setTyping(true);
    if (this.autoTitle) {
      const title = titleFromMarkdown(markdown) || UNTITLED_DOCUMENT;
      if (title !== this.editorTitle()) this.editorTitle.set(title);
    }
    if (this.blankPage() && !this.activeItem()) {
      this.blankPageTexts.set(this.blankGeneration, markdown);
      if (markdown.trim() && Date.now() >= this.blankRetryAt) this.createFromBlankPage();
    }
  }

  /** New, or no writing to return to: an empty page with the caret in it. */
  startBlankPage(): void {
    this.rememberCaret();
    // Whatever was typed in the open writing in the last two seconds is saved,
    // not dropped with the debounce the switch would cancel.
    const open = this.activeItem();
    if (open) void this.flushActiveWritingBeforeSource(open);
    this.blankGeneration++;
    this.blankRetryAt = 0;
    this.activeItem.set(null);
    this.blankPage.set(true);
    this.autoTitle = true;
    this.editorWordCount.set(null);
    this.editorText.set('');
    this.editorTitle.set('');
    this.keptSources.set([]);
    this.inspectedSource.set(null);
    if (this.showFileSidebar() && this.isDrawer('files')) {
      this.showFileSidebar.set(false);
      this.compactDrawerCaret = null;
    }
    this.focusPageSoon();
  }

  /**
   * The first words on a blank page create its writing. Nothing is created
   * for a page left empty, so abandoned blank pages leave no files behind.
   */
  private createFromBlankPage(): void {
    if (this.creatingFromBlank) return;
    this.creatingFromBlank = true;
    const generation = this.blankGeneration;
    // Named on its first save, from its first line: at the first keystroke
    // the line is a single letter.
    this.writingsService.create({ name: UNTITLED_DOCUMENT, type: 'Document', parentId: null }).subscribe({
      next: (created) => {
        this.creatingFromBlank = false;
        if (generation !== this.blankGeneration || !this.blankPage()) {
          // The page was left while this was in flight: its words still
          // belong in the writing they created, not nowhere.
          this.saveLeftBlankPage(created.id, generation);
          return;
        }
        this.blankPageTexts.delete(generation);
        // Empty server content: the nudge makes the save lane store the page.
        this.activeItem.set({ id: created.id, name: created.name, content: '', updatedAt: created.updatedAt });
        this.editorTitle.set(titleFromMarkdown(this.editorText()) || created.name);
        this.blankPage.set(false);
        this.saveNudge.update((n) => n + 1);
        writeStudioValue(STUDIO_LAST_WRITING_KEY, created.id);
        this.recent.record({ kind: 'document', id: created.id, label: created.name, sub: 'Document' });
        this.loadTree();
        this.loadKeptSources(created.id);
      },
      error: () => {
        this.creatingFromBlank = false;
        // One message, then a pause: not a request and a toast per keystroke.
        const first = this.blankRetryAt === 0;
        this.blankRetryAt = Date.now() + 5000;
        if (first) this.toast.error('Could not create a document. Your words are still on the page.');
      },
    });
  }

  /** Stores what a blank page held when it was left before its writing existed. */
  private saveLeftBlankPage(id: string, generation: number): void {
    const content = this.blankPageTexts.get(generation) ?? '';
    this.blankPageTexts.delete(generation);
    const name = titleFromMarkdown(content) || UNTITLED_DOCUMENT;
    this.writingsService.update(id, { name, content }).subscribe({
      next: () => this.loadTree(),
      error: () => this.toast.error('Could not save the page you left'),
    });
  }

  /** Opens where the writer left off: the last writing, or a blank page. */
  private openStartingPage(): void {
    const lastId = readStudioValue(STUDIO_LAST_WRITING_KEY);
    const last = lastId ? this.rootItems().find((i) => i.id === lastId && i.type === 'Document') : null;
    if (last) this.openWritingDocument(last.id, false);
    else this.startBlankPage();
  }

  /** Remembers the caret and scroll of the open writing for next time. */
  private readonly rememberCaret = () => {
    const item = this.activeItem();
    const state = this.markdownEditor?.captureTransientState();
    if (!item || !state) return;
    const rest = readCarets().filter((entry) => entry.id !== item.id);
    writeCarets([{ id: item.id, state }, ...rest]);
  };

  private restoreRememberedCaret(item: WritingContentDto): void {
    const state = readCarets().find((entry) => entry.id === item.id)?.state ?? null;
    const editor = () => this.markdownEditor;
    const apply = () => {
      if (this.activeItem()?.id !== item.id) return;
      if (state) void editor()?.restoreTransientState(state, item.content);
      this.focusPageSoon();
    };
    setTimeout(apply);
  }

  private focusPageSoon(): void {
    setTimeout(() => {
      if (this.drawerOpen()) return;
      this.markdownEditor?.focus();
    });
  }

  private setTyping(typing: boolean): void {
    if (typing === this.isTyping()) return;
    this.isTyping.set(typing);
    document.body.classList.toggle('nostos-studio-typing', typing);
    if (!typing) this.lastPointer = null;
  }

  /** A real pointer movement (not the jitter of a resting hand) brings the chrome back. */
  private readonly onPointerMove = (event: PointerEvent) => {
    if (!this.isTyping()) return;
    const last = this.lastPointer;
    this.lastPointer = { x: event.clientX, y: event.clientY };
    if (last && Math.hypot(event.clientX - last.x, event.clientY - last.y) > 6) this.setTyping(false);
  };

  /** A tap or click is deliberate: on touch screens it is how the chrome comes back. */
  private readonly onPointerDown = () => this.setTyping(false);

  undoOnPage(): void {
    this.markdownEditor?.undo();
  }

  /** Words, characters, reading time: the corner stat cycles on click. */
  cycleStat(): void {
    const order = ['words', 'characters', 'reading'] as const;
    this.statMode.set(order[(order.indexOf(this.statMode()) + 1) % order.length]);
  }

  statText(): string {
    const words = this.displayWordCount();
    switch (this.statMode()) {
      case 'characters': {
        const chars = this.editorCharacterCount();
        return `${chars} ${chars === 1 ? 'character' : 'characters'}`;
      }
      case 'reading': {
        const minutes = Math.max(1, Math.ceil(words / READING_WORDS_PER_MINUTE));
        return words === 0 ? '0 min read' : `${minutes} min read`;
      }
      default:
        return `${words} ${words === 1 ? 'word' : 'words'}`;
    }
  }

  // --- Zen (focus) mode — issue #49 ---

  private readonly onKeyDown = (event: KeyboardEvent) => {
    if (this.handleStudioShortcut(event)) return;
    if (event.key !== 'Escape') return;
    // Esc closes the topmost layer first, and only then brings the chrome back.
    // A layer inside (an open dropdown list) that already handled it wins.
    if (event.defaultPrevented) return;
    if (this.shortcutsOpen()) {
      this.shortcutsOpen.set(false);
      event.preventDefault();
      return;
    }
    if (this.pageSettingsOpen()) {
      this.closePageSettings();
      event.preventDefault();
      return;
    }
    if (this.isZen()) {
      this.exitZen();
      return;
    }
    if (event.defaultPrevented) return;

    if (this.showFileSidebar() && this.isDrawer('files')) {
      this.setRailOpen('files', false);
      event.preventDefault();
    } else if (this.showBrainSidebar() && this.isDrawer('reference')) {
      this.setRailOpen('reference', false);
      event.preventDefault();
    } else if (this.isTyping()) {
      this.setTyping(false);
    }
  };

  /**
   * Alt+M files, Alt+R Reference, Ctrl/Cmd+/ (or ? outside the page) the
   * shortcut sheet. Matched on the physical key, so a layout whose Alt types
   * characters still reaches them.
   */
  private handleStudioShortcut(event: KeyboardEvent): boolean {
    const mod = event.ctrlKey || event.metaKey;
    if (event.altKey && !mod && !event.shiftKey && (event.code === 'KeyM' || event.code === 'KeyR')) {
      event.preventDefault();
      if (event.code === 'KeyM') this.toggleFilesRail();
      else this.toggleReferenceRail();
      return true;
    }
    if (mod && !event.altKey && event.key === ',') {
      event.preventDefault();
      this.togglePageSettings();
      return true;
    }
    const target = event.target as HTMLElement | null;
    const typingIntoSomething = !!target?.closest?.('input, textarea, [contenteditable="true"]');
    if ((mod && event.key === '/') || (event.key === '?' && !mod && !typingIntoSomething)) {
      event.preventDefault();
      this.shortcutsOpen.update((open) => !open);
      return true;
    }
    return false;
  }

  enterZen() {
    if (this.isZen()) return;
    this.isZen.set(true);
    // Remember where focus was so it can be restored on exit (accessibility).
    this.zenFocusReturn =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    document.body.classList.add('nostos-zen');
  }

  exitZen() {
    if (!this.isZen()) return;
    this.isZen.set(false);
    document.body.classList.remove('nostos-zen');

    // Restore focus to the element that opened zen, falling back to the
    // (now visible again) zen toggle in the document-action cluster.
    const fallback = this.hostElement.nativeElement.querySelector(
      '.zen-toggle',
    ) as HTMLButtonElement | null;
    const target =
      this.zenFocusReturn && this.zenFocusReturn.isConnected ? this.zenFocusReturn : fallback;
    this.zenFocusReturn = null;
    target?.focus();
  }

  loadTree() {
    this.writingsService.list().subscribe({
      next: (items) => {
        this.rootItems.set(items);
        const firstLoad = !this.writingTreeLoaded;
        this.writingTreeLoaded = true;
        const requested = this.requestedWritingId;
        if (requested) this.openWritingFromHandoff(requested);
        else if (firstLoad && !this.activeItem() && !this.blankPage()) this.openStartingPage();
      },
      error: () => {
        const firstLoad = !this.writingTreeLoaded;
        this.writingTreeLoaded = true;
        if (this.requestedWritingId) this.toast.error('That writing is unavailable');
        else if (firstLoad && !this.activeItem()) this.startBlankPage();
      },
    });
  }

  private watchWritingHandoff(): void {
    if (!this.route) return;

    const queryParamMap = this.route.queryParamMap;
    if (queryParamMap?.subscribe) {
      this.writingHandoffSubscription = queryParamMap.subscribe((params) => {
        this.handleWritingHandoffId(params.get('writingId'));
      });
      return;
    }

    this.handleWritingHandoffId(this.route.snapshot?.queryParamMap?.get('writingId') ?? null);
  }

  private handleWritingHandoffId(rawId: string | null): void {
    const writingId = rawId?.trim() || null;
    this.requestedWritingId = writingId;
    this.handoffOpenGeneration++;

    if (!writingId) {
      this.handledWritingHandoffId = null;
      return;
    }

    if (this.writingTreeLoaded) this.openWritingFromHandoff(writingId);
  }

  private openWritingFromHandoff(writingId: string): void {
    if (this.handledWritingHandoffId === writingId) return;
    this.handledWritingHandoffId = writingId;

    const item = this.rootItems().find((candidate) => candidate.id === writingId);
    if (!item || item.type !== 'Document') {
      this.toast.error('That writing is unavailable');
      return;
    }

    this.openWritingDocument(item.id, true, this.handoffOpenGeneration);
  }

  getNameForId(id: string): string {
    return this.rootItems().find((item) => item.id === id)?.name || '';
  }

  loadKeptSources(writingId: string) {
    this.keptSourcesState.set('loading');
    this.writingsService.listSources(writingId).subscribe({
      next: (sources) => {
        // A slow response for a document that is no longer active must not overwrite the
        // kept list of the document the writer has since switched to.
        if (this.activeItem()?.id !== writingId) return;
        this.keptSources.set(sources);
        this.keptSourcesState.set('loaded');
        const pendingInspection = this.pendingInspectedSourceRestore;
        if (pendingInspection?.mode === 'writing') {
          const match = this.keptNotes().find((note) => note.id === pendingInspection.id);
          if (match) this.inspectedSource.set(match);
          this.pendingInspectedSourceRestore = null;
        }
      },
      error: () => {
        if (this.activeItem()?.id !== writingId) return;
        this.keptSourcesState.set('error');
        this.toast.error('Could not load the kept sources');
      },
    });
  }

  keepNote(noteId: string) {
    const active = this.activeItem();
    if (!active) return;

    if (this.keptNoteIds().has(noteId)) {
      // Already kept: no-op
      return;
    }

    if (this.keepingNoteIds().has(noteId)) {
      // Request already in flight: return early to prevent concurrent race
      return;
    }

    this.keepingNoteIds.update((s) => new Set(s).add(noteId));

    this.writingsService.addSource(active.id, noteId).subscribe({
      next: (source) => {
        this.keepingNoteIds.update((s) => {
          const next = new Set(s);
          next.delete(noteId);
          return next;
        });
        // The writer may have switched documents while the request was in flight; the
        // response belongs to `active`, not to whatever is open now.
        if (this.activeItem()?.id !== active.id) return;
        // Update keptSources locally without full reload if already present or append
        this.keptSources.update((prev) => {
          if (prev.some((s) => s.id === source.id)) return prev;
          return [...prev, source];
        });
      },
      error: () => {
        this.keepingNoteIds.update((s) => {
          const next = new Set(s);
          next.delete(noteId);
          return next;
        });
        this.toast.error('Could not keep the source');
        // Re-fetch kept list on error so UI does not leave an optimistic lie
        if (this.activeItem()?.id === active.id) this.loadKeptSources(active.id);
      },
    });
  }

  // ------------------------------------------------------------------
  // The @ source palette (issue #1039)
  // ------------------------------------------------------------------

  /** The text typed after `@`, or null while no query is open. */
  readonly paletteQuery = signal<string | null>(null);
  readonly paletteAnchor = signal<PaletteAnchor | null>(null);
  @ViewChild(SourcePaletteComponent) private sourcePalette?: SourcePaletteComponent;

  /** Keys the editor forwards while an `@` query is open. */
  readonly paletteKeyHandler = (event: KeyboardEvent): boolean =>
    this.sourcePalette?.handleKey(event) ?? false;

  onSourceSuggestion(state: SourceSuggestionState | null): void {
    this.paletteQuery.set(state ? state.query : null);
    this.paletteAnchor.set(state?.rect ?? null);
  }

  /**
   * Put the chosen source where `@` was typed, and keep it with the writing so
   * the Reference rail shows it under "For this writing". A passage is never
   * inserted whole: it opens in the viewer, and the sentences chosen there go
   * where the `@` was.
   */
  insertFromPalette({ source, mode }: PaletteChoice): void {
    const editor = this.markdownEditor;
    if (!editor || !this.activeItem()) return;

    if (source.kind === 'passage' && mode === 'quote') {
      // Kept and remembered only once something is quoted from it.
      editor.clearSourceQuery();
      this.palettePassageSource = source;
      this.passageViewer.open(source.id, this.palettePassageOptions);
      return;
    } else if (source.bookId) {
      void this.insertStyledFromPalette(source, mode);
      return;
    } else {
      const markdown =
        mode === 'reference'
          ? buildReferenceMarkdown(source)
          : mode === 'note'
            ? (buildNoteMarkdown(source) ?? buildQuoteMarkdown(source))
            : (buildQuoteMarkdown(source) ?? buildNoteMarkdown(source));
      if (!markdown || !editor.replaceSourceQuery(markdown, mode === 'reference' ? 'inline' : 'block')) {
        this.toast.error('The editor is not ready for insertion yet');
        return;
      }
    }
    this.keepNote(source.id);
    this.sourcePalette?.remember(source);
  }

  // --- Citation styles (issue #1109) ---

  /** The palette's choice as a live citation in the writer's style; plain house text when the book cannot be read. */
  private async insertStyledFromPalette(source: PaletteChoice['source'], mode: PaletteChoice['mode']): Promise<void> {
    const editor = this.markdownEditor;
    if (!editor) return;
    const styled = await this.styledInsertion(source, mode);
    const markdown = styled?.markdown
      ?? (mode === 'reference'
        ? buildReferenceMarkdown(source)
        : mode === 'note'
          ? (buildNoteMarkdown(source) ?? buildQuoteMarkdown(source))
          : (buildQuoteMarkdown(source) ?? buildNoteMarkdown(source)));
    if (!markdown || !editor.replaceSourceQuery(markdown, mode === 'reference' ? 'inline' : 'block', footnoteOf(styled))) {
      this.toast.error('The editor is not ready for insertion yet');
      return;
    }
    this.keepNote(source.id);
    this.sourcePalette?.remember(source);
    if (styled) void this.warnIfIncomplete(source.bookId);
  }

  /**
   * A quote, note or reference as a live citation in the chosen style
   * (issues #1109, #1157), or null to fall back to plain house text (the book
   * cannot be read). A note style gives a book's first citation in full and
   * later ones short; the refresh that follows keeps that true as notes move.
   */
  private async styledInsertion(
    source: CitableSource & { id?: string },
    mode: 'quote' | 'note' | 'reference',
  ): Promise<StyledInsertion | null> {
    const style = this.pagePrefs.citationStyle();
    if (!this.markdownEditor || !source.bookId) return null;
    const subsequent = style.kind === 'note' && this.markdownEditor.citedBooks().includes(source.bookId);
    const role: CitationRole = mode;
    let text: string | null = null;
    try {
      text = await this.citations.cite(source, style, subsequent, role);
    } catch {
      text = null;
    }
    if (!text) {
      if (style.kind !== 'house') this.toast.error(`Could not format this source in ${style.label}; it went in as Nostos writes it`);
      return null;
    }
    const citation = citationMarkdown(text, {
      book: source.bookId,
      page: citationLocator(source)?.page ?? null,
      src: source.id ?? null,
      as: role,
    });
    const footnote = this.markdownEditor.nextFootnoteNumber();
    if (mode === 'reference') return styledReference(citation, style, footnote);
    if (mode === 'note') {
      return styledNote(source.content, citation, style, footnote) ?? styledQuote(source.selectedText, citation, style, footnote);
    }
    return styledQuote(source.selectedText, citation, style, footnote) ?? styledNote(source.content, citation, style, footnote);
  }

  /**
   * `/` → Bibliography: every book cited in this writing, in the chosen
   * style. Its entries are live (issue #1157): they follow the style, the
   * books' details and what the writing goes on to cite.
   */
  async insertBibliography(): Promise<void> {
    const editor = this.markdownEditor;
    if (!editor) return;
    if (editor.hasBibliography()) {
      this.toast.info('This writing already has a bibliography. It keeps itself up to date.');
      return;
    }
    const style = this.pagePrefs.citationStyle();
    const books = this.writingBooks();
    let entries: { book: string; text: string }[] = [];
    try {
      entries = await this.citations.bibliographyEntries(books, style);
    } catch {
      this.toast.error('Could not build the bibliography');
      return;
    }
    const markdown = bibliographyMarkdown(
      entries.map((entry) => citationMarkdown(entry.text, { book: entry.book, page: null, src: null, as: 'bib' })),
      style,
    );
    if (!markdown) {
      this.toast.info('Nothing is cited in this writing yet');
      return;
    }
    if (!editor.insertBlockMarkdown(markdown)) {
      this.toast.error('The editor is not ready for insertion yet');
      return;
    }
    void this.warnIncompleteBibliography(entries.map((entry) => entry.book));
  }

  /** Every book this writing cites or keeps, cited ones first. */
  private writingBooks(): string[] {
    const cited = this.markdownEditor?.citedBooks() ?? [];
    return [...new Set([...cited, ...this.keptSources().map((source) => source.bookId)].filter(Boolean))];
  }

  // --- Live citations (issue #1157) ---

  private citationRefreshTimer: ReturnType<typeof setTimeout> | null = null;
  private citationRefreshRun = 0;
  /** "writing:book" pairs already told about missing details, so each is said once. */
  private readonly toldIncomplete = new Set<string>();

  /** The style, a book's details or the kept sources changed: the open writing's citations follow. */
  protected readonly followCitationInputs = effect(() => {
    this.pagePrefs.citationStyle();
    this.citations.revision();
    this.keptSources();
    untracked(() => this.scheduleCitationRefresh());
  });

  /** The editor's citations changed (one went in, moved or out, or a writing opened). */
  onCitationsChange(): void {
    this.scheduleCitationRefresh();
  }

  private scheduleCitationRefresh(delay = 250): void {
    if (this.citationRefreshTimer) clearTimeout(this.citationRefreshTimer);
    this.citationRefreshTimer = setTimeout(() => {
      this.citationRefreshTimer = null;
      void this.refreshCitations();
    }, delay);
  }

  /** Writes the open writing's citations again from the books' details, in the chosen style. */
  async refreshCitations(): Promise<boolean> {
    const editor = this.markdownEditor;
    const writing = this.activeItem()?.id;
    if (!editor || !writing) return false;
    const books = editor.citedBooks();
    if (!books.length) return false;
    const run = ++this.citationRefreshRun;
    let renderer;
    try {
      renderer = await this.citations.renderer(this.pagePrefs.citationStyle(), books, this.keptSources());
    } catch {
      return false;
    }
    // The writer may have switched writings, or a newer refresh started.
    if (run !== this.citationRefreshRun || this.activeItem()?.id !== writing || this.markdownEditor !== editor) return false;
    return editor.refreshCitations(renderer);
  }

  /** Opens the Citations settings at the books missing details, this one first. */
  openCitationDetails(bookId: string | null = null): void {
    if (this.drawerOpen()) {
      this.showFileSidebar.set(false);
      if (this.isDrawer('reference')) this.showBrainSidebar.set(false);
    }
    this.citedBookIds.set(this.writingBooks());
    this.pageSettingsStart.set({ view: 'citations', book: bookId });
    // Reopened, so a toast's action lands on the Citations list even when the settings were open.
    this.pageSettingsOpen.set(false);
    queueMicrotask(() => this.pageSettingsOpen.set(true));
  }

  /**
   * A book was cited without a detail the style needs: say so once per
   * writing, with the way to add it. The citation is in already, written as
   * the style writes a missing detail ("n.d."), and follows once it is added.
   */
  private async warnIfIncomplete(bookId: string | null | undefined): Promise<void> {
    const writing = this.activeItem()?.id;
    if (!bookId || !writing || this.toldIncomplete.has(`${writing}:${bookId}`)) return;
    const style = this.pagePrefs.citationStyle();
    const book = await this.citations.book(bookId);
    if (!book) return;
    const missing = missingForStyle(bookToCslItem(book), style);
    if (!missing.length) return;
    this.toldIncomplete.add(`${writing}:${bookId}`);
    this.toast.show(
      `“${book.title}” has no ${missingPhrase(missing)} in its details, so ${style.label} cites it without.`,
      'info',
      8000,
      { label: 'Add', run: () => this.openCitationDetails(bookId) },
    );
  }

  /** A bibliography went in: name the books whose entries lack details, once. */
  private async warnIncompleteBibliography(bookIds: readonly string[]): Promise<void> {
    const style = this.pagePrefs.citationStyle();
    const books = await Promise.all(bookIds.map((id) => this.citations.book(id)));
    const incomplete = books.filter((book) => !!book && missingForStyle(bookToCslItem(book), style).length);
    if (!incomplete.length) return;
    const message =
      incomplete.length === 1
        ? `“${incomplete[0]!.title}” has no ${missingPhrase(missingForStyle(bookToCslItem(incomplete[0]!), style))} in its details, so its entry is incomplete.`
        : `${incomplete.length} sources lack details ${style.label} needs, so their entries are incomplete.`;
    this.toast.show(message, 'info', 8000, { label: 'Fix', run: () => this.openCitationDetails(incomplete[0]!.id) });
  }

  /** A book's details were saved from the Citations list. */
  onCitationBookUpdated(): void {
    this.scheduleCitationRefresh(0);
  }

  /** A book from this writing, for the citation style examples. */
  readonly citationExampleBookId = computed(() => this.keptSources()[0]?.bookId ?? null);

  private palettePassageSource: PaletteChoice['source'] | null = null;

  /** "Browse reference" from an empty palette: the Books list, searched. */
  browseFromPalette(query: string): void {
    this.markdownEditor?.closeSourceQuery();
    this.setReferenceMode('library');
    this.setLibraryTab('notes');
    this.bookQuery.set(query);
    this.setRailOpen('reference', true);
  }

  /** The viewer opened from the palette puts its quote after the caret's paragraph. */
  private readonly palettePassageOptions: Omit<PassageViewerRequest, 'noteId'> = {
    quoteSelection: (text: string, passage: Passage) =>
      passage.bookId
        ? this.styledInsertion({ selectedText: text, bookId: passage.bookId, id: passage.id, passageTitle: passage.title }, 'quote').then(
            (styled) => this.quoteFromPalettePassage(text, passage, styled),
          )
        : this.quoteFromPalettePassage(text, passage, null),
  };

  /** Puts a quote from a passage opened from the palette after the caret's paragraph. */
  private quoteFromPalettePassage(text: string, passage: Passage, styled: StyledInsertion | null): boolean {
    const markdown = styled?.markdown ?? buildQuoteMarkdown({
      selectedText: text,
      bookTitle: passage.bookTitle,
      bookAuthor: passage.bookAuthor,
      passageTitle: passage.title,
    });
    if (markdown && this.markdownEditor?.insertBlockMarkdown(markdown, footnoteOf(styled))) {
      if (styled) void this.warnIfIncomplete(passage.bookId);
      this.keepNote(passage.id);
      const source = this.palettePassageSource;
      if (source?.id === passage.id) this.sourcePalette?.remember(source);
      return true;
    }
    this.toast.error('The editor is not ready for insertion yet');
    return false;
  }

  removeKeptSource(noteId: string, event?: Event) {
    event?.stopPropagation();
    const active = this.activeItem();
    if (!active) return;

    this.writingsService.removeSource(active.id, noteId).subscribe({
      next: () => {
        if (this.activeItem()?.id !== active.id) return;
        this.keptSources.update((prev) => prev.filter((s) => s.id !== noteId));
        if (this.inspectedSource()?.id === noteId) {
          this.inspectedSource.set(null);
          this.restoreCurrentReferenceScroll();
        }
      },
      error: () => {
        this.toast.error('Could not remove the source');
        if (this.activeItem()?.id === active.id) this.loadKeptSources(active.id);
      },
    });
  }

  handleItemSelected(node: any) {
    const item = node as WritingDto;
    if (item.type === 'Folder') return;

    // Manual selection wins over any slower route-handoff request already in flight.
    this.handoffOpenGeneration++;
    this.openWritingDocument(item.id, false, undefined, true);
  }

  private openWritingDocument(
    id: string,
    fromHandoff: boolean,
    handoffGeneration?: number,
    fromFileList = false,
  ): void {
    this.writingsService.get(id).subscribe({
      next: (contentDto) => {
        if (
          fromHandoff &&
          (handoffGeneration !== this.handoffOpenGeneration || this.requestedWritingId !== id)
        ) {
          return;
        }
        if (this.activeItem()?.id !== contentDto.id) {
          this.rememberCaret();
          this.editorWordCount.set(null);
        }
        if (this.blankPage()) this.blankGeneration++;
        this.blankPage.set(false);
        this.autoTitle = contentDto.name === UNTITLED_DOCUMENT;
        this.activeItem.set(contentDto);
        writeStudioValue(STUDIO_LAST_WRITING_KEY, contentDto.id);
        // Opening from the file tree does not change the URL, so the command
        // palette's Recent list would never see it; tell it directly.
        this.recent.record({ kind: 'document', id: contentDto.id, label: contentDto.name, sub: 'Document' });
        this.editorTitle.set(contentDto.name);
        this.editorText.set(contentDto.content);
        this.inspectedSource.set(null);

        // Choosing a writing in the file list closes the list; a handoff lands
        // in the editor on a compact screen. A slow load never closes a
        // slide-over the writer opened in the meantime.
        if ((fromFileList || (fromHandoff && this.isCompact())) && this.drawerOpen()) {
          this.compactDrawerCaret = null;
          this.showFileSidebar.set(false);
          if (fromHandoff && this.isDrawer('reference')) this.showBrainSidebar.set(false);
        }

        if (fromHandoff) this.restoreSourceReturnSurface(contentDto);
        else this.restoreRememberedCaret(contentDto);
        this.loadKeptSources(contentDto.id);
      },
      error: () => {
        if (
          fromHandoff &&
          (handoffGeneration !== this.handoffOpenGeneration || this.requestedWritingId !== id)
        ) {
          return;
        }
        if (fromHandoff) this.toast.error('That writing is unavailable');
      },
    });
  }

  handleItemMove(event: { item: any; newParentId: string | null }) {
    const item = event.item as WritingDto;
    const newParentId = event.newParentId;

    this.writingsService.move(item.id, newParentId).subscribe({
      next: () => {
        this.loadTree();
      },
      error: () => {
        this.toast.error('Could not move the item');
        this.loadTree();
      },
    });
  }

  /**
   * New document: a blank page with the caret in it, created on the server
   * with its first words (issue #1040). New folder: created now and named in
   * the file list.
   */
  createItem(type: 'Folder' | 'Document') {
    // A new document is a blank page; it becomes a writing when words arrive.
    if (type === 'Document') {
      this.startBlankPage();
      return;
    }
    this.writingsService.create({ name: 'Untitled folder', type, parentId: null }).subscribe({
      next: (created) => {
        // The file list is a slide-over; the rename field has to be on
        // screen to be typed into.
        if (!this.showFileSidebar()) this.setRailOpen('files', true);
        this.editingId.set(created.id);
        this.loadTree();
      },
      error: () => this.toast.error('Could not create a folder'),
    });
  }

  startRename(node: any) {
    this.renameError.set(null);
    this.editingId.set(node.id);
  }

  /**
   * The flat tree's rename has already refused an empty or unchanged name, so
   * this only saves. A refusal stays in the field, under it, rather than
   * closing it and losing what was typed.
   */
  saveRename(id: string, newName: string) {
    if (this.renameBusy()) return;
    this.renameBusy.set(true);
    this.renameError.set(null);
    this.writingsService.update(id, { name: newName }).subscribe({
      next: () => {
        this.renameBusy.set(false);
        this.loadTree();
        this.editingId.set(null);
        this.recent.relabel('document', id, newName);

        // Opened after the save lands, so the editor title shows the new name.
        if (this.activeItem()?.id === id) {
          this.editorTitle.set(newName);
          // Named by hand: it no longer follows its first line.
          this.autoTitle = false;
        }
      },
      error: () => {
        this.renameBusy.set(false);
        this.renameError.set('Could not rename it. Try again.');
      },
    });
  }

  cancelRename() {
    if (this.renameBusy()) return;
    this.renameError.set(null);
    this.editingId.set(null);
  }

  /** Writing id awaiting delete confirmation (asked through ConfirmModal). */
  pendingDelete = signal<string | null>(null);

  deleteItem(id: string) {
    this.pendingDelete.set(id);
  }

  cancelDeleteItem(): void {
    this.pendingDelete.set(null);
  }

  confirmDeleteItem(): void {
    const id = this.pendingDelete();
    if (!id) return;
    this.pendingDelete.set(null);

    this.writingsService.delete(id).subscribe(() => {
      this.loadTree();

      writeCarets(readCarets().filter((entry) => entry.id !== id));
      if (readStudioValue(STUDIO_LAST_WRITING_KEY) === id) writeStudioValue(STUDIO_LAST_WRITING_KEY, null);
      if (this.activeItem()?.id === id) {
        // The page stays: a deleted writing leaves a blank one behind.
        this.activeItem.set(null);
        this.startBlankPage();
      }
    });
  }

  // --- Brain & Notes Logic ---

  loadBrain() {
    this.topicIndexState.set('loading');
    this.topicsService.list().subscribe({
      next: (data) => {
        this.topics.set(data);
        this.topicIndexState.set('loaded');
      },
      error: () => this.topicIndexState.set('error'),
    });
  }

  loadBooks() {
    this.bookIndexState.set('loading');
    this.booksService
      .list({
        page: 1,
        pageSize: 50,
      })
      .subscribe({
        next: (data) => {
          this.books.set(data.items);
          this.bookIndexState.set('loaded');
        },
        error: () => this.bookIndexState.set('error'),
      });
  }

  setReferenceMode(mode: 'writing' | 'library'): void {
    this.rememberCurrentReferenceScroll();
    if (this.referenceMode() !== mode) {
      this.inspectedSource.set(null);
    }
    this.referenceMode.set(mode);
    this.restoreCurrentReferenceScroll();
  }

  setLibraryTab(tab: 'brain' | 'notes'): void {
    this.rememberCurrentReferenceScroll();
    if (this.activeSidebarTab() !== tab) {
      this.inspectedSource.set(null);
    }
    this.activeSidebarTab.set(tab);
    this.restoreCurrentReferenceScroll();
  }

  selectTopic(id: string) {
    this.rememberReferenceScroll('topics');
    this.inspectedSource.set(null);
    this.selectedTopicId.set(id);
    this.topicNotesState.set('loading');
    this.topicsService.get(id).subscribe({
      next: (d) => {
        // Map NoteContextDto (noteId) → Note (id) for Reference row/card compatibility.
        const mapped: Note[] = d.notes.map((n: NoteContextDto) => ({
          id: n.noteId,
          bookId: n.bookId,
          content: n.content,
          cfiRange: n.cfiRange,
          selectedText: n.selectedText,
          createdAt: n.createdAt ?? '',
          bookTitle: n.bookTitle,
          bookAuthor: n.bookAuthor,
          sourceAnchorKind: n.sourceAnchorKind,
          sourceAnchorValue: n.sourceAnchorValue,
          anchorVerified: n.anchorVerified,
          highlightColour: n.highlightColour,
          kind: n.kind,
          passageTitle: n.passageTitle,
          passageEnd: n.passageEnd,
          passageWordCount: n.passageWordCount,
        }));
        this.selectedTopicNotes.set(mapped);
        this.topicNotesState.set('loaded');
        const pendingInspection = this.pendingInspectedSourceRestore;
        if (pendingInspection?.mode === 'library' && pendingInspection.tab === 'brain') {
          const match = mapped.find((note) => note.id === pendingInspection.id);
          if (match) this.inspectedSource.set(match);
          this.pendingInspectedSourceRestore = null;
        }
      },
      error: () => this.topicNotesState.set('error'),
    });
  }

  retryTopicNotes(): void {
    const id = this.selectedTopicId();
    if (id) this.selectTopic(id);
  }

  /** Bound for the shared detail-header template, which calls its `back` context value. */
  readonly backToTopicsAction = (): void => this.backToTopics();
  readonly backToBooksAction = (): void => this.backToBooks();

  backToTopics(): void {
    this.rememberReferenceScroll('topicNotes');
    this.selectedTopicId.set(null);
    this.restoreReferenceScroll('topics');
  }

  selectBook(id: string) {
    this.rememberReferenceScroll('books');
    this.inspectedSource.set(null);
    this.selectedBookId.set(id);
    this.bookNotesState.set('loading');
    this.notesService.list(id).subscribe({
      next: (notes) => {
        this.selectedBookNotes.set(notes);
        this.bookNotesState.set('loaded');
        const pendingInspection = this.pendingInspectedSourceRestore;
        if (pendingInspection?.mode === 'library' && pendingInspection.tab === 'notes') {
          const match = notes.find((note) => note.id === pendingInspection.id);
          if (match) this.inspectedSource.set(match);
          this.pendingInspectedSourceRestore = null;
        }
      },
      error: () => this.bookNotesState.set('error'),
    });
  }

  retryBookNotes(): void {
    const id = this.selectedBookId();
    if (id) this.selectBook(id);
  }

  backToBooks(): void {
    this.rememberReferenceScroll('bookNotes');
    this.selectedBookId.set(null);
    this.restoreReferenceScroll('books');
  }

  inspectSource(note: Note): void {
    this.rememberCurrentReferenceScroll();
    this.inspectedSource.set(note);
  }

  closeInspectedSource(): void {
    this.inspectedSource.set(null);
    this.restoreCurrentReferenceScroll();
  }

  captureReferenceScroll(event: Event): void {
    const element = event.target as HTMLElement;
    const key = element.dataset['referenceScroll'] as ReferenceScrollKey | undefined;
    if (key) this.referenceScrollPositions[key] = element.scrollTop;
  }

  private currentReferenceScrollKey(): ReferenceScrollKey {
    if (this.referenceMode() === 'writing') return 'writing';
    if (this.activeSidebarTab() === 'brain') {
      return this.selectedTopicId() ? 'topicNotes' : 'topics';
    }
    return this.selectedBookId() ? 'bookNotes' : 'books';
  }

  private rememberCurrentReferenceScroll(): void {
    this.rememberReferenceScroll(this.currentReferenceScrollKey());
  }

  private rememberReferenceScroll(key: ReferenceScrollKey): void {
    const element = this.hostElement.nativeElement.querySelector(
      `[data-reference-scroll="${key}"]`,
    );
    if (element) this.referenceScrollPositions[key] = element.scrollTop;
  }

  private restoreCurrentReferenceScroll(): void {
    this.restoreReferenceScroll(this.currentReferenceScrollKey());
  }

  private restoreReferenceScroll(key: ReferenceScrollKey): void {
    const restore = () => {
      const element = this.hostElement.nativeElement.querySelector(
        '[data-reference-scroll="' + key + '"]',
      );
      if (element) element.scrollTop = this.referenceScrollPositions[key];
    };
    if (typeof requestAnimationFrame === 'function') requestAnimationFrame(restore);
    else setTimeout(restore, 0);
  }

  referencePreview(note: Note): string {
    const value = hasMeaningfulSelectedText(note) ? note.selectedText : note.content;
    const markdown = noteWikilinksToText(value ?? '');
    const html = marked.parse(markdown, { async: false });
    const text = new DOMParser().parseFromString(html, 'text/html').body.textContent ?? '';
    return text.replace(/\s+/g, ' ').trim();
  }

  hasQuotePreview(note: Note): boolean {
    return hasMeaningfulSelectedText(note);
  }

  sourceProvenance(note: Note): string {
    const title = note.bookTitle?.trim() || 'Unknown book';
    const locator = this.sourceDisplayLocator(note);
    return locator ? `${title} · ${locator}` : title;
  }

  sourceRowLabel(note: Note): string {
    return `Inspect source from ${this.sourceProvenance(note)}: ${this.referencePreview(note)}`;
  }

  keepActionLabel(noteId: string): string {
    if (!this.activeItem()) return 'Open a writing to keep or insert sources';
    if (this.keepingNoteIds().has(noteId)) return 'Keeping with this writing';
    if (this.keptNoteIds().has(noteId)) {
      return 'Already kept with this writing; no change on click';
    }
    return 'Keep with this writing';
  }

  sourceActionDisabledReason(): string | null {
    if (!this.activeItem()) return 'Open a writing to keep or insert sources';
    if (this.editorWordCount() === null) return 'The editor is not ready for insertion yet';
    return null;
  }

  retryKeptSources(): void {
    const writingId = this.activeItem()?.id;
    if (writingId) this.loadKeptSources(writingId);
  }

  retryTopicIndex(): void {
    this.loadBrain();
  }

  retryBookIndex(): void {
    this.loadBooks();
  }

  sourceDisplayLocator(source: Note): string | null {
    return sourceHumanLabel(source)?.locator ?? null;
  }

  canInsertQuote(source: Note | null = this.inspectedSource()): boolean {
    // A passage's list copy is only its opening; it is quoted from the viewer.
    if (isPassage(source)) return false;
    return !!source && hasMeaningfulSelectedText(source) && !!sourceHumanLabel(source);
  }

  /** Whether the source is a passage, quoted by reading it (issue #1021). */
  isPassageSource(source: Note | null = this.inspectedSource()): boolean {
    return isPassage(source);
  }

  /**
   * The passage viewer opened from Studio offers "Quote in writing": the
   * words selected in it go into the draft as a quotation from the passage.
   */
  /** The pen a source row's quotation is shown in; null when the row does not know it. */
  readonly quotePen = quotePen;
  readonly noTopicsMatch = NO_TOPICS_MATCH;

  readonly passageViewerOptions: Omit<PassageViewerRequest, 'noteId'> = {
    quoteSelection: (text: string, passage: Passage) => this.quotePassageSelection(text, passage),
  };

  readPassage(source: Note): void {
    this.passageViewer.open(source.id, this.passageViewerOptions);
  }

  /** Whether the quote went in; the viewer confirms success on its own button. */
  private async quotePassageSelection(text: string, passage: Passage): Promise<boolean> {
    const styled = await this.styledInsertion({ selectedText: text, bookId: passage.bookId, id: passage.id, passageTitle: passage.title }, 'quote');
    const markdown = styled?.markdown ?? buildQuoteMarkdown({
      selectedText: text,
      bookTitle: passage.bookTitle,
      bookAuthor: passage.bookAuthor,
      passageTitle: passage.title,
    });
    if (!markdown || !this.activeItem() || !this.markdownEditor) {
      this.toast.error('Open a writing and place the cursor in the editor first');
      return false;
    }
    if (await this.markdownEditor.insertMarkdown(markdown, footnoteOf(styled))) {
      if (styled) void this.warnIfIncomplete(passage.bookId);
      return true;
    }
    this.toast.error('The editor is not ready for insertion yet');
    return false;
  }

  canInsertNote(source: Note | null = this.inspectedSource()): boolean {
    return !!source && hasMeaningfulNoteContent(source) && !!sourceHumanLabel(source);
  }

  canInsertReference(source: Note | null = this.inspectedSource()): boolean {
    return !!source && !!sourceHumanLabel(source);
  }

  async insertQuote(source: Note): Promise<void> {
    const styled = await this.styledInsertion(source, 'quote');
    await this.insertSourceMarkdown(styled?.markdown ?? buildQuoteMarkdown(source), styled, source.bookId);
  }

  async insertNote(source: Note): Promise<void> {
    const styled = await this.styledInsertion(source, 'note');
    await this.insertSourceMarkdown(styled?.markdown ?? buildNoteMarkdown(source), styled, source.bookId);
  }

  async insertReference(source: Note): Promise<void> {
    const styled = await this.styledInsertion(source, 'reference');
    await this.insertSourceMarkdown(styled?.markdown ?? buildReferenceMarkdown(source), styled, source.bookId);
  }

  /** The title a passage row leads with, or null for any other source. */
  passageRowTitle(note: Note): string | null {
    return isPassage(note) ? note.passageTitle?.trim() || 'Passage' : null;
  }

  private async insertSourceMarkdown(markdown: string | null, styled: StyledInsertion | null = null, bookId: string | null = null): Promise<void> {
    if (!markdown) return;
    if (!this.activeItem() || !this.markdownEditor) {
      this.toast.error('Open a writing and place the cursor in the editor first');
      return;
    }

    const inserted = await this.markdownEditor.insertMarkdown(markdown, footnoteOf(styled));
    if (!inserted) {
      this.toast.error('The editor is not ready for insertion yet');
      return;
    }
    if (styled) void this.warnIfIncomplete(bookId);

    if (this.isCompact()) this.setRailOpen('reference', false, false, false);
  }

  async openSource(source: Note): Promise<void> {
    if (!this.router || !source.bookId) return;

    const active = this.activeItem();
    if (!active) {
      this.toast.error('Open a writing before opening a source');
      return;
    }

    // The 2s autosave debounce must never turn "Open source" into a data-loss
    // path. The explicit flush joins the same serialized save lane and only
    // navigates after the latest title/prose is safely persisted.
    if (!(await this.flushActiveWritingBeforeSource(active))) return;

    // Capture the actual final caret/scroll/UI context immediately before
    // leaving. If the writer typed while a save was in flight, the stable flush
    // above has already persisted it and this snapshot follows that final caret.
    const snapshot = this.captureSourceReturnSnapshot(active.id);
    this.attachSourceReturnSnapshot(snapshot);

    const kind = source.sourceAnchorKind?.trim().toLowerCase();
    const value = source.sourceAnchorValue?.trim();

    if (source.anchorVerified === true && kind === 'pdf_page' && value) {
      const page = Number(value);
      if (Number.isInteger(page) && page > 0) {
        await this.navigateFromStudio(['/read', source.bookId], {
          queryParams: { sourcePage: page },
          state: readerReturnOriginState(active.id),
        });
        return;
      }
    }

    if (source.anchorVerified === true && kind === 'epub_cfi' && value) {
      await this.navigateFromStudio(['/read', source.bookId], {
        queryParams: { sourceCfi: value },
        state: readerReturnOriginState(active.id),
      });
      return;
    }

    const legacyCfi = source.cfiRange?.trim();
    if (legacyCfi?.startsWith('epubcfi(')) {
      await this.navigateFromStudio(['/read', source.bookId], {
        queryParams: { sourceCfi: legacyCfi },
        state: readerReturnOriginState(active.id),
      });
      return;
    }

    // Unsupported/unverified anchors remain honest Book Detail fallbacks. The
    // prepared Studio history entry still means ordinary browser Back restores
    // the exact writing context, but Book Detail receives no Reader marker.
    await this.navigateFromStudio(['/library', source.bookId]);
  }

  private captureSourceReturnSnapshot(writingId: string): StudioSourceReturnSnapshotV1 {
    const editor = this.markdownEditor?.captureTransientState() ?? undefined;
    return {
      version: 1,
      writingId,
      ...(editor ? { editor } : {}),
      references: {
        mode: this.referenceMode(),
        activeLibraryTab: this.activeSidebarTab(),
        wasOpen: this.showBrainSidebar(),
        inspectedSourceId: this.inspectedSource()?.id ?? null,
        selectedTopicId: this.selectedTopicId(),
        selectedBookId: this.selectedBookId(),
      },
    };
  }

  private async flushActiveWritingBeforeSource(item: WritingContentDto): Promise<boolean> {
    // Usually one pass. Re-check after each awaited write because the editor
    // remains live: text typed while the request is in flight must be included
    // before teardown can cancel the ordinary debounce.
    for (let attempt = 0; attempt < 4; attempt++) {
      const current = this.activeItem();
      if (!current || current.id !== item.id) return false;

      const title = this.editorTitle();
      const content = this.editorText();
      const dirty = title !== current.name || content !== current.content;

      // If no delayed/in-flight autosave exists and the document is already
      // persisted, opening a source should not generate a redundant write.
      if (!dirty && this.pendingSaveCount === 0) return true;

      const generation = ++this.saveGeneration;
      try {
        await this.queueWritingSave(current, title, content, generation);
      } catch {
        if (generation === this.saveGeneration) this.saveStatus.set('Unsaved');
        this.toast.error('Could not save this writing. Your draft is still open.');
        return false;
      }

      const saved = this.activeItem();
      if (
        saved?.id === item.id &&
        this.editorTitle() === saved.name &&
        this.editorText() === saved.content &&
        this.pendingSaveCount === 0
      ) {
        return true;
      }
    }

    // Avoid ever choosing navigation over data safety if the document changes
    // continuously during the flush window.
    this.saveStatus.set('Unsaved');
    this.toast.error('The writing changed while opening the source. Try again.');
    return false;
  }

  private queueWritingSave(
    item: WritingContentDto,
    title: string,
    content: string,
    generation: number,
  ): Promise<void> {
    this.pendingSaveCount++;

    const run = async () => {
      // A newer debounce/flush superseded this request before it began.
      if (generation !== this.saveGeneration) return;

      this.saveStatus.set('Saving…');
      try {
        const updated = await firstValueFrom(
          this.writingsService.update(item.id, { name: title, content }),
        );

        // An explicit flush may have been queued behind this request. Let that
        // newer write own the UI and final server state.
        if (generation !== this.saveGeneration) return;
        if (this.activeItem()?.id !== item.id) return;

        this.activeItem.set(updated);
        this.saveStatus.set('Saved');
        if (title !== item.name) {
          this.loadTree();
          this.recent.relabel('document', item.id, title);
        }
      } catch (error) {
        if (generation === this.saveGeneration) this.saveStatus.set('Unsaved');
        throw error;
      }
    };

    const queued = this.saveQueue.catch(() => undefined).then(run);
    this.saveQueue = queued.catch(() => undefined);
    const settlePending = () => {
      this.pendingSaveCount = Math.max(0, this.pendingSaveCount - 1);
    };
    void queued.then(settlePending, settlePending);
    return queued;
  }

  private attachSourceReturnSnapshot(snapshot: StudioSourceReturnSnapshotV1): void {
    const canonicalUrl = `/studio?writingId=${encodeURIComponent(snapshot.writingId)}`;
    window.history.replaceState(
      withStudioSourceReturnState(window.history.state, snapshot),
      '',
      canonicalUrl,
    );
    this.pendingSourceReturnSnapshot = snapshot;
  }

  private async navigateFromStudio(
    commands: readonly unknown[],
    extras?: {
      queryParams?: Record<string, unknown>;
      state?: Record<string, unknown>;
    },
  ): Promise<void> {
    const navigated = extras
      ? await this.router?.navigate(commands as any[], extras as any)
      : await this.router?.navigate(commands as any[]);
    if (navigated === false) {
      // Staying in Studio must not leave a return snapshot waiting to replay.
      this.consumeSourceReturnSnapshot();
    }
  }

  private prepareSourceReturnRestore(): void {
    const state = window.history.state;
    this.pendingSourceReturnSnapshot = readStudioSourceReturnSnapshot(state);

    // Malformed/old versions are one-shot state too. Remove them rather than
    // letting a stale blob survive unrelated Studio visits.
    if (!this.pendingSourceReturnSnapshot && hasStudioSourceReturnState(state)) {
      this.consumeSourceReturnSnapshot();
    }
  }

  private restoreSourceReturnSurface(content: WritingContentDto): void {
    const snapshot = this.pendingSourceReturnSnapshot;
    if (!snapshot) return;

    if (snapshot.writingId !== content.id) {
      this.consumeSourceReturnSnapshot();
      return;
    }

    const references = snapshot.references;
    if (references) {
      this.referenceMode.set(references.mode);
      this.activeSidebarTab.set(references.activeLibraryTab);
      this.selectedTopicId.set(references.selectedTopicId ?? null);
      this.selectedBookId.set(references.selectedBookId ?? null);
      this.pendingInspectedSourceRestore = references.inspectedSourceId
        ? {
            id: references.inspectedSourceId,
            mode: references.mode,
            tab: references.activeLibraryTab,
          }
        : null;

      if (this.isCompact()) {
        // Mobile remains one-pane: Documents never reopens over the returned
        // editor. The reference drawer may reopen because its existing close
        // control is the explicit route back to drafting.
        this.showFileSidebar.set(false);
        this.showBrainSidebar.set(references.wasOpen);
      } else {
        this.showBrainSidebar.set(references.wasOpen);
      }

      if (references.mode === 'library') {
        if (references.activeLibraryTab === 'brain' && references.selectedTopicId) {
          this.selectTopic(references.selectedTopicId);
        } else if (references.activeLibraryTab === 'notes' && references.selectedBookId) {
          this.selectBook(references.selectedBookId);
        } else {
          this.pendingInspectedSourceRestore = null;
        }
      }
    }

    // ViewChild may not exist yet for synchronous test/service observables.
    // ngAfterViewInit retries without polling; real HTTP handoffs generally
    // arrive after the editor view already exists.
    queueMicrotask(() => this.tryRestoreEditorSourceReturn());
  }

  private tryRestoreEditorSourceReturn(): void {
    const snapshot = this.pendingSourceReturnSnapshot;
    const active = this.activeItem();
    const editor = this.markdownEditor;
    if (!snapshot || !active || snapshot.writingId !== active.id || !editor) return;

    const state = snapshot.editor as MarkdownEditorTransientState | undefined;
    if (!state) {
      this.consumeSourceReturnSnapshot();
      return;
    }

    void editor.restoreTransientState(state, active.content).then(() => {
      // Caret/scroll are best-effort, but this state represents one completed
      // return cycle. Never let an unsupported bookmark replay on a later visit.
      if (this.pendingSourceReturnSnapshot === snapshot) {
        this.consumeSourceReturnSnapshot();
      }
    });
  }

  private consumeSourceReturnSnapshot(): void {
    this.pendingSourceReturnSnapshot = null;

    if (!hasStudioSourceReturnState(window.history.state)) return;
    window.history.replaceState(
      withoutStudioSourceReturnState(window.history.state),
      '',
      `${window.location.pathname}${window.location.search}${window.location.hash}`,
    );
  }
}

/** A cited source the server returned, in the shape the panel's rows draw. */
function hitToNote(hit: NoteSearchHit): Note {
  return {
    id: hit.id,
    bookId: hit.bookId,
    content: hit.content,
    selectedText: hit.selectedText ?? undefined,
    cfiRange: hit.cfiRange ?? undefined,
    createdAt: hit.createdAt,
    bookTitle: hit.bookTitle ?? undefined,
    bookAuthor: hit.bookAuthor ?? undefined,
    sourceAnchorKind: hit.sourceAnchorKind ?? undefined,
    sourceAnchorValue: hit.sourceAnchorValue ?? undefined,
    anchorVerified: hit.anchorVerified ?? undefined,
    kind: hit.kind,
    passageTitle: hit.passageTitle,
    passageEnd: hit.passageEnd,
    passageWordCount: hit.passageWordCount,
  };
}
