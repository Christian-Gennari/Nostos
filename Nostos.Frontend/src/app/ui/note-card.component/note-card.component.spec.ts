import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { NoteCardComponent } from './note-card.component';
import type { Note } from '../../core/dtos/note.dtos';
import { PassageViewerService } from '../passage-viewer/passage-viewer.service';

describe('NoteCard source navigation', () => {
  let fixture: ComponentFixture<NoteCardComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NoteCardComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(NoteCardComponent);
    fixture.componentInstance.note = {
      id: 'note-1',
      bookId: 'book-1',
      content: 'A saved thought.',
      createdAt: '2026-10-01T00:00:00Z',
      bookTitle: 'A Source Book',
      cfiRange: 'epubcfi(/6/2)',
    } satisfies Note;
    fixture.componentInstance.showSource = true;
  });

  it('keeps the default Library/CFI destination for existing consumers', () => {
    fixture.detectChanges();
    const link = fixture.nativeElement.querySelector('.source-badge') as HTMLAnchorElement;
    const destination = new URL(link.href);

    expect(destination.pathname).toBe('/library/book-1');
    expect(destination.searchParams.get('cfi')).toBe('epubcfi(/6/2)');
    expect(link.textContent).toContain('Return to passage');
  });

  it('uses a context-resolved verified PDF destination when supplied', () => {
    fixture.componentInstance.sourceNavigation = {
      commands: ['/read', 'book-1'],
      queryParams: { sourcePage: 53 },
      label: 'Return to passage',
    };
    fixture.detectChanges();

    const link = fixture.nativeElement.querySelector('.source-badge') as HTMLAnchorElement;
    const destination = new URL(link.href);

    expect(destination.pathname).toBe('/read/book-1');
    expect(destination.searchParams.get('sourcePage')).toBe('53');
    expect(link.getAttribute('aria-label')).toBe('Return to passage in A Source Book');
  });
});

