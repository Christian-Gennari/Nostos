import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { NoteItemComponent, type NoteItemEdit } from './note-item.component';
import type { NoteSearchHit } from '../../core/dtos/note.dtos';
import type { TopicDto } from '../../core/services/topics.service';

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
      providers: [provideRouter([{ path: '**', component: BlankPage }])],
    }).compileComponents();
    fixture = TestBed.createComponent(NoteItemComponent);
    element = fixture.nativeElement as HTMLElement;
  });

  it('shows the quote, the words without the reference line, and the topics as chips', () => {
    render();

    expect(element.querySelector('.note-item-quote')?.textContent).toContain('Virtue, then, is a state');
    expect(element.querySelector('.note-item-thought')?.textContent?.trim()).toBe('A compact definition of virtue.');
    const chips = Array.from(element.querySelectorAll('.note-item-topic-name')).map((chip) => chip.textContent?.trim());
    expect(chips).toEqual(['mean', 'virtue']);
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

    const chip = element.querySelector('.note-item-topic-name') as HTMLAnchorElement;
    (element.querySelector('.note-item-topic-remove') as HTMLButtonElement).click();

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
    expect(fixture.componentInstance.editing()).toBe(false);
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
});
