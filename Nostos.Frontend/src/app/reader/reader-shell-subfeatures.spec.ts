import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Component, input, output, signal, type Type } from '@angular/core';
import { BreakpointObserver } from '@angular/cdk/layout';
import { ReaderSelectionSurface } from './reader-selection-surface.component';
import { ReaderHighlightCard } from './reader-highlight-card.component';
import { ReaderPassageSheet } from './reader-passage-sheet.component';
import { ReaderSearchPanel } from './reader-search-panel.component';
import { ReaderTocPanel } from './reader-toc-panel.component';
import { ReaderNotesPanel } from './reader-notes-panel.component';
import { ReaderViewSettings } from './reader-view-settings.component';
import { BrainStateService } from '../brain/brain-state.service';
import { BrainNoteActionsService } from '../brain/brain-note-actions.service';
import { BrainTopicSuggestionsService } from '../brain/brain-topic-suggestions.service';
import { NoteItemComponent } from '../ui/note-item/note-item.component';
import type { Note } from '../core/dtos/note.dtos';
import type { TocItem } from './reader.interface';

/**
 * The reader's sub-features, each its own component since #1607. These specs
 * guard the contract the shell relies on: what each one renders for its inputs,
 * and that every decision leaves as an output instead of reaching into the
 * shell.
 */

function brainStub() {
  return {
    topics: signal([]),
    lookup: signal(new Map()),
    ensureLoaded: vi.fn(),
    ensureTopicsLoaded: vi.fn(),
    refresh: vi.fn(),
  };
}

const noteActionsStub = {
  busy: signal(new Set<string>()),
  addTopic: vi.fn(),
  removeTopic: vi.fn(),
  keepWithWriting: vi.fn(),
  restored: vi.fn(),
  askDelete: vi.fn(),
  edit: vi.fn(),
};

const suggestionsStub = {
  request: vi.fn(),
  closeFor: vi.fn(),
  namesFor: () => [],
  stateFor: () => 'off' as const,
};

/** A note-item stand-in: the panel's own contract, not the card's. */
@Component({
  selector: 'app-note-item',
  standalone: true,
  template: '',
  host: { '[attr.data-note-id]': 'note()?.id' },
})
class NoteItemStub {
  readonly note = input<{ id: string } | null>(null);
  readonly topics = input<unknown>(null);
  readonly topicMap = input<unknown>(null);
  readonly showBook = input(true);
  readonly showFormat = input(false);
  readonly readOnly = input(false);
  readonly showMoreFromBook = input(true);
  readonly canSelect = input(true);
  readonly inBook = input(false);
  readonly audioTime = input<number | null>(null);
  readonly passageViewerOptions = input<unknown>(null);
  readonly busy = input(false);
  readonly suggestions = input<unknown>(null);
  readonly suggestionState = input<unknown>(null);
  readonly topicAdded = output<string>();
  readonly topicRemoved = output<string>();
  readonly pickerOpened = output<void>();
  readonly pickerClosed = output<void>();
  readonly edited = output<unknown>();
  readonly keepWithWriting = output<void>();
  readonly deleteRequested = output<void>();
  readonly restored = output<unknown>();
  readonly jumpRequested = output<void>();
}

function note(overrides: Partial<Note> = {}): Note {
  return {
    id: 'n-1',
    bookId: 'book-1',
    content: 'About [[wrath]]',
    selectedText: 'Sing, goddess',
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  } as Note;
}

function configure<T>(component: Type<T>, providers: unknown[] = []) {
  TestBed.configureTestingModule({
    imports: [component],
    providers: [
      { provide: BrainStateService, useFactory: brainStub },
      { provide: BrainNoteActionsService, useValue: noteActionsStub },
      { provide: BrainTopicSuggestionsService, useValue: suggestionsStub },
      {
        provide: BreakpointObserver,
        useValue: { isMatched: () => false, observe: () => ({ subscribe: () => ({ unsubscribe() {} }) }) },
      },
      ...providers,
    ],
  });
  return TestBed.createComponent(component);
}

