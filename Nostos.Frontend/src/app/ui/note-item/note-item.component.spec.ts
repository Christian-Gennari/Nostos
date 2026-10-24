import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';

import { NoteItemComponent, type NoteItemEdit } from './note-item.component';
import type { NoteSearchHit } from '../../core/dtos/note.dtos';
import type { TopicDto } from '../../core/services/topics.service';
import { PassageViewerService } from '../passage-viewer/passage-viewer.service';

const NOTE: NoteSearchHit = {
  id: 'note-1',
  bookId: 'book-1',
  bookTitle: 'Nicomachean Ethics',
  bookAuthor: 'Aristotle',
  content: 'A compact definition of virtue.\n\n[[virtue]] [[mean]]',
  selectedText: 'Virtue, then, is a state of character concerned with choice, lying in a mean.',
  snippet: null,
  topicNames: ['mean', 'virtue'],
  createdAt: '2026-07-28T14:00:00Z',
  sourceAnchorKind: 'pdf_page',
  sourceAnchorValue: '30',
  anchorVerified: true,
  highlightColour: 'sage',
};

@Component({ standalone: true, template: '' })
class BlankPage {}

const TOPICS: TopicDto[] = [
  { id: 't-virtue', name: 'virtue', usageCount: 4 },
  { id: 't-mean', name: 'mean', usageCount: 1 },
  { id: 't-choice', name: 'choice', usageCount: 1 },
];

