import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Component, input, output, Input } from '@angular/core';
import { BehaviorSubject, Observable, Subject, of, throwError } from 'rxjs';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { By } from '@angular/platform-browser';
import { StudioPreferencesService } from './page-settings/studio-preferences.service';
import { CitationService } from './citations/citation.service';

import { WritingStudio, titleFromMarkdown } from './writing-studio.component';
import { normalizeStudioPreferences } from './page-settings/studio-preferences.service';
import { ThemeService } from '../core/services/theme.service';
import { RecentItemsService } from '../ui/command-palette/recent-items.service';
import { WritingsService } from '../core/services/writings.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import { BooksService } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { FlatTreeComponent } from '../ui/flat-tree/flat-tree.component';
import { NoteItemComponent } from '../ui/note-item/note-item.component';
import { MarkdownEditorComponent } from '../ui/markdown-editor/markdown-editor.component';
import { PassageViewerService } from '../ui/passage-viewer/passage-viewer.service';
import { NOSTOS_ICONS } from '../ui/icon/nostos-icons';
import { WritingContentDto, WritingSourceDto } from '../core/dtos/writing.dtos';

// Heavy editor / UI children are stubbed out: MarkdownEditor boots a real
// TipTap editor (covered by its own spec), and the tree/note cards pull in drag-drop and
// unrelated pipes. Same pattern as reader-shell.component.spec.ts.
@Component({ selector: 'app-markdown-editor', standalone: true, template: '' })
class MarkdownEditorStub {
  readonly initialContent = input<string>('');
  readonly typewriter = input<boolean>(false);
  readonly spellcheck = input<boolean>(true);
  readonly sounds = input<boolean>(false);
  readonly soundVolume = input<number>(0.6);
  readonly soundPack = input<string>('typewriter');
  readonly contentChange = output<string>();
  readonly wordCountChange = output<number>();
  readonly insertMarkdown = vi.fn(async (_markdown: string) => true);
  readonly captureTransientState = vi.fn(() => ({
    bookmark: { start: [1, 0], forward: true },
    scrollY: 240,
  }));
  readonly restoreTransientState = vi.fn(async (_state: unknown, _expectedMarkdown: string) => true);
  readonly sourceSuggestionKeyDown = input<((event: KeyboardEvent) => boolean) | null>(null);
  readonly sourceSuggestionActiveId = input<string | null>(null);
  readonly sourceSuggestion = output<unknown>();
  readonly replaceSourceQuery = vi.fn((_markdown: string, _placement: 'block' | 'inline') => true);
  readonly clearSourceQuery = vi.fn(() => true);
  readonly closeSourceQuery = vi.fn();
  readonly insertBlockMarkdown = vi.fn((_markdown: string) => true);
  readonly nextFootnoteNumber = vi.fn(() => 1);
  readonly appendFootnote = vi.fn((_number: number, _markdown: string) => true);
  readonly citedBooks = vi.fn((): string[] => []);
  readonly hasBibliography = vi.fn(() => false);
  readonly refreshCitations = vi.fn((_renderer: unknown) => false);
  readonly bibliographyRequested = output<void>();
  readonly citationsChange = output<string>();
  readonly focus = vi.fn();
  readonly undo = vi.fn();
}

@Component({ selector: 'app-flat-tree', standalone: true, template: '' })
class FlatTreeStub {
  readonly items = input.required<unknown[]>();
  readonly activeId = input<string | null>(null);
  readonly editingId = input<string | null>(null);
  readonly renameError = input<string | null>(null);
  readonly renameBusy = input(false);
  readonly nodeSelected = output<unknown>();
  readonly nodeMoved = output<unknown>();
  readonly nodeRenamed = output<unknown>();
  readonly nodeDeleted = output<string>();
  readonly nodeRenameSaved = output<unknown>();
  readonly nodeRenameCancelled = output<void>();
}

@Component({ selector: 'app-note-item', standalone: true, template: '' })
class NoteItemStub {
  @Input() note: unknown;
  @Input() topicMap: Map<string, unknown> = new Map();
  @Input() readOnly = false;
  @Input() showMeta = true;
  @Input() passageViewerOptions: unknown = null;
}

