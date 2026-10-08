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
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { DragDropModule } from '@angular/cdk/drag-drop';
import { firstValueFrom } from 'rxjs';

import { WritingsService } from '../core/services/writings.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService, TopicDto, NoteContextDto } from '../core/services/topics.service';
import { BooksService, Book as BookDto } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { NoteCardComponent } from '../ui/note-card.component/note-card.component';
import { WritingDto, WritingContentDto, WritingSourceDto } from '../core/dtos/writing.dtos';
import { Note } from '../core/dtos/note.dtos';
import {
  MarkdownEditorComponent,
  MarkdownEditorTransientState,
} from '../ui/markdown-editor/markdown-editor.component';
import { FlatTreeComponent } from '../ui/flat-tree/flat-tree.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { ButtonComponent } from '../ui/button/button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { BadgeComponent } from '../ui/badge/badge.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
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

/** localStorage flag for typewriter mode in the studio. */
const TYPEWRITER_KEY = 'nostos.typewriter';
const STUDIO_SIDEBAR_WIDTH_KEY = 'nostos.studio.leftSidebarWidth';
const STUDIO_FILES_RAIL_KEY = 'nostos.studio.filesRailOpen';
const STUDIO_REFERENCE_RAIL_KEY = 'nostos.studio.referenceRailOpen';
const STUDIO_SIDEBAR_MIN = 220;
const STUDIO_SIDEBAR_MAX = 420;
const STUDIO_SIDEBAR_DEFAULT_MAX = 280;
const STUDIO_COMPACT_WIDTH = 900;
const STUDIO_COMPACT_HEIGHT = 500;

function isStudioCompactViewport(): boolean {
  return window.innerWidth <= STUDIO_COMPACT_WIDTH || window.innerHeight <= STUDIO_COMPACT_HEIGHT;
}

function studioSidebarMaxWidth(): number {
  return Math.max(
    STUDIO_SIDEBAR_MIN,
    Math.min(STUDIO_SIDEBAR_MAX, Math.floor(window.innerWidth * 0.42)),
  );
}

function clampStudioSidebarWidth(width: number): number {
  return Math.min(studioSidebarMaxWidth(), Math.max(STUDIO_SIDEBAR_MIN, Math.round(width)));
}

function readStudioSidebarWidth(): number {
  try {
    const stored = Number(localStorage.getItem(STUDIO_SIDEBAR_WIDTH_KEY));
    return Number.isFinite(stored) && stored > 0
      ? clampStudioSidebarWidth(stored)
      : Math.max(
          STUDIO_SIDEBAR_MIN,
          Math.min(STUDIO_SIDEBAR_DEFAULT_MAX, Math.floor(window.innerWidth * 0.24)),
        );
  } catch {
    return Math.max(
      STUDIO_SIDEBAR_MIN,
      Math.min(STUDIO_SIDEBAR_DEFAULT_MAX, Math.floor(window.innerWidth * 0.24)),
    );
  }
}

function readStudioRailOpen(key: string): boolean {
  try {
    const stored = localStorage.getItem(key);
    return stored === null ? true : stored === '1';
  } catch {
    return true;
  }
}

function readTypewriter(): boolean {
  try {
    return localStorage.getItem(TYPEWRITER_KEY) === '1';
  } catch {
    return false;
  }
}

@Component({
  selector: 'app-writing-studio',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    NostosIconComponent,
    DragDropModule,
    FlatTreeComponent,
    NoteCardComponent,
    MarkdownEditorComponent,
    IconButtonComponent,
    ButtonComponent,
    InputDirective,
    BadgeComponent,
    ConfirmModal,
  ],
  templateUrl: './writing-studio.component.html',
  styleUrls: ['./writing-studio.component.css'],
})
export class WritingStudio implements OnInit, AfterViewInit {
  private writingsService = inject(WritingsService);
  private toast = inject(ToastService);
  private topicsService = inject(TopicsService);
  private booksService = inject(BooksService);
  private notesService = inject(NotesService);
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
  showFileSidebar = signal(
    !this.initialCompactMode && readStudioRailOpen(STUDIO_FILES_RAIL_KEY),
  );
  showBrainSidebar = signal(
    !this.initialCompactMode && readStudioRailOpen(STUDIO_REFERENCE_RAIL_KEY),
  );
  private compactDrawerCaret: MarkdownEditorTransientState | null = null;

