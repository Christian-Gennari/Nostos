import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { BrainStateService } from '../../brain/brain-state.service';
import {
  NoteComposerComponent,
  draftChanged,
  draftForNote,
  draftIsEmpty,
  draftToSave,
} from './note-composer.component';

describe('NoteComposerComponent', () => {
  let fixture: ComponentFixture<NoteComposerComponent>;
  let composer: NoteComposerComponent;
  let element: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NoteComposerComponent],
      providers: [
        provideRouter([]),
        {
          provide: BrainStateService,
          useValue: {
            topics: signal([{ id: 't-faith', name: 'Faith', usageCount: 2 }]),
            ensureTopicsLoaded: () => {},
          },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(NoteComposerComponent);
    composer = fixture.componentInstance;
    element = fixture.nativeElement as HTMLElement;
  });

  const pills = () => Array.from(element.querySelectorAll('.topic-chip-name')).map((pill) => pill.textContent?.trim());

  it('shows the shared placeholder and a single add button, + Topic', () => {
    fixture.detectChanges();

    expect(element.querySelector('textarea')?.getAttribute('placeholder')).toBe('Write your thought, or link a [[Topic]]');
    const buttons = Array.from(element.querySelectorAll('button')).map((button) => button.textContent?.trim());
    expect(buttons).toEqual(['Add a topic']);
  });

  it('shows a topic typed inside a sentence as a pill too', () => {
    composer.words.set('[[Attention]] is a spiritual journey.');
    fixture.detectChanges();

    expect(pills()).toEqual(['Attention']);
  });

  it('adds a pill once, in any case, and not for a topic the words already link', () => {
    composer.words.set('[[Attention]] is a spiritual journey.');
    composer.addTopic('Faith');
    composer.addTopic('faith');
    composer.addTopic('attention');
    fixture.detectChanges();

    expect(pills()).toEqual(['Attention', 'Faith']);
    expect(composer.draft()).toEqual({ words: '[[Attention]] is a spiritual journey.', topics: ['Faith'] });
  });

  it('removing the pill of a typed topic turns its word plain', () => {
    composer.words.set('[[Attention]] is a spiritual journey.');
    composer.removeTopic('Attention');

    expect(composer.words()).toBe('Attention is a spiritual journey.');
  });

  it('removing an added pill leaves the words alone', () => {
    composer.words.set('A thought.');
    composer.topics.set(['Faith']);
    composer.removeTopic('faith');

    expect(composer.topics()).toEqual([]);
    expect(composer.words()).toBe('A thought.');
  });

  it('pills do not link away while the note is being written', () => {
    composer.topics.set(['Faith']);
    fixture.detectChanges();

    expect(element.querySelector('a.topic-chip-name')).toBeNull();
  });
});

describe('note drafts', () => {
  it('opens a saved note with its words, its topic lines as pills, and an older link as a pill', () => {
    expect(draftForNote('A thought.\n\n[[Faith]]', ['Faith', 'older'])).toEqual({
      words: 'A thought.',
      topics: ['Faith', 'older'],
    });
  });

  it('does not repeat an inline topic as a pill', () => {
    expect(draftForNote('On [[Faith]].', ['faith'])).toEqual({ words: 'On [[Faith]].', topics: [] });
  });

  it('sees a change in the words or the topics, not in their order or case', () => {
    const before = { words: 'A thought.', topics: ['Faith', 'Hope'] };

    expect(draftChanged({ words: 'A thought. ', topics: ['hope', 'faith'] }, before)).toBe(false);
    expect(draftChanged({ words: 'A thought.', topics: ['Faith'] }, before)).toBe(true);
    expect(draftChanged({ words: 'Another.', topics: ['Faith', 'Hope'] }, before)).toBe(true);
  });

  it('saves the words trimmed and only the pills the words do not link', () => {
    expect(draftToSave({ words: ' On [[Faith]]. ', topics: ['faith', 'Hope'] })).toEqual({
      words: 'On [[Faith]].',
      topics: ['Hope'],
    });
  });

  it('is empty with no words and no topics', () => {
    expect(draftIsEmpty({ words: '  ', topics: [] })).toBe(true);
    expect(draftIsEmpty({ words: '', topics: ['Faith'] })).toBe(false);
  });
});