describe('NoteItemComponent', () => {
  let fixture: ComponentFixture<NoteItemComponent>;
  let element: HTMLElement;

  function render(note: Partial<NoteSearchHit> = {}): void {
    fixture.componentRef.setInput('note', { ...NOTE, ...note });
    fixture.componentRef.setInput('topics', TOPICS);
    fixture.detectChanges();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NoteItemComponent],
      providers: [
        provideRouter([{ path: '**', component: BlankPage }]),
        // The editor's [[ autocomplete reads the topics.
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(NoteItemComponent);
    element = fixture.nativeElement as HTMLElement;
  });

  it('shows the quote, the words without the reference line, and the topics as chips', () => {
    render();

    expect(element.querySelector('.note-item-quote')?.textContent).toContain('Virtue, then, is a state');
    expect(element.querySelector('.note-item-thought')?.textContent?.trim()).toBe('A compact definition of virtue.');
    const chips = Array.from(element.querySelectorAll('.topic-chip-name')).map((chip) => chip.textContent?.trim());
    expect(chips).toEqual(['mean', 'virtue']);
  });

  it('says how many writings cite the note as a quiet button whose menu opens each (issue #1162)', () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    render();
    expect(element.querySelector('.note-item-cited')).toBeNull();

    fixture.componentRef.setInput('citedWritings', [{ id: 'w1', name: 'On Optimism' }]);
    fixture.detectChanges();
    expect(element.querySelector('.note-item-meta button.note-item-cited')?.textContent?.trim()).toBe('Cited in 1 writing');

    fixture.componentRef.setInput('citedWritings', [{ id: 'w1', name: 'On Optimism' }, { id: 'w2', name: 'Theodicy' }]);
    fixture.detectChanges();
    const button = element.querySelector('button.note-item-cited') as HTMLButtonElement;
    expect(button.getAttribute('type')).toBe('button');
    expect(button.textContent?.trim()).toBe('Cited in 2 writings');
    expect(button.getAttribute('aria-haspopup')).toBe('menu');

    button.click();
    fixture.detectChanges();
    const items = Array.from(document.querySelectorAll<HTMLButtonElement>('.nostos-menu-item'));
    expect(items.map((item) => item.textContent?.trim())).toEqual(['On Optimism', 'Theodicy']);
    items[1].click();
    expect(navigate).toHaveBeenCalledWith(['/studio'], { queryParams: { writingId: 'w2' } });
    document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
  });

  it('draws the quote rule in the note’s pen', () => {
    render({ highlightColour: 'rose' });

    const quote = element.querySelector('.note-item-quote') as HTMLElement;
    expect(quote.style.getPropertyValue('--nostos-accent-rail-color')).toBe('var(--highlight-rose)');
  });

  it('names the book, author, date and page, and links into the book at that page', () => {
    render();

    const meta = element.querySelector('.note-item-meta')?.textContent ?? '';
    expect(meta).toContain('Nicomachean Ethics');
    expect(meta).toContain('Aristotle');
    expect(meta).toContain('28 Jul 2026');
    expect(meta).toContain('p. 30');
    const link = new URL((element.querySelector('.note-item-source') as HTMLAnchorElement).href);
    expect(link.pathname).toBe('/read/book-1');
    expect(link.searchParams.get('sourcePage')).toBe('30');
  });

  it('leaves the book out where a heading already names it', () => {
    fixture.componentRef.setInput('showBook', false);
    render();

    expect(element.querySelector('.note-item-book')).toBeNull();
  });

  it('links a topic chip to its page and asks to remove a topic', () => {
    render();
    const removed: string[] = [];
    fixture.componentInstance.topicRemoved.subscribe((name) => removed.push(name));

    const chip = element.querySelector('.topic-chip-name') as HTMLAnchorElement;
    (element.querySelector('.topic-chip-remove') as HTMLButtonElement).click();

    expect(new URL(chip.href).pathname).toBe('/brain/topics/t-mean');
    expect(removed).toEqual(['mean']);
  });

  it('opens an inline [[link]] in the app, at the address the link names', async () => {
    render({ content: 'On [[virtue]] as a mean.' });
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    const link = element.querySelector('.note-item-thought a.topic-tag') as HTMLAnchorElement;

    link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));

    expect(navigate).toHaveBeenCalledWith(link.getAttribute('href'));
  });

  it('offers "Add a topic" on a note with none', () => {
    render({ topicNames: [], content: '' });

    expect(element.querySelector('.note-item-add')?.textContent?.trim()).toBe('Add a topic');
    expect(element.querySelector('.note-item-thought')).toBeNull();
  });

  it('adds a topic chosen in the picker and closes it', () => {
    render();
    const added: string[] = [];
    fixture.componentInstance.topicAdded.subscribe((name) => added.push(name));

    fixture.componentInstance.openPicker();
    fixture.componentInstance.chooseTopic('choice');

    expect(added).toEqual(['choice']);
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
  });

  it('does not open the picker while a topic change is in flight', () => {
    fixture.componentRef.setInput('busy', true);
    render();

    fixture.componentInstance.openPicker();

    expect(fixture.componentInstance.pickerOpen()).toBe(false);
  });

  it('emits only what an edit changed', () => {
    render();
    const edits: NoteItemEdit[] = [];
    fixture.componentInstance.edited.subscribe((edit) => edits.push(edit));

    fixture.componentInstance.startEdit();
    fixture.componentInstance.editContent.set('A sharper definition.\n\n[[virtue]] [[mean]]');
    fixture.componentInstance.editColour.set('blue');
    fixture.componentInstance.saveEdit();

    expect(edits).toEqual([{
      id: 'note-1',
      content: 'A sharper definition.\n\n[[virtue]] [[mean]]',
      selectedText: NOTE.selectedText ?? undefined,
      highlightColour: 'blue',
    }]);
    // The form waits for the save to settle.
    expect(fixture.componentInstance.editing()).toBe(true);
    expect(fixture.componentInstance.saving()).toBe(true);
  });

  function saveAndSettle(replaceNote: boolean): void {
    render();
    fixture.componentInstance.startEdit();
    fixture.componentInstance.editContent.set('Words I do not want to lose.');
    fixture.componentInstance.saveEdit();
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    if (replaceNote) fixture.componentRef.setInput('note', { ...NOTE, content: 'Words I do not want to lose.' });
    fixture.componentRef.setInput('busy', false);
    fixture.detectChanges();
  }

  it('closes the form once the saved note comes back', () => {
    saveAndSettle(true);
    expect(fixture.componentInstance.editing()).toBe(false);
    expect(fixture.componentInstance.saving()).toBe(false);
  });

  it('keeps the words in the form when the save failed', () => {
    saveAndSettle(false);
    expect(fixture.componentInstance.editing()).toBe(true);
    expect(fixture.componentInstance.saving()).toBe(false);
    expect(fixture.componentInstance.editContent()).toBe('Words I do not want to lose.');
  });

  it('emits nothing when an edit changed nothing', () => {
    render();
    const edits: NoteItemEdit[] = [];
    fixture.componentInstance.edited.subscribe((edit) => edits.push(edit));

    fixture.componentInstance.startEdit();
    fixture.componentInstance.saveEdit();

    expect(edits).toEqual([]);
  });

  it('shows a passage by its title and size, with only its opening', () => {
    render({
      kind: 'passage',
      passageTitle: 'Book II: virtue as a mean',
      passageEnd: '35',
      passageWordCount: 900,
    });

    expect(element.querySelector('.note-item-passage-title')?.textContent).toContain('Book II: virtue as a mean');
    expect(element.querySelector('.note-item-passage-size')?.textContent).toContain('Pages 30–35');
    expect(element.querySelector('.note-item-quote')?.classList).toContain('is-opening');
  });

  it('shows a checkbox only when selecting, and reports a change', () => {
    render();
    expect(element.querySelector('input[type=checkbox]')).toBeNull();

    fixture.componentRef.setInput('selectable', true);
    fixture.detectChanges();
    const changes: boolean[] = [];
    fixture.componentInstance.selectedChange.subscribe((value) => changes.push(value));
    (element.querySelector('input[type=checkbox]') as HTMLInputElement).click();

    expect(changes).toEqual([true]);
  });

  describe('the highlight pen (issue #861)', () => {
    const pens = () => Array.from(element.querySelectorAll<HTMLButtonElement>('.pen-swatch'));

    it('offers the four pens in edit, with the stored one selected, each drawn from a token', () => {
      render({ highlightColour: 'blue' });
      fixture.componentInstance.startEdit();
      fixture.detectChanges();

      expect(pens().map((pen) => pen.getAttribute('aria-label'))).toEqual([
        'Sage highlighter', 'Amber highlighter', 'Blue highlighter', 'Rose highlighter',
      ]);
      expect(pens().map((pen) => pen.getAttribute('aria-pressed'))).toEqual(['false', 'false', 'true', 'false']);
      expect(pens()[3].style.backgroundColor).toBe('var(--highlight-rose)');
    });

    it('starts a highlight with no recorded pen (an import) on the default pen', () => {
      render({ highlightColour: null, captureSource: 'import' });
      fixture.componentInstance.startEdit();
      fixture.detectChanges();

      expect(pens().map((pen) => pen.getAttribute('aria-pressed'))).toEqual(['true', 'false', 'false', 'false']);
    });

    it('sends no pen when only the words changed', () => {
      render();
      const edits: NoteItemEdit[] = [];
      fixture.componentInstance.edited.subscribe((edit) => edits.push(edit));

      fixture.componentInstance.startEdit();
      fixture.componentInstance.editContent.set('Another thought.');
      fixture.componentInstance.saveEdit();

      expect(edits).toHaveLength(1);
      expect(edits[0]).not.toHaveProperty('highlightColour');
    });

    it('keeps the neutral rule when the source does not say which pen', () => {
      const { highlightColour: _unknown, ...withoutPen } = NOTE;
      fixture.componentRef.setInput('note', withoutPen);
      fixture.detectChanges();

      const quote = element.querySelector('.note-item-quote') as HTMLElement;
      expect(quote.style.getPropertyValue('--nostos-accent-rail-color')).toBe('');
    });

    it('offers no pens for a note that is not a highlight', () => {
      render({ selectedText: null, content: 'Just a thought.' });
      fixture.componentInstance.startEdit();
      fixture.detectChanges();

      expect(pens()).toHaveLength(0);
    });
  });

  describe('a long note', () => {
    it('cuts a long quote to a few lines behind "Show all", and unfolds it', () => {
      render({ selectedText: 'A sentence worth keeping. '.repeat(30) });

      const quote = element.querySelector('.note-item-quote') as HTMLElement;
      const toggle = element.querySelector('.note-item-expand') as HTMLButtonElement;
      expect(quote.classList).toContain('is-clamped');
      expect(toggle.textContent?.trim()).toBe('Show all');
      expect(toggle.getAttribute('aria-expanded')).toBe('false');

      toggle.click();
      fixture.detectChanges();

      expect(quote.classList).not.toContain('is-clamped');
      expect(toggle.textContent?.trim()).toBe('Show less');
    });

    it('leaves an ordinary quote whole', () => {
      render();

      expect(element.querySelector('.note-item-quote')?.classList).not.toContain('is-clamped');
      expect(element.querySelector('.note-item-expand')).toBeNull();
    });
  });

  describe('inside its own open book (the reader)', () => {
    it('says "Show in book" and asks to go there instead of opening the reader again', () => {
      fixture.componentRef.setInput('inBook', true);
      render();
      const jumps = vi.fn();
      fixture.componentInstance.jumpRequested.subscribe(jumps);

      const source = element.querySelector('.note-item-source') as HTMLElement;
      expect(source.tagName).toBe('BUTTON');
      expect(source.textContent?.trim()).toBe('Show in book');
      source.click();

      expect(jumps).toHaveBeenCalledTimes(1);
    });

    it('leaves out the menu items a list of this one book has no use for', () => {
      fixture.componentRef.setInput('showMoreFromBook', false);
      fixture.componentRef.setInput('canSelect', false);
      render();

      (element.querySelector('.note-item-more') as HTMLButtonElement).click();
      fixture.detectChanges();
      const items = Array.from(document.querySelectorAll('.nostos-menu-item')).map((item) => item.textContent?.trim());
      expect(items).toEqual(['Edit note', 'Keep with writing…', 'Delete note']);
      document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
    });
  });

  describe('an audiobook note’s time (issue #906)', () => {
    const time = () => element.querySelector('[data-testid="note-audio-time"]') as HTMLButtonElement | null;

    it('leads the meta line with the time, and goes to it when activated', () => {
      fixture.componentRef.setInput('audioTime', 754.5);
      render({ sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '754.5' });
      const jumps = vi.fn();
      fixture.componentInstance.jumpRequested.subscribe(jumps);

      const button = time()!;
      expect(button.closest('.note-item-meta')).not.toBeNull();
      expect(button.textContent?.trim()).toBe('12:34');
      expect(button.getAttribute('aria-label')).toBe('Jump to 12:34');
      // Said once, as the button, not again as the note's location.
      expect(element.querySelector('.note-item-meta')?.textContent?.match(/12:34/g)).toHaveLength(1);

      button.click();
      expect(jumps).toHaveBeenCalledTimes(1);
    });

    it('uses h:mm:ss past the first hour', () => {
      fixture.componentRef.setInput('audioTime', 3725);
      render();

      expect(time()!.textContent?.trim()).toBe('1:02:05');
    });

    it('shows nothing extra when no time is given', () => {
      render();

      expect(time()).toBeNull();
    });
  });

  describe('a passage (issue #1014)', () => {
    const PASSAGE: Partial<NoteSearchHit> = {
      kind: 'passage',
      passageTitle: 'The Grand Inquisitor',
      selectedText: '“Even this must have a preface—that is, a literary preface,” laughed Ivan…',
      sourceAnchorKind: 'epub_cfi',
      sourceAnchorValue: 'epubcfi(/6/80!/4/2/1:0)',
      passageEnd: 'epubcfi(/6/80!/4/112/1:285)',
      passageWordCount: 9262,
    };

    it('never offers to unfold its opening in place, however long', () => {
      render({ ...PASSAGE, selectedText: 'A long opening. '.repeat(60) });

      expect(element.querySelector('.note-item-expand')).toBeNull();
      expect(element.querySelector('.note-item-passage-size')?.textContent).toBe('9,262 words · 37 min');
    });

    it('opens the passage viewer with the options its host gave it', () => {
      const viewer = TestBed.inject(PassageViewerService);
      const openInBook = vi.fn();
      fixture.componentRef.setInput('passageViewerOptions', { openInBook });
      render(PASSAGE);

      (element.querySelector('.note-item-read') as HTMLButtonElement).click();

      expect(viewer.request()).toEqual({ noteId: 'note-1', openInBook });
    });

    it('edits the title and words, and never sends the opening back as the text', () => {
      render(PASSAGE);
      const edits: NoteItemEdit[] = [];
      fixture.componentInstance.edited.subscribe((edit) => edits.push(edit));

      fixture.componentInstance.startEdit();
      fixture.componentInstance.editTitle.set('The Legend');
      fixture.componentInstance.saveEdit();

      expect(edits).toHaveLength(1);
      expect(edits[0].passageTitle).toBe('The Legend');
      expect(edits[0]).not.toHaveProperty('selectedText');
    });
  });

  describe('read only (the Studio’s inspected source)', () => {
    beforeEach(() => {
      fixture.componentRef.setInput('readOnly', true);
    });

    it('shows no menu, no topic controls and no capture details', () => {
      render();

      expect(element.classList).toContain('is-read-only');
      expect(element.querySelector('.note-item-more')).toBeNull();
      expect(element.querySelector('.note-item-topics')).toBeNull();
      expect(element.querySelector('app-note-capture-details')).toBeNull();
    });

    it('keeps the note’s [[links]] in its words, since there are no chips to list them', () => {
      render();

      const links = Array.from(element.querySelectorAll('.note-item-thought a.topic-tag')).map((a) => a.textContent);
      expect(links).toEqual(['virtue', 'mean']);
    });

    it('leaves the meta line out where the surface names the source itself', () => {
      fixture.componentRef.setInput('showMeta', false);
      render();

      expect(element.querySelector('.note-item-meta')).toBeNull();
    });
  });
});
