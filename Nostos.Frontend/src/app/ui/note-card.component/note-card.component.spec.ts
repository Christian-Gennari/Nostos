import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { NoteCardComponent } from './note-card.component';
import type { Note } from '../../core/dtos/note.dtos';

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
      'note-edit-pen-amber',
      'note-edit-pen-sage',
      'note-edit-pen-blue',
      'note-edit-pen-rose',
    ]);
    expect(pens().map((pen) => pen.getAttribute('aria-pressed'))).toEqual([
      'false', 'true', 'false', 'false',
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
      return fixture.nativeElement.querySelector('.note-pen') as HTMLElement | null;
    }

    it('marks a highlight with one small dot in its pen', async () => {
      const dot = await show(highlight);

      expect(dot?.getAttribute('data-testid')).toBe('note-pen-sage');
      expect(dot?.style.background).toBe('var(--highlight-sage)');
      expect(dot?.getAttribute('aria-label')).toBe('Sage highlight');
    });

    it('shows amber for a highlight known to have no recorded pen', async () => {
      const dot = await show({ ...highlight, highlightColour: null });

      expect(dot?.getAttribute('data-testid')).toBe('note-pen-amber');
    });

    it('shows nothing when the source does not say which pen (search hits)', async () => {
      const { highlightColour: _unknown, ...hit } = highlight;

      expect(await show(hit)).toBeNull();
    });

    it('shows nothing for a note that is not a highlight', async () => {
      expect(await show({ ...highlight, selectedText: undefined, content: 'A thought.' })).toBeNull();
    });
  });

  it('shows no pens for a note that is not a highlight', async () => {
    await edit({ ...highlight, selectedText: undefined, content: 'Just a thought.' });

    expect(pens()).toEqual([]);
  });
});