describe('NoteCard highlight pen (issue #861)', () => {
  let fixture: ComponentFixture<NoteCardComponent>;

  const pens = () =>
    Array.from(fixture.nativeElement.querySelectorAll('.edit-pen')) as HTMLButtonElement[];

  async function edit(note: Note) {
    await TestBed.configureTestingModule({
      imports: [NoteCardComponent],
      providers: [provideRouter([])],
    }).compileComponents();
    fixture = TestBed.createComponent(NoteCardComponent);
    fixture.componentInstance.note = note;
    // Entered before the first render: flipping `isEditing` between two
    // synchronous checks trips Angular's dev-mode changed-after-checked guard.
    fixture.componentInstance.startEdit();
    fixture.detectChanges();
  }

  const highlight: Note = {
    id: 'note-1',
    bookId: 'book-1',
    content: '',
    selectedText: 'A highlighted passage.',
    createdAt: '2026-10-01T00:00:00Z',
    cfiRange: 'epubcfi(/6/2)',
    highlightColour: 'sage',
  };

  it('offers the four pens in edit, with the stored one selected', async () => {
    await edit(highlight);

    expect(pens().map((pen) => pen.getAttribute('data-testid'))).toEqual([
      'note-edit-pen-sage',
      'note-edit-pen-amber',
      'note-edit-pen-blue',
      'note-edit-pen-rose',
    ]);
    expect(pens().map((pen) => pen.getAttribute('aria-pressed'))).toEqual([
      'true', 'false', 'false', 'false',
    ]);
    // A fill is a token reference, never a hex.
    expect(pens()[3].style.background).toBe('var(--highlight-rose)');
  });

  it('emits the new pen when only the colour changed', async () => {
    await edit(highlight);
    const emitted = vi.fn();
    fixture.componentInstance.update.subscribe(emitted);

    pens()[3].click();
    fixture.componentInstance.saveEdit();

    expect(emitted).toHaveBeenCalledWith({
      id: 'note-1',
      content: '',
      selectedText: 'A highlighted passage.',
      highlightColour: 'rose',
    });
  });

  it('sends no pen when the colour was left alone', async () => {
    await edit(highlight);
    const emitted = vi.fn();
    fixture.componentInstance.update.subscribe(emitted);

    fixture.componentInstance.editContent = 'A thought about it.';
    fixture.componentInstance.saveEdit();

    expect(emitted).toHaveBeenCalledTimes(1);
    expect(emitted.mock.calls[0][0]).not.toHaveProperty('highlightColour');
  });

  it('starts an imported highlight, which has no recorded pen, on amber', async () => {
    await edit({ ...highlight, cfiRange: undefined, highlightColour: null, captureSource: 'import' });

    expect(pens().map((pen) => pen.getAttribute('aria-pressed'))).toEqual([
      'true', 'false', 'false', 'false',
    ]);
  });

  describe('the pen on the card', () => {
    async function show(note: Note) {
      await TestBed.configureTestingModule({
        imports: [NoteCardComponent],
        providers: [provideRouter([])],
      }).compileComponents();
      fixture = TestBed.createComponent(NoteCardComponent);
      fixture.componentInstance.note = note;
      fixture.detectChanges();
      return fixture.nativeElement.querySelector('.note-quote') as HTMLElement | null;
    }

    it('draws the quote rule in the highlight\'s pen', async () => {
      const quote = await show(highlight);

      expect(quote?.getAttribute('data-testid')).toBe('note-pen-sage');
      expect(quote?.style.borderLeftColor).toBe('var(--highlight-sage)');
      expect(quote?.querySelector('.visually-hidden')?.textContent).toBe('Sage highlight');
    });

    it('uses sage for a highlight known to have no recorded pen', async () => {
      const quote = await show({ ...highlight, highlightColour: null });

      expect(quote?.getAttribute('data-testid')).toBe('note-pen-sage');
    });

    it('keeps the accent rule when the source does not say which pen (search hits)', async () => {
      const { highlightColour: _unknown, ...hit } = highlight;
      const quote = await show(hit);

      expect(quote?.style.borderLeftColor).toBe('');
      expect(quote?.hasAttribute('data-testid')).toBe(false);
      expect(quote?.querySelector('.visually-hidden')).toBeNull();
    });

    it('shows no dot in the footer', async () => {
      await show(highlight);

      expect(fixture.nativeElement.querySelector('.note-pen')).toBeNull();
    });
  });

  it('shows the quotation in quotation marks and names the book', async () => {
    await TestBed.configureTestingModule({
      imports: [NoteCardComponent],
      providers: [provideRouter([])],
    }).compileComponents();
    fixture = TestBed.createComponent(NoteCardComponent);
    fixture.componentInstance.note = { ...highlight, bookTitle: 'Meditations' };
    fixture.componentInstance.showSource = true;
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.quote-text')?.textContent).toBe(`\u201c${highlight.selectedText}\u201d`);
    expect(el.querySelector('.source-book-title')?.textContent).toBe('Meditations');
  });

  it('shows no pens for a note that is not a highlight', async () => {
    await edit({ ...highlight, selectedText: undefined, content: 'Just a thought.' });

    expect(pens()).toEqual([]);
  });
});

describe('NoteCard audio time (issue #906)', () => {
  let fixture: ComponentFixture<NoteCardComponent>;

  const time = () =>
    fixture.nativeElement.querySelector('[data-testid="note-audio-time"]') as HTMLButtonElement | null;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NoteCardComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(NoteCardComponent);
    fixture.componentInstance.note = {
      id: 'note-1',
      bookId: 'book-1',
      content: 'Worth hearing again.',
      createdAt: '2026-10-01T00:00:00Z',
      cfiRange: '754.5',
    } satisfies Note;
    fixture.componentInstance.showNavigation = true;
  });

  it('shows the time in the metadata row and jumps when it is activated', () => {
    fixture.componentInstance.audioTime = 754.5;
    const jumps = vi.fn();
    fixture.componentInstance.cardClick.subscribe(jumps);
    fixture.detectChanges();

    const button = time()!;
    expect(button.closest('.note-metadata')).not.toBeNull();
    expect(button.textContent?.trim()).toBe('12:34');
    expect(button.getAttribute('aria-label')).toBe('Jump to 12:34');

    button.click();
    expect(jumps).toHaveBeenCalledTimes(1);
  });

  it('uses h:mm:ss past the first hour', () => {
    fixture.componentInstance.audioTime = 3725;
    fixture.detectChanges();

    expect(time()!.textContent?.trim()).toBe('1:02:05');
  });

  it('shows nothing extra when the reader supplies no time', () => {
    fixture.detectChanges();

    expect(time()).toBeNull();
    expect(fixture.nativeElement.querySelector('.note-metadata .meta-separator')).toBeNull();
  });
});

