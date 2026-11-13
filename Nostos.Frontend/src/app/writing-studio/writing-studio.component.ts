import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  OnInit,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, debounceTime, distinctUntilChanged, finalize, forkJoin, map, of, switchMap } from 'rxjs';
import { WritingContentDto, WritingDto, WritingSourceDto } from '../core/dtos/writing.dtos';
import { Note, NoteSearchHit, Passage, isPassage } from '../core/dtos/note.dtos';
import {
  hasStudioSourceReturnState,
  readerReturnOriginState,
  readStudioSourceReturnSnapshot,
  StudioSourceReturnSnapshotV1,
  withStudioSourceReturnState,
  withoutStudioSourceReturnState,
} from '../core/navigation/studio-reader-navigation';
import { modifierKeyLabel } from '../core/platform';
import { BooksService, Book as BookDto } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService, TopicDto } from '../core/services/topics.service';
import { WritingsService } from '../core/services/writings.service';
import { editionChoices } from '../book-detail/edition-switch-options';
import { AssistantLauncherService } from '../ui/assistant/assistant-launcher.service';
import { ButtonComponent } from '../ui/button/button.component';
import { RecentItemsService } from '../ui/command-palette/recent-items.service';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { type DropdownOption } from '../ui/dropdown/dropdown.component';
import { TreeItem } from '../ui/flat-tree/flat-tree.helper';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { InlineRenameComponent } from '../ui/inline-rename/inline-rename.component';
import {
  MarkdownEditorComponent,
  MarkdownEditorTransientState,
  SourceSuggestionState,
} from '../ui/markdown-editor/markdown-editor.component';
import { citedSourceIds } from '../ui/markdown-editor/citation.extension';
import { NOSTOS_MENU } from '../ui/menu/menu.component';
import { ModalShell } from '../ui/modal-shell/modal-shell.component';
import { noteAsItem } from '../ui/note-item/note-item.helpers';
import { PassageViewerRequest, PassageViewerService } from '../ui/passage-viewer/passage-viewer.service';
import { NostosDatePipe } from '../ui/pipes/nostos-date.pipe';
import { lockScrollWhile } from '../ui/scroll-lock/scroll-lock.service';
import { CitableSource, CitationService } from './citations/citation.service';
import { CitationRefreshCoordinator } from './citations/citation-refresh.coordinator';
import { StyledInsertion } from './citations/styled-insertion';
import { ExportDialogComponent } from './export/export-dialog.component';
import { hasExportableContent } from './export/export-resolve';
import { noteContextToNote, noteSearchHitToNote, writingSourceToNote } from './note-context.mapper';
import { StudioPageSettingsComponent } from './page-settings/studio-page-settings.component';
import { StudioPreferencesService } from './page-settings/studio-preferences.service';
import {
  computeReferencePreview,
  formatSourceProvenance,
  formatSourceRowLabel,
} from './reference-preview';
import {
  ReferenceLoadState,
  ReferenceScrollKey,
  StudioReferenceDrawerComponent,
} from './reference-drawer/studio-reference-drawer.component';
import {
  buildNoteMarkdown,
  buildQuoteMarkdown,
  buildReferenceMarkdown,
  hasMeaningfulNoteContent,
  hasMeaningfulSelectedText,
  sourceHumanLabel,
} from './source-insertion.helpers';
import { PaletteAnchor, SourcePaletteComponent } from './source-palette/source-palette.component';
import { PaletteChoice } from './source-palette/source-palette.helpers';
import { answerStarterOffer, starterOfferAnswered } from './starter-draft/starter-draft';
import { StarterDraftService } from './starter-draft/starter-draft.service';
import { StudioBodyClassService } from './studio-body-class.service';
import { DeleteWritingModalComponent } from './tree/delete-writing-modal.component';
import { MoveWritingDialogComponent } from './tree/move-writing-dialog.component';
import {
  MoveRow,
  buildDeleteWritingCopy,
  buildMoveRows,
  moveLocationLabel,
  moveRowKey,
} from './tree/move-writing.helpers';
import { StudioFilesSidebarComponent } from './tree/studio-files-sidebar.component';
import { AutosaveHostAdapter, WritingAutosaveService } from './writing-autosave.service';
import { removedCitedIds, sourceKey, touchedTopics } from './writing-touches.helpers';

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
    CommonModule,
    NostosIconComponent,
    MarkdownEditorComponent,
    SourcePaletteComponent,
    StudioPageSettingsComponent,
    IconButtonComponent,
    ButtonComponent,
    ConfirmModal,
    ModalShell,
    NOSTOS_MENU,
    ExportDialogComponent,
    InlineRenameComponent,
    NostosDatePipe,
    StudioFilesSidebarComponent,
    StudioReferenceDrawerComponent,
    MoveWritingDialogComponent,
    DeleteWritingModalComponent,
  ],
  providers: [StudioBodyClassService, WritingAutosaveService, CitationRefreshCoordinator],
  templateUrl: './writing-studio.component.html',
  styleUrls: ['./writing-studio.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
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
  private readonly bodyClass = inject(StudioBodyClassService);
  private readonly autosave = inject(WritingAutosaveService);
  private readonly citationCoordinator = inject(CitationRefreshCoordinator);
  @ViewChild('editor') private markdownEditor?: MarkdownEditorComponent;

  private requestedWritingId: string | null = null;
  private handledWritingHandoffId: string | null = null;
  private handoffOpenGeneration = 0;
  private writingTreeLoaded = false;
  private destroyed = false;
  private readonly pendingTimeouts = new Set<ReturnType<typeof setTimeout>>();
  private readonly pendingRafs = new Set<number>();

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
  showFileSidebar = signal(false);
  showBrainSidebar = signal(false);

  /** A blank page with no writing behind it yet; the first words create one. */
  blankPage = signal(false);
  /** The open writing takes its name from its first line until it is renamed. */
  private autoTitle = false;
  /** The folder the blank page's writing will be created in. */
  private blankParentId: string | null = null;
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

  /** Reference surface top-level mode: "For this document" vs "Library" (issue #491) */
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
  keptNotes = computed<Note[]>(() =>
    [...this.keptSources()]
      .sort((a, b) => new Date(a.addedAt).getTime() - new Date(b.addedAt).getTime())
      .map(writingSourceToNote),
  );

  rootItems = signal<WritingDto[]>([]);
  activeItem = signal<WritingContentDto | null>(null);
  /** The folder last chosen in the file list; New puts its page there. */
  private readonly chosenFolderId = signal<string | null>(null);
  /** Where a new page or folder goes: the chosen folder, else the open writing's folder, else the top. */
  private readonly newItemParentId = computed<string | null>(() => {
    const items = this.rootItems();
    const folder = this.chosenFolderId();
    if (folder && items.some((i) => i.id === folder && i.type === 'Folder')) return folder;
    const open = this.activeItem()?.id;
    return (open ? items.find((i) => i.id === open)?.parentId : null) ?? null;
  });
  /** The row marked in the file list: the chosen folder while one is, else the open writing. */
  readonly markedItemId = computed<string | null>(() => {
    const folder = this.chosenFolderId();
    if (folder && this.rootItems().some((i) => i.id === folder)) return folder;
    return this.activeItem()?.id ?? null;
  });

  // --- What the writing touches (issue #1162) ---

  /** Note ids the open writing cites (lower-case, once each, in order). */
  readonly citedIds = computed(() => (this.activeItem() ? citedSourceIds(this.editorText()) : []));
  readonly citedIdSet = computed(() => new Set(this.citedIds()));
  /** Kept and cited sources as the server has them now, with their topics. */
  readonly sourceLookup = signal<ReadonlyMap<string, NoteSearchHit>>(new Map());
  /** The ids `sourceLookup` answers for: an id asked about and absent was removed. */
  private readonly sourceLookedUp = signal<ReadonlySet<string>>(new Set());
  /** Every id worth looking up, as one stable key, so typing alone never refetches. */
  private readonly sourceLookupKey = computed(() =>
    [...new Set([...this.keptSources().map((s) => sourceKey(s.id)), ...this.citedIds()])]
      .filter(Boolean)
      .sort()
      .join(','),
  );
  /** The topics the cited sources carry, for the panel's one Touches line. */
  readonly touchedTopics = computed(() => touchedTopics(this.citedIds(), this.sourceLookup()));
  /** Cited sources whose note (or book) was deleted; the citation text stays. */
  readonly removedCitedCount = computed(
    () => removedCitedIds(this.citedIds(), this.sourceLookup(), this.sourceLookedUp()).length,
  );
  /** Sources cited without being kept (a citation pasted from another writing). */
  readonly citedOnlyNotes = computed<Note[]>(() => {
    const kept = new Set(this.keptSources().map((s) => sourceKey(s.id)));
    const lookup = this.sourceLookup();
    return this.citedIds()
      .filter((id) => !kept.has(id) && lookup.has(id))
      .map((id) => noteSearchHitToNote(lookup.get(id)!));
  });

  /** Topic ids by folded name, so a source's topic pill can open its page. */
  readonly topicIdsByName = computed(
    () => new Map(this.topics().map((topic) => [topic.name.toLocaleLowerCase(), topic.id])),
  );

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
  readonly saveStatus = this.autosave.saveStatus;
  /**
   * Set when a save is refused because the writing was changed elsewhere
   * (issue #1363). Holds the newer copy and the writer's own text.
   */
  readonly saveConflict = this.autosave.saveConflict;

  // Zen (focus) mode — issue #49. Session-only: never persisted.
  isZen = signal(false);

  /** The page's own settings (issue #1041): type, measure, behaviour. */
  readonly pagePrefs = inject(StudioPreferencesService);
  private readonly citations = inject(CitationService);
  private readonly starter = inject(StarterDraftService);

  private readonly autosaveAdapter: AutosaveHostAdapter = {
    getActiveItem: () => this.activeItem(),
    setActiveItem: (updated) => this.activeItem.set(updated),
    getTitle: () => this.editorTitle(),
    setTitle: (title) => this.editorTitle.set(title),
    getContent: () => this.editorText(),
    setContent: (content) => this.editorText.set(content),
    flushPendingEditorChange: () => this.markdownEditor?.flushPendingChange(),
    onRenamedDuringSave: (id, newTitle) => {
      this.loadTree();
      this.recent.relabel('document', id, newTitle);
    },
  };

  // --- A starter draft (issue #1220) ---

  readonly starterOffer = signal<NoteSearchHit[] | null>(null);
  readonly startingDraft = signal(false);
  readonly showStarterOffer = computed(
    () => !!this.starterOffer()?.length && this.blankPage() && !this.activeItem(),
  );

  private checkStarterOffer(): void {
    if (starterOfferAnswered()) return;
    this.starter.passages().then(
      (passages) => {
        if (this.destroyed) return;
        if (this.rootItems().length === 0 && !this.activeItem()) {
          this.starterOffer.set(passages.length ? passages : null);
        }
      },
      () => {
        if (!this.destroyed) this.starterOffer.set(null);
      },
    );
  }

  /** The offer taken: the draft is created and opened with the caret after its passages. */
  async startStarterDraft(): Promise<void> {
    const passages = this.starterOffer();
    if (!passages?.length || this.startingDraft()) return;
    this.startingDraft.set(true);
    try {
      const id = await this.starter.create(passages);
      if (this.destroyed) return;
      if (!id) {
        this.toast.error('None of your imported highlights has words to quote');
        return;
      }
      answerStarterOffer('taken');
      this.starterOffer.set(null);
      this.loadTree();
      this.openWritingDocument(id, false);
    } catch {
      if (!this.destroyed) this.toast.error('Could not start the draft. Try again.');
    } finally {
      if (!this.destroyed) this.startingDraft.set(false);
    }
  }

  /** Not now, and not again on this device: the blank page is the writer's. */
  declineStarterOffer(): void {
    answerStarterOffer('declined');
    this.starterOffer.set(null);
    this.focusPageSoon();
  }

  /** A starter draft opens with the caret on a fresh line after its passages. */
  private caretAfterText(item: WritingContentDto): void {
    this.scheduleTimeout(() => {
      if (this.activeItem()?.id !== item.id) return;
      void this.markdownEditor?.restoreTransientState({ at: 'body-end' }, item.content);
    });
  }

  pageSettingsOpen = signal(false);
  /** The export dialog (issue #1164). */
  exportOpen = signal(false);
  readonly exportBlockedReason = computed(() => {
    const item = this.activeItem();
    if (!item) return 'Nothing to export yet';
    if (hasExportableContent(this.editorText())) return null;
    const parent = this.rootItems().find((candidate) => candidate.id === item.id)?.parentId;
    return parent ? null : 'This document is empty';
  });
  readonly pageSettingsStart = signal<{ view: 'main' | 'citations'; book: string | null }>({
    view: 'main',
    book: null,
  });
  readonly citedBookIds = signal<string[]>([]);
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

  /** The "Move to…" dialog: Top level and the folders, to file the open writing in. */
  readonly moveOpen = signal(false);
  readonly moveBusy = signal(false);
  readonly movePendingKey = signal<string | null>(null);

  readonly moveCurrentParent = computed(() => {
    const id = this.activeItem()?.id;
    return this.rootItems().find((item) => item.id === id)?.parentId ?? null;
  });

  readonly moveRows = computed<MoveRow[]>(() =>
    buildMoveRows(this.rootItems(), this.activeItem()?.id),
  );

  readonly moveLocation = computed(() =>
    moveLocationLabel(this.moveCurrentParent(), this.rootItems()),
  );

  readonly moveHasDestination = computed(() => this.moveRows().some((row) => !row.current));

  readonly moveInitialFocusKey = computed(
    () => this.moveRows().find((row) => !row.current)?.key ?? null,
  );

  openMove(): void {
    if (!this.activeItem()) return;
    this.pageSettingsOpen.set(false);
    this.movePendingKey.set(null);
    this.moveOpen.set(true);
  }

  closeMove(): void {
    if (!this.moveBusy()) this.moveOpen.set(false);
  }

  /** Moves the open writing. */
  moveTo(parentId: string | null): void {
    const item = this.activeItem();
    if (!item || this.moveBusy()) return;
    if (parentId === this.moveCurrentParent()) return;
    this.moveBusy.set(true);
    this.movePendingKey.set(moveRowKey(parentId));
    this.writingsService
      .move(item.id, parentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.moveBusy.set(false);
          this.movePendingKey.set(null);
          this.moveOpen.set(false);
          const folder = parentId
            ? this.rootItems().find((candidate) => candidate.id === parentId)
            : null;
          this.loadTree();
          this.toast.success(folder ? `Moved to “${folder.name}”` : 'Moved to the top level');
        },
        error: () => {
          this.moveBusy.set(false);
          this.movePendingKey.set(null);
          this.toast.error('Could not move it');
        },
      });
  }

  togglePageSettings(): void {
    const open = !this.pageSettingsOpen();
    if (open) {
      this.pageSettingsStart.set({ view: 'main', book: null });
      this.citedBookIds.set(this.writingBooks());
    }
    if (open && this.drawerOpen()) {
      this.showFileSidebar.set(false);
      if (this.isDrawer('reference')) this.showBrainSidebar.set(false);
    }
    this.pageSettingsOpen.set(open);
    if (!open) this.markdownEditor?.focus();
  }

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
    this.scheduleAnimationFrame(focus);
  }

  private captureCompactDrawerCaret(): void {
    this.compactDrawerCaret = this.markdownEditor?.captureTransientState() ?? null;
  }

  private restoreCompactDrawerCaret(): void {
    const state = this.compactDrawerCaret;
    this.compactDrawerCaret = null;
    if (!state || !this.activeItem() || !this.markdownEditor) return;
    this.markdownEditor.flushPendingChange();
    void this.markdownEditor.restoreTransientState(state, this.editorText());
  }

  /** Element that had focus when zen was entered; restored on exit. */
  private zenFocusReturn: HTMLElement | null = null;

  wordCount = computed(() => {
    const text = this.editorText().trim();
    return text ? text.split(/\s+/).length : 0;
  });

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
  readonly selectedWork = computed(() => {
    const id = this.selectedBookId();
    return (
      this.books().find((book) => book.id === id || book.otherEditions?.some((e) => e.id === id)) ??
      null
    );
  });
  readonly selectedEditionOptions = computed<DropdownOption[]>(() =>
    editionChoices(this.selectedWork()).map((edition) => ({
      value: edition.id,
      label: edition.label,
    })),
  );
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

  inspectionReturnLabel = computed(() => {
    if (this.referenceMode() === 'writing') return 'For this document';

    if (this.activeSidebarTab() === 'brain') {
      const topicId = this.selectedTopicId();
      return this.topics().find((topic) => topic.id === topicId)?.name ?? 'Topic notes';
    }

    return this.selectedWork()?.title ?? 'Book notes';
  });

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
    lockScrollWhile(() => this.drawerOpen());
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

      const referenceWasColumn = !this.isCompact() && this.showBrainSidebar();
      this.isCompact.set(compact);
      if (compact) {
        if (referenceWasColumn) this.showBrainSidebar.set(false);
      } else {
        if (!this.drawerOpen()) this.compactDrawerCaret = null;
      }
    };

    window.addEventListener('resize', onResize);
    window.addEventListener('keydown', this.onKeyDown);
    window.addEventListener('pointermove', this.onPointerMove, { passive: true });
    window.addEventListener('pointerdown', this.onPointerDown, { passive: true });
    window.addEventListener('pagehide', this.rememberCaret);

    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      for (const timer of this.pendingTimeouts) clearTimeout(timer);
      this.pendingTimeouts.clear();
      if (typeof window.cancelAnimationFrame === 'function') {
        for (const raf of this.pendingRafs) window.cancelAnimationFrame(raf);
      }
      this.pendingRafs.clear();
      this.rememberCaret();
      window.removeEventListener('resize', onResize);
      window.removeEventListener('keydown', this.onKeyDown);
      window.removeEventListener('pointermove', this.onPointerMove);
      window.removeEventListener('pointerdown', this.onPointerDown);
      window.removeEventListener('pagehide', this.rememberCaret);
      this.bodyClass.clearAll();
    });

    effect((onCleanup) => {
      const text = this.editorText();
      const title = this.editorTitle();
      this.saveNudge();
      const item = untracked(() => this.activeItem());

      if (!item) return;

      this.autosave.scheduleDebouncedSave(item, title, text, this.autosaveAdapter);
      onCleanup(() => this.autosave.cancelDebounce());
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

  private scheduleTimeout(fn: () => void, delay = 0): void {
    if (this.destroyed) return;
    const id = setTimeout(() => {
      this.pendingTimeouts.delete(id);
      if (!this.destroyed) fn();
    }, delay);
    this.pendingTimeouts.add(id);
  }

  private scheduleAnimationFrame(fn: () => void): void {
    if (this.destroyed) return;
    if (typeof window.requestAnimationFrame === 'function') {
      const rafId = window.requestAnimationFrame(() => {
        this.pendingRafs.delete(rafId);
        if (!this.destroyed) fn();
      });
      this.pendingRafs.add(rafId);
    } else {
      this.scheduleTimeout(fn, 0);
    }
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
    const open = this.activeItem();
    if (open) void this.flushActiveWritingBeforeSource(open);
    else this.markdownEditor?.flushPendingChange();
    this.blankParentId = this.newItemParentId();
    this.titleEditing.set(false);
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

  private createFromBlankPage(): void {
    if (this.creatingFromBlank) return;
    this.creatingFromBlank = true;
    const generation = this.blankGeneration;
    this.writingsService
      .create({ name: UNTITLED_DOCUMENT, type: 'Document', parentId: this.blankParentId })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (created) => {
          this.creatingFromBlank = false;
          if (generation !== this.blankGeneration || !this.blankPage()) {
            this.saveLeftBlankPage(created.id, generation);
            return;
          }
          this.blankPageTexts.delete(generation);
          this.chosenFolderId.set(null);
          this.activeItem.set({
            id: created.id,
            name: created.name,
            content: '',
            updatedAt: created.updatedAt,
          });
          this.editorTitle.set(titleFromMarkdown(this.editorText()) || created.name);
          this.blankPage.set(false);
          this.saveNudge.update((n) => n + 1);
          writeStudioValue(STUDIO_LAST_WRITING_KEY, created.id);
          this.recent.record({
            kind: 'document',
            id: created.id,
            label: created.name,
            sub: 'Document',
          });
          this.loadTree();
          this.loadKeptSources(created.id);
        },
        error: () => {
          this.creatingFromBlank = false;
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
    this.writingsService
      .update(id, { name, content })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.loadTree(),
        error: () => this.toast.error('Could not save the page you left'),
      });
  }

  /** Opens where the writer left off: the last writing, or a blank page. */
  private openStartingPage(): void {
    const lastId = readStudioValue(STUDIO_LAST_WRITING_KEY);
    const last = lastId
      ? this.rootItems().find((i) => i.id === lastId && i.type === 'Document')
      : null;
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
    this.scheduleTimeout(apply);
  }

  private focusPageSoon(): void {
    this.scheduleTimeout(() => {
      if (this.drawerOpen()) return;
      this.markdownEditor?.focus();
    });
  }

  private setTyping(typing: boolean): void {
    if (typing === this.isTyping()) return;
    this.isTyping.set(typing);
    this.bodyClass.setTyping(typing);
    if (!typing) this.lastPointer = null;
  }

  private readonly onPointerMove = (event: PointerEvent) => {
    if (!this.isTyping()) return;
    const last = this.lastPointer;
    this.lastPointer = { x: event.clientX, y: event.clientY };
    if (last && Math.hypot(event.clientX - last.x, event.clientY - last.y) > 6) {
      this.setTyping(false);
    }
  };

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
    if (event.defaultPrevented) return;
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
    this.zenFocusReturn =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    this.bodyClass.setZen(true);
  }

  exitZen() {
    if (!this.isZen()) return;
    this.isZen.set(false);
    this.bodyClass.setZen(false);

    const fallback = this.hostElement.nativeElement.querySelector(
      '.zen-toggle',
    ) as HTMLButtonElement | null;
    const target =
      this.zenFocusReturn && this.zenFocusReturn.isConnected ? this.zenFocusReturn : fallback;
    this.zenFocusReturn = null;
    target?.focus();
  }

  /** The file list could not be read (and nothing is held to show instead). */
  readonly treeFailed = signal(false);

  loadTree() {
    this.writingsService
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (items) => {
          this.treeFailed.set(false);
          this.rootItems.set(items);
          const firstLoad = !this.writingTreeLoaded;
          this.writingTreeLoaded = true;
          if (firstLoad && items.length === 0) this.checkStarterOffer();
          const requested = this.requestedWritingId;
          if (requested) this.openWritingFromHandoff(requested);
          else if (firstLoad && !this.activeItem() && !this.blankPage()) this.openStartingPage();
        },
        error: () => {
          if (this.rootItems().length === 0) this.treeFailed.set(true);
          else this.toast.error('Could not refresh the writings. Try again.');
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
      if (typeof queryParamMap.pipe === 'function') {
        queryParamMap
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe((params) => this.handleWritingHandoffId(params.get('writingId')));
      } else {
        const sub = queryParamMap.subscribe((params) =>
          this.handleWritingHandoffId(params.get('writingId')),
        );
        this.destroyRef.onDestroy(() => sub.unsubscribe());
      }
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
    this.writingsService
      .listSources(writingId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (sources) => {
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

    if (this.keptNoteIds().has(noteId)) return;
    if (this.keepingNoteIds().has(noteId)) return;

    this.keepingNoteIds.update((s) => new Set(s).add(noteId));

    this.writingsService
      .addSource(active.id, noteId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (source) => {
          this.keepingNoteIds.update((s) => {
            const next = new Set(s);
            next.delete(noteId);
            return next;
          });
          if (this.activeItem()?.id !== active.id) return;
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
          if (this.activeItem()?.id === active.id) this.loadKeptSources(active.id);
        },
      });
  }

  // ------------------------------------------------------------------
  // The @ source palette (issue #1039)
  // ------------------------------------------------------------------

  readonly paletteQuery = signal<string | null>(null);
  readonly paletteAnchor = signal<PaletteAnchor | null>(null);
  @ViewChild(SourcePaletteComponent) private sourcePalette?: SourcePaletteComponent;

  readonly paletteKeyHandler = (event: KeyboardEvent): boolean =>
    this.sourcePalette?.handleKey(event) ?? false;

  onSourceSuggestion(state: SourceSuggestionState | null): void {
    this.paletteQuery.set(state ? state.query : null);
    this.paletteAnchor.set(state?.rect ?? null);
  }

  insertFromPalette({ source, mode }: PaletteChoice): void {
    const editor = this.markdownEditor;
    if (!editor || !this.activeItem()) return;

    if (source.kind === 'passage' && mode === 'quote') {
      editor.holdSourceQuery();
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

  private async insertStyledFromPalette(
    source: PaletteChoice['source'],
    mode: PaletteChoice['mode'],
  ): Promise<void> {
    const editor = this.markdownEditor;
    if (!editor) return;
    const styled = await this.styledInsertion(source, mode);
    if (this.destroyed) return;
    const markdown =
      styled?.markdown ??
      (mode === 'reference'
        ? buildReferenceMarkdown(source)
        : mode === 'note'
          ? (buildNoteMarkdown(source) ?? buildQuoteMarkdown(source))
          : (buildQuoteMarkdown(source) ?? buildNoteMarkdown(source)));
    if (
      !markdown ||
      !editor.replaceSourceQuery(markdown, mode === 'reference' ? 'inline' : 'block', footnoteOf(styled))
    ) {
      this.toast.error('The editor is not ready for insertion yet');
      return;
    }
    this.keepNote(source.id);
    this.sourcePalette?.remember(source);
    if (styled) void this.warnIfIncomplete(source.bookId);
  }

  private styledInsertion(
    source: CitableSource & { id?: string },
    mode: 'quote' | 'note' | 'reference',
  ): Promise<StyledInsertion | null> {
    return this.citationCoordinator.styledInsertion(
      this.markdownEditor,
      source,
      mode,
      this.pagePrefs.citationStyle(),
    );
  }

  async insertBibliography(): Promise<void> {
    const editor = this.markdownEditor;
    if (!editor) return;
    if (editor.hasBibliography()) {
      this.toast.info('This writing already has a bibliography. It keeps itself up to date.');
      return;
    }
    const style = this.pagePrefs.citationStyle();
    const result = await this.citationCoordinator.buildBibliographyMarkdown(this.writingBooks(), style);
    if (this.destroyed || !result) return;
    if (!result.markdown) {
      this.toast.info('Nothing is cited in this document yet');
      return;
    }
    if (!editor.insertBlockMarkdown(result.markdown)) {
      this.toast.error('The editor is not ready for insertion yet');
      return;
    }
    void this.citationCoordinator.warnIncompleteBibliography(
      result.bookIds,
      style,
      (bookId) => this.openCitationDetails(bookId),
    );
  }

  /** Every book this writing cites or keeps, cited ones first. */
  private writingBooks(): string[] {
    const cited = this.markdownEditor?.citedBooks() ?? [];
    return [...new Set([...cited, ...this.keptSources().map((source) => source.bookId)].filter(Boolean))];
  }

  // --- Live citations (issue #1157) ---

  /** The style, a book's details or the kept sources changed: the open writing's citations follow. */
  protected readonly followCitationInputs = effect(() => {
    this.pagePrefs.citationStyle();
    this.citations.revision();
    this.keptSources();
    untracked(() => this.citationCoordinator.scheduleRefresh(() => this.refreshCitations()));
  });

  /** The editor's citations changed (one went in, moved or out, or a writing opened). */
  onCitationsChange(): void {
    this.citationCoordinator.scheduleRefresh(() => this.refreshCitations());
  }

  /** Writes the open writing's citations again from the books' details, in the chosen style. */
  refreshCitations(): Promise<boolean> {
    const editor = this.markdownEditor;
    const writingId = this.activeItem()?.id;
    return this.citationCoordinator.refreshCitations({
      editor,
      writingId,
      isCurrent: (id, ed) => this.activeItem()?.id === id && this.markdownEditor === ed,
      style: this.pagePrefs.citationStyle(),
      keptSources: this.keptSources(),
    });
  }

  /** Opens the Citations settings at the books missing details, this one first. */
  openCitationDetails(bookId: string | null = null): void {
    if (this.drawerOpen()) {
      this.showFileSidebar.set(false);
      if (this.isDrawer('reference')) this.showBrainSidebar.set(false);
    }
    this.citedBookIds.set(this.writingBooks());
    this.pageSettingsStart.set({ view: 'citations', book: bookId });
    this.pageSettingsOpen.set(false);
    queueMicrotask(() => {
      if (!this.destroyed) this.pageSettingsOpen.set(true);
    });
  }

  private warnIfIncomplete(bookId: string | null | undefined): Promise<void> {
    return this.citationCoordinator.warnIfIncomplete(
      bookId,
      this.activeItem()?.id,
      this.pagePrefs.citationStyle(),
      (id) => this.openCitationDetails(id),
    );
  }

  /** A book's details were saved from the Citations list. */
  onCitationBookUpdated(): void {
    this.citationCoordinator.scheduleRefresh(() => this.refreshCitations(), 0);
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

  private readonly palettePassageOptions: Omit<PassageViewerRequest, 'noteId'> = {
    quoteSelection: (text: string, passage: Passage) =>
      passage.bookId
        ? this.styledInsertion(
            { selectedText: text, bookId: passage.bookId, id: passage.id, passageTitle: passage.title },
            'quote',
          ).then((styled) => this.quoteFromPalettePassage(text, passage, styled))
        : this.quoteFromPalettePassage(text, passage, null),
  };

  private quoteFromPalettePassage(
    text: string,
    passage: Passage,
    styled: StyledInsertion | null,
  ): boolean {
    const markdown =
      styled?.markdown ??
      buildQuoteMarkdown({
        selectedText: text,
        bookTitle: passage.bookTitle,
        bookAuthor: passage.bookAuthor,
        passageTitle: passage.title,
      });
    if (markdown && this.markdownEditor?.insertBlockMarkdown(markdown, footnoteOf(styled))) {
      this.markdownEditor.releaseHeldQuery();
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

    this.writingsService
      .removeSource(active.id, noteId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
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

  handleItemSelected(node: TreeItem & { updatedAt?: string }) {
    if (node.type === 'Folder') {
      this.chosenFolderId.set(node.id);
      return;
    }
    this.chosenFolderId.set(null);

    this.handoffOpenGeneration++;
    this.openWritingDocument(node.id, false, undefined, true);
  }

  private openWritingDocument(
    id: string,
    fromHandoff: boolean,
    handoffGeneration?: number,
    fromFileList = false,
  ): void {
    this.writingsService
      .get(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
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
          this.chosenFolderId.set(null);
          this.titleEditing.set(false);
          this.autoTitle = contentDto.name === UNTITLED_DOCUMENT;
          this.activeItem.set(contentDto);
          writeStudioValue(STUDIO_LAST_WRITING_KEY, contentDto.id);
          this.recent.record({
            kind: 'document',
            id: contentDto.id,
            label: contentDto.name,
            sub: 'Document',
          });
          this.editorTitle.set(contentDto.name);
          this.editorText.set(contentDto.content);
          this.inspectedSource.set(null);

          if ((fromFileList || (fromHandoff && this.isCompact())) && this.drawerOpen()) {
            this.compactDrawerCaret = null;
            this.showFileSidebar.set(false);
            if (fromHandoff && this.isDrawer('reference')) this.showBrainSidebar.set(false);
          }

          if (fromHandoff) this.restoreSourceReturnSurface(contentDto);
          else this.restoreRememberedCaret(contentDto);
          if (this.starter.takeCaret(contentDto.id)) this.caretAfterText(contentDto);
          this.loadKeptSources(contentDto.id);
        },
        error: () => {
          if (
            fromHandoff &&
            (handoffGeneration !== this.handoffOpenGeneration || this.requestedWritingId !== id)
          ) {
            return;
          }
          this.toast.error(
            fromHandoff ? 'That writing is unavailable' : 'Could not open the writing. Try again.',
          );
        },
      });
  }

  handleItemMove(event: { item: TreeItem; newParentId: string | null }) {
    this.writingsService
      .move(event.item.id, event.newParentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.loadTree();
        },
        error: () => {
          this.toast.error('Could not move the item');
          this.loadTree();
        },
      });
  }

  createItem(type: 'Folder' | 'Document') {
    if (type === 'Document') {
      this.startBlankPage();
      return;
    }
    this.writingsService
      .create({ name: 'Untitled folder', type, parentId: this.newItemParentId() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (created) => {
          if (!this.showFileSidebar()) this.setRailOpen('files', true);
          this.editingId.set(created.id);
          this.loadTree();
        },
        error: () => this.toast.error('Could not create a folder'),
      });
  }

  /** The open writing's name in the header, being edited where it is shown. */
  readonly titleEditing = signal(false);

  startTitleRename(): void {
    this.renameError.set(null);
    this.titleEditing.set(true);
  }

  stopTitleRename(): void {
    if (this.renameBusy()) return;
    this.renameError.set(null);
    this.titleEditing.set(false);
  }

  startRename(node: TreeItem) {
    this.renameError.set(null);
    this.editingId.set(node.id);
  }

  saveRename(id: string, newName: string) {
    if (this.renameBusy()) return;
    this.renameBusy.set(true);
    this.renameError.set(null);
    this.writingsService
      .update(id, { name: newName })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.renameBusy.set(false);
          this.loadTree();
          this.editingId.set(null);
          this.titleEditing.set(false);
          this.recent.relabel('document', id, newName);

          if (this.activeItem()?.id === id) {
            this.editorTitle.set(newName);
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

  /** The delete is on its way: the confirm stays open and says so. */
  readonly deletingItem = signal(false);

  readonly deleteCopy = computed(() =>
    buildDeleteWritingCopy(this.pendingDelete(), this.rootItems()),
  );

  cancelDeleteItem(): void {
    if (!this.deletingItem()) this.pendingDelete.set(null);
  }

  confirmDeleteItem(): void {
    const id = this.pendingDelete();
    if (!id || this.deletingItem()) return;
    const { name } = this.deleteCopy();
    this.deletingItem.set(true);

    this.writingsService
      .delete(id)
      .pipe(
        finalize(() => this.deletingItem.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          this.pendingDelete.set(null);
          this.loadTree();
          this.toast.success(name ? `Deleted “${name}”` : 'Deleted');

          writeCarets(readCarets().filter((entry) => entry.id !== id));
          if (readStudioValue(STUDIO_LAST_WRITING_KEY) === id) {
            writeStudioValue(STUDIO_LAST_WRITING_KEY, null);
          }
          if (this.activeItem()?.id === id) {
            this.activeItem.set(null);
            this.startBlankPage();
          }
        },
        error: () => this.toast.error('Could not delete it. Try again.'),
      });
  }

  // --- Brain & Notes Logic ---

  loadBrain() {
    this.topicIndexState.set('loading');
    this.topicsService
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
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
        groupByWork: true,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
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
    this.topicsService
      .get(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (d) => {
          const mapped: Note[] = d.notes.map(noteContextToNote);
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
    this.notesService
      .list(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
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

  onReferenceScrollCaptured(event: { key: ReferenceScrollKey; scrollTop: number }): void {
    this.referenceScrollPositions[event.key] = event.scrollTop;
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
    this.scheduleAnimationFrame(() => {
      const element = this.hostElement.nativeElement.querySelector(
        `[data-reference-scroll="${key}"]`,
      );
      if (element) element.scrollTop = this.referenceScrollPositions[key];
    });
  }

  referencePreview(note: Note): string {
    return computeReferencePreview(note);
  }

  sourceProvenance(note: Note): string {
    return formatSourceProvenance(note);
  }

  sourceRowLabel(note: Note): string {
    return formatSourceRowLabel(note);
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
    if (isPassage(source)) return false;
    return !!source && hasMeaningfulSelectedText(source) && !!sourceHumanLabel(source);
  }

  isPassageSource(source: Note | null = this.inspectedSource()): boolean {
    return isPassage(source);
  }

  /** ⌘ on Apple devices, Ctrl elsewhere: what the shortcut sheet shows. */
  readonly modKey = modifierKeyLabel();
  readonly assistantLauncher = inject(AssistantLauncherService);

  readonly passageViewerOptions: Omit<PassageViewerRequest, 'noteId'> = {
    quoteSelection: (text: string, passage: Passage) => this.quotePassageSelection(text, passage),
  };

  readPassage(source: Note): void {
    this.passageViewer.open(source.id, this.passageViewerOptions);
  }

  private async quotePassageSelection(text: string, passage: Passage): Promise<boolean> {
    const styled = await this.styledInsertion(
      { selectedText: text, bookId: passage.bookId, id: passage.id, passageTitle: passage.title },
      'quote',
    );
    if (this.destroyed) return false;
    const markdown =
      styled?.markdown ??
      buildQuoteMarkdown({
        selectedText: text,
        bookTitle: passage.bookTitle,
        bookAuthor: passage.bookAuthor,
        passageTitle: passage.title,
      });
    if (!markdown || !this.activeItem() || !this.markdownEditor) {
      this.toast.error('Open a document and place the cursor in the editor first');
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
    if (this.destroyed) return;
    await this.insertSourceMarkdown(
      styled?.markdown ?? buildQuoteMarkdown(source),
      styled,
      source.bookId,
    );
  }

  async insertNote(source: Note): Promise<void> {
    const styled = await this.styledInsertion(source, 'note');
    if (this.destroyed) return;
    await this.insertSourceMarkdown(
      styled?.markdown ?? buildNoteMarkdown(source),
      styled,
      source.bookId,
    );
  }

  async insertReference(source: Note): Promise<void> {
    const styled = await this.styledInsertion(source, 'reference');
    if (this.destroyed) return;
    await this.insertSourceMarkdown(
      styled?.markdown ?? buildReferenceMarkdown(source),
      styled,
      source.bookId,
    );
  }

  private async insertSourceMarkdown(
    markdown: string | null,
    styled: StyledInsertion | null = null,
    bookId: string | null = null,
  ): Promise<void> {
    if (!markdown) return;
    if (!this.activeItem() || !this.markdownEditor) {
      this.toast.error('Open a document and place the cursor in the editor first');
      return;
    }

    const inserted = await this.markdownEditor.insertMarkdown(markdown, footnoteOf(styled));
    if (this.destroyed) return;
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
      this.toast.error('Open a document before opening a source');
      return;
    }

    if (!(await this.flushActiveWritingBeforeSource(active))) return;

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

  private flushActiveWritingBeforeSource(item: WritingContentDto): Promise<boolean> {
    return this.autosave.flushBeforeNavigation(item, this.autosaveAdapter);
  }

  /** "Keep mine": saves the typed text over the newer copy. */
  keepMineAfterConflict(): void {
    this.autosave.keepMineAfterConflict(this.autosaveAdapter);
  }

  /** "Load the newer version". Copies user text to clipboard first. */
  loadNewerAfterConflict(): void {
    this.autosave.loadNewerAfterConflict(this.autosaveAdapter);
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
      this.consumeSourceReturnSnapshot();
    }
  }

  private prepareSourceReturnRestore(): void {
    const state = window.history.state;
    this.pendingSourceReturnSnapshot = readStudioSourceReturnSnapshot(state);

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

    queueMicrotask(() => {
      if (!this.destroyed) this.tryRestoreEditorSourceReturn();
    });
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
