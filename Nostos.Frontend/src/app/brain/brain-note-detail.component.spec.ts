import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import type { TopicDetailDto, TopicDto, TopicWritingUsageDto } from '../core/services/topics.service';
import { TopicsService } from '../core/services/topics.service';
import { BrainNoteActionsService } from './brain-note-actions.service';
import { BrainNoteDetailComponent } from './brain-note-detail.component';
import { BrainStateService } from './brain-state.service';
import { BrainTopicSuggestionsService } from './brain-topic-suggestions.service';

const ATTENTION: TopicDto = { id: 't-attention', name: 'Attention', usageCount: 3 };

const NOTE: NoteSearchHit = {
  id: 'note-1',
  bookId: 'book-1',
  bookTitle: 'Walden',
  bookAuthor: 'Henry David Thoreau',
  content: 'Not escape, but a test: what is essential enough to keep?\n\n[[Attention]]',
  selectedText: 'I went to the woods because I wished to live deliberately.',
  snippet: null,
  topicNames: ['Attention'],
  createdAt: '2026-11-08T10:00:00Z',
  sourceAnchorKind: 'physical_page',
  sourceAnchorValue: '91',
  anchorVerified: false,
  bookFormat: 'physical',
};

const TOPIC_NOTES: TopicDetailDto = {
  id: ATTENTION.id,
  name: ATTENTION.name,
  notes: [
    {
      noteId: 'note-1',
      content: 'Not escape, but a test.',
      bookId: 'book-1',
      bookTitle: 'Walden',
      createdAt: '2026-11-08T10:00:00Z',
    },
    {
      noteId: 'note-2',
      content: 'The cost of a thing is the amount of life which is required to be exchanged for it.',
      bookId: 'book-2',
      bookTitle: 'Walden',
      createdAt: '2026-10-14T10:00:00Z',
    },
  ],
};

const USAGE: TopicWritingUsageDto = {
  total: 1,
  writings: [{ id: 'writing-1', name: 'On owning less', updatedAt: '2026-11-01T10:00:00Z' }],
  noteCitations: [{ noteId: 'note-1', writings: 1, writingIds: ['writing-1'] }],
};

describe('BrainNoteDetailComponent', () => {
  let fixture: ComponentFixture<BrainNoteDetailComponent>;
  let element: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BrainNoteDetailComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: BrainStateService,
          useValue: {
            topics: signal([ATTENTION]),
            lookup: signal(new Map([['attention', ATTENTION]])),
            topicsRewritten: signal(0),
          },
        },
        {
          provide: BrainNoteActionsService,
          useValue: {
            busy: signal(new Set<string>()),
            addTopic: vi.fn(),
            removeTopic: vi.fn(),
            edit: vi.fn(),
            keepWithWriting: vi.fn(),
            askDelete: vi.fn(),
            restored: vi.fn(),
          },
        },
        {
          provide: BrainTopicSuggestionsService,
          useValue: {
            namesFor: () => [],
            stateFor: () => 'off',
            request: vi.fn(),
            closeFor: vi.fn(),
          },
        },
        {
          provide: TopicsService,
          useValue: {
            get: vi.fn(() => of(TOPIC_NOTES)),
            getWritingUsage: vi.fn(() => of(USAGE)),
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(BrainNoteDetailComponent);
    element = fixture.nativeElement as HTMLElement;
    fixture.componentRef.setInput('note', NOTE);
    fixture.detectChanges();
    fixture.detectChanges();
  });

  it('shows the note in full: book, quote, words, source, topics and citations', () => {
    expect(element.querySelector('.note-detail-title')?.textContent?.trim()).toBe('Walden');
    expect(element.querySelector('.note-detail-author')?.textContent?.trim()).toBe('Henry David Thoreau');
    expect(element.querySelector('.note-item-quote')?.textContent).toContain('I went to the woods');
    expect(element.querySelector('.note-item-thought')?.textContent?.trim()).toBe('Not escape, but a test: what is essential enough to keep?');
    expect(element.querySelector('.note-item-source')?.getAttribute('href')).toBe('/library/book-1');
    expect(element.querySelector('.note-item-quote')?.classList.contains('is-lg')).toBe(true);

    const chips = Array.from(element.querySelectorAll('.topic-chip-name')).map((chip) => chip.textContent?.trim());
    expect(chips).toEqual(['Attention']);
    expect(element.querySelector('.note-item-cited')?.textContent?.trim()).toBe('Cited in 1 document');
  });

  it('shows the notes sharing its topics, and opens one when chosen', () => {
    const chosen: string[] = [];
    fixture.componentInstance.relatedChosen.subscribe((id) => chosen.push(id));

    const rows = Array.from(element.querySelectorAll<HTMLButtonElement>('.related-row'));
    expect(rows).toHaveLength(1);
    expect(rows[0].querySelector('.related-text')?.textContent?.trim()).toBe(
      'The cost of a thing is the amount of life which is required to be exchanged for it.',
    );
    expect(rows[0].querySelector('.related-meta')?.textContent?.trim()).toBe('Walden · 14 Oct 2026');
    expect(element.querySelector('.related-sharing')?.textContent?.trim()).toBe('sharing Attention');

    rows[0].click();
    expect(chosen).toEqual(['note-2']);
  });

  it('uses the card quote step when the host asks for the smaller one', () => {
    fixture.componentRef.setInput('quoteSize', 'md');
    fixture.detectChanges();
    expect(element.querySelector('.note-item-quote')?.classList.contains('is-lg')).toBe(false);
  });
});