describe('WritingStudio zen mode (issue #49) + paper frame (expert design §2/§5)', () => {
  let fixture: ComponentFixture<WritingStudio>;
  let component: WritingStudio;

  const sampleDocument: WritingContentDto = {
    id: 'doc-1',
    name: 'Sample',
    content: 'one two three four',
    updatedAt: '2026-08-13T00:00:00Z',
  };

  /** All component CSS injected by Angular (emulated encapsulation). */
  const componentCss = (): string =>
    Array.from(document.querySelectorAll('style'))
      .map((s) => s.textContent ?? '')
      .join('\n');

  beforeEach(async () => {
    TestBed.overrideComponent(WritingStudio, {
      remove: { imports: [FlatTreeComponent, NoteItemComponent, MarkdownEditorComponent] },
      add: { imports: [FlatTreeStub, NoteItemStub, MarkdownEditorStub] },
    });

    await TestBed.configureTestingModule({
      imports: [WritingStudio],
      providers: [
        {
          provide: WritingsService,
          useValue: {
            list: vi.fn(() => of([])),
            get: vi.fn(() => of(sampleDocument)),
            create: vi.fn(() => of({})),
            update: vi.fn(() => of({})),
            delete: vi.fn(() => of({})),
            move: vi.fn(() => of({})),
            listSources: vi.fn(() => of([])),
            addSource: vi.fn(() => of({})),
            removeSource: vi.fn(() => of({})),
          },
        },
        { provide: ToastService, useValue: { error: vi.fn(), success: vi.fn() } },
        { provide: TopicsService, useValue: { list: vi.fn(() => of([])), get: vi.fn() } },
        { provide: BooksService, useValue: { list: vi.fn(() => of({ items: [] })) } },
        { provide: NotesService, useValue: { list: vi.fn(() => of([])), getMany: vi.fn(() => of([])) } },
      ],
    }).compileComponents();

    // Clean slate in case a previous test left state behind.
    localStorage.clear();
    document.body.classList.remove('nostos-zen');
    document.documentElement.removeAttribute('data-theme');

    fixture = TestBed.createComponent(WritingStudio);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    document.body.classList.remove('nostos-zen');
    document.documentElement.removeAttribute('data-palette');
  });

  it('wears the app theme and leaves it alone on leaving (#1074)', () => {
    const root = document.documentElement;
    const theme = TestBed.inject(ThemeService);

    theme.setChoice('study');
    fixture.detectChanges();
    expect(root.getAttribute('data-palette')).toBe('study');
    expect(root.getAttribute('data-theme')).toBe('dark');

    fixture.destroy();
    expect(root.getAttribute('data-palette')).toBe('study');
    expect(root.getAttribute('data-theme')).toBe('dark');
    theme.setChoice('system');
  });

  it('gives the Reference header one control, close, on every screen', () => {
    for (const compact of [false, true]) {
      component.isCompact.set(compact);
      component.showBrainSidebar.set(true);
      fixture.detectChanges();
      const buttons = fixture.nativeElement.querySelectorAll('.sidebar-right .sidebar-title-row button');
      expect(buttons.length).toBe(1);
      expect(buttons[0].classList).toContain('reference-rail-collapse');
    }
  });

  const zenToggle = (): HTMLButtonElement | null =>
    fixture.nativeElement.querySelector('.zen-toggle') as HTMLButtonElement | null;

  const zenExit = (): HTMLButtonElement | null =>
    fixture.nativeElement.querySelector('.zen-exit') as HTMLButtonElement | null;

  const deskTelemetry = (): HTMLElement | null =>
    fixture.nativeElement.querySelector('.editor-desk-telemetry') as HTMLElement | null;

  const headerSaveStatus = (): HTMLElement | null =>
    fixture.nativeElement.querySelector('.header-save-status') as HTMLElement | null;

  const openDocument = () => {
    component.activeItem.set(sampleDocument);
    component.editorTitle.set(sampleDocument.name);
    component.editorText.set(sampleDocument.content);
    fixture.detectChanges();
  };

  // --- Nostos UI v1 migration boundaries ---
  it('uses canonical UI primitives for ordinary Studio controls', () => {
    component.isCompact.set(true);
    component.referenceMode.set('library');
    component.topics.set([{ id: 'topic-1', name: 'Memory', usageCount: 3 } as any]);
    fixture.detectChanges();

    const newDocument = fixture.nativeElement.querySelector(
      '.files-new-document',
    ) as HTMLButtonElement;
    expect(newDocument.classList.contains('nostos-button')).toBe(true);
    expect(newDocument.classList.contains('nostos-button--secondary')).toBe(true);
    expect(newDocument.classList.contains('nostos-button--sm')).toBe(true);

    const search = fixture.nativeElement.querySelector(
      'input[placeholder="Search topics…"]',
    ) as HTMLInputElement;
    expect(search.classList.contains('nostos-form-control')).toBe(true);
    expect(search.classList.contains('nostos-form-control--compact')).toBe(true);

    const count = fixture.nativeElement.querySelector('.badge-count') as HTMLSpanElement;
    // A topic's usage is a plain muted number, not a badge (issue #1204).
    expect(count.tagName).toBe('SPAN');
    expect(count.classList.contains('nostos-badge')).toBe(false);

    const createButtons = Array.from(
      fixture.nativeElement.querySelectorAll('.sidebar-left .actions .icon-btn'),
    ) as HTMLButtonElement[];
    expect(createButtons.length).toBeGreaterThanOrEqual(2);
    expect(createButtons.every((button) => button.classList.contains('icon-btn--xs'))).toBe(true);
  });

  it('keeps reference tabs and navigation rows product-owned while preserving their semantics', () => {
    component.referenceMode.set('library');
    component.topics.set([{ id: 'topic-1', name: 'Memory', usageCount: 3 } as any]);
    fixture.detectChanges();

    const tabs = Array.from(
      fixture.nativeElement.querySelectorAll('.library-tabs .tab-btn'),
    ) as HTMLButtonElement[];
    expect(tabs).toHaveLength(2);
    expect(tabs[0].getAttribute('role')).toBe('tab');
    expect(tabs[0].getAttribute('aria-selected')).toBe('true');
    expect(tabs.every((tab) => tab.classList.contains('toggle-opt'))).toBe(true);
    expect(tabs.every((tab) => !tab.classList.contains('nostos-button'))).toBe(true);
    expect(tabs.every((tab) => !tab.classList.contains('nostos-chip'))).toBe(true);

    const tabIcons = fixture.debugElement.queryAll(
      By.css('.library-tabs .tab-btn nostos-icon'),
    );
    expect(tabIcons.map((icon) => icon.componentInstance.name())).toEqual(['hash', 'book']);

    const topicRow = fixture.nativeElement.querySelector('.list-item') as HTMLButtonElement;
    expect(topicRow.tagName).toBe('BUTTON');
    expect(topicRow.classList.contains('nostos-button')).toBe(false);

    tabs[1].click();
    fixture.detectChanges();
    expect(component.activeSidebarTab()).toBe('notes');
    expect(tabs[1].getAttribute('aria-selected')).toBe('true');
  });

  it('uses the canonical search field and ghost action in both reference modes', () => {
    component.referenceMode.set('library');
    component.activeSidebarTab.set('notes');
    component.selectedBookId.set(null);
    fixture.detectChanges();

    const bookSearch = fixture.nativeElement.querySelector(
      'input[placeholder="Search books…"]',
    ) as HTMLInputElement;
    expect(bookSearch.classList.contains('nostos-form-control')).toBe(true);
    expect(bookSearch.classList.contains('nostos-form-control--compact')).toBe(true);

    component.selectedBookId.set('book-1');
    fixture.detectChanges();
    expect(
      fixture.nativeElement.querySelector('input[placeholder="Search books…"]'),
    ).toBeNull();

    const back = fixture.nativeElement.querySelector('.goto-btn') as HTMLButtonElement;
    // The header names the topic the notes belong to, not only the way back.
    expect(fixture.nativeElement.querySelector('.reference-detail-title')).toBeTruthy();
    expect(back.tagName).toBe('BUTTON');
    expect(back.classList.contains('nostos-button--ghost')).toBe(true);
    expect(back.classList.contains('nostos-button--sm')).toBe(true);
  });

  it('lists books to pick as choice rows with the format glyph and an author fallback', () => {
    component.referenceMode.set('library');
    component.activeSidebarTab.set('notes');
    component.selectedBookId.set(null);
    component.books.set([
      { id: 'b1', title: 'Middlemarch', author: 'George Eliot', type: 'audiobook', fileName: null },
      { id: 'b2', title: 'Moby-Dick', author: null, type: 'ebook', fileName: 'moby-dick.pdf' },
    ] as never);
    component.bookIndexState.set('loaded');
    fixture.detectChanges();

    const rows = Array.from(
      fixture.nativeElement.querySelectorAll('.book-choice-list .book-choice') as NodeListOf<HTMLButtonElement>,
    );
    expect(rows.map((row) => row.classList.contains('nostos-choice-row'))).toEqual([true, true]);
    expect(rows[0].querySelector('.nostos-choice-row__sub')?.textContent).toBe('George Eliot');
    expect(rows[0].querySelector('.nostos-choice-row__meta')?.textContent?.trim()).toBe('Audiobook');
    expect(rows[1].querySelector('.nostos-choice-row__sub')?.textContent).toBe('Unknown author');
    expect(rows[1].querySelector('.nostos-choice-row__meta')?.textContent?.trim()).toBe('PDF');

    rows[1].click();
    expect(component.selectedBookId()).toBe('b2');
  });

  it('does not run the shared transform entry animation on Studio sidebars', () => {
    const css = componentCss();

    expect(css).not.toContain('animation: sidebar-enter');
    expect(css).not.toContain('animation: sidebar-enter-right');

    const mobile = css.slice(css.indexOf('@media (max-width: 768px)'));
    expect(mobile).toContain('transform: translateX(-100%)');
    expect(mobile).toContain('transform: translateX(100%)');
  });

  // --- The page first (issue #1040) ---
  it('opens on a blank page with the rails closed and no empty state', () => {
    expect(component.blankPage()).toBe(true);
    expect(component.activeItem()).toBeNull();
    expect(fixture.nativeElement.querySelector('.editor-pane nostos-empty-state')).toBeNull();
    expect(fixture.nativeElement.querySelector('app-markdown-editor')).toBeTruthy();
    expect(component.showFileSidebar()).toBe(false);
    expect(component.showBrainSidebar()).toBe(false);
    expect(fixture.nativeElement.querySelector('.files-toggle')?.getAttribute('aria-label')).toBe(
      'Show files',
    );
    expect(
      fixture.nativeElement.querySelector('.reference-toggle')?.getAttribute('aria-label'),
    ).toBe('Show reference');
    expect(fixture.nativeElement.querySelector('.header-doc-title')?.textContent?.trim()).toBe('New page');
    const newInFiles = fixture.nativeElement.querySelector('.files-new-document') as HTMLButtonElement;
    expect(newInFiles.textContent?.trim()).toBe('New');
    expect(newInFiles.getAttribute('aria-label')).toBe('New document');
    // Both rails are slide-overs, so each carries its own close control.
    expect(fixture.nativeElement.querySelector('.files-rail-collapse')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.reference-rail-collapse')).toBeTruthy();
  });

  it('slides Files over the page and sets Reference beside it on a wide screen', () => {
    const filesToggle = fixture.nativeElement.querySelector('.files-toggle') as HTMLButtonElement;
    const referenceToggle = fixture.nativeElement.querySelector(
      '.reference-toggle',
    ) as HTMLButtonElement;

    filesToggle.click();
    fixture.detectChanges();
    expect(component.showFileSidebar()).toBe(true);
    expect(component.drawerOpen()).toBe(true);
    expect(fixture.nativeElement.querySelector('.backdrop.visible')).toBeTruthy();

    component.toggleFilesRail();
    fixture.detectChanges();
    expect(component.drawerOpen()).toBe(false);

    // Reference is a column beside the page: it does not dim the page.
    referenceToggle.click();
    fixture.detectChanges();
    expect(component.showBrainSidebar()).toBe(true);
    expect(component.drawerOpen()).toBe(false);
    expect(fixture.nativeElement.querySelector('.studio-layout.drawer-reference')).toBeNull();

    // Opening Files leaves the Reference column where it is.
    filesToggle.click();
    fixture.detectChanges();
    expect(component.showFileSidebar()).toBe(true);
    expect(component.showBrainSidebar()).toBe(true);

    // Reference closes from its own header.
    component.toggleFilesRail();
    (fixture.nativeElement.querySelector('.reference-rail-collapse') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(component.showBrainSidebar()).toBe(false);
  });

  it('slides Reference over the page on a compact screen, one slide-over at a time', () => {
    component.isCompact.set(true);
    component.toggleFilesRail();
    fixture.detectChanges();
    component.toggleReferenceRail();
    fixture.detectChanges();
    expect(component.showFileSidebar()).toBe(false);
    expect(component.showBrainSidebar()).toBe(true);
    expect(component.drawerOpen()).toBe(true);
  });

  it('closes an open slide-over with Esc before anything else', () => {
    component.toggleFilesRail();
    fixture.detectChanges();
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(component.showFileSidebar()).toBe(false);
  });

  it('fades the chrome while typing and brings it back on Esc', () => {
    component.onEditorChange('Some words');
    fixture.detectChanges();
    expect(component.isTyping()).toBe(true);
    expect(document.body.classList.contains('nostos-studio-typing')).toBe(true);
    expect(fixture.nativeElement.querySelector('.studio-layout.is-typing')).toBeTruthy();

    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(component.isTyping()).toBe(false);
    expect(document.body.classList.contains('nostos-studio-typing')).toBe(false);
  });

  it('saves a blank page left while its writing was being created', () => {
    const writings = TestBed.inject(WritingsService) as unknown as {
      create: ReturnType<typeof vi.fn>;
      update: ReturnType<typeof vi.fn>;
    };
    const created = new Subject<{ id: string; name: string; type: string; updatedAt: string }>();
    writings.create.mockReturnValue(created);
    writings.update.mockReturnValue(of({}));

    component.startBlankPage();
    component.onEditorChange('# Left behind\n\nWords that must not be lost.');
    component.startBlankPage(); // New again, before the create answered
    created.next({ id: 'left-1', name: 'Untitled document', type: 'Document', updatedAt: '' });

    expect(writings.update).toHaveBeenCalledWith('left-1', {
      name: 'Left behind',
      content: '# Left behind\n\nWords that must not be lost.',
    });
    expect(component.activeItem()).toBeNull();
    expect(component.blankPage()).toBe(true);
  });

  it('asks the server once, not per keystroke, when creating fails', () => {
    const writings = TestBed.inject(WritingsService) as unknown as { create: ReturnType<typeof vi.fn> };
    writings.create.mockReturnValue(throwError(() => new Error('offline')));
    component.startBlankPage();
    component.onEditorChange('a');
    component.onEditorChange('ab');
    component.onEditorChange('abc');
    expect(writings.create).toHaveBeenCalledTimes(1);
    expect(TestBed.inject(ToastService).error).toHaveBeenCalledTimes(1);
  });

  it('names a writing from its first line with words in it', () => {
    expect(titleFromMarkdown('# Morning pages\n\nText')).toBe('Morning pages');
    expect(titleFromMarkdown('#\n\n> *A* quoted opening')).toBe('A quoted opening');
    expect(titleFromMarkdown('---\n\n')).toBe('');
    expect(titleFromMarkdown('x'.repeat(30) + ' ' + 'y'.repeat(70)).endsWith('…')).toBe(true);
  });

  it('cycles words, characters and reading time in the stat corner', () => {
    openDocument();
    component.editorWordCount.set(476);
    component.editorCharacterCount.set(2600);
    expect(component.statText()).toBe('476 words');
    component.cycleStat();
    expect(component.statText()).toBe('2600 characters');
    component.cycleStat();
    expect(component.statText()).toBe('2 min read');
    component.cycleStat();
    expect(component.statText()).toBe('476 words');
  });

  it('opens the Files and Reference rails with Alt+M and Alt+R, and the sheet with Ctrl+/', () => {
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'µ', code: 'KeyM', altKey: true }));
    expect(component.showFileSidebar()).toBe(true);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: '®', code: 'KeyR', altKey: true }));
    expect(component.showBrainSidebar()).toBe(true);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: '/', ctrlKey: true }));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="shortcut-sheet"]')).toBeTruthy();
  });

  it('keeps an open file drawer open when the window crosses the compact breakpoint', () => {
    const setWidth = (width: number) => {
      Object.defineProperty(window, 'innerWidth', { configurable: true, value: width });
      window.dispatchEvent(new Event('resize'));
    };
    const original = window.innerWidth;
    try {
      setWidth(1440);
      component.toggleFilesRail();
      expect(component.showFileSidebar()).toBe(true);
      setWidth(600);
      expect(component.isCompact()).toBe(true);
      expect(component.showFileSidebar()).toBe(true);
      setWidth(1440);
      expect(component.isCompact()).toBe(false);
      expect(component.showFileSidebar()).toBe(true);
    } finally {
      setWidth(original);
    }
  });

  it('keeps compact drawers mutually exclusive', () => {
    component.isCompact.set(true);
    component.showFileSidebar.set(false);
    component.showBrainSidebar.set(false);
    fixture.detectChanges();

    const filesToggle = fixture.nativeElement.querySelector('.files-toggle') as HTMLButtonElement;
    const referenceToggle = fixture.nativeElement.querySelector(
      '.reference-toggle',
    ) as HTMLButtonElement;

    filesToggle.click();
    fixture.detectChanges();
    expect(component.showFileSidebar()).toBe(true);
    expect(component.showBrainSidebar()).toBe(false);

    const filesClose = fixture.nativeElement.querySelector('.files-rail-collapse') as HTMLButtonElement;
    expect(filesClose).toBeTruthy();
    filesClose.click();
    fixture.detectChanges();
    expect(component.showFileSidebar()).toBe(false);
    expect(document.activeElement).toBe(filesToggle);

    filesToggle.click();
    fixture.detectChanges();
    referenceToggle.click();
    fixture.detectChanges();
    expect(component.showFileSidebar()).toBe(false);
    expect(component.showBrainSidebar()).toBe(true);

    const collapse = fixture.nativeElement.querySelector(
      '.reference-rail-collapse',
    ) as HTMLButtonElement;
    collapse.click();
    fixture.detectChanges();
    expect(component.showBrainSidebar()).toBe(false);
    expect(document.activeElement).toBe(referenceToggle);
  });

  it('shows the zen toggle in the document-action cluster with an active document', () => {
    openDocument();

    const button = zenToggle();
    expect(button).toBeTruthy();

    const header = fixture.nativeElement.querySelector('.editor-header') as HTMLElement;
    const actions = fixture.nativeElement.querySelector('.doc-actions') as HTMLElement;
    expect(header).toBeTruthy();
    expect(actions).toBeTruthy();
    expect(header.contains(button)).toBe(true);
    expect(actions.contains(button)).toBe(true);
  });

  // --- Placement: save status in header, word count grounded on desk baseline ---
  it('renders save state in header and word count in desk telemetry with an active document', () => {
    openDocument();
    component.editorText.set('one two three four');
    fixture.detectChanges();

    const telemetry = deskTelemetry();
    expect(telemetry).toBeTruthy();
    expect(telemetry!.textContent).toContain('4 words');
    const wrapper = fixture.nativeElement.querySelector('.editor-wrapper') as HTMLElement;
    expect(wrapper.contains(telemetry)).toBe(false);

    const saveStatus = headerSaveStatus();
    expect(saveStatus).toBeTruthy();
    expect(saveStatus!.textContent?.trim()).toBe('Saved');
  });

  it('uses the singular word form in desk telemetry for a single word', () => {
    openDocument();
    component.editorText.set('one');
    fixture.detectChanges();

    expect(deskTelemetry()?.textContent).toContain('1 word');
  });

  it('prefers the editor wordcount-plugin count in desk telemetry when emitted', () => {
    openDocument();
    component.editorWordCount.set(123);
    fixture.detectChanges();

    expect(deskTelemetry()?.textContent).toContain('123 words');
  });

  // --- Enter/exit manages body.nostos-zen ---
  it('entering zen sets component state and adds the body class', () => {
    component.enterZen();

    expect(component.isZen()).toBe(true);
    expect(document.body.classList.contains('nostos-zen')).toBe(true);
  });

  it('toggling the zen button enters zen and the fixed Exit-zen control exits it', () => {
    openDocument();

    const button = zenToggle()!;
    button.click();
    fixture.detectChanges();
    expect(component.isZen()).toBe(true);
    expect(document.body.classList.contains('nostos-zen')).toBe(true);

    const exit = zenExit();
    expect(exit).toBeTruthy();
    exit!.click();
    fixture.detectChanges();
    expect(component.isZen()).toBe(false);
    expect(document.body.classList.contains('nostos-zen')).toBe(false);
  });

  it('renders the fixed Exit-zen control only while zen is active', () => {
    openDocument();
    expect(zenExit()).toBeNull();

    component.enterZen();
    fixture.detectChanges();
    expect(zenExit()).toBeTruthy();

    component.exitZen();
    fixture.detectChanges();
    expect(zenExit()).toBeNull();
  });

  it('Esc exits zen and removes the body class', () => {
    component.enterZen();
    expect(document.body.classList.contains('nostos-zen')).toBe(true);

    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));

    expect(component.isZen()).toBe(false);
    expect(document.body.classList.contains('nostos-zen')).toBe(false);
  });

  it('Esc outside zen closes a compact drawer and restores its header toggle focus', () => {
    component.isCompact.set(true);
    component.showFileSidebar.set(true);
    fixture.detectChanges();

    const filesToggle = fixture.nativeElement.querySelector('.files-toggle') as HTMLButtonElement;
    const filesEscape = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    window.dispatchEvent(filesEscape);
    fixture.detectChanges();

    expect(component.showFileSidebar()).toBe(false);
    expect(filesEscape.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(filesToggle);

    component.showBrainSidebar.set(true);
    fixture.detectChanges();
    const referenceToggle = fixture.nativeElement.querySelector('.reference-toggle') as HTMLButtonElement;
    const referenceEscape = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    window.dispatchEvent(referenceEscape);
    fixture.detectChanges();

    expect(component.showBrainSidebar()).toBe(false);
    expect(referenceEscape.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(referenceToggle);
  });

  it('does not close a compact drawer when a nested control has consumed Escape', () => {
    component.isCompact.set(true);
    component.showBrainSidebar.set(true);
    fixture.detectChanges();

    const event = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    event.preventDefault();
    window.dispatchEvent(event);

    expect(component.showBrainSidebar()).toBe(true);
  });

  it('entering zen twice is idempotent', () => {
    component.enterZen();
    component.enterZen();
    expect(component.isZen()).toBe(true);
    expect(document.body.classList.contains('nostos-zen')).toBe(true);

    component.exitZen();
    expect(document.body.classList.contains('nostos-zen')).toBe(false);
  });

  // --- Exit restores focus ---
  it('records a document opened from the file tree in the palette Recent list', () => {
    component.handleItemSelected({ id: 'doc-1', name: 'Sample', type: 'Document', updatedAt: '' });

    const recent = TestBed.inject(RecentItemsService);
    expect(recent.items()[0]).toEqual({ kind: 'document', id: 'doc-1', label: 'Sample', sub: 'Document' });

    component.saveRename('doc-1', 'Renamed');
    expect(recent.items()[0].label).toBe('Renamed');
  });

  it('restores focus to the element that opened zen after exit', () => {
    openDocument();

    const button = zenToggle()!;
    button.focus();
    button.click();
    expect(document.body.classList.contains('nostos-zen')).toBe(true);

    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));

    expect(component.isZen()).toBe(false);
    expect(document.activeElement).toBe(button);
  });

  it('restores focus to the zen toggle when the original element is gone', () => {
    openDocument();

    const button = zenToggle()!;
    const dummy = document.createElement('button');
    document.body.appendChild(dummy);
    dummy.focus();

    component.enterZen();
    dummy.remove();
    component.exitZen();

    expect(document.activeElement).toBe(button);
  });

  // --- Destruction always removes the body class ---
  it('does not leak zen state or listeners after destroy', () => {
    component.enterZen();
    expect(document.body.classList.contains('nostos-zen')).toBe(true);

    const removeSpy = vi.spyOn(window, 'removeEventListener');
    fixture.destroy();

    expect(removeSpy).toHaveBeenCalledWith('keydown', (component as any).onKeyDown);
    expect(document.body.classList.contains('nostos-zen')).toBe(false);
  });

  // --- Computed zen grid has one track ---
  it('collapses the studio grid to a single 1fr track in zen', () => {
    const layout = fixture.nativeElement.querySelector('.studio-layout') as HTMLElement;

    component.enterZen();
    fixture.detectChanges();

    const cols = getComputedStyle(layout).gridTemplateColumns;
    expect(cols).toBe('1fr');
  });

  // --- Sidebars, dock, menus, formatting toolbar hidden in zen ---
  it('hides the document header/action strip in zen and fades desk telemetry', () => {
    openDocument();
    const header = fixture.nativeElement.querySelector('.editor-header') as HTMLElement;
    const telemetry = deskTelemetry()!;

    component.enterZen();
    fixture.detectChanges();

    expect(getComputedStyle(header).display).toBe('none');
    // Focus hides the reserved footer line with the rest of the editor chrome.
    expect(getComputedStyle(telemetry).opacity).toBe('0');
  });

  it('hides both sidebars in zen', () => {
    const left = fixture.nativeElement.querySelector('.sidebar-left') as HTMLElement;
    const right = fixture.nativeElement.querySelector('.sidebar-right') as HTMLElement;

    component.enterZen();
    fixture.detectChanges();

    expect(getComputedStyle(left).display).toBe('none');
    expect(getComputedStyle(right).display).toBe('none');
  });

  it('caps the writing surface at 100% in zen', () => {
    openDocument();
    const editor = fixture.nativeElement.querySelector('app-markdown-editor') as HTMLElement;
    const wrapper = fixture.nativeElement.querySelector('.editor-wrapper') as HTMLElement;

    component.enterZen();
    fixture.detectChanges();

    expect(getComputedStyle(editor).maxWidth).toBe('100%');
    expect(getComputedStyle(wrapper).alignItems).toBe('stretch');
  });

  // --- Exactly one scroll container remains ---
  it('keeps the editor wrapper overflow-visible in zen so the paper shadow is never clipped; pane and layout stay hidden', () => {
    openDocument();
    const wrapper = fixture.nativeElement.querySelector('.editor-wrapper') as HTMLElement;
    const pane = fixture.nativeElement.querySelector('.editor-pane') as HTMLElement;
    const layout = fixture.nativeElement.querySelector('.studio-layout') as HTMLElement;

    component.enterZen();
    fixture.detectChanges();

    expect(getComputedStyle(wrapper).overflow).toBe('visible');
    expect(getComputedStyle(pane).overflow === 'hidden' || getComputedStyle(pane).overflowY === 'hidden').toBe(true);
    expect(getComputedStyle(layout).overflow === 'hidden' || getComputedStyle(layout).overflowY === 'hidden').toBe(true);
  });

  // --- Scroll position survives enter/exit ---
  it('does not recreate the editor surface across enter/exit (same scroll owner)', () => {
    openDocument();
    const editor = fixture.nativeElement.querySelector('app-markdown-editor') as HTMLElement;

    component.enterZen();
    fixture.detectChanges();
    component.exitZen();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('app-markdown-editor')).toBe(editor);
    expect(component.editorText()).toBe(sampleDocument.content);
  });

  it('computes the word count from the editor content', () => {
    component.editorText.set('one two three four');
    expect(component.wordCount()).toBe(4);

    component.editorText.set('  spaced   out \n\n words ');
    expect(component.wordCount()).toBe(3);

    component.editorText.set('');
    expect(component.wordCount()).toBe(0);
  });

  // --- Seamless editorial canvas: CSS-level contract ---
  it('declares the seamless editorial canvas on the editor host', () => {
    const css = componentCss();

    expect(css).toContain('max-width: 100%');
    expect(css).toContain('background: var(--bg-surface)');
    expect(css).toContain('border: none');
    expect(css).toContain('box-shadow: none');
  });

  it('hides the editor toolbar in zen so only the page remains', () => {
    const css = componentCss();

    const zenToolbar = css.slice(css.indexOf('body.nostos-zen'));
    expect(zenToolbar).toContain('.editor-toolbar');
    expect(zenToolbar).toContain('display: none !important');
  });

  it('gives the stage safe padding: dock clearance reserves room for the floating dock', () => {
    const css = componentCss();

    // Dock is 65px tall, floating 24px off the viewport bottom: 96px clears it.
    expect(css).toContain('--studio-dock-clearance: 96px');
    expect(css).toContain('overflow: visible');
  });

  it('reserves the dock clearance exactly once (pane must not pad it again)', () => {
    const css = componentCss();

    /**
     * The declaration block for a selector, brace-balanced. Angular's emulated
     * encapsulation rewrites selectors to `.editor-pane[_ngcontent-xxx]`, so
     * match on the selector name and then walk to its opening brace.
     */
    const blockOf = (selector: string): string => {
      const start = css.indexOf(selector);
      expect(start, `${selector} missing from component CSS`).toBeGreaterThanOrEqual(0);
      const open = css.indexOf('{', start);
      expect(open, `${selector} has no declaration block`).toBeGreaterThanOrEqual(0);
      return css.slice(open, css.indexOf('}', open));
    };

    // Regression: .editor-pane carried its own 8rem pad on top of the
    // wrapper's clearance, which stacked to ~250px of dead desk below the
    // word count and squeezed the editor surface.
    const paneBlock = blockOf('.editor-pane');
    expect(paneBlock).toContain('padding-bottom: 0');
    expect(paneBlock).not.toContain('8rem');

    // The wrapper consumes the token rather than a detached literal, so the
    // clearance has one source of truth across zen/mobile overrides.
    const wrapperBlock = blockOf('.editor-wrapper');
    expect(wrapperBlock).toContain('padding-bottom: var(--studio-dock-clearance)');
    expect(wrapperBlock).not.toContain('padding-bottom: 8rem');

    // The count now reserves a line outside the editor and leaves the shared dock
    // clearance below it instead of floating over manuscript text.
    expect(css).toContain('.editor-footer');
    expect(css).toContain('margin: 0 2.5rem var(--studio-dock-clearance)');
    expect(css).toContain('position: static');
    expect(css).toContain('body.nostos-zen');
    expect(css).toContain('display: none');
  });

  it('declares the zen sheet full-width on seamless surface', () => {
    const css = componentCss();

    expect(css).toContain('max-width: 100%');
  });

  it('declares mobile edge-to-edge (<=700px): gutters zeroed, radius and shadow removed', () => {
    const css = componentCss();

    const mobileBlock = css.slice(css.indexOf('@media (max-width: 700px)'));
    expect(mobileBlock).toContain('--studio-inline-gutter: 0px');
    expect(mobileBlock).toContain('--studio-top-gutter: 0px');
    expect(mobileBlock).toContain('width: 100%');
    expect(mobileBlock).toContain('border-radius: 0');
    expect(mobileBlock).toContain('box-shadow: none');
  });
});