describe('ReaderSelectionSurface (#1607)', () => {
  let fixture: ComponentFixture<ReaderSelectionSurface>;
  let component: ReaderSelectionSurface;

  beforeEach(() => {
    fixture = configure(ReaderSelectionSurface);
    component = fixture.componentInstance;
  });

  afterEach(() => TestBed.resetTestingModule());

  it('docks the bar at the top with the quote and the pens, and reports the decisions', () => {
    fixture.componentRef.setInput('placement', null);
    fixture.componentRef.setInput('preview', 'Sing, goddess, the anger of Peleus son Achilles.');
    fixture.componentRef.setInput('canMarkPassage', true);
    fixture.detectChanges();

    const bar = fixture.nativeElement.querySelector('[data-testid="selection-bar"]') as HTMLElement;
    expect(bar).not.toBeNull();
    expect(bar.querySelector('.selected-text')?.textContent).toContain('Sing, goddess');
    expect(bar.querySelector('app-pen-swatches')).not.toBeNull();
    expect(bar.querySelector('[data-testid="selection-highlight"]')).not.toBeNull();
    expect(bar.querySelector('[data-testid="selection-add-note"]')).not.toBeNull();

    const cancelled = vi.fn();
    const highlighted = vi.fn();
    component.cancelled.subscribe(cancelled);
    component.highlight.subscribe(highlighted);

    (bar.querySelector('[data-testid="selection-cancel"]') as HTMLButtonElement).click();
    (bar.querySelector('[data-testid="selection-highlight"]') as HTMLButtonElement).click();

    expect(cancelled).toHaveBeenCalledTimes(1);
    expect(highlighted).toHaveBeenCalledTimes(1);
  });

  it('anchors the menu at the text with a dismiss scrim', () => {
    fixture.componentRef.setInput('placement', { left: 24, width: 320, top: 90, bottom: null });
    fixture.componentRef.setInput('preview', 'A passage');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="selection-bar"]')).toBeNull();
    const menu = fixture.nativeElement.querySelector('[data-testid="selection-menu"]') as HTMLElement;
    expect(menu).not.toBeNull();
    expect(menu.style.left).toBe('24px');
    expect(fixture.nativeElement.querySelector('[data-testid="selection-scrim"]')).not.toBeNull();
  });

  it('shows Save note only once the draft can be saved', () => {
    fixture.componentRef.setInput('noteDraftOpen', true);
    fixture.componentRef.setInput('noteDraft', 'A thought');
    fixture.componentRef.setInput('noteDraftReady', false);
    fixture.detectChanges();

    const save = () =>
      fixture.nativeElement.querySelector('[data-testid="selection-save-note"]') as HTMLButtonElement;
    expect(save().disabled).toBe(true);

    fixture.componentRef.setInput('noteDraftReady', true);
    fixture.detectChanges();
    expect(save().disabled).toBe(false);
  });
});

describe('ReaderHighlightCard (#1607)', () => {
  let fixture: ComponentFixture<ReaderHighlightCard>;
  let component: ReaderHighlightCard;

  beforeEach(() => {
    fixture = configure(ReaderHighlightCard);
    component = fixture.componentInstance;
  });

  afterEach(() => TestBed.resetTestingModule());

  it('docks a saved highlight with its note and reports its actions', () => {
    fixture.componentRef.setInput('note', note());
    fixture.componentRef.setInput('hasNote', true);
    fixture.componentRef.setInput('words', 'About wrath');
    fixture.componentRef.setInput('topics', []);
    fixture.componentRef.setInput('preview', 'Sing, goddess');
    fixture.componentRef.setInput('placement', null);
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector('[data-testid="highlight-card"]') as HTMLElement;
    expect(card).not.toBeNull();
    expect(card.classList).toContain('highlight-card-docked');
    expect(card.querySelector('[data-testid="highlight-card-note"]')?.textContent).toContain('About wrath');

    const deleted = vi.fn();
    const edited = vi.fn();
    component.deleteHighlight.subscribe(deleted);
    component.editNote.subscribe(edited);

    (card.querySelector('[data-testid="highlight-card-delete"]') as HTMLButtonElement).click();
    (card.querySelector('[data-testid="highlight-card-edit"]') as HTMLButtonElement).click();

    expect(deleted).toHaveBeenCalledTimes(1);
    expect(edited).toHaveBeenCalledTimes(1);
  });

  it('anchors the card at the mark when there is room beside it', () => {
    fixture.componentRef.setInput('note', note());
    fixture.componentRef.setInput('preview', 'Sing, goddess');
    fixture.componentRef.setInput('placement', { left: 10, width: 280, top: 40, bottom: null });
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector('[data-testid="highlight-card"]') as HTMLElement;
    expect(card.classList).toContain('selection-menu');
    expect(card.style.top).toBe('40px');

    const closed = vi.fn();
    component.closed.subscribe(closed);
    (card.querySelector('[data-testid="highlight-card-close"]') as HTMLButtonElement).click();
    expect(closed).toHaveBeenCalledTimes(1);
  });

  it('says Remove note when saving would empty an existing note', () => {
    fixture.componentRef.setInput('note', note());
    fixture.componentRef.setInput('hasNote', true);
    fixture.componentRef.setInput('editing', true);
    fixture.componentRef.setInput('noteDraft', '   ');
    fixture.componentRef.setInput('noteChanged', true);
    fixture.componentRef.setInput('noteEmpty', true);
    fixture.detectChanges();

    const save = fixture.nativeElement.querySelector('[data-testid="highlight-card-save"]') as HTMLButtonElement;
    expect(save.textContent).toContain('Remove note');
    expect(save.disabled).toBe(false);
  });
});