describe('NoteCard passage (issue #1014)', () => {
  let fixture: ComponentFixture<NoteCardComponent>;
  let viewer: PassageViewerService;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NoteCardComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    viewer = TestBed.inject(PassageViewerService);
    fixture = TestBed.createComponent(NoteCardComponent);
    fixture.componentInstance.note = {
      id: 'passage-1',
      bookId: 'book-1',
      content: 'Reread every year.',
      selectedText: '“Even this must have a preface—that is, a literary preface,” laughed Ivan…',
      createdAt: '2026-10-16T00:00:00Z',
      kind: 'passage',
      passageTitle: 'The Grand Inquisitor',
      passageEnd: 'epubcfi(/6/80!/4/112/1:285)',
      passageWordCount: 9262,
      sourceAnchorKind: 'epub_cfi',
      sourceAnchorValue: 'epubcfi(/6/80!/4/2/1:0)',
      anchorVerified: true,
      highlightColour: 'blue',
    } satisfies Note;
  });

  it('leads with its title and size and never offers to unfold the text in place', () => {
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="passage-card-title"]')?.textContent).toBe('The Grand Inquisitor');
    expect(el.querySelector('.passage-size')?.textContent).toBe('9,262 words · 37 min');
    expect(el.querySelector('.quote-text')?.textContent).toContain('Even this must have a preface');
    expect(el.querySelector('.expand-btn')).toBeNull();
    expect(el.querySelector('.note-meta-placeholder')).toBeNull();
  });

  it('names its link into the book "Open in book", not a second passage', () => {
    fixture.componentInstance.showSource = true;
    fixture.componentInstance.note = { ...fixture.componentInstance.note, bookTitle: 'The Brothers Karamazov' };
    fixture.componentInstance.sourceNavigation = {
      commands: ['/read', 'book-1'],
      queryParams: { sourceCfi: 'epubcfi(/6/80!/4/2/1:0)' },
      label: 'Return to passage',
    };
    fixture.detectChanges();
    const link = fixture.nativeElement.querySelector('.source-badge') as HTMLAnchorElement;
    expect(link.textContent).toContain('Open in book');
    expect(link.getAttribute('aria-label')).toBe('Open in book: The Brothers Karamazov');
  });

  it('opens the passage viewer with the options its host gave it', () => {
    const openInBook = vi.fn();
    fixture.componentInstance.passageViewerOptions = { openInBook };
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('[data-testid="passage-card-read-button"]') as HTMLButtonElement).click();

    expect(viewer.request()).toEqual({ noteId: 'passage-1', openInBook });
  });

  it('edits the title, note and pen, and never sends the opening back as the text', () => {
    const emitted: unknown[] = [];
    fixture.componentInstance.update.subscribe((event) => emitted.push(event));
    fixture.componentInstance.startEdit();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.edit-quote-input')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="passage-edit-title"]')).not.toBeNull();

    fixture.componentInstance.editPassageTitle = 'The Inquisitor';
    fixture.componentInstance.editContent = 'Reread every year.';
    fixture.componentInstance.saveEdit();

    expect(emitted).toEqual([{ id: 'passage-1', content: 'Reread every year.', passageTitle: 'The Inquisitor' }]);
  });
});

describe('NoteCard inline topic links', () => {
  it('opens a [[topic]] in the Brain without a page load', async () => {
    await TestBed.configureTestingModule({
      imports: [NoteCardComponent],
      providers: [provideRouter([])],
    }).compileComponents();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(NoteCardComponent);
    fixture.componentInstance.note = {
      id: 'n1', bookId: 'b1', content: 'See [[Virtue]] here.', createdAt: '2026-08-01T08:00:00Z',
    } as Note;
    fixture.componentInstance.topicMap = new Map([['virtue', { id: 't1', name: 'Virtue', usageCount: 1 }]]);
    fixture.detectChanges();

    const link = (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('a.topic-tag')!;
    expect(link.getAttribute('href')).toBe('/brain/topics/t1');
    const click = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 });
    link.dispatchEvent(click);

    expect(navigate).toHaveBeenCalledWith('/brain/topics/t1');
    expect(click.defaultPrevented).toBe(true);
  });
});
