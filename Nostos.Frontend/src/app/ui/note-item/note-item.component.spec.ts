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

  it('shows the quote, the words without the reference line, and the topics as one quiet line', () => {
    render();

    expect(element.querySelector('.note-item-quote')?.textContent).toContain('Virtue, then, is a state');
    expect(element.querySelector('.note-item-thought')?.textContent?.trim()).toBe('A compact definition of virtue.');
    const line = element.querySelector('.topic-line') as HTMLElement;
    const names = Array.from(line.querySelectorAll('.topic-line-name')).map((chip) => chip.textContent?.trim());
    expect(names).toEqual(['#mean', '#virtue']);
    // No pills, no crosses and no add pill until the line is tapped.
    expect(element.querySelector('.topic-chip')).toBeNull();
    expect(element.querySelector('[data-testid="topic-row-add"]')).toBeNull();
  });

  it('says how many writings cite the note as a quiet button whose menu opens each (issue #1162)', () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    render();
    expect(element.querySelector('.note-item-cited')).toBeNull();

    fixture.componentRef.setInput('citedWritings', [{ id: 'w1', name: 'On Optimism' }]);
    fixture.detectChanges();
    expect(element.querySelector('.note-item-meta button.note-item-cited')?.textContent?.trim()).toBe('Cited in 1 document');

    fixture.componentRef.setInput('citedWritings', [{ id: 'w1', name: 'On Optimism' }, { id: 'w2', name: 'Theodicy' }]);
    fixture.detectChanges();
    const button = element.querySelector('button.note-item-cited') as HTMLButtonElement;
    expect(button.getAttribute('type')).toBe('button');
    expect(button.textContent?.trim()).toBe('Cited in 2 documents');
    expect(button.getAttribute('aria-haspopup')).toBe('menu');

    button.click();
    fixture.detectChanges();
    const items = Array.from(document.querySelectorAll<HTMLButtonElement>('.nostos-menu-item'));
    expect(items.map((item) => item.textContent?.trim())).toEqual(['On Optimism', 'Theodicy']);
    items[1].click();
    expect(navigate).toHaveBeenCalledWith(['/studio'], { queryParams: { writingId: 'w2' } });
    document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
  });

  it('moves focus into the words when Edit note opens, and back to the ⋯ button on Cancel', async () => {
    document.body.appendChild(element);
    render();
    fixture.componentInstance.startEdit();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(element.querySelector('app-topic-input textarea'));

    fixture.componentInstance.cancelEdit();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(element.querySelector('.note-item-more'));
    element.remove();
  });

  it('returns focus to the ⋯ button once a saved edit settles', async () => {
    document.body.appendChild(element);
    render();
    fixture.componentInstance.startEdit();
    fixture.detectChanges();
    fixture.componentInstance.editWords.set('New words.');
    fixture.componentInstance.saveEdit();
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    fixture.componentRef.setInput('note', { ...NOTE, content: 'New words.' });
    fixture.componentRef.setInput('busy', false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(element.querySelector('.note-item-more'));
    element.remove();
  });

  it('Escape in the edit form cancels the edit and goes no further (a drawer around it stays open)', () => {
    render();
    const outer = vi.fn();
    document.addEventListener('keydown', outer);
    fixture.componentInstance.startEdit();
    fixture.detectChanges();

    const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    (element.querySelector('.note-item-edit') as HTMLElement).dispatchEvent(event);
    fixture.detectChanges();
    document.removeEventListener('keydown', outer);

    expect(fixture.componentInstance.editing()).toBe(false);
    expect(event.defaultPrevented).toBe(true);
    expect(outer).not.toHaveBeenCalled();
  });

  it('draws the quote rule in the note’s pen', () => {
    render({ highlightColour: 'rose' });

    const quote = element.querySelector('.note-item-quote') as HTMLElement;
    expect(quote.style.getPropertyValue('--nostos-accent-rail-color')).toBe('var(--highlight-rose)');
  });

  it('names the source under the quote as a citation that opens the book at the page (issue #1798)', () => {
    render();

    const citation = element.querySelector('[data-testid="note-citation"]') as HTMLAnchorElement;
    expect(citation.tagName).toBe('A');
    expect(citation.textContent?.trim()).toBe('— Aristotle, Nicomachean Ethics, p. 30');
    const link = new URL(citation.href);
    expect(link.pathname).toBe('/read/book-1');
    expect(link.searchParams.get('sourcePage')).toBe('30');
    // The date stays, in the small meta line; the book is named once, above.
    const meta = element.querySelector('.note-item-meta')?.textContent ?? '';
    expect(meta).toContain('28 Jul 2026');
    expect(meta).not.toContain('Nicomachean Ethics');
    expect(element.querySelector('.note-item-source')).toBeNull();
    expect(element.textContent).not.toContain('Open in book');
  });

  it('reads quote, citation, words, topics and date in that order (issue #1798)', () => {
    render();
    const quote = element.querySelector('.note-item-quote') as HTMLElement;
    const citation = element.querySelector('[data-testid="note-citation"]') as HTMLElement;
    const thought = element.querySelector('.note-item-thought') as HTMLElement;
    const topics = element.querySelector('.note-item-topics') as HTMLElement;
    const date = element.querySelector('.note-item-meta time') as HTMLElement;

    const precedes = (first: Node, second: Node) =>
      !!(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING);
    expect(precedes(quote, citation)).toBe(true);
    expect(precedes(citation, thought)).toBe(true);
    expect(precedes(thought, topics)).toBe(true);
    expect(precedes(topics, date)).toBe(true);
  });

  it('keeps a small source line on a note with no quote', () => {
    render({ selectedText: null });

    const thought = element.querySelector('.note-item-thought') as HTMLElement;
    const source = element.querySelector('.note-item-citation-line [data-testid="note-citation"]') as HTMLAnchorElement;
    expect(source.textContent?.trim()).toBe('— Aristotle, Nicomachean Ethics, p. 30');
    expect(source.closest('.note-item-citation-line')).not.toBeNull();
    expect(thought.compareDocumentPosition(source) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('marks the edition on the citation only when asked, from the note\'s own format', () => {
    render({ bookFormat: 'pdf' });
    expect(element.querySelector('[data-testid="note-edition-format"]')).toBeNull();

    fixture.componentRef.setInput('showFormat', true);
    fixture.detectChanges();
    const format = element.querySelector('[data-testid="note-edition-format"]');
    // The format is the citation's glyph, not another word on the card.
    expect(format?.getAttribute('aria-label')).toBe('PDF');
    expect(format?.textContent?.trim()).toBe('');
    expect(format?.querySelector('nostos-icon')).not.toBeNull();
    expect(format?.closest('[data-testid="note-citation"]')).not.toBeNull();

    render({ bookFormat: 'audiobook', sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '3600' });
    expect(element.querySelector('[data-testid="note-edition-format"]')?.getAttribute('aria-label')).toBe('Audiobook');

    render({ bookFormat: null });
    expect(element.querySelector('[data-testid="note-edition-format"]')).toBeNull();
  });

  it('calls the citation a way to "Open book" for a physical book, which has no reader', () => {
    render({ bookFormat: 'physical', sourceAnchorKind: undefined, sourceAnchorValue: undefined });

    const citation = element.querySelector('[data-testid="note-citation"]') as HTMLAnchorElement;
    expect(citation.getAttribute('aria-label')).toBe('Open book');
    expect(new URL(citation.href).pathname).toBe('/library/book-1');
  });

  it('opens a note from another edition in that edition', () => {
    render({ bookId: 'audio-edition', sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '42', bookFormat: 'audiobook' });

    const link = new URL((element.querySelector('[data-testid="note-citation"]') as HTMLAnchorElement).href);
    expect(link.pathname).toBe('/read/audio-edition');
    expect(link.searchParams.get('sourceTime')).toBe('42');
  });

  it('leaves the book out of the citation where a heading already names it', () => {
    fixture.componentRef.setInput('showBook', false);
    render();

    const citation = element.querySelector('[data-testid="note-citation"]') as HTMLElement;
    expect(citation.textContent?.trim()).toBe('— p. 30');
  });

  it('links a topic name to its page, and turns the line into its controls on a tap', () => {
    render();
    const removed: string[] = [];
    fixture.componentInstance.topicRemoved.subscribe((name) => removed.push(name));

    const chip = element.querySelector('.topic-line-name') as HTMLAnchorElement;
    expect(new URL(chip.href).pathname).toBe('/brain/topics/t-mean');
    expect(element.querySelector('.topic-chip')).toBeNull();

    // One tap on the line's control: the same topics become removable pills
    // with **+ Topic** beside them.
    (element.querySelector('[data-testid="topic-line-edit"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.querySelectorAll('.topic-chip')).toHaveLength(2);
    expect(element.querySelector('[data-testid="topic-row-add"]')).not.toBeNull();

    (element.querySelector('.topic-chip-remove') as HTMLButtonElement).click();
    expect(removed).toEqual(['mean']);
  });

  it('collapses five topics to three names and a +2 count (issue #1797)', () => {
    render({ topicNames: ['one', 'two', 'three', 'four', 'five'], content: 'A thought.' });

    const line = element.querySelector('.topic-line') as HTMLElement;
    expect(Array.from(line.querySelectorAll('.topic-line-name')).map((name) => name.textContent?.trim()))
      .toEqual(['#one', '#two', '#three']);
    expect(line.querySelector('[data-testid="topic-line-more"]')?.textContent?.trim()).toBe('+2');
    expect(element.querySelector('.topic-chip')).toBeNull();

    // Opening it shows all five, with a cross each and the add control.
    (line.querySelector('[data-testid="topic-line-more"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.querySelectorAll('.topic-chip')).toHaveLength(5);
    expect(element.querySelector('[data-testid="topic-row-add"]')).not.toBeNull();
  });

  it('does not unfold the controls when a topic name is tapped', () => {
    render();
    const link = element.querySelector('.topic-line-name') as HTMLAnchorElement;

    link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
    fixture.detectChanges();

    expect(element.querySelector('.topic-chip')).toBeNull();
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

    expect(element.querySelector('[data-testid="topic-row-add"]')?.textContent?.trim()).toBe('Add a topic');
    expect(element.querySelector('.note-item-thought')).toBeNull();
  });

  it('reports a topic added from the pill row', () => {
    render();
    const added: string[] = [];
    fixture.componentInstance.topicAdded.subscribe((name) => added.push(name));

    const row = fixture.debugElement.query((node) => node.name === 'app-topic-row');
    row.triggerEventHandler('added', 'choice');

    expect(added).toEqual(['choice']);
  });

  it('opens the editor with the words only and the topic lines as pills', async () => {
    render();
    fixture.componentInstance.startEdit();
    fixture.detectChanges();
    // Template-driven forms write the value a tick later.
    await fixture.whenStable();
    fixture.detectChanges();

    const words = element.querySelector('textarea.note-input') as HTMLTextAreaElement;
    expect(words.value).toBe('A compact definition of virtue.');
    expect(words.value).not.toContain('[[');
    const pills = Array.from(element.querySelectorAll('app-note-composer .topic-chip-name')).map((chip) => chip.textContent?.trim());
    expect(pills).toEqual(['virtue', 'mean']);
    // While writing, a pill does not navigate away from the words.
    expect(element.querySelector('app-note-composer a.topic-chip-name')).toBeNull();
  });

  it('keeps an older link the text never declared as a pill in the editor', () => {
    render({ content: 'A thought.', topicNames: ['older'] });
    fixture.componentInstance.startEdit();

    expect(fixture.componentInstance.editTopics()).toEqual(['older']);
  });

  it('sends a removed pill as a topic list without it', () => {
    render();
    const edits: NoteItemEdit[] = [];
    fixture.componentInstance.edited.subscribe((edit) => edits.push(edit));

    fixture.componentInstance.startEdit();
    fixture.componentInstance.editTopics.set(['virtue']);
    fixture.componentInstance.saveEdit();

    expect(edits[0]).toMatchObject({ content: 'A compact definition of virtue.', topics: ['virtue'] });
  });

  it('emits only what an edit changed', () => {
    render();
    const edits: NoteItemEdit[] = [];
    fixture.componentInstance.edited.subscribe((edit) => edits.push(edit));

    fixture.componentInstance.startEdit();
    fixture.componentInstance.editWords.set('A sharper definition.');
    fixture.componentInstance.editColour.set('blue');
    fixture.componentInstance.saveEdit();

    expect(edits).toEqual([{
      id: 'note-1',
      content: 'A sharper definition.',
      topics: ['virtue', 'mean'],
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
    fixture.componentInstance.editWords.set('Words I do not want to lose.');
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
    expect(fixture.componentInstance.editWords()).toBe('Words I do not want to lose.');
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
      fixture.componentInstance.editWords.set('Another thought.');
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
    it('cuts a long quote to a few lines behind a quiet More link, and unfolds it (issue #1799)', () => {
      render({ selectedText: 'A sentence worth keeping. '.repeat(30) });

      const quote = element.querySelector('.note-item-quote') as HTMLElement;
      const toggle = element.querySelector('.note-item-expand') as HTMLButtonElement;
      expect(quote.classList).toContain('is-clamped');
      expect(quote.classList).toContain('is-faded');
      // No box: the toggle is the link variant, not a bordered button.
      expect(toggle.classList).not.toContain('nostos-button--secondary');
      expect(toggle.classList).toContain('nostos-button--link');
      expect(toggle.textContent?.trim()).toBe('More');
      expect(toggle.getAttribute('aria-expanded')).toBe('false');

      toggle.click();
      fixture.detectChanges();

      expect(quote.classList).not.toContain('is-clamped');
      expect(quote.classList).not.toContain('is-faded');
      expect(toggle.textContent?.trim()).toBe('Less');
      expect(toggle.getAttribute('aria-expanded')).toBe('true');

      toggle.click();
      fixture.detectChanges();

      // Back to exactly the clipped card it was before "More".
      expect(quote.classList).toContain('is-clamped');
      expect(quote.classList).toContain('is-faded');
      expect(toggle.textContent?.trim()).toBe('More');
    });

    it('unfolds the note when the clamped quote itself is tapped', () => {
      render({ selectedText: 'A sentence worth keeping. '.repeat(30) });
      const quote = element.querySelector('.note-item-quote') as HTMLElement;

      quote.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));

      expect(fixture.componentInstance.expanded()).toBe(true);
    });

    it('unfolds the note when its own clamped words are tapped', () => {
      render({ content: 'a thought worth keeping. '.repeat(40) });
      const thought = element.querySelector('.note-item-thought') as HTMLElement;

      thought.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));

      expect(fixture.componentInstance.expanded()).toBe(true);
    });

    it('follows a link inside the words instead of unfolding the note', () => {
      render({ content: '[[virtue]] ' + 'a thought worth keeping. '.repeat(40) });
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
      const link = element.querySelector('.note-item-thought a.topic-tag') as HTMLAnchorElement;

      link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));

      expect(navigate).toHaveBeenCalledWith(link.getAttribute('href'));
      expect(fixture.componentInstance.expanded()).toBe(false);
    });

    it('marks only the paragraph that is actually cut', () => {
      render({ selectedText: 'A sentence worth keeping. '.repeat(30) });
      const thought = element.querySelector('.note-item-thought') as HTMLElement;
      expect(thought.classList).toContain('is-clamped');
      expect(thought.classList).not.toContain('is-faded');

      render({ content: 'a thought worth keeping. '.repeat(28) });
      expect(element.querySelector('.note-item-quote')?.classList).not.toContain('is-faded');
      expect(element.querySelector('.note-item-thought')?.classList).toContain('is-faded');
    });

    it('leaves an ordinary quote whole', () => {
      render();

      expect(element.querySelector('.note-item-quote')?.classList).not.toContain('is-clamped');
      expect(element.querySelector('.note-item-quote')?.classList).not.toContain('is-faded');
      expect(element.querySelector('.note-item-thought')?.classList).not.toContain('is-faded');
      expect(element.querySelector('.note-item-expand')).toBeNull();
    });
  });

  describe('inside its own open book (the reader)', () => {
    it('makes the citation the way to the note in the book', () => {
      fixture.componentRef.setInput('inBook', true);
      render();
      const jumps = vi.fn();
      fixture.componentInstance.jumpRequested.subscribe(jumps);

      const source = element.querySelector('[data-testid="note-citation"]') as HTMLElement;
      expect(source.tagName).toBe('BUTTON');
      expect(source.textContent?.trim()).toBe('— p. 30');
      expect(source.getAttribute('aria-label')).toBe('Show in book at p. 30');
      source.click();

      expect(jumps).toHaveBeenCalledTimes(1);
    });

    it('says "Show in book" where the note has no location to cite', () => {
      fixture.componentRef.setInput('inBook', true);
      render({ sourceAnchorKind: 'epub_cfi', sourceAnchorValue: 'epubcfi(/6/4!/2/1:0)' });

      const source = element.querySelector('[data-testid="note-citation"]') as HTMLElement;
      expect(source.tagName).toBe('BUTTON');
      expect(source.textContent?.trim()).toBe('— Show in book');
    });

    it('leaves out the menu items a list of this one book has no use for', () => {
      fixture.componentRef.setInput('showMoreFromBook', false);
      fixture.componentRef.setInput('canSelect', false);
      render();

      (element.querySelector('.note-item-more') as HTMLButtonElement).click();
      fixture.detectChanges();
      const items = Array.from(document.querySelectorAll('.nostos-menu-item')).map((item) => item.textContent?.trim());
      expect(items).toEqual(['Edit note', 'Keep in a document…', 'Delete note']);
      document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
    });
  });

  describe('an audiobook note’s time (issue #906)', () => {
    const citation = () => element.querySelector('[data-testid="note-citation"]') as HTMLElement | null;

    it('names the time in the citation, which goes back to it when activated', () => {
      fixture.componentRef.setInput('audioTime', 754.5);
      fixture.componentRef.setInput('inBook', true);
      render({ sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '754.5' });
      const jumps = vi.fn();
      fixture.componentInstance.jumpRequested.subscribe(jumps);

      const control = citation()!;
      expect(control.textContent?.trim()).toBe('— 12:34');
      expect(control.getAttribute('aria-label')).toBe('Show in book at 12:34');

      control.click();
      expect(jumps).toHaveBeenCalledTimes(1);
    });

    it('uses h:mm:ss past the first hour', () => {
      fixture.componentRef.setInput('audioTime', 3725);
      render();

      expect(citation()?.textContent).toContain('1:02:05');
    });

    it('shows no time when none is given', () => {
      render();

      expect(element.querySelector('[data-testid="note-audio-time"]')).toBeNull();
      expect(citation()?.textContent).not.toContain(':');
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
      expect(element.querySelector('.note-item-quote')?.classList).not.toContain('is-faded');
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
      expect(element.querySelector('[data-testid="note-citation"]')).toBeNull();
    });
  });
});