describe('ReaderPassageSheet (#1607)', () => {
  let fixture: ComponentFixture<ReaderPassageSheet>;
  let component: ReaderPassageSheet;

  beforeEach(() => {
    fixture = configure(ReaderPassageSheet);
    component = fixture.componentInstance;
  });

  afterEach(() => TestBed.resetTestingModule());

  it('names a passage with its excerpt and size, and reports the save', () => {
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('draft', {
      anchorKind: 'epub_cfi',
      start: 'epubcfi(/6/2)',
      end: 'epubcfi(/6/4)',
      title: 'Book I',
      html: null,
      text: 'Sing, goddess',
      words: 3,
      opening: 'Sing, goddess',
      closing: 'the anger',
    });
    fixture.componentRef.setInput('title', 'Book I');
    fixture.detectChanges();

    const size = fixture.nativeElement.querySelector('[data-testid="passage-sheet-size"]') as HTMLElement;
    expect(size.textContent).toContain('3 words');
    expect(fixture.nativeElement.querySelector('[data-testid="passage-sheet-excerpt"]')?.textContent)
      .toContain('Sing, goddess');

    const saved = vi.fn();
    component.save.subscribe(saved);
    (fixture.nativeElement.querySelector('[data-testid="passage-sheet-save"]') as HTMLButtonElement).click();
    expect(saved).toHaveBeenCalledTimes(1);
  });

  it('moves a clip end by 15 seconds without passing the other end', () => {
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('draft', {
      anchorKind: 'audio_timestamp',
      start: '30',
      end: '45',
      title: 'Clip',
      html: null,
      text: null,
      words: 0,
      opening: '0:30',
      closing: '0:45',
    });
    fixture.componentRef.setInput('title', 'Clip');
    fixture.detectChanges();

    const changed: Array<{ start: string; end: string }> = [];
    component.draftChange.subscribe((draft) => changed.push({ start: draft.start, end: draft.end }));

    (fixture.nativeElement.querySelector('[data-testid="passage-sheet-end-later"]') as HTMLButtonElement).click();
    expect(changed.at(-1)).toEqual({ start: '30', end: '60' });

    fixture.componentRef.setInput('draft', { ...fixture.componentInstance.draft()!, start: '40', end: '50' });
    fixture.detectChanges();
    const nudges = fixture.nativeElement.querySelectorAll('[data-testid="passage-sheet-nudges"] button');
    (nudges[0] as HTMLButtonElement).click();
    expect(changed.at(-1)).toEqual({ start: '25', end: '50' });

    // Start never overtakes the end, even when the step would pass it.
    fixture.componentRef.setInput('draft', { ...fixture.componentInstance.draft()!, start: '49', end: '50' });
    fixture.detectChanges();
    const later = fixture.nativeElement.querySelectorAll('[data-testid="passage-sheet-nudges"] button');
    (later[1] as HTMLButtonElement).click();
    expect(changed.at(-1)).toEqual({ start: '49', end: '50' });
  });
});