  /** Desktop file tree width. The divider is user-resizable; mobile owns its drawer width. */
  leftSidebarWidth = signal(readStudioSidebarWidth());

  activeSidebarTab = signal<'brain' | 'notes'>('brain');

  /** Reference surface top-level mode: "For this writing" vs "Library" (issue #491) */
  referenceMode = signal<'writing' | 'library'>('writing');

  /** Kept source notes for the active writing document */
  keptSources = signal<WritingSourceDto[]>([]);

  /** Note IDs currently being kept (in-flight addSource request) to prevent concurrent races */
  keepingNoteIds = signal<Set<string>>(new Set());

  /** Set of kept note IDs for fast lookup */
  keptNoteIds = computed(() => new Set(this.keptSources().map((s) => s.id)));

  /** Source currently being inspected. Browsing never mutates the writing. */
  inspectedSource = signal<Note | null>(null);

  /**
   * Kept sources mapped to Note shape for NoteCardComponent,
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
        sourceAnchorKind: s.sourceAnchorKind ?? undefined,
        sourceAnchorValue: s.sourceAnchorValue ?? undefined,
        anchorVerified: s.anchorVerified ?? undefined,
      }));
  });

  rootItems = signal<WritingDto[]>([]);
  activeItem = signal<WritingContentDto | null>(null);

  editingId = signal<string | null>(null);

  editorText = signal('');
  editorTitle = signal('');
  saveStatus = signal<'Saved' | 'Saving...' | 'Unsaved'>('Saved');

  // Zen (focus) mode — issue #49. Session-only: never persisted.
  isZen = signal(false);

  /** Typewriter mode: keep the caret line centered. Persisted across visits. */
  typewriter = signal(readTypewriter());

  toggleTypewriter(): void {
    const next = !this.typewriter();
    try {
      localStorage.setItem(TYPEWRITER_KEY, next ? '1' : '0');
    } catch {
      // Toggle still applies for the session even if it won't persist.
    }
    this.typewriter.set(next);
  }

