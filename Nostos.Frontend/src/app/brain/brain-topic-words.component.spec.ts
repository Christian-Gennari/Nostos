import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';

import { AssistantService } from '../ui/assistant/assistant.service';
import { NotesService } from '../core/services/notes.service';
import { TopicsService, type TopicDto } from '../core/services/topics.service';
import { BrainStateService } from './brain-state.service';
import { BrainTopicWordsComponent } from './brain-topic-words.component';

describe('BrainTopicWordsComponent', () => {
  let fixture: ComponentFixture<BrainTopicWordsComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BrainTopicWordsComponent],
      providers: [
        provideRouter([]),
        {
          provide: TopicsService,
          useValue: {
            list: () => of([{ id: 't2', name: 'Evaluation', usageCount: 1 }]),
            setDescription: vi.fn(),
          },
        },
        { provide: NotesService, useValue: { summary: () => of({ total: 0 }) } },
        { provide: AssistantService, useValue: { actionExecuted: new Subject() } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(BrainTopicWordsComponent);
    fixture.componentRef.setInput('topic', {
      id: 't1',
      name: 'Metrics',
      usageCount: 0,
      description: 'See [[Evaluation]] and <b>not bold</b>.',
    } as TopicDto);
  });

  it('shows a [[link]] in the words as a link to the topic, and other markup as typed', () => {
    TestBed.inject(BrainStateService).ensureTopicsLoaded();
    fixture.detectChanges();

    const text = fixture.nativeElement.querySelector('.words-paragraph') as HTMLElement;
    expect(text.querySelector('a.topic-tag')?.getAttribute('href')).toBe('/brain/topics/t2');
    expect(text.textContent).toContain('<b>not bold</b>');
    expect(text.textContent).not.toContain('[[');
  });

  it('opens the linked topic instead of the editor when the link is clicked', () => {
    TestBed.inject(BrainStateService).ensureTopicsLoaded();
    fixture.detectChanges();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    (fixture.nativeElement.querySelector('a.topic-tag') as HTMLAnchorElement).click();
    fixture.detectChanges();

    expect(navigate).toHaveBeenCalledWith('/brain/topics/t2');
    expect(fixture.nativeElement.querySelector('textarea')).toBeNull();
  });
});