describe('ReaderSearchPanel (#1607)', () => {
  let fixture: ComponentFixture<ReaderSearchPanel>;
  let component: ReaderSearchPanel;

  beforeEach(() => {
    fixture = configure(ReaderSearchPanel);
    component = fixture.componentInstance;
  });

  afterEach(() => TestBed.resetTestingModule());

  it('reports the query and the two steps through the matches', () => {
    fixture.componentRef.setInput('query', 'wrath');
    fixture.componentRef.setInput('state', { status: 'ready', current: 2, total: 5 });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.reader-search-status').textContent).toContain('2 of 5');

    const queries: string[] = [];
    const next = vi.fn();
    const previous = vi.fn();
    component.queryChange.subscribe((value) => queries.push(value));
    component.next.subscribe(next);
    component.previous.subscribe(previous);

    const input = fixture.nativeElement.querySelector('.reader-search-input') as HTMLInputElement;
    input.value = 'rage';
    input.dispatchEvent(new Event('input'));

    (fixture.nativeElement.querySelector('[aria-label="Next match"]') as HTMLButtonElement).click();
    (fixture.nativeElement.querySelector('[aria-label="Previous match"]') as HTMLButtonElement).click();

    expect(queries).toEqual(['rage']);
    expect(next).toHaveBeenCalledTimes(1);
    expect(previous).toHaveBeenCalledTimes(1);
  });

  it('disables the steps when the book has no matches', () => {
    fixture.componentRef.setInput('state', { status: 'not-found', current: 0, total: 0 });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.reader-search-status').textContent).toContain('No matches');
    expect((fixture.nativeElement.querySelector('[aria-label="Next match"]') as HTMLButtonElement).disabled)
      .toBe(true);
  });
});

describe('ReaderTocPanel (#1607)', () => {
  let fixture: ComponentFixture<ReaderTocPanel>;
  let component: ReaderTocPanel;
  const items: TocItem[] = [
    { label: 'Book I', target: 'epubcfi(/6/2)', children: [{ label: 'Chapter 1', target: 'epubcfi(/6/4)' }] },
    { label: 'Book II', target: 'epubcfi(/6/10)' },
  ];

  beforeEach(() => {
    fixture = configure(ReaderTocPanel);
    component = fixture.componentInstance;
  });

  afterEach(() => TestBed.resetTestingModule());

  it('renders the outline, marks the reader’s place and reports the chosen row', () => {
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('fileType', 'epub');
    fixture.componentRef.setInput('items', items);
    fixture.componentRef.setInput('activeTarget', 'epubcfi(/6/4)');
    fixture.detectChanges();

    const panel = fixture.nativeElement.querySelector('.toc-panel') as HTMLElement;
    expect(panel.classList).toContain('open');
    expect(panel.querySelectorAll('.toc-item').length).toBe(3);
    expect((panel.querySelector('.toc-item.active') as HTMLElement).textContent).toContain('Chapter 1');

    const chosen: TocItem[] = [];
    component.itemSelected.subscribe((item) => chosen.push(item));
    (panel.querySelectorAll('.toc-item')[2] as HTMLButtonElement).click();
    expect(chosen[0].label).toBe('Book II');
  });

  it('stays shut for a PDF, which draws its own sidebar', () => {
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('fileType', 'pdf');
    fixture.componentRef.setInput('items', items);
    fixture.detectChanges();

    expect((fixture.nativeElement.querySelector('.toc-panel') as HTMLElement).classList).not.toContain('open');
  });
});