  leftSidebarMaxWidth(): number {
    return studioSidebarMaxWidth();
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

  private setRailOpen(
    rail: 'files' | 'reference',
    open: boolean,
    fromHeading = false,
    restoreCaretOnClose = true,
  ): void {
    const isFiles = rail === 'files';

    if (this.isCompact()) {
      if (open) {
        this.captureCompactDrawerCaret();
        if (isFiles) this.showBrainSidebar.set(false);
        else this.showFileSidebar.set(false);
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

    if (isFiles) this.showFileSidebar.set(open);
    else this.showBrainSidebar.set(open);
    this.persistStudioRailOpen(
      isFiles ? STUDIO_FILES_RAIL_KEY : STUDIO_REFERENCE_RAIL_KEY,
      open,
    );

    // The heading control is removed with its rail when it collapses, so return
    // focus to the persistent editor-header toggle in that case.
    if (!open && fromHeading) this.focusHeaderRailToggle(rail);
  }

  private persistStudioRailOpen(key: string, open: boolean): void {
    try {
      localStorage.setItem(key, open ? '1' : '0');
    } catch {
      // The rail still toggles for this session when storage is unavailable.
    }
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

  startLeftSidebarResize(event: PointerEvent): void {
    if (this.isCompact() || event.button !== 0) return;

    const handle = event.currentTarget as HTMLElement | null;
    if (!handle) return;

    event.preventDefault();
    const startX = event.clientX;
    const startWidth = this.leftSidebarWidth();

    handle.setPointerCapture?.(event.pointerId);

    const onPointerMove = (moveEvent: PointerEvent) => {
      this.leftSidebarWidth.set(
        clampStudioSidebarWidth(startWidth + moveEvent.clientX - startX),
      );
    };

    const finish = (finishEvent: PointerEvent) => {
      handle.removeEventListener('pointermove', onPointerMove);
      handle.removeEventListener('pointerup', finish);
      handle.removeEventListener('pointercancel', finish);
      if (handle.hasPointerCapture?.(finishEvent.pointerId)) {
        handle.releasePointerCapture(finishEvent.pointerId);
      }
      this.persistLeftSidebarWidth();
    };

    handle.addEventListener('pointermove', onPointerMove);
    handle.addEventListener('pointerup', finish);
    handle.addEventListener('pointercancel', finish);
  }

  resizeLeftSidebarWithKeyboard(event: KeyboardEvent): void {
    if (this.isCompact()) return;

    const delta = event.key === 'ArrowLeft' ? -16 : event.key === 'ArrowRight' ? 16 : 0;
    if (delta === 0) return;

    event.preventDefault();
    this.leftSidebarWidth.set(clampStudioSidebarWidth(this.leftSidebarWidth() + delta));
    this.persistLeftSidebarWidth();
  }

  private persistLeftSidebarWidth(): void {
    try {
      localStorage.setItem(STUDIO_SIDEBAR_WIDTH_KEY, String(this.leftSidebarWidth()));
    } catch {
      // Resizing still works for the session if storage is unavailable.
    }
  }

  /** Element that had focus when zen was entered; restored on exit. */
  private zenFocusReturn: HTMLElement | null = null;

  wordCount = computed(() => {
    const text = this.editorText().trim();
    return text ? text.split(/\s+/).length : 0;
  });

  /**
   * Word count reported by the TinyMCE wordcount plugin (authoritative for
   * the rendered document). Falls back to the markdown-derived count until
   * the editor boots and emits its first count.
   */
  editorWordCount = signal<number | null>(null);
  displayWordCount = computed(() => this.editorWordCount() ?? this.wordCount());

  // Brain / Topics State
  brainQuery = signal('');
  topics = signal<TopicDto[]>([]);
  selectedTopicId = signal<string | null>(null);
  selectedTopicNotes = signal<Note[]>([]);

  // Books / Notes State
  books = signal<BookDto[]>([]);
  bookQuery = signal('');
  selectedBookId = signal<string | null>(null);
  selectedBookNotes = signal<Note[]>([]);

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
    const onResize = () => {
      const compact = isStudioCompactViewport();
      this.leftSidebarWidth.set(clampStudioSidebarWidth(this.leftSidebarWidth()));

      if (compact === this.isCompact()) return;

      this.isCompact.set(compact);
      if (compact) {
        this.captureCompactDrawerCaret();
        this.showFileSidebar.set(false);
        this.showBrainSidebar.set(false);
      } else {
        this.compactDrawerCaret = null;
        this.showFileSidebar.set(readStudioRailOpen(STUDIO_FILES_RAIL_KEY));
        this.showBrainSidebar.set(readStudioRailOpen(STUDIO_REFERENCE_RAIL_KEY));
      }
    };

    window.addEventListener('resize', onResize);
    this.destroyRef.onDestroy(() => window.removeEventListener('resize', onResize));

    // Zen mode: Esc exits focus mode; listener removed on destroy.
    window.addEventListener('keydown', this.onKeyDown);
    this.destroyRef.onDestroy(() => {
      window.removeEventListener('keydown', this.onKeyDown);
      this.writingHandoffSubscription?.unsubscribe();
      // Never leave the body class behind if the component is torn down mid-zen.
      document.body.classList.remove('nostos-zen');
    });

    effect((onCleanup) => {
      const text = this.editorText();
      const title = this.editorTitle();
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
    if (!this.isCompact()) return;
    const returnTo: 'files' | 'reference' = this.showFileSidebar() ? 'files' : 'reference';
    this.showFileSidebar.set(false);
    this.showBrainSidebar.set(false);
    this.focusHeaderRailToggle(returnTo);
    this.restoreCompactDrawerCaret();
  }

  // --- Zen (focus) mode — issue #49 ---

  private readonly onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Escape' && this.isZen()) {
      this.exitZen();
    }
  };

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
        this.writingTreeLoaded = true;
        const requested = this.requestedWritingId;
        if (requested) this.openWritingFromHandoff(requested);
      },
      error: () => {
        this.writingTreeLoaded = true;
        if (this.requestedWritingId) this.toast.error('That writing is unavailable');
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
    this.writingsService.listSources(writingId).subscribe({
      next: (sources) => {
        // A slow response for a document that is no longer active must not overwrite the
        // kept list of the document the writer has since switched to.
        if (this.activeItem()?.id !== writingId) return;
        this.keptSources.set(sources);
        const pendingInspection = this.pendingInspectedSourceRestore;
        if (pendingInspection?.mode === 'writing') {
          const match = this.keptNotes().find((note) => note.id === pendingInspection.id);
          if (match) this.inspectedSource.set(match);
          this.pendingInspectedSourceRestore = null;
        }
      },
      error: () => this.toast.error('Failed to load kept sources'),
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
        this.toast.error('Failed to keep source');
        // Re-fetch kept list on error so UI does not leave an optimistic lie
        if (this.activeItem()?.id === active.id) this.loadKeptSources(active.id);
      },
    });
  }

  removeKeptSource(noteId: string, event?: Event) {
    event?.stopPropagation();
    const active = this.activeItem();
    if (!active) return;

    this.writingsService.removeSource(active.id, noteId).subscribe({
      next: () => {
        if (this.activeItem()?.id !== active.id) return;
        this.keptSources.update((prev) => prev.filter((s) => s.id !== noteId));
        if (this.inspectedSource()?.id === noteId) this.inspectedSource.set(null);
      },
      error: () => {
        this.toast.error('Failed to remove source');
        if (this.activeItem()?.id === active.id) this.loadKeptSources(active.id);
      },
    });
  }

  handleItemSelected(node: any) {
    const item = node as WritingDto;
    if (item.type === 'Folder') return;

    // Manual selection wins over any slower route-handoff request already in flight.
    this.handoffOpenGeneration++;
    this.openWritingDocument(item.id, false);
  }

  private openWritingDocument(
    id: string,
    fromHandoff: boolean,
    handoffGeneration?: number,
  ): void {
    this.writingsService.get(id).subscribe({
      next: (contentDto) => {
        if (
          fromHandoff &&
          (handoffGeneration !== this.handoffOpenGeneration || this.requestedWritingId !== id)
        ) {
          return;
        }
        this.activeItem.set(contentDto);
        this.editorTitle.set(contentDto.name);
        this.editorText.set(contentDto.content);
        this.inspectedSource.set(null);

        if (this.isCompact()) {
          const returnTo: 'files' | 'reference' = this.showFileSidebar()
            ? 'files'
            : 'reference';
          this.compactDrawerCaret = null;
          this.showFileSidebar.set(false);
          if (fromHandoff) this.showBrainSidebar.set(false);
          this.focusHeaderRailToggle(returnTo);
        }

        if (fromHandoff) this.restoreSourceReturnSurface(contentDto);
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
        this.toast.error('Failed to move item');
        this.loadTree();
      },
    });
  }

  createItem(type: 'Folder' | 'Document') {
    const name = prompt(`Enter ${type} Name:`);
    if (!name) return;
    this.writingsService.create({ name, type, parentId: null }).subscribe(() => this.loadTree());
  }

  startRename(node: any) {
    this.editingId.set(node.id);
  }

  saveRename(id: string, newName: string) {
    if (!newName.trim()) {
      this.cancelRename();
      return;
    }

    this.writingsService.update(id, { name: newName }).subscribe(() => {
      this.loadTree();
      this.editingId.set(null);

      if (this.activeItem()?.id === id) {
        this.editorTitle.set(newName);
      }
    });
  }

  cancelRename() {
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

      if (this.activeItem()?.id === id) {
        this.activeItem.set(null);
        this.editorText.set('');
        this.editorTitle.set('');
        this.keptSources.set([]);
        this.inspectedSource.set(null);
      }
    });
  }

  // --- Brain & Notes Logic ---

  loadBrain() {
    this.topicsService.list().subscribe((data) => this.topics.set(data));
  }

  loadBooks() {
    this.booksService
      .list({
        page: 1,
        pageSize: 50,
      })
      .subscribe((data) => {
        this.books.set(data.items);
      });
  }

  setReferenceMode(mode: 'writing' | 'library'): void {
    if (this.referenceMode() !== mode) this.inspectedSource.set(null);
    this.referenceMode.set(mode);
  }

  setLibraryTab(tab: 'brain' | 'notes'): void {
    if (this.activeSidebarTab() !== tab) this.inspectedSource.set(null);
    this.activeSidebarTab.set(tab);
  }

  selectTopic(id: string) {
    this.inspectedSource.set(null);
    this.selectedTopicId.set(id);
    this.topicsService.get(id).subscribe((d) => {
      // Map NoteContextDto (noteId) → Note (id) for NoteCardComponent compatibility
      const mapped: Note[] = d.notes.map((n: NoteContextDto) => ({
        id: n.noteId,
        bookId: n.bookId,
        content: n.content,
        cfiRange: n.cfiRange,
        selectedText: n.selectedText,
        createdAt: '',
        bookTitle: n.bookTitle,
      }));
      this.selectedTopicNotes.set(mapped);
      const pendingInspection = this.pendingInspectedSourceRestore;
      if (pendingInspection?.mode === 'library' && pendingInspection.tab === 'brain') {
        const match = mapped.find((note) => note.id === pendingInspection.id);
        if (match) this.inspectedSource.set(match);
        this.pendingInspectedSourceRestore = null;
      }
    });
  }

  selectBook(id: string) {
    this.inspectedSource.set(null);
    this.selectedBookId.set(id);
    this.notesService.list(id).subscribe((notes) => {
      this.selectedBookNotes.set(notes);
      const pendingInspection = this.pendingInspectedSourceRestore;
      if (pendingInspection?.mode === 'library' && pendingInspection.tab === 'notes') {
        const match = notes.find((note) => note.id === pendingInspection.id);
        if (match) this.inspectedSource.set(match);
        this.pendingInspectedSourceRestore = null;
      }
    });
  }

  inspectSource(note: Note): void {
    this.inspectedSource.set(note);
  }

  closeInspectedSource(): void {
    this.inspectedSource.set(null);
  }

  sourceDisplayLocator(source: Note): string | null {
    return sourceHumanLabel(source)?.locator ?? null;
  }

  canInsertQuote(source: Note | null = this.inspectedSource()): boolean {
    return !!source && hasMeaningfulSelectedText(source) && !!sourceHumanLabel(source);
  }

  canInsertNote(source: Note | null = this.inspectedSource()): boolean {
    return !!source && hasMeaningfulNoteContent(source) && !!sourceHumanLabel(source);
  }

  canInsertReference(source: Note | null = this.inspectedSource()): boolean {
    return !!source && !!sourceHumanLabel(source);
  }

  async insertQuote(source: Note): Promise<void> {
    await this.insertSourceMarkdown(buildQuoteMarkdown(source));
  }

  async insertNote(source: Note): Promise<void> {
    await this.insertSourceMarkdown(buildNoteMarkdown(source));
  }

  async insertReference(source: Note): Promise<void> {
    await this.insertSourceMarkdown(buildReferenceMarkdown(source));
  }

  private async insertSourceMarkdown(markdown: string | null): Promise<void> {
    if (!markdown) return;
    if (!this.activeItem() || !this.markdownEditor) {
      this.toast.error('Open a writing and place the cursor in the editor first');
      return;
    }

    const inserted = await this.markdownEditor.insertMarkdown(markdown);
    if (!inserted) {
      this.toast.error('The editor is not ready for insertion yet');
      return;
    }

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

      this.saveStatus.set('Saving...');
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
        if (title !== item.name) this.loadTree();
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
