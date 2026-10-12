import type { OutputEmitterRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import type { TopicDetailDto } from '../../core/services/topics.service';
import type { AssistantSuggestionDto } from '../../ui/assistant/assistant.service';
import {
  BrainTopicProposalsComponent,
  type BrainTopicProposalState,
} from './brain-topic-proposals.component';

const candidate = {
  kind: 'topic',
  noteId: 'n1',
  value: 't1',
  label: 'Stoicism',
  reason: 'The note is about endurance.',
} as AssistantSuggestionDto;

describe('BrainTopicProposalsComponent', () => {
  let fixture: ComponentFixture<BrainTopicProposalsComponent>;
  let element: HTMLElement;

  const render = (state: BrainTopicProposalState, inputs: Record<string, unknown> = {}) => {
    fixture.componentRef.setInput('state', state);
    fixture.componentRef.setInput('candidates', state === 'ready' ? [candidate] : []);
    for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  };
  const buttonNamed = (text: string) =>
    [...element.querySelectorAll('button')].find((button) => button.textContent?.trim() === text);
  const emitted = <T>(name: 'dismissed' | 'linkRetried' | 'suggestionsRetried' | 'inspected' | 'accepted' | 'topicOpened' | 'questionAsked') => {
    const values: T[] = [];
    (fixture.componentInstance[name] as unknown as OutputEmitterRef<T>).subscribe((value) =>
      values.push(value),
    );
    return values;
  };

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [BrainTopicProposalsComponent] });
    fixture = TestBed.createComponent(BrainTopicProposalsComponent);
    element = fixture.nativeElement;
  });

  it('shows nothing until suggestions were asked for', () => {
    render('idle');

    expect(element.querySelector('[data-testid="brain-proposals"]')).toBeNull();
  });

  it('offers Cancel while looking and Dismiss afterwards', () => {
    const dismissed = emitted<void>('dismissed');

    render('loading');
    expect(element.textContent).toContain('Looking at this note');
    buttonNamed('Cancel')!.click();

    render('empty');
    expect(element.textContent).toContain('No additional connections found.');
    buttonNamed('Dismiss')!.click();

    expect(dismissed).toHaveLength(2);
  });

  it('lists each suggestion with its reason and reports which one was accepted', () => {
    const accepted = emitted<AssistantSuggestionDto>('accepted');
    render('ready');

    const card = element.querySelector('[data-testid="brain-proposal"]')!;
    expect(card.querySelector('h3')?.textContent).toBe('Stoicism');
    expect(card.textContent).toContain('The note is about endurance.');
    buttonNamed('Link note')!.click();

    expect(accepted).toEqual([candidate]);
  });

  it('shows the existing evidence of the suggestion being inspected', () => {
    const inspected = emitted<AssistantSuggestionDto>('inspected');
    const opened = emitted<string>('topicOpened');
    render('ready');
    buttonNamed('Inspect evidence')!.click();
    expect(inspected).toEqual([candidate]);

    render('ready', { inspectingId: 't1' });
    expect(element.textContent).toContain('Loading existing evidence');

    const detail: TopicDetailDto = {
      id: 't1',
      name: 'Stoicism',
      notes: [{ noteId: 'n9', content: 'Amor fati.', bookId: 'b1', bookTitle: 'Meditations' }],
    };
    render('ready', { inspectingId: 't1', detail });
    expect(buttonNamed('Hide evidence')).toBeTruthy();
    expect(element.querySelector('.brain-proposal-evidence')?.textContent).toContain('1 linked note in “Stoicism”');
    buttonNamed('Open topic')!.click();

    expect(opened).toEqual(['Stoicism']);
  });

  it('explains a failure and offers the retries that apply', () => {
    const linkRetried = emitted<void>('linkRetried');
    const suggestionsRetried = emitted<void>('suggestionsRetried');

    render('error', { message: 'No link was made.' });
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('No link was made.');
    expect(buttonNamed('Try linking again')).toBeUndefined();
    buttonNamed('Try suggestions again')!.click();

    render('error', { canRetryLink: true });
    buttonNamed('Try linking again')!.click();

    expect(suggestionsRetried).toHaveLength(1);
    expect(linkRetried).toHaveLength(1);
  });

  it('locks Dismiss while a link is being confirmed, and always offers a question', () => {
    const asked = emitted<void>('questionAsked');
    render('linking');

    expect(buttonNamed('Dismiss')!.disabled).toBe(true);
    buttonNamed('Ask Nostos a question about this note')!.click();

    expect(asked).toHaveLength(1);
  });
});