describe('WritingStudio typewriter mode', () => {
  let fixture: ComponentFixture<WritingStudio>;
  let component: WritingStudio;

  beforeEach(async () => {
    localStorage.clear();
    TestBed.overrideComponent(WritingStudio, {
      remove: { imports: [FlatTreeComponent, NoteItemComponent, MarkdownEditorComponent] },
      add: { imports: [FlatTreeStub, NoteItemStub, MarkdownEditorStub] },
    });

    await TestBed.configureTestingModule({
      imports: [WritingStudio],
      providers: [
        {
          provide: WritingsService,
          useValue: {
            list: vi.fn(() => of([])),
            get: vi.fn(),
            create: vi.fn(() => of({})),
            update: vi.fn(() => of({})),
            delete: vi.fn(() => of({})),
            move: vi.fn(() => of({})),
            listSources: vi.fn(() => of([])),
            addSource: vi.fn(() => of({})),
            removeSource: vi.fn(() => of({})),
          },
        },
        { provide: ToastService, useValue: { error: vi.fn(), success: vi.fn() } },
        { provide: TopicsService, useValue: { list: vi.fn(() => of([])), get: vi.fn() } },
        { provide: BooksService, useValue: { list: vi.fn(() => of({ items: [] })) } },
        { provide: NotesService, useValue: { list: vi.fn(() => of([])), getMany: vi.fn(() => of([])) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(WritingStudio);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('starts off and toggles with persistence in the page settings', async () => {
    expect(component.typewriter()).toBe(false);
    const saved = async () => {
      TestBed.tick();
      await new Promise((resolve) => setTimeout(resolve, 300));
      return JSON.parse(localStorage.getItem('nostos.studio.preferences')!).typewriter;
    };

    component.toggleTypewriter();
    expect(component.typewriter()).toBe(true);
    expect(await saved()).toBe(true);

    component.toggleTypewriter();
    expect(component.typewriter()).toBe(false);
    expect(await saved()).toBe(false);
  });

  it('leaves the panel open when Esc was meant for a list inside it', () => {
    component.togglePageSettings();
    fixture.detectChanges();
    const handled = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    handled.preventDefault();
    window.dispatchEvent(handled);
    expect(component.pageSettingsOpen()).toBe(true);
  });

  it('closes an open slide-over when the page settings open', () => {
    component.toggleFilesRail();
    expect(component.showFileSidebar()).toBe(true);
    component.togglePageSettings();
    expect(component.showFileSidebar()).toBe(false);
    expect(component.pageSettingsOpen()).toBe(true);
  });

  it('lives in the page settings, opened from the header or Ctrl+,', () => {
    component.activeItem.set({
      id: 'doc-1',
      name: 'Sample',
      content: 'hello',
      updatedAt: '2026-08-13T00:00:00Z',
    });
    component.editorText.set('hello');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.typewriter-toggle')).toBeNull();
    const settingsToggle = fixture.nativeElement.querySelector('.page-settings-toggle') as HTMLButtonElement;
    expect(settingsToggle).toBeTruthy();
    settingsToggle.click();
    fixture.detectChanges();

    // The panel renders in the overlay (the shared settings popover).
    const centred = document.querySelector(
      '[data-testid="page-settings"] input[aria-label^="Keep the line centred"]',
    ) as HTMLInputElement;
    expect(centred.checked).toBe(false);
    centred.click();
    fixture.detectChanges();
    expect(component.typewriter()).toBe(true);

    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="page-settings"]')).toBeNull();

    window.dispatchEvent(new KeyboardEvent('keydown', { key: ',', ctrlKey: true }));
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="page-settings"]')).toBeTruthy();
  });

  it('carries over typewriter centring set before it moved into the page settings', () => {
    expect(normalizeStudioPreferences(null, true).typewriter).toBe(true);
  });
});

describe('WritingStudio delete (no window.confirm)', () => {
  let fixture: ComponentFixture<WritingStudio>;
  let component: WritingStudio;

  beforeEach(async () => {
    TestBed.overrideComponent(WritingStudio, {
      remove: { imports: [FlatTreeComponent, NoteItemComponent, MarkdownEditorComponent] },
      add: { imports: [FlatTreeStub, NoteItemStub, MarkdownEditorStub] },
    });

    await TestBed.configureTestingModule({
      imports: [WritingStudio],
      providers: [
        {
          provide: WritingsService,
          useValue: {
            list: vi.fn(() => of([])),
            get: vi.fn(),
            create: vi.fn(() => of({})),
            update: vi.fn(() => of({})),
            delete: vi.fn(() => of({})),
            move: vi.fn(() => of({})),
            listSources: vi.fn(() => of([])),
            addSource: vi.fn(() => of({})),
            removeSource: vi.fn(() => of({})),
          },
        },
        { provide: ToastService, useValue: { error: vi.fn(), success: vi.fn() } },
        { provide: TopicsService, useValue: { list: vi.fn(() => of([])), get: vi.fn() } },
        { provide: BooksService, useValue: { list: vi.fn(() => of({ items: [] })) } },
        { provide: NotesService, useValue: { list: vi.fn(() => of([])), getMany: vi.fn(() => of([])) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(WritingStudio);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('opens ConfirmModal and only deletes on confirm', () => {
    const writings = TestBed.inject(WritingsService) as unknown as {
      delete: ReturnType<typeof vi.fn>;
    };
    writings.delete.mockClear();

    component.deleteItem('doc-9');
    expect(component.pendingDelete()).toBe('doc-9');
    expect(writings.delete).not.toHaveBeenCalled();

    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeTruthy();

    component.confirmDeleteItem();
    expect(writings.delete).toHaveBeenCalledWith('doc-9');
    expect(component.pendingDelete()).toBeNull();
  });

  it('keeps the confirm open and busy until the delete answers', () => {
    const writings = TestBed.inject(WritingsService) as unknown as {
      delete: ReturnType<typeof vi.fn>;
    };
    const answer = new Subject<void>();
    writings.delete.mockReturnValueOnce(answer);

    component.deleteItem('doc-9');
    component.confirmDeleteItem();
    fixture.detectChanges();
    expect(component.deletingItem()).toBe(true);
    expect(component.pendingDelete()).toBe('doc-9');
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')?.textContent).toContain('Deleting…');

    // Cancel and a second confirm do nothing while it is on its way.
    component.cancelDeleteItem();
    component.confirmDeleteItem();
    expect(writings.delete).toHaveBeenCalledTimes(1);
    expect(component.pendingDelete()).toBe('doc-9');

    answer.next();
    answer.complete();
    expect(component.deletingItem()).toBe(false);
    expect(component.pendingDelete()).toBeNull();
  });

  describe('New document / New folder', () => {
    type Mocked = {
      create: ReturnType<typeof vi.fn>;
      get: ReturnType<typeof vi.fn>;
      update: ReturnType<typeof vi.fn>;
    };
    let writings: Mocked;
    const promptSpy = vi.fn();

    beforeEach(() => {
      writings = TestBed.inject(WritingsService) as unknown as Mocked;
      writings.get.mockReturnValue(of({ id: 'new-1', name: 'Untitled document', content: '', updatedAt: '' }));
      vi.stubGlobal('prompt', promptSpy);
      promptSpy.mockClear();
    });

    afterEach(() => vi.unstubAllGlobals());

    it('New gives a blank page and creates nothing until there are words', () => {
      component.createItem('Document');

      expect(promptSpy).not.toHaveBeenCalled();
      expect(writings.create).not.toHaveBeenCalled();
      expect(component.blankPage()).toBe(true);
      expect(component.activeItem()).toBeNull();
    });

    it('creates the writing with its first words and names it from its first line', () => {
      writings.create.mockReturnValue(
        of({ id: 'new-1', name: 'Untitled document', type: 'Document', updatedAt: '' }),
      );
      component.createItem('Document');

      component.onEditorChange('# Morning pages\n\nThe first words.');

      expect(writings.create).toHaveBeenCalledWith({ name: 'Untitled document', type: 'Document', parentId: null });
      expect(component.activeItem()?.id).toBe('new-1');
      expect(component.blankPage()).toBe(false);
      expect(component.editorTitle()).toBe('Morning pages');
      expect(localStorage.getItem('nostos.studio.lastWritingId')).toBe('new-1');

      // Only once, however fast the next words come.
      component.onEditorChange('# Morning pages\n\nThe first words. More.');
      expect(writings.create).toHaveBeenCalledTimes(1);
    });

    it('renames a new folder in place and opens nothing', () => {
      writings.create.mockReturnValue(of({ id: 'f-1', name: 'Untitled folder', type: 'Folder' }));

      component.createItem('Folder');
      expect(writings.create).toHaveBeenCalledWith({ name: 'Untitled folder', type: 'Folder', parentId: null });
      expect(component.editingId()).toBe('f-1');

      component.cancelRename();
      expect(writings.get).not.toHaveBeenCalled();
    });

    it('says so when the item cannot be created', () => {
      writings.create.mockReturnValue(throwError(() => new Error('offline')));
      component.createItem('Document');
      component.onEditorChange('Words');

      expect(TestBed.inject(ToastService).error).toHaveBeenCalledWith(
        'Could not create a document. Your words are still on the page.',
      );
      expect(component.editingId()).toBeNull();
    });
  });

  it('cancelling performs nothing', () => {
    const writings = TestBed.inject(WritingsService) as unknown as {
      delete: ReturnType<typeof vi.fn>;
    };
    writings.delete.mockClear();

    component.deleteItem('doc-9');
    component.cancelDeleteItem();
    expect(component.pendingDelete()).toBeNull();
    expect(writings.delete).not.toHaveBeenCalled();
  });
});

describe('WritingStudio kept sources (#491)', () => {
  async function settleSaveQueue(): Promise<void> {
    // queueWritingSave intentionally chains through the previous save promise.
    // Let both the firstValueFrom continuation and the serialized .then() lane
    // drain without depending on wall-clock timers.
    for (let i = 0; i < 8; i++) await Promise.resolve();
  }

  let fixture: ComponentFixture<WritingStudio>;
  let component: WritingStudio;
  let writingsService: {
    list: ReturnType<typeof vi.fn>;
    get: ReturnType<typeof vi.fn>;
    create: ReturnType<typeof vi.fn>;
    update: ReturnType<typeof vi.fn>;
    delete: ReturnType<typeof vi.fn>;
    move: ReturnType<typeof vi.fn>;
    listSources: ReturnType<typeof vi.fn>;
    addSource: ReturnType<typeof vi.fn>;
    removeSource: ReturnType<typeof vi.fn>;
  };
  let toastService: {
    error: ReturnType<typeof vi.fn>;
    success: ReturnType<typeof vi.fn>;
    info: ReturnType<typeof vi.fn>;
    show: ReturnType<typeof vi.fn>;
  };

  const sampleDoc1: WritingContentDto = {
    id: 'doc-1',
    name: 'Chapter 1',
    content: 'Once upon a time',
    updatedAt: '2026-09-01T00:00:00Z',
  };

  const sampleDoc2: WritingContentDto = {
    id: 'doc-2',
    name: 'Chapter 2',
    content: 'The second chapter',
    updatedAt: '2026-09-02T00:00:00Z',
  };

  const sourceAlpha: WritingSourceDto = {
    id: 'note-alpha',
    bookId: 'book-1',
    bookTitle: 'Book Alpha',
    content: 'First kept thought',
    selectedText: 'Alpha excerpt',
    cfiRange: 'epubcfi(/6/2)',
    createdAt: '2026-08-01T10:00:00Z',
    addedAt: '2026-09-01T12:00:00Z',
    sourceAnchorKind: 'epub_cfi',
    sourceAnchorValue: 'epubcfi(/6/2)',
    anchorVerified: true,
  };

  const sourceBeta: WritingSourceDto = {
    id: 'note-beta',
    bookId: 'book-2',
    bookTitle: 'Book Beta',
    content: 'Second kept thought',
    selectedText: 'Beta excerpt',
    cfiRange: null,
    createdAt: '2026-08-05T10:00:00Z',
    addedAt: '2026-09-02T15:00:00Z',
    sourceAnchorKind: 'pdf_page',
    sourceAnchorValue: '42',
    anchorVerified: true,
  };

  beforeEach(async () => {
    writingsService = {
      list: vi.fn(() => of([])),
      get: vi.fn((id: string) => of(id === 'doc-2' ? sampleDoc2 : sampleDoc1)),
      create: vi.fn(() => of({})),
      update: vi.fn(() => of({})),
      delete: vi.fn(() => of({})),
      move: vi.fn(() => of({})),
      listSources: vi.fn(() => of([])),
      addSource: vi.fn(() => of({})),
      removeSource: vi.fn(() => of(undefined)),
    };

    toastService = {
      error: vi.fn(),
      success: vi.fn(),
      info: vi.fn(),
      show: vi.fn(),
    };

    TestBed.overrideComponent(WritingStudio, {
      remove: { imports: [FlatTreeComponent, NoteItemComponent, MarkdownEditorComponent] },
      add: { imports: [FlatTreeStub, NoteItemStub, MarkdownEditorStub] },
    });

    await TestBed.configureTestingModule({
      imports: [WritingStudio],
      providers: [
        { provide: WritingsService, useValue: writingsService },
        { provide: ToastService, useValue: toastService },
        {
          provide: TopicsService,
          useValue: {
            list: vi.fn(() => of([{ id: 'c-1', name: 'Philosophy', usageCount: 1 }])),
            get: vi.fn(() =>
              of({
                id: 'c-1',
                name: 'Philosophy',
                notes: [
                  {
                    noteId: 'note-alpha',
                    bookId: 'book-1',
                    content: 'First kept thought',
                    selectedText: 'Alpha excerpt',
                    cfiRange: 'epubcfi(/6/2)',
                    bookTitle: 'Book Alpha',
                  },
                ],
              }),
            ),
          },
        },
        {
          provide: BooksService,
          useValue: {
            list: vi.fn(() => of({ items: [] })),
            // The books behind the sources, as citations read them (issue #1157).
            get: vi.fn((id: string) =>
              id === 'book-3'
                ? of({ id, title: 'The World as Will and Representation', author: 'Arthur Schopenhauer', publisher: 'Dover', publishedDate: '1818' })
                : id === 'book-2'
                  ? of({ id, title: 'Book Beta', author: null, publisher: null, publishedDate: null })
                  : throwError(() => new Error('not found')),
            ),
          },
        },
        { provide: NotesService, useValue: { list: vi.fn(() => of([])), getMany: vi.fn(() => of([])) } },
      ],
    }).compileComponents();

    localStorage.clear();
    document.body.classList.remove('nostos-zen');
    window.history.replaceState({}, '', '/studio');

    fixture = TestBed.createComponent(WritingStudio);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  // 1. the References surface renders For this document and Library as distinct surfaces,
  // and Topics/Books browsing is inside Library (not beside it).
  it('renders For this document and Library as distinct surfaces, and Topics/Books browsing inside Library', () => {
    const referenceTabs = Array.from(
      fixture.nativeElement.querySelectorAll('.reference-mode-switch .tab-btn'),
    ) as HTMLButtonElement[];
    expect(referenceTabs).toHaveLength(2);
    expect(referenceTabs[0].textContent?.trim()).toBe('For this document');
    expect(referenceTabs[1].textContent?.trim()).toBe('Library');

    // In 'writing' mode, inner Library tabs (Topics/Books) are not rendered
    expect(component.referenceMode()).toBe('writing');
    expect(fixture.nativeElement.querySelector('.library-tabs')).toBeNull();
    expect(fixture.nativeElement.querySelector('.kept-sources-content')).toBeTruthy();

    // Switch to Library mode
    referenceTabs[1].click();
    fixture.detectChanges();

    expect(component.referenceMode()).toBe('library');
    const libraryTabs = Array.from(
      fixture.nativeElement.querySelectorAll('.library-tabs .tab-btn'),
    ) as HTMLButtonElement[];
    expect(libraryTabs).toHaveLength(2);
    expect(libraryTabs[0].textContent).toContain('Topics');
    expect(libraryTabs[1].textContent).toContain('Books');
    expect(fixture.nativeElement.querySelector('.kept-sources-content')).toBeNull();
  });

  // 2. selecting a document loads and renders its kept sources (stubbed service returns two).
  it('selecting a document loads and renders its kept sources', () => {
    writingsService.listSources.mockReturnValue(of([sourceAlpha, sourceBeta]));

    component.handleItemSelected({ id: 'doc-1', type: 'Document', name: 'Chapter 1' });
    fixture.detectChanges();

    expect(writingsService.listSources).toHaveBeenCalledWith('doc-1');
    expect(component.keptSources()).toHaveLength(2);

    const keptRows = fixture.nativeElement.querySelectorAll(
      '.kept-sources-content .reference-source-row',
    );
    expect(keptRows).toHaveLength(2);
    expect(fixture.nativeElement.querySelectorAll('.kept-sources-content app-note-item')).toHaveLength(0);
  });

  // 3. empty kept list shows the calm empty state and no note cards.
  it('shows calm empty state when document is open but kept list is empty', () => {
    component.activeItem.set(sampleDoc1);
    component.keptSources.set([]);
    fixture.detectChanges();

    const emptyText = fixture.nativeElement.querySelector('.reference-empty-state');
    expect(emptyText).toBeTruthy();
    expect(emptyText.textContent).toContain('No sources are kept in this document yet.');
    expect(fixture.nativeElement.querySelectorAll('.kept-sources-content .reference-source-row')).toHaveLength(0);
  });

  it('keeps list loading and retryable error states distinct', () => {
    component.referenceMode.set('library');
    component.topicIndexState.set('loading');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="status"]')?.textContent).toContain(
      'Loading topics',
    );
    expect(fixture.nativeElement.querySelector('[role="alert"]')).toBeNull();

    component.topicIndexState.set('error');
    fixture.detectChanges();
    const topicAlert = fixture.nativeElement.querySelector('[role="alert"]');
    expect(topicAlert?.textContent).toContain('Topics could not be loaded.');
    (topicAlert.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(component.topicIndexState()).toBe('loaded');

    component.activeItem.set(sampleDoc1);
    component.keptSourcesState.set('error');
    component.referenceMode.set('writing');
    fixture.detectChanges();
    const keptAlert = fixture.nativeElement.querySelector('[role="alert"]');
    expect(keptAlert?.textContent).toContain('Kept sources could not be loaded.');
    (keptAlert.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(component.keptSourcesState()).toBe('loaded');
  });

  it('says the file list could not be loaded, with Try again, and opens a failed writing with a toast', () => {
    writingsService.list.mockReturnValueOnce(throwError(() => new Error('offline')));
    component.loadTree();
    fixture.detectChanges();

    const list = fixture.nativeElement.querySelector('.file-list') as HTMLElement;
    expect(list.querySelector('[role="alert"]')?.textContent).toContain('Your writings could not be loaded.');
    expect(list.querySelector('app-flat-tree')).toBeNull();

    (list.querySelector('[role="alert"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(list.querySelector('[role="alert"]')).toBeNull();
    expect(list.querySelector('app-flat-tree')).not.toBeNull();

    writingsService.get.mockReturnValueOnce(throwError(() => new Error('offline')));
    component.handleItemSelected({ id: 'doc-1', name: 'Chapter 1', type: 'Document' });
    expect(toastService.error).toHaveBeenCalledWith('Could not open the writing. Try again.');
  });

  // 4. keep action calls addSource with the active document id + note id.
  it('keep action calls addSource with active document id and note id', () => {
    component.activeItem.set(sampleDoc1);
    component.referenceMode.set('library');
    component.selectTopic('c-1');
    fixture.detectChanges();

    writingsService.addSource.mockReturnValue(of(sourceAlpha));

    const keepBtn = fixture.nativeElement.querySelector(
      '.reference-source-row .library-keep-action',
    ) as HTMLButtonElement;
    expect(keepBtn).toBeTruthy();

    keepBtn.click();
    fixture.detectChanges();

    expect(writingsService.addSource).toHaveBeenCalledWith('doc-1', 'note-alpha');
    expect(component.keptNoteIds().has('note-alpha')).toBe(true);
  });

  // 5. an already-kept note shows the kept state and clicking again does not call addSource twice.
  it('already-kept note shows kept state and clicking again does not call addSource twice', () => {
    component.activeItem.set(sampleDoc1);
    component.keptSources.set([sourceAlpha]);
    component.referenceMode.set('library');
    component.selectTopic('c-1');
    fixture.detectChanges();

    const keepBtn = fixture.nativeElement.querySelector(
      '.reference-source-row .library-keep-action',
    ) as HTMLButtonElement;
    expect(keepBtn.classList.contains('active')).toBe(true);
    expect(keepBtn.getAttribute('title')).toBe(
      'Already kept in this document; no change on click',
    );

    keepBtn.click();
    fixture.detectChanges();

    expect(writingsService.addSource).not.toHaveBeenCalled();
  });

  // 6. keep action disabled (with the documented reason) when no document is open.
  it('keep action is disabled with documented reason when no document is open', () => {
    component.activeItem.set(null);
    component.referenceMode.set('library');
    component.selectTopic('c-1');
    fixture.detectChanges();

    const keepBtn = fixture.nativeElement.querySelector(
      '.reference-source-row .library-keep-action',
    ) as HTMLButtonElement;
    expect(keepBtn.disabled).toBe(true);
    expect(keepBtn.getAttribute('title')).toBe('Open a document to keep or insert sources');
    expect(fixture.nativeElement.querySelector('.reference-action-notice')?.textContent)
      .toContain('Open a document to keep or insert sources.');

    keepBtn.click();
    fixture.detectChanges();

    expect(writingsService.addSource).not.toHaveBeenCalled();

    component.inspectSource(sourceAlpha as any);
    fixture.detectChanges();
    const insertButton = fixture.nativeElement.querySelector(
      '.source-action-primary-row button',
    ) as HTMLButtonElement;
    expect(insertButton.disabled).toBe(true);
    expect(fixture.nativeElement.querySelector('.source-action-disabled-reason')?.textContent)
      .toBe('Open a document to keep or insert sources');
  });

  // 7. remove action calls removeSource and drops the row locally.
  it('remove action calls removeSource and drops the row locally', () => {
    component.activeItem.set(sampleDoc1);
    component.editorText.set(sampleDoc1.content);
    component.keptSources.set([sourceAlpha, sourceBeta]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.kept-sources-content .reference-source-row')).toHaveLength(2);

    const removeButtons = fixture.nativeElement.querySelectorAll(
      '.kept-sources-content .reference-row-action',
    ) as NodeListOf<HTMLButtonElement>;
    expect(removeButtons).toHaveLength(2);

    removeButtons[0].click();
    fixture.detectChanges();

    expect(writingsService.removeSource).toHaveBeenCalledWith('doc-1', 'note-alpha');
    expect(component.editorText()).toBe(sampleDoc1.content);
    expect(component.keptSources()).toHaveLength(1);
    expect(component.keptSources()[0].id).toBe('note-beta');
    expect(fixture.nativeElement.querySelectorAll('.kept-sources-content .reference-source-row')).toHaveLength(1);
  });

  // 8. switching the active document reloads the kept list (no stale list from the previous document).
  it('switching the active document reloads the kept list', () => {
    writingsService.listSources.mockImplementation((id: string) =>
      id === 'doc-2' ? of([sourceBeta]) : of([sourceAlpha]),
    );

    // Select doc-1
    component.handleItemSelected({ id: 'doc-1', type: 'Document', name: 'Chapter 1' });
    fixture.detectChanges();
    expect(writingsService.listSources).toHaveBeenCalledWith('doc-1');
    expect(component.keptSources()).toEqual([sourceAlpha]);

    // Switch to doc-2
    component.handleItemSelected({ id: 'doc-2', type: 'Document', name: 'Chapter 2' });
    fixture.detectChanges();
    expect(writingsService.listSources).toHaveBeenCalledWith('doc-2');
    expect(component.keptSources()).toEqual([sourceBeta]);
  });

  // 9. a failing addSource surfaces an error toast and does not fake the kept state.
  it('a failing addSource surfaces an error toast and does not fake the kept state', () => {
    component.activeItem.set(sampleDoc1);
    component.referenceMode.set('library');
    component.selectTopic('c-1');
    fixture.detectChanges();

    writingsService.addSource.mockReturnValue(
      throwError(() => new Error('Server error')),
    );
    writingsService.listSources.mockReturnValue(of([]));

    const keepBtn = fixture.nativeElement.querySelector(
      '.reference-source-row .library-keep-action',
    ) as HTMLButtonElement;
    keepBtn.click();
    fixture.detectChanges();

    expect(toastService.error).toHaveBeenCalledWith('Could not keep the source');
    expect(component.keptNoteIds().has('note-alpha')).toBe(false);
    expect(writingsService.listSources).toHaveBeenCalledWith('doc-1');
  });

  // 11. a keep response that arrives after the writer switched documents belongs to the OLD
  //     document: it must not be appended to the newly opened document's kept list.
  it('ignores a keep response that arrives after switching documents', () => {
    let resolveAdd: (value: WritingSourceDto) => void = () => undefined;
    writingsService.addSource.mockReturnValue(
      new Observable<WritingSourceDto>((subscriber) => {
        resolveAdd = (value) => {
          subscriber.next(value);
          subscriber.complete();
        };
      }),
    );
    writingsService.listSources.mockImplementation((id: string) =>
      of(id === 'doc-2' ? [sourceBeta] : []),
    );

    component.activeItem.set(sampleDoc1);
    component.referenceMode.set('library');
    component.selectTopic('c-1');
    fixture.detectChanges();

    const keepBtn = fixture.nativeElement.querySelector(
      '.reference-source-row .library-keep-action',
    ) as HTMLButtonElement;
    keepBtn.click();
    fixture.detectChanges();
    expect(writingsService.addSource).toHaveBeenCalledWith('doc-1', 'note-alpha');

    // The writer switches documents before the keep request comes back.
    component.handleItemSelected({ id: 'doc-2', type: 'Document', name: 'Chapter 2' });
    fixture.detectChanges();
    expect(component.keptSources()).toEqual([sourceBeta]);

    resolveAdd(sourceAlpha);
    fixture.detectChanges();

    // The response belonged to doc-1: doc-2's kept list is untouched.
    expect(component.keptSources()).toEqual([sourceBeta]);
    expect(component.keptNoteIds().has('note-alpha')).toBe(false);
  });

  // 10. compact: the Reference toggle opens/closes the rail and restores focus.
  it('compact: the Reference toggle still opens and closes the rail', () => {
    component.isCompact.set(true);
    component.activeItem.set(sampleDoc1);
    component.showBrainSidebar.set(false);
    component.showFileSidebar.set(false);
    fixture.detectChanges();

    const toggleBtn = fixture.nativeElement.querySelector('.reference-toggle') as HTMLButtonElement;
    expect(toggleBtn).toBeTruthy();

    // Open drawer
    toggleBtn.click();
    fixture.detectChanges();
    expect(component.showBrainSidebar()).toBe(true);

    // Close from the rail heading; focus returns to the persistent header toggle.
    const closeBtn = fixture.nativeElement.querySelector('.reference-rail-collapse') as HTMLButtonElement;
    expect(closeBtn).toBeTruthy();
    closeBtn.click();
    fixture.detectChanges();
    expect(component.showBrainSidebar()).toBe(false);
    expect(document.activeElement).toBe(toggleBtn);
  });

  it('browsing a kept source only inspects it and never inserts or changes prose', () => {
    component.activeItem.set(sampleDoc1);
    component.editorText.set(sampleDoc1.content);
    component.keptSources.set([sourceAlpha]);
    fixture.detectChanges();

    const editor = (component as any).markdownEditor as MarkdownEditorStub;
    editor.insertMarkdown.mockClear();

    const card = fixture.nativeElement.querySelector(
      '.kept-sources-content .reference-source-row-main',
    ) as HTMLElement;
    card.click();
    fixture.detectChanges();

    expect(component.inspectedSource()?.id).toBe('note-alpha');
    expect(component.editorText()).toBe(sampleDoc1.content);
    expect(editor.insertMarkdown).not.toHaveBeenCalled();
  });

  it('browsing a Library source only inspects it and never inserts or changes prose', () => {
    component.activeItem.set(sampleDoc1);
    component.editorText.set(sampleDoc1.content);
    component.referenceMode.set('library');
    component.selectTopic('c-1');
    fixture.detectChanges();

    const editor = (component as any).markdownEditor as MarkdownEditorStub;
    editor.insertMarkdown.mockClear();

    const card = fixture.nativeElement.querySelector(
      '.reference-note-list .reference-source-row-main',
    ) as HTMLElement;
    card.click();
    fixture.detectChanges();

    expect(component.inspectedSource()?.id).toBe('note-alpha');
    expect(component.editorText()).toBe(sampleDoc1.content);
    expect(editor.insertMarkdown).not.toHaveBeenCalled();
  });

  it('renders source previews as readable Markdown text with wikilinks resolved to their labels', () => {
    const preview = component.referencePreview({
      id: 'preview-note',
      bookId: 'book-1',
      content:
        '## [[S2 Topic 01 — Attention and the Reader]]\n\n**A note without** _markup_ and [[Attention|the displayed text]].',
      createdAt: '2026-08-01T10:00:00Z',
    } as any);

    expect(preview).toBe(
      'S2 Topic 01 — Attention and the Reader A note without markup and the displayed text.',
    );
    expect(preview).not.toMatch(/[\[\]_*`#|]/);
  });

  it('uses the filled bookmark glyph only while a Library source is kept', () => {
    const libraryNote = {
      id: sourceAlpha.id,
      bookId: sourceAlpha.bookId,
      content: sourceAlpha.content,
      selectedText: sourceAlpha.selectedText,
      createdAt: sourceAlpha.createdAt,
      bookTitle: sourceAlpha.bookTitle,
    } as any;
    const pathsIn = (root: ParentNode): string[] =>
      Array.from(root.querySelectorAll('path')).map((path) => path.getAttribute('d') ?? '');
    const markupPaths = (markup: string): string[] =>
      pathsIn(new DOMParser().parseFromString(markup, 'image/svg+xml'));

    component.referenceMode.set('library');
    component.activeSidebarTab.set('notes');
    component.selectedBookId.set('book-1');
    component.selectedBookNotes.set([libraryNote]);
    component.keptSources.set([]);
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector('.library-keep-action') as HTMLButtonElement;
    expect(button.classList.contains('active')).toBe(false);
    expect(pathsIn(button.querySelector('svg')!)).toEqual(
      markupPaths(NOSTOS_ICONS['bookmark-simple'].regular),
    );

    component.keptSources.set([sourceAlpha]);
    fixture.detectChanges();

    expect(button.classList.contains('active')).toBe(true);
    expect(pathsIn(button.querySelector('svg')!)).toEqual(
      markupPaths(NOSTOS_ICONS['bookmark-simple'].fill!),
    );
  });

  it('hides Reference index search in detail and inspection, then restores query and index scroll', async () => {
    const note = {
      id: 'note-alpha',
      bookId: 'book-1',
      bookTitle: 'Book Alpha',
      content: 'A note about attention',
      createdAt: '2026-08-01T10:00:00Z',
    } as any;
    component.referenceMode.set('library');
    component.activeSidebarTab.set('brain');
    component.topics.set([{ id: 'topic-1', name: 'Attention', usageCount: 1 } as any]);
    component.brainQuery.set('Attention');
    fixture.detectChanges();

    const indexScroll = fixture.nativeElement.querySelector(
      '[data-reference-scroll="topics"]',
    ) as HTMLElement;
    indexScroll.scrollTop = 123;
    component.captureReferenceScroll({ target: indexScroll } as unknown as Event);

    component.selectedTopicId.set('topic-1');
    component.selectedTopicNotes.set([note]);
    fixture.detectChanges();
    const topicNotesScroll = fixture.nativeElement.querySelector(
      '[data-reference-scroll="topicNotes"]',
    ) as HTMLElement;
    topicNotesScroll.scrollTop = 456;
    component.captureReferenceScroll({ target: topicNotesScroll } as unknown as Event);
    expect(
      fixture.nativeElement.querySelector('input[placeholder="Search topics…"]'),
    ).toBeNull();

    component.inspectSource(note);
    fixture.detectChanges();
    expect(
      fixture.nativeElement.querySelector('input[placeholder="Search topics…"]'),
    ).toBeNull();
    expect(
      (fixture.nativeElement.querySelector('.inspected-source-back') as HTMLButtonElement).textContent,
    ).toContain('Back to Attention');

    component.closeInspectedSource();
    component.backToTopics();
    fixture.detectChanges();
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));

    const search = fixture.nativeElement.querySelector(
      'input[placeholder="Search topics…"]',
    ) as HTMLInputElement;
    expect(search.value).toBe('Attention');
    const restoredIndex = fixture.nativeElement.querySelector(
      '[data-reference-scroll="topics"]',
    ) as HTMLElement;
    expect(restoredIndex.scrollTop).toBe(123);
  });

  it('replaces the kept-source list with one inspected-source drill-down and returns to it', () => {
    component.activeItem.set(sampleDoc1);
    component.keptSources.set([sourceAlpha]);
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector(
      '.kept-sources-content .reference-source-row-main',
    ) as HTMLElement;
    card.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.inspected-source-panel')).toBeTruthy();
    expect(fixture.nativeElement.querySelectorAll('.kept-sources-content .reference-source-row')).toHaveLength(0);

    const back = fixture.nativeElement.querySelector(
      '.inspected-source-back',
    ) as HTMLButtonElement;
    expect(back.textContent).toContain('For this document');
    expect(back.getAttribute('aria-label')).toBe('Back to For this document');

    back.click();
    fixture.detectChanges();

    expect(component.inspectedSource()).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.kept-sources-content .reference-source-row')).toHaveLength(1);
  });

  it('treats Library topic inspection as a child view without duplicating the selected note', () => {
    component.activeItem.set(sampleDoc1);
    component.referenceMode.set('library');
    component.selectTopic('c-1');
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector(
      '.reference-note-list .reference-source-row-main',
    ) as HTMLElement;
    card.click();
    fixture.detectChanges();

    expect(component.inspectedSource()?.id).toBe('note-alpha');
    expect(component.selectedTopicId()).toBe('c-1');
    expect(fixture.nativeElement.querySelector('.inspected-source-panel')).toBeTruthy();
    expect(fixture.nativeElement.querySelectorAll('.inspected-source-card')).toHaveLength(1);
    expect(fixture.nativeElement.querySelectorAll('.reference-note-list .reference-source-row')).toHaveLength(0);
    expect(
      fixture.nativeElement.querySelector('input[placeholder="Search topics…"]'),
    ).toBeNull();

    const back = fixture.nativeElement.querySelector(
      '.inspected-source-back',
    ) as HTMLButtonElement;
    expect(back.textContent).toContain('Back to Philosophy');
    expect(back.getAttribute('aria-label')).toBe('Back to Philosophy');
    expect(back.title).toBe('Back to Philosophy');

    back.click();
    fixture.detectChanges();

    expect(component.inspectedSource()).toBeNull();
    expect(component.selectedTopicId()).toBe('c-1');
    expect(fixture.nativeElement.querySelectorAll('.reference-note-list .reference-source-row')).toHaveLength(1);
    expect(
      fixture.nativeElement.querySelector('input[placeholder="Search topics…"]'),
    ).toBeNull();
  });

  it('preserves book context in the inspected-source return path', () => {
    component.referenceMode.set('library');
    component.activeSidebarTab.set('notes');
    component.books.set([{ id: 'book-1', title: 'Book Alpha', author: 'Author' } as any]);
    component.selectedBookId.set('book-1');
    component.selectedBookNotes.set([
      {
        id: sourceAlpha.id,
        bookId: sourceAlpha.bookId,
        bookTitle: sourceAlpha.bookTitle ?? undefined,
        content: sourceAlpha.content,
        selectedText: sourceAlpha.selectedText ?? undefined,
        cfiRange: sourceAlpha.cfiRange ?? undefined,
        createdAt: sourceAlpha.createdAt,
      },
    ]);
    component.inspectSource(component.selectedBookNotes()[0]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.reference-note-list .reference-source-row')).toHaveLength(0);
    const back = fixture.nativeElement.querySelector(
      '.inspected-source-back',
    ) as HTMLButtonElement;
    expect(back.textContent).toContain('Book Alpha');
    expect(back.getAttribute('aria-label')).toBe('Back to Book Alpha');

    back.click();
    fixture.detectChanges();

    expect(component.selectedBookId()).toBe('book-1');
    expect(fixture.nativeElement.querySelectorAll('.reference-note-list .reference-source-row')).toHaveLength(1);
  });

  it('prioritizes the source insertion and discloses only valid secondary actions', async () => {
    component.activeItem.set(sampleDoc1);

    component.inspectSource({
      id: 'quote-only',
      bookId: 'book-1',
      bookTitle: 'Book Alpha',
      selectedText: 'A quotation',
      content: 'A reflection',
      createdAt: '2026-08-01T10:00:00Z',
    });
    fixture.detectChanges();

    const primary = fixture.nativeElement.querySelector(
      '.source-action-primary-row .nostos-button',
    ) as HTMLButtonElement;
    expect(primary.textContent?.trim()).toBe('Insert quote');
    expect(primary.classList.contains('nostos-button--primary')).toBe(true);
    // As a note says it (issue #1211): a real button, the words and the arrow icon.
    const open = fixture.nativeElement.querySelector('.inspected-source-open') as HTMLButtonElement;
    expect(open.tagName).toBe('BUTTON');
    expect(open.textContent?.trim()).toBe('Open in book');
    expect(open.querySelector('nostos-icon')).not.toBeNull();

    // The secondary actions are a nostos-menu, as everywhere else (issue #1211).
    const menuItems = () =>
      Array.from(document.querySelectorAll('.nostos-menu-item')).map(
        (item) => item.querySelector('.nostos-menu-item__label')?.textContent?.trim(),
      );
    const more = fixture.nativeElement.querySelector(
      '.source-more-trigger',
    ) as HTMLButtonElement;
    expect(more.textContent?.trim()).toContain('More');
    expect(more.getAttribute('aria-haspopup')).toBe('menu');
    more.click();
    fixture.detectChanges();
    expect(more.getAttribute('aria-expanded')).toBe('true');
    expect(menuItems()).toEqual(['Insert as note', 'Insert as reference']);

    (document.querySelector('.nostos-menu') as HTMLElement).dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(menuItems()).toEqual([]);
    expect(more.getAttribute('aria-expanded')).toBe('false');

    component.inspectSource({
      id: 'note-only',
      bookId: 'book-1',
      bookTitle: 'Book Alpha',
      content: 'A reflection',
      createdAt: '2026-08-01T10:00:00Z',
    });
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('.source-action-primary-row .nostos-button')
        ?.textContent?.trim(),
    ).toBe('Insert as note');
    const noteMore = fixture.nativeElement.querySelector(
      '.source-more-trigger',
    ) as HTMLButtonElement;
    noteMore.click();
    fixture.detectChanges();
    expect(menuItems()).toEqual(['Insert as reference']);
  });

  describe('the @ source palette (#1039)', () => {
    const paletteQuote = {
      id: 'note-gamma',
      bookId: 'book-3',
      kind: 'note' as const,
      bookTitle: 'The World as Will and Representation',
      bookAuthor: 'Arthur Schopenhauer',
      selectedText: 'The world is my representation.',
      content: 'Opening line',
      sourceAnchorKind: 'pdf_page',
      sourceAnchorValue: '3',
      anchorVerified: true,
      passageTitle: null,
      lead: 'The world is my representation.',
      note: 'Opening line',
      kept: false,
    };

    beforeEach(() => {
      component.activeItem.set(sampleDoc1);
      component.editorText.set(sampleDoc1.content);
      fixture.detectChanges();
    });

    it('opens while an @ query is typed and closes with it', () => {
      component.onSourceSuggestion({ query: 'schop', rect: { top: 10, bottom: 30, left: 40, right: 50 } });
      expect(component.paletteQuery()).toBe('schop');
      component.onSourceSuggestion(null);
      expect(component.paletteQuery()).toBeNull();
    });

    describe('in a citation style (issue #1109)', () => {
      let prefs: StudioPreferencesService;
      let citations: CitationService;

      beforeEach(() => {
        prefs = TestBed.inject(StudioPreferencesService);
        citations = TestBed.inject(CitationService);
      });

      afterEach(() => prefs.update({ citationStyle: 'nostos' }));

      it('closes the quote with an in-text citation built from the book', async () => {
        const editor = (component as any).markdownEditor as MarkdownEditorStub;
        prefs.update({ citationStyle: 'apa' });
        const cite = vi.spyOn(citations, 'cite').mockResolvedValue('(Schopenhauer, 1818, p. 3)');

        component.insertFromPalette({ source: paletteQuote, mode: 'quote' });
        await vi.waitFor(() => expect(editor.replaceSourceQuery).toHaveBeenCalled());

        expect(cite).toHaveBeenCalledWith(paletteQuote, expect.objectContaining({ id: 'apa' }), false, 'quote');
        // A live citation (issue #1157): the words, with the book and page they follow.
        expect(editor.replaceSourceQuery).toHaveBeenCalledWith(
          '> The world is my representation.\n>\n> [(Schopenhauer, 1818, p. 3)](nostos:cite/book-3?p=3&as=quote&src=note-gamma)',
          'block',
          null,
        );
        expect(writingsService.addSource).toHaveBeenCalledWith('doc-1', 'note-gamma');
      });

      it('in a note style marks the quote and puts the note at the end; a later citation of the book is short', async () => {
        const editor = (component as any).markdownEditor as MarkdownEditorStub;
        prefs.update({ citationStyle: 'chicago-notes' });
        editor.nextFootnoteNumber.mockReturnValue(4);
        // The book is cited in the writing already, so its note is the short one.
        editor.citedBooks.mockReturnValue(['book-3']);
        const cite = vi.spyOn(citations, 'cite').mockResolvedValue('Schopenhauer, *The World*, 3.');

        component.insertFromPalette({ source: paletteQuote, mode: 'quote' });
        await vi.waitFor(() => expect(editor.replaceSourceQuery).toHaveBeenCalled());

        expect(cite).toHaveBeenCalledWith(paletteQuote, expect.objectContaining({ id: 'chicago-notes' }), true, 'quote');
        // The marker and its note go in together, as one insertion.
        expect(editor.replaceSourceQuery).toHaveBeenCalledWith('> The world is my representation.[^4]', 'block', {
          number: 4,
          markdown: '[Schopenhauer, *The World*, 3.](nostos:cite/book-3?p=3&as=quote&src=note-gamma)',
        });
      });

      it('falls back to the Nostos format, and says so, when the book cannot be read', async () => {
        const editor = (component as any).markdownEditor as MarkdownEditorStub;
        prefs.update({ citationStyle: 'mla' });
        vi.spyOn(citations, 'cite').mockResolvedValue(null);
        const error = vi.spyOn(TestBed.inject(ToastService), 'error');

        component.insertFromPalette({ source: paletteQuote, mode: 'reference' });
        await vi.waitFor(() => expect(editor.replaceSourceQuery).toHaveBeenCalled());

        expect(editor.replaceSourceQuery).toHaveBeenCalledWith(
          '*See: Arthur Schopenhauer, The World as Will and Representation, p. 3*',
          'inline',
          null,
        );
        expect(error).toHaveBeenCalledWith(expect.stringContaining('MLA 9'));
      });

      it('builds the bibliography from the books cited in this document', async () => {
        const editor = (component as any).markdownEditor as MarkdownEditorStub;
        prefs.update({ citationStyle: 'mla' });
        component.keptSources.set([
          { ...paletteQuote, id: 'a', bookId: 'book-3' } as any,
          { ...paletteQuote, id: 'b', bookId: 'book-4' } as any,
        ]);
        const bibliography = vi.spyOn(citations, 'bibliographyEntries').mockResolvedValue([
          { book: 'book-3', text: 'Entry one.' },
          { book: 'book-4', text: 'Entry two.' },
        ]);

        await component.insertBibliography();

        expect(bibliography).toHaveBeenCalledWith(['book-3', 'book-4'], expect.objectContaining({ id: 'mla' }));
        expect(editor.insertBlockMarkdown).toHaveBeenCalledWith(
          '## Works Cited\n\n[Entry one.](nostos:cite/book-3?as=bib)\n\n[Entry two.](nostos:cite/book-4?as=bib)',
        );
      });

      it('lists the cited books first, and does not put in a second bibliography', async () => {
        const editor = (component as any).markdownEditor as MarkdownEditorStub;
        editor.citedBooks.mockReturnValue(['book-4']);
        component.keptSources.set([{ ...paletteQuote, id: 'a', bookId: 'book-3' } as any]);
        const bibliography = vi.spyOn(citations, 'bibliographyEntries').mockResolvedValue([]);

        await component.insertBibliography();
        expect(bibliography).toHaveBeenCalledWith(['book-4', 'book-3'], expect.anything());
        expect(toastService.info).toHaveBeenCalledWith('Nothing is cited in this document yet');

        editor.hasBibliography.mockReturnValue(true);
        bibliography.mockClear();
        await component.insertBibliography();
        expect(bibliography).not.toHaveBeenCalled();
        expect(toastService.info).toHaveBeenCalledWith(expect.stringContaining('already has a bibliography'));
      });

      it('says once that a cited book lacks the year, with a way to add it', async () => {
        const editor = (component as any).markdownEditor as MarkdownEditorStub;
        prefs.update({ citationStyle: 'apa' });
        vi.spyOn(citations, 'cite').mockResolvedValue('(Schopenhauer, n.d.)');
        vi.spyOn(citations, 'book').mockResolvedValue({
          id: 'book-3',
          title: 'The World',
          author: 'Arthur Schopenhauer',
          publisher: 'Dover',
          publishedDate: null,
        } as any);

        component.insertFromPalette({ source: paletteQuote, mode: 'reference' });
        await vi.waitFor(() => expect(toastService.show).toHaveBeenCalled());
        expect(toastService.show).toHaveBeenCalledWith(
          '“The World” has no year in its details, so APA 7 cites it without.',
          'info',
          8000,
          expect.objectContaining({ label: 'Add' }),
        );

        // The action opens the Citations list at that book.
        const action = toastService.show.mock.calls[0][3] as { run: () => void };
        action.run();
        await Promise.resolve();
        expect(component.pageSettingsOpen()).toBe(true);
        expect(component.pageSettingsStart()).toEqual({ view: 'citations', book: 'book-3' });

        // Said once per writing.
        toastService.show.mockClear();
        editor.replaceSourceQuery.mockClear();
        component.insertFromPalette({ source: paletteQuote, mode: 'reference' });
        await vi.waitFor(() => expect(editor.replaceSourceQuery).toHaveBeenCalled());
        await new Promise((resolve) => setTimeout(resolve, 0));
        expect(toastService.show).not.toHaveBeenCalled();
      });

      it('writes the open writing\'s citations again when the style changes', async () => {
        const editor = (component as any).markdownEditor as MarkdownEditorStub;
        editor.citedBooks.mockReturnValue(['book-3']);
        const renderer = vi.spyOn(citations, 'renderer').mockImplementation(async (style) => ({
          placement: style.kind === 'note' ? 'note' : 'in-text',
          bibliographyHeading: 'Bibliography',
          bibliographyBooks: [],
          inText: () => null,
          note: () => null,
          bibliography: () => null,
        }));

        prefs.update({ citationStyle: 'chicago-notes' });
        fixture.detectChanges();
        await vi.waitFor(() => expect(editor.refreshCitations).toHaveBeenCalled());
        expect(renderer).toHaveBeenCalledWith(expect.objectContaining({ id: 'chicago-notes' }), ['book-3'], expect.any(Array));
        const given = editor.refreshCitations.mock.calls.at(-1)![0] as { placement: string };
        expect(given.placement).toBe('note');
      });
    });

    it('replaces the @ query with a quote that names the author, and keeps the source', async () => {
      const editor = (component as any).markdownEditor as MarkdownEditorStub;
      component.insertFromPalette({ source: paletteQuote, mode: 'quote' });

      // A live citation in the house format, from the book's details (issue #1157).
      await vi.waitFor(() => expect(editor.replaceSourceQuery).toHaveBeenCalled());
      expect(editor.replaceSourceQuery).toHaveBeenCalledWith(
        '> The world is my representation.\n>\n> [— Arthur Schopenhauer, *The World as Will and Representation*, p. 3](nostos:cite/book-3?p=3&as=quote&src=note-gamma)',
        'block',
        null,
      );
      expect(writingsService.addSource).toHaveBeenCalledWith('doc-1', 'note-gamma');
    });

    it('puts a reference inline, in place of the query', async () => {
      const editor = (component as any).markdownEditor as MarkdownEditorStub;
      component.insertFromPalette({ source: paletteQuote, mode: 'reference' });

      await vi.waitFor(() => expect(editor.replaceSourceQuery).toHaveBeenCalled());
      expect(editor.replaceSourceQuery).toHaveBeenCalledWith(
        '[*See: Arthur Schopenhauer, The World as Will and Representation, p. 3*](nostos:cite/book-3?p=3&as=reference&src=note-gamma)',
        'inline',
        null,
      );
    });

    it('falls back to plain house text for a source with no book', () => {
      const editor = (component as any).markdownEditor as MarkdownEditorStub;
      component.insertFromPalette({ source: { ...paletteQuote, bookId: null } as any, mode: 'reference' });

      expect(editor.replaceSourceQuery).toHaveBeenCalledWith(
        '*See: Arthur Schopenhauer, The World as Will and Representation, p. 3*',
        'inline',
      );
    });

    it('opens a passage in the viewer instead of inserting it whole', () => {
      const editor = (component as any).markdownEditor as MarkdownEditorStub;
      const viewer = TestBed.inject(PassageViewerService);
      const open = vi.spyOn(viewer, 'open').mockImplementation(() => undefined);
      const passage = { ...paletteQuote, id: 'passage-1', kind: 'passage' as const, passageTitle: 'On the Vanity of Existence' };

      component.insertFromPalette({ source: passage, mode: 'quote' });

      expect(editor.clearSourceQuery).toHaveBeenCalled();
      expect(editor.replaceSourceQuery).not.toHaveBeenCalled();
      expect(open).toHaveBeenCalledWith('passage-1', expect.objectContaining({ quoteSelection: expect.any(Function) }));

      const options = open.mock.calls[0][1] as { quoteSelection: (text: string, passage: unknown) => boolean };
      expect(writingsService.addSource).not.toHaveBeenCalledWith('doc-1', 'passage-1');
      const toast = TestBed.inject(ToastService);
      const success = vi.spyOn(toast, 'success');
      const quoted = options.quoteSelection('Time is that in which all things pass away.', {
        id: 'passage-1',
        bookTitle: 'Studies in Pessimism',
        bookAuthor: 'Arthur Schopenhauer',
        title: 'On the Vanity of Existence',
      });
      expect(editor.insertBlockMarkdown).toHaveBeenCalledWith(
        '> Time is that in which all things pass away.\n>\n> — Arthur Schopenhauer, *Studies in Pessimism*, “On the Vanity of Existence”',
        null,
      );
      // Kept only once something was quoted from it.
      expect(writingsService.addSource).toHaveBeenCalledWith('doc-1', 'passage-1');
      // The viewer confirms on its own button; no toast lands on the page.
      expect(quoted).toBe(true);
      expect(success).not.toHaveBeenCalled();
    });

    it('reports a quote from the Reference viewer as inserted, or not, without a success toast', async () => {
      const editor = (component as any).markdownEditor as MarkdownEditorStub;
      const toast = TestBed.inject(ToastService);
      const success = vi.spyOn(toast, 'success');
      const passage = { id: 'passage-1', bookTitle: 'Studies in Pessimism', bookAuthor: 'Arthur Schopenhauer', title: 'On the Vanity of Existence' };

      await expect(component.passageViewerOptions.quoteSelection!('Time is that in which all things pass away.', passage as never)).resolves.toBe(true);
      expect(editor.insertMarkdown).toHaveBeenCalled();
      expect(success).not.toHaveBeenCalled();

      editor.insertMarkdown.mockResolvedValueOnce(false);
      await expect(component.passageViewerOptions.quoteSelection!('Again.', passage as never)).resolves.toBe(false);
      expect(toast.error).toHaveBeenCalledWith('The editor is not ready for insertion yet');
    });

    it('sends "Browse reference" to the searched Books list', () => {
      component.browseFromPalette('schopenhaur');
      expect(component.referenceMode()).toBe('library');
      expect(component.activeSidebarTab()).toBe('notes');
      expect(component.bookQuery()).toBe('schopenhaur');
    });
  });

  it('delegates deliberate insertion to the editor boundary without concatenating editorText', async () => {
    component.activeItem.set(sampleDoc1);
    component.editorText.set(sampleDoc1.content);
    fixture.detectChanges();

    const editor = (component as any).markdownEditor as MarkdownEditorStub;
    editor.insertMarkdown.mockClear();

    await component.insertQuote(sourceBeta as any);

    expect(editor.insertMarkdown).toHaveBeenCalledWith(
      '> Beta excerpt\n>\n> [— *Book Beta*, p. 42](nostos:cite/book-2?p=42&as=quote&src=note-beta)',
      null,
    );
    expect(component.editorText()).toBe(sampleDoc1.content);
  });

  it('returns mobile drafting to the editor after a successful deliberate insertion', async () => {
    component.activeItem.set(sampleDoc1);
    component.isCompact.set(true);
    component.showBrainSidebar.set(true);
    fixture.detectChanges();

    await component.insertReference(sourceBeta as any);

    expect(component.showBrainSidebar()).toBe(false);
  });

  it('fails insertion honestly when the editor is not ready', async () => {
    component.activeItem.set(sampleDoc1);
    fixture.detectChanges();

    const editor = (component as any).markdownEditor as MarkdownEditorStub;
    editor.insertMarkdown.mockResolvedValueOnce(false);

    await component.insertReference(sourceBeta as any);

    expect(toastService.error).toHaveBeenCalledWith(
      'The editor is not ready for insertion yet',
    );
  });

  it('keeps Open source typed navigation while adding only an explicit Studio origin marker', async () => {
    const navigate = vi.fn(() => Promise.resolve(true));
    (component as any).router = { navigate };
    component.activeItem.set(sampleDoc1);
    component.editorTitle.set(sampleDoc1.name);
    component.editorText.set(sampleDoc1.content);
    fixture.detectChanges();

    window.history.replaceState({ navigationId: 7, unrelated: 'preserve-me' }, '', '/studio');

    await component.openSource(sourceBeta as any);
    expect(window.location.pathname + window.location.search).toBe('/studio?writingId=doc-1');
    expect(window.history.state.unrelated).toBe('preserve-me');
    expect(window.history.state.nostosStudioSourceReturn).toMatchObject({
      version: 1,
      writingId: 'doc-1',
      editor: { scrollY: 240 },
      references: { mode: 'writing', activeLibraryTab: 'brain' },
    });
    expect(navigate).toHaveBeenLastCalledWith(['/read', 'book-2'], {
      queryParams: { sourcePage: 42 },
      state: {
        nostosReaderReturnOrigin: { version: 1, kind: 'studio', writingId: 'doc-1' },
      },
    });

    await component.openSource(sourceAlpha as any);
    expect(navigate).toHaveBeenLastCalledWith(['/read', 'book-1'], {
      queryParams: { sourceCfi: 'epubcfi(/6/2)' },
      state: {
        nostosReaderReturnOrigin: { version: 1, kind: 'studio', writingId: 'doc-1' },
      },
    });

    await component.openSource({
      id: 'physical',
      bookId: 'book-3',
      bookTitle: 'Physical Book',
      content: 'note',
      createdAt: '2026-08-01T10:00:00Z',
      sourceAnchorKind: 'physical_page',
      sourceAnchorValue: '17',
      anchorVerified: true,
    });
    expect(navigate).toHaveBeenLastCalledWith(['/library', 'book-3']);
  });

  it('keeps legacy EPUB CFI navigation and never puts return/editor state in query params', async () => {
    const navigate = vi.fn(() => Promise.resolve(true));
    (component as any).router = { navigate };
    component.activeItem.set(sampleDoc1);
    component.editorTitle.set(sampleDoc1.name);
    component.editorText.set(sampleDoc1.content);
    fixture.detectChanges();

    await component.openSource({
      ...sourceAlpha,
      sourceAnchorKind: null,
      sourceAnchorValue: null,
      anchorVerified: false,
      cfiRange: 'epubcfi(/8/4)',
    } as any);

    expect(navigate).toHaveBeenCalledWith(['/read', 'book-1'], {
      queryParams: { sourceCfi: 'epubcfi(/8/4)' },
      state: {
        nostosReaderReturnOrigin: { version: 1, kind: 'studio', writingId: 'doc-1' },
      },
    });
    const readerExtras = (navigate.mock.calls[0] as unknown as [unknown, any])[1];
    expect(readerExtras.queryParams).toEqual({ sourceCfi: 'epubcfi(/8/4)' });
  });

  it('does not leave Studio when there is no active Writing return context', async () => {
    const navigate = vi.fn(() => Promise.resolve(true));
    (component as any).router = { navigate };

    await component.openSource(sourceBeta as any);

    expect(navigate).not.toHaveBeenCalled();
    expect(toastService.error).toHaveBeenCalledWith('Open a document before opening a source');
  });

  it('does not write an already-saved Writing merely to open a source', async () => {
    const navigate = vi.fn(() => Promise.resolve(true));
    (component as any).router = { navigate };
    component.activeItem.set(sampleDoc1);
    component.editorTitle.set(sampleDoc1.name);
    component.editorText.set(sampleDoc1.content);
    fixture.detectChanges();
    writingsService.update.mockClear();

    await component.openSource(sourceBeta as any);

    expect(writingsService.update).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledTimes(1);
  });

  it('flushes pending prose before source navigation and keeps the draft on save failure', async () => {
    const navigate = vi.fn(() => Promise.resolve(true));
    (component as any).router = { navigate };
    component.activeItem.set(sampleDoc1);
    component.editorTitle.set(sampleDoc1.name);
    component.editorText.set('Newest unsaved paragraph');
    fixture.detectChanges();

    writingsService.update.mockReturnValueOnce(
      throwError(() => new Error('save failed')),
    );

    await component.openSource(sourceBeta as any);

    expect(writingsService.update).toHaveBeenCalledWith('doc-1', {
      name: sampleDoc1.name,
      content: 'Newest unsaved paragraph',
    });
    expect(navigate).not.toHaveBeenCalled();
    expect(component.editorText()).toBe('Newest unsaved paragraph');
    expect(component.saveStatus()).toBe('Unsaved');
    expect(toastService.error).toHaveBeenCalledWith(
      'Could not save this document. Your draft is still open.',
    );
  });

  it('re-flushes text typed while the explicit source save itself is in flight', async () => {
    const navigate = vi.fn(() => Promise.resolve(true));
    (component as any).router = { navigate };
    const firstSave = new Subject<WritingContentDto>();
    const secondSave = new Subject<WritingContentDto>();
    writingsService.update
      .mockReturnValueOnce(firstSave.asObservable())
      .mockReturnValueOnce(secondSave.asObservable());

    component.activeItem.set(sampleDoc1);
    component.editorTitle.set(sampleDoc1.name);
    component.editorText.set('First unsaved version');
    fixture.detectChanges();

    const opening = component.openSource(sourceBeta as any);
    await settleSaveQueue();
    expect(writingsService.update).toHaveBeenCalledTimes(1);

    component.editorText.set('Typed while save was running');
    fixture.detectChanges();

    firstSave.next({ ...sampleDoc1, content: 'First unsaved version' });
    firstSave.complete();
    await settleSaveQueue();

    expect(writingsService.update).toHaveBeenCalledTimes(2);
    expect(writingsService.update).toHaveBeenLastCalledWith('doc-1', {
      name: sampleDoc1.name,
      content: 'Typed while save was running',
    });
    expect(navigate).not.toHaveBeenCalled();

    secondSave.next({ ...sampleDoc1, content: 'Typed while save was running' });
    secondSave.complete();
    await opening;

    expect(navigate).toHaveBeenCalledTimes(1);
    expect(component.activeItem()?.content).toBe('Typed while save was running');
  });

  it('serializes an older autosave ahead of the source flush so stale content cannot win last', async () => {
    vi.useFakeTimers();
    try {
      const navigate = vi.fn(() => Promise.resolve(true));
      (component as any).router = { navigate };
      const oldSave = new Subject<WritingContentDto>();
      const latestSave = new Subject<WritingContentDto>();
      writingsService.update
        .mockReturnValueOnce(oldSave.asObservable())
        .mockReturnValueOnce(latestSave.asObservable());

      component.activeItem.set(sampleDoc1);
      component.editorTitle.set(sampleDoc1.name);
      component.editorText.set('Older debounce text');
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(2000);

      component.editorText.set('Latest text before source');
      fixture.detectChanges();
      const opening = component.openSource(sourceBeta as any);
      await Promise.resolve();

      expect(writingsService.update).toHaveBeenCalledTimes(1);
      oldSave.next({ ...sampleDoc1, content: 'Older debounce text' });
      oldSave.complete();
      await settleSaveQueue();

      expect(writingsService.update).toHaveBeenCalledTimes(2);
      expect(writingsService.update).toHaveBeenLastCalledWith('doc-1', {
        name: sampleDoc1.name,
        content: 'Latest text before source',
      });

      latestSave.next({ ...sampleDoc1, content: 'Latest text before source' });
      latestSave.complete();
      await opening;

      expect(navigate).toHaveBeenCalledTimes(1);
      expect(component.activeItem()?.content).toBe('Latest text before source');
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('WritingStudio writingId handoff (#492/#493 seam)', () => {
  let fixture: ComponentFixture<WritingStudio>;
  let component: WritingStudio;
  let queryParams: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  let writingsService: {
    list: ReturnType<typeof vi.fn>;
    get: ReturnType<typeof vi.fn>;
    create: ReturnType<typeof vi.fn>;
    update: ReturnType<typeof vi.fn>;
    delete: ReturnType<typeof vi.fn>;
    move: ReturnType<typeof vi.fn>;
    listSources: ReturnType<typeof vi.fn>;
    addSource: ReturnType<typeof vi.fn>;
    removeSource: ReturnType<typeof vi.fn>;
  };
  let toastService: { error: ReturnType<typeof vi.fn>; success: ReturnType<typeof vi.fn> };

  const doc = {
    id: 'doc-handoff',
    name: 'Brain handoff draft',
    type: 'Document' as const,
    parentId: null,
    updatedAt: '2026-09-25T00:00:00Z',
  };
  const folder = {
    id: 'folder-handoff',
    name: 'Folder',
    type: 'Folder' as const,
    parentId: null,
    updatedAt: '2026-09-25T00:00:00Z',
  };
  const content: WritingContentDto = {
    id: doc.id,
    name: doc.name,
    content: 'Question\n\nWorking paragraph.',
    updatedAt: doc.updatedAt,
  };
  const kept: WritingSourceDto = {
    id: 'note-handoff',
    bookId: 'book-handoff',
    bookTitle: 'The Republic',
    content: 'Reflection',
    selectedText: 'Justice',
    createdAt: '2026-09-20T00:00:00Z',
    addedAt: '2026-09-25T00:00:00Z',
    sourceAnchorKind: 'pdf_page',
    sourceAnchorValue: '42',
    anchorVerified: true,
  };

  async function createWithParams(
    params: Record<string, string> = {},
    historyState: Record<string, unknown> = {},
    lastWritingId: string | null = null,
  ) {
    // The Studio reopens the last writing; each case starts with none unless given.
    localStorage.clear();
    if (lastWritingId) localStorage.setItem('nostos.studio.lastWritingId', lastWritingId);
    const query = new URLSearchParams(params).toString();
    window.history.replaceState(historyState, '', `/studio${query ? `?${query}` : ''}`);
    queryParams = new BehaviorSubject(convertToParamMap(params));
    writingsService = {
      list: vi.fn(() => of([doc, folder])),
      get: vi.fn(() => of(content)),
      create: vi.fn(() => of({})),
      update: vi.fn(() => of(content)),
      delete: vi.fn(() => of(undefined)),
      move: vi.fn(() => of({})),
      listSources: vi.fn(() => of([kept])),
      addSource: vi.fn(() => of(kept)),
      removeSource: vi.fn(() => of(undefined)),
    };
    toastService = { error: vi.fn(), success: vi.fn() };

    TestBed.overrideComponent(WritingStudio, {
      remove: { imports: [FlatTreeComponent, NoteItemComponent, MarkdownEditorComponent] },
      add: { imports: [FlatTreeStub, NoteItemStub, MarkdownEditorStub] },
    });

    await TestBed.configureTestingModule({
      imports: [WritingStudio],
      providers: [
        { provide: WritingsService, useValue: writingsService },
        { provide: ToastService, useValue: toastService },
        { provide: ActivatedRoute, useValue: {
          queryParamMap: queryParams.asObservable(),
          snapshot: { queryParamMap: queryParams.value },
        } },
        { provide: TopicsService, useValue: { list: vi.fn(() => of([])), get: vi.fn() } },
        { provide: BooksService, useValue: { list: vi.fn(() => of({ items: [] })) } },
        { provide: NotesService, useValue: { list: vi.fn(() => of([])), getMany: vi.fn(() => of([])) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(WritingStudio);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('opens a valid writingId with title, content and kept sources', async () => {
    await createWithParams({ writingId: doc.id });

    expect(writingsService.get).toHaveBeenCalledWith(doc.id);
    expect(component.activeItem()?.id).toBe(doc.id);
    expect(component.editorTitle()).toBe(doc.name);
    expect(component.editorText()).toBe(content.content);
    expect(writingsService.listSources).toHaveBeenCalledWith(doc.id);
    expect(component.keptSources()).toEqual([kept]);
  });

  it('reacts to writingId query-param changes without inventing another handoff contract', async () => {
    await createWithParams();

    expect(writingsService.get).not.toHaveBeenCalled();
    queryParams.next(convertToParamMap({ writingId: doc.id }));
    fixture.detectChanges();

    expect(writingsService.get).toHaveBeenCalledWith(doc.id);
    expect(component.activeItem()?.id).toBe(doc.id);
  });

  it('does not treat a folder id as an editor document', async () => {
    await createWithParams({ writingId: folder.id });

    expect(writingsService.get).not.toHaveBeenCalled();
    expect(component.activeItem()).toBeNull();
    expect(toastService.error).toHaveBeenCalledWith('That writing is unavailable');
  });

  it('handles a missing writing id safely', async () => {
    await createWithParams({ writingId: 'missing-id' });

    expect(writingsService.get).not.toHaveBeenCalled();
    expect(component.activeItem()).toBeNull();
    expect(toastService.error).toHaveBeenCalledWith('That writing is unavailable');
  });

  it('handles a deleted writing that disappears between list and get safely', async () => {
    await createWithParams();
    writingsService.get.mockReturnValueOnce(
      throwError(() => new Error('404')),
    );

    queryParams.next(convertToParamMap({ writingId: doc.id }));
    fixture.detectChanges();

    expect(component.activeItem()).toBeNull();
    expect(toastService.error).toHaveBeenCalledWith('That writing is unavailable');
  });

  it('opens a blank page on /studio with no writingId and no writing to return to', async () => {
    await createWithParams();

    expect(writingsService.get).not.toHaveBeenCalled();
    expect(component.activeItem()).toBeNull();
    expect(component.blankPage()).toBe(true);
    expect(component.referenceMode()).toBe('writing');
  });

  it('reopens the last writing on /studio with no writingId', async () => {
    await createWithParams({}, {}, doc.id);

    expect(writingsService.get).toHaveBeenCalledWith(doc.id);
    expect(component.activeItem()?.id).toBe(doc.id);
    expect(component.blankPage()).toBe(false);
  });

  it('lands a mobile handoff in the editor with both drawers closed', async () => {
    await createWithParams();

    component.isCompact.set(true);
    component.showFileSidebar.set(true);
    component.showBrainSidebar.set(true);

    queryParams.next(convertToParamMap({ writingId: doc.id }));
    fixture.detectChanges();

    expect(component.activeItem()?.id).toBe(doc.id);
    expect(component.showFileSidebar()).toBe(false);
    expect(component.showBrainSidebar()).toBe(false);
  });

  it('restores and consumes a matching Studio source-return snapshot only after opening that Writing', async () => {
    await createWithParams(
      { writingId: doc.id },
      {
        navigationId: 31,
        unrelated: 'keep',
        nostosStudioSourceReturn: {
          version: 1,
          writingId: doc.id,
          editor: { bookmark: { start: [4, 0] }, scrollY: 515 },
          references: {
            mode: 'writing',
            activeLibraryTab: 'brain',
            wasOpen: true,
            inspectedSourceId: kept.id,
            selectedTopicId: null,
            selectedBookId: null,
          },
        },
      },
    );

    await Promise.resolve();
    await Promise.resolve();

    const editor = (component as any).markdownEditor as MarkdownEditorStub;
    expect(component.activeItem()?.id).toBe(doc.id);
    expect(component.referenceMode()).toBe('writing');
    expect(component.showBrainSidebar()).toBe(true);
    expect(component.inspectedSource()?.id).toBe(kept.id);
    expect(editor.restoreTransientState).toHaveBeenCalledWith(
      { bookmark: { start: [4, 0] }, scrollY: 515 },
      content.content,
    );
    expect(window.history.state.nostosStudioSourceReturn).toBeUndefined();
    expect(window.history.state.unrelated).toBe('keep');
    expect(writingsService.update).not.toHaveBeenCalled();
  });

  it('never applies a return snapshot for Writing B to requested Writing A', async () => {
    await createWithParams(
      { writingId: doc.id },
      {
        nostosStudioSourceReturn: {
          version: 1,
          writingId: 'different-writing',
          editor: { bookmark: { start: [9, 0] }, scrollY: 999 },
        },
      },
    );

    await Promise.resolve();

    const editor = (component as any).markdownEditor as MarkdownEditorStub;
    expect(component.activeItem()?.id).toBe(doc.id);
    expect(editor.restoreTransientState).not.toHaveBeenCalled();
    expect(window.history.state.nostosStudioSourceReturn).toBeUndefined();
  });

  it('restores mobile reference state as one pane without reopening Documents', async () => {
    await createWithParams();

    component.isCompact.set(true);
    component.showFileSidebar.set(true);
    component.showBrainSidebar.set(false);

    window.history.replaceState(
      {
        nostosStudioSourceReturn: {
          version: 1,
          writingId: doc.id,
          references: {
            mode: 'writing',
            activeLibraryTab: 'brain',
            wasOpen: true,
            inspectedSourceId: null,
            selectedTopicId: null,
            selectedBookId: null,
          },
        },
      },
      '',
      `/studio?writingId=${doc.id}`,
    );
    (component as any).prepareSourceReturnRestore();
    queryParams.next(convertToParamMap({ writingId: doc.id }));
    fixture.detectChanges();
    await Promise.resolve();

    expect(component.activeItem()?.id).toBe(doc.id);
    expect(component.showFileSidebar()).toBe(false);
    expect(component.showBrainSidebar()).toBe(true);
  });
});

describe('WritingStudio export menu (issue #1164)', () => {
  let fixture: ComponentFixture<WritingStudio>;
  let component: WritingStudio;

  beforeEach(async () => {
    TestBed.overrideComponent(WritingStudio, {
      remove: { imports: [FlatTreeComponent, NoteItemComponent, MarkdownEditorComponent] },
      add: { imports: [FlatTreeStub, NoteItemStub, MarkdownEditorStub] },
    });

    await TestBed.configureTestingModule({
      imports: [WritingStudio],
      providers: [
        {
          provide: WritingsService,
          useValue: {
            list: vi.fn(() => of([])),
            get: vi.fn(() => of({ id: 'doc', name: 'Doc', content: 'Words.', updatedAt: '' })),
            create: vi.fn(() => of({})),
            update: vi.fn(() => of({})),
            delete: vi.fn(() => of({})),
            move: vi.fn(() => of({})),
            listSources: vi.fn(() => of([])),
            addSource: vi.fn(() => of({})),
            removeSource: vi.fn(() => of({})),
            getPageSetup: vi.fn(() => of(null)),
            savePageSetup: vi.fn(() => of({})),
          },
        },
        { provide: ToastService, useValue: { error: vi.fn(), success: vi.fn(), show: vi.fn() } },
        { provide: TopicsService, useValue: { list: vi.fn(() => of([])), get: vi.fn() } },
        { provide: BooksService, useValue: { list: vi.fn(() => of({ items: [] })) } },
        { provide: NotesService, useValue: { list: vi.fn(() => of([])), getMany: vi.fn(() => of([])) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(WritingStudio);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => fixture.destroy());

  function open(id: string, content: string, parentId: string | null = null): void {
    component.rootItems.set([
      { id: 'folder', name: 'Folder', type: 'Folder', parentId: null, updatedAt: '' },
      { id, name: 'Doc', type: 'Document', parentId, updatedAt: '' },
    ]);
    component.activeItem.set({ id, name: 'Doc', content, updatedAt: '' });
    component.editorText.set(content);
    fixture.detectChanges();
  }

  function exportItem(): HTMLButtonElement | null {
    (fixture.nativeElement.querySelector('[data-testid="studio-more"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return document.querySelector('[data-testid="studio-export"]');
  }

  it('offers one “Export…” item in the Studio menu, which opens the export dialog', async () => {
    open('doc', 'Words.');
    const item = exportItem();
    expect(item?.textContent).toContain('Export…');
    expect(item?.getAttribute('aria-disabled')).not.toBe('true');

    item!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(component.exportOpen()).toBe(true);
    expect(fixture.nativeElement.querySelector('app-export-dialog')).toBeTruthy();
  });

  it('disables it for an empty writing, and says why', () => {
    open('doc', '   \n');
    expect(component.exportBlockedReason()).toBe('This document is empty');
    const item = exportItem();
    expect(item?.getAttribute('aria-disabled')).toBe('true');
    expect(item?.textContent).toContain('This document is empty');

    component.openExport();
    expect(component.exportOpen()).toBe(false);
  });

  it('keeps it open for an empty writing in a folder, which may still be exported whole', () => {
    open('doc', '', 'folder');
    expect(component.exportBlockedReason()).toBeNull();
  });

  it('has nothing to export on a blank page', () => {
    component.activeItem.set(null);
    expect(component.exportBlockedReason()).toBe('Nothing to export yet');
  });
});

describe('WritingStudio: what the writing touches (issue #1162)', () => {
  let fixture: ComponentFixture<WritingStudio>;
  let component: WritingStudio;
  let getMany: ReturnType<typeof vi.fn>;
  const router = { navigate: vi.fn(async () => true) };

  const book = '11111111-1111-1111-1111-111111111111';
  const cited = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
  const alsoCited = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
  const keptOnly = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
  const pasted = 'dddddddd-dddd-dddd-dddd-dddddddddddd';
  const removed = 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee';
  const plain = 'ffffffff-ffff-ffff-ffff-ffffffffffff';
  const cite = (id: string, extra = 'as=quote') => `[(Voltaire, 1759)](nostos:cite/${book}?${extra}&src=${id})`;

  const doc: WritingContentDto = { id: 'doc-touch', name: 'Essay', content: '', updatedAt: '2026-10-01T00:00:00Z' };

  const kept = (id: string, content: string): WritingSourceDto => ({
    id,
    bookId: book,
    bookTitle: 'Candide',
    content,
    selectedText: null,
    cfiRange: null,
    createdAt: '2026-10-01T00:00:00Z',
    addedAt: '2026-10-01T00:00:00Z',
    sourceAnchorKind: 'unknown',
    sourceAnchorValue: null,
    anchorVerified: false,
    kind: 'note',
  });

  const topicsByNote: Record<string, string[]> = {
    [cited]: ['Theodicy', 'Optimism'],
    [alsoCited]: ['Optimism'],
    [keptOnly]: ['Gardening'],
    [pasted]: ['Providence'],
    [plain]: [],
  };

  beforeEach(async () => {
    vi.useFakeTimers();
    getMany = vi.fn((ids: string[]) =>
      of(ids.filter((id) => topicsByNote[id]).map((id) => ({
        id,
        bookId: book,
        bookTitle: 'Candide',
        content: `Note ${id.slice(0, 4)}`,
        selectedText: null,
        snippet: null,
        topicNames: topicsByNote[id],
        createdAt: '2026-10-01T00:00:00Z',
      }))));

    TestBed.overrideComponent(WritingStudio, {
      remove: { imports: [FlatTreeComponent, NoteItemComponent, MarkdownEditorComponent] },
      add: { imports: [FlatTreeStub, NoteItemStub, MarkdownEditorStub] },
    });
    await TestBed.configureTestingModule({
      imports: [WritingStudio],
      providers: [
        {
          provide: WritingsService,
          useValue: {
            list: vi.fn(() => of([])),
            get: vi.fn(() => of(doc)),
            create: vi.fn(() => of({})),
            update: vi.fn(() => of({})),
            delete: vi.fn(() => of({})),
            move: vi.fn(() => of({})),
            listSources: vi.fn(() => of([])),
            addSource: vi.fn(() => of({})),
            removeSource: vi.fn(() => of({})),
            getPageSetup: vi.fn(() => of(null)),
            savePageSetup: vi.fn(() => of({})),
          },
        },
        { provide: ToastService, useValue: { error: vi.fn(), success: vi.fn(), show: vi.fn() } },
        {
          provide: TopicsService,
          useValue: {
            list: vi.fn(() => of([
              { id: 'topic-optimism', name: 'Optimism', usageCount: 2 },
              { id: 'topic-theodicy', name: 'Theodicy', usageCount: 1 },
            ])),
            get: vi.fn(),
          },
        },
        { provide: BooksService, useValue: { list: vi.fn(() => of({ items: [] })) } },
        { provide: NotesService, useValue: { list: vi.fn(() => of([])), getMany } },
        { provide: Router, useValue: router },
      ],
    }).compileComponents();
    localStorage.clear();

    fixture = TestBed.createComponent(WritingStudio);
    component = fixture.componentInstance;
    fixture.detectChanges();
    component.activeItem.set(doc);
    component.referenceMode.set('writing');
    component.keptSourcesState.set('loaded');
    component.keptSources.set([kept(cited, 'All is for the best'), kept(alsoCited, 'The earthquake'), kept(keptOnly, 'The garden')]);
  });

  afterEach(() => vi.useRealTimers());

  /** The editor reported new Markdown; the lookup settles after its debounce. */
  const type = (markdown: string) => {
    component.editorText.set(markdown);
    fixture.detectChanges();
    vi.advanceTimersByTime(350);
    fixture.detectChanges();
  };

  const panel = (): HTMLElement => fixture.nativeElement.querySelector('.kept-sources-content') as HTMLElement;
  const touches = (): HTMLElement | null => fixture.nativeElement.querySelector('[data-testid="writing-touches"]');

  it('shows the cited sources’ topics as topic chips and one Touches line, live from the document', () => {
    type(`${cite(cited)} and ${cite(alsoCited)}`);

    const rows = [...panel().querySelectorAll('.reference-source-row')] as HTMLElement[];
    const pills = (row: HTMLElement) => [...row.querySelectorAll('.reference-source-use app-topic-chip')].map((p) => p.textContent?.trim());
    expect(pills(rows[0])).toEqual(['Theodicy', 'Optimism']);
    expect(pills(rows[1])).toEqual(['Optimism']);
    expect(touches()?.querySelector('.writing-touches-line')?.textContent?.trim()).toBe('Touches Optimism · Theodicy');
    expect(touches()?.querySelector('.writing-touches-note')?.textContent?.trim()).toBe('From the sources above.');

    // Deleting a citation in the editor updates the panel before any save.
    type(cite(alsoCited));
    expect(touches()?.querySelector('.writing-touches-line')?.textContent?.trim()).toBe('Touches Optimism');
  });

  it('marks a kept source the writing does not cite as kept, not cited, and never counts it', () => {
    type(cite(alsoCited));

    const rows = [...panel().querySelectorAll('.reference-source-row')] as HTMLElement[];
    expect(rows[2].querySelector('.reference-source-use.is-uncited')?.textContent?.trim()).toBe('Kept, not cited');
    expect(rows[2].querySelector('app-topic-chip')).toBeNull();
    expect(touches()?.textContent).not.toContain('Gardening');
  });

  it('shows no Touches line when nothing is cited or the cited notes have no topics', () => {
    type('Prose that cites nothing.');
    expect(touches()).toBeNull();

    type(cite(plain));
    expect(touches()).toBeNull();
    expect(panel().querySelectorAll('.reference-source-row.is-cited-only')).toHaveLength(1);
  });

  it('never counts a book-only citation or a bibliography entry', () => {
    type(`[(Voltaire)](nostos:cite/${book}?p=3)\n\n[^1]: ${cite(cited, 'as=bib')}`);

    expect(touches()).toBeNull();
  });

  it('lists a pasted citation’s source and says when a cited source was removed', () => {
    type(`${cite(pasted)} ${cite(removed)}`);

    expect(panel().querySelectorAll('.reference-source-row.is-cited-only')).toHaveLength(1);
    expect(panel().querySelector('[data-testid="studio-removed-sources"]')?.textContent).toContain('A cited source was removed.');
    expect(touches()?.querySelector('.writing-touches-line')?.textContent?.trim()).toBe('Touches Providence');
  });

  it('looks sources up once the ids settle, not on every keystroke', () => {
    getMany.mockClear();
    component.editorText.set(`${cite(cited)} a`);
    component.editorText.set(`${cite(cited)} ab`);
    component.editorText.set(`${cite(cited)} abc`);
    fixture.detectChanges();
    vi.advanceTimersByTime(350);

    expect(getMany).toHaveBeenCalledTimes(1);
    type(`${cite(cited)} abcd`);
    expect(getMany).toHaveBeenCalledTimes(1);
  });

  it('opens a cited source’s topic page in the Brain from its topic chip', async () => {
    type(cite(cited));
    // As saved: nothing to flush before leaving.
    component.activeItem.set({ ...doc, content: component.editorText() });
    component.editorTitle.set(doc.name);
    fixture.detectChanges();
    router.navigate.mockClear();

    const pill = panel().querySelector('.reference-source-use .reference-source-topic button.topic-chip-name') as HTMLButtonElement;
    expect(pill.getAttribute('type')).toBe('button');
    expect(pill.textContent?.trim()).toBe('Theodicy');

    pill.click();
    for (let i = 0; i < 20 && !router.navigate.mock.calls.length; i++) await vi.advanceTimersByTimeAsync(50);
    expect(router.navigate).toHaveBeenCalledWith(['/brain/topics', 'topic-theodicy']);
  });

  it('offers no control that adds a topic to the writing', () => {
    type(cite(cited));

    const text = panel().textContent ?? '';
    expect(text).not.toMatch(/add (a )?topic/i);
    expect(touches()?.querySelectorAll('button, input, a')).toHaveLength(0);
  });
});