describe('ReaderNotesPanel (#1607)', () => {
  let fixture: ComponentFixture<ReaderNotesPanel>;
  let component: ReaderNotesPanel;

  beforeEach(async () => {
    TestBed.overrideComponent(ReaderNotesPanel, {
      remove: { imports: [NoteItemComponent] },
      add: { imports: [NoteItemStub] },
    });
    fixture = configure(ReaderNotesPanel);
    component = fixture.componentInstance;
  });

  afterEach(() => TestBed.resetTestingModule());

  it('lists this edition’s notes, the pens and the quick note', () => {
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('fileType', 'epub');
    fixture.componentRef.setInput('rows', [{ note: note(), item: note() as never }]);
    fixture.componentRef.setInput('flashedNoteId', 'n-1');
    fixture.componentRef.setInput('quickNoteContent', 'A thought');
    fixture.componentRef.setInput('quickNoteReady', true);
    fixture.detectChanges();

    const panel = fixture.nativeElement.querySelector('.notes-panel') as HTMLElement;
    expect(panel.classList).toContain('open');
    expect(panel.querySelectorAll('app-note-item').length).toBe(1);
    expect(panel.querySelector('app-pen-swatches')).not.toBeNull();

    const closed = vi.fn();
    const saved = vi.fn();
    component.closed.subscribe(closed);
    component.quickNoteSave.subscribe(saved);
    (panel.querySelector('[aria-label="Close notes"]') as HTMLButtonElement).click();
    (panel.querySelector('.quick-actions button') as HTMLButtonElement).click();
    expect(closed).toHaveBeenCalledTimes(1);
    expect(saved).toHaveBeenCalledTimes(1);
  });

  it('keeps audio timestamps and hides the pens for an audiobook', () => {
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('fileType', 'audio');
    fixture.componentRef.setInput('rows', [
      {
        note: note({ anchorVerified: true, sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '90' }),
        item: note() as never,
      },
    ]);
    fixture.detectChanges();

    const panel = fixture.nativeElement.querySelector('.notes-panel') as HTMLElement;
    expect(panel.querySelector('app-pen-swatches')).toBeNull();
    expect(
      component.noteAudioTime(
        note({ anchorVerified: true, sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '90' }),
      ),
    ).toBe(90);
  });

  it('offers Try again instead of an empty list when the notes could not be read', () => {
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('fileType', 'epub');
    fixture.componentRef.setInput('rows', []);
    fixture.componentRef.setInput('notesFailed', true);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Your notes could not be loaded');
  });
});

describe('ReaderViewSettings (#1607)', () => {
  let fixture: ComponentFixture<ReaderViewSettings>;

  beforeEach(() => {
    fixture = configure(ReaderViewSettings);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
  });

  function typographyStub() {
    return {
      fontOptions: [{ value: 'libron', label: 'Libron' }],
      marginOptions: [{ value: 'normal', label: 'Normal' }],
      typography: signal({ fontFamily: 'libron', lineHeight: 1.6, margin: 'normal' }),
      fontSizePercent: signal(100),
      zoomIn: vi.fn(),
      zoomOut: vi.fn(),
      setTypography: vi.fn(),
      resetTypography: vi.fn(),
    };
  }

  it('keeps the app theme first and drives the EPUB text rows', () => {
    const epub = typographyStub();
    fixture.componentRef.setInput('fileType', 'epub');
    fixture.componentRef.setInput('epub', epub as never);
    fixture.detectChanges();

    const panel = document.querySelector('[data-testid="typo-panel"]') as HTMLElement;
    expect(panel).not.toBeNull();
    expect(panel.querySelector('[data-testid="reader-appearance"]')).not.toBeNull();
    expect(panel.querySelectorAll('nostos-segmented').length).toBe(3);

    (panel.querySelector('[aria-label="Larger text"]') as HTMLButtonElement).click();
    (panel.querySelector('[aria-label="Reset text settings"]') as HTMLButtonElement).click();
    expect(epub.zoomIn).toHaveBeenCalledTimes(1);
    expect(epub.resetTypography).toHaveBeenCalledTimes(1);
    expect(panel.textContent).toContain('100%');
  });

  it('shows the fixed-layout page rows for a PDF', () => {
    const pdf = {
      isDark: () => false,
      pageInverted: signal(false),
      setPageInverted: vi.fn(),
      zoomPresets: [{ value: 'fit-page', label: 'Fit page' }],
      zoomLabel: () => '120%',
      isZoomPreset: () => true,
      setZoom: vi.fn(),
      scrollMode: () => 'vertical',
      readingModes: [{ value: 'vertical', label: 'Continuous' }],
      isScrollMode: () => true,
      setScrollMode: vi.fn(),
      zoomIn: vi.fn(),
      zoomOut: vi.fn(),
      rotateClockwise: vi.fn(),
      rotateCounterclockwise: vi.fn(),
      resetRotation: vi.fn(),
    };
    fixture.componentRef.setInput('fileType', 'pdf');
    fixture.componentRef.setInput('pdf', pdf as never);
    fixture.detectChanges();

    const panel = document.querySelector('[data-testid="typo-panel"]') as HTMLElement;
    expect(panel.textContent).toContain('Page');
    expect(panel.textContent).toContain('120%');
    (panel.querySelector('[aria-label="Zoom in"]') as HTMLButtonElement).click();
    expect(pdf.zoomIn).toHaveBeenCalledTimes(1);
  });
});
