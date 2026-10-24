import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { NO_TOPICS_MATCH, NO_TOPICS_YET, TopicOptionComponent } from './topic-option.component';

@Component({
  standalone: true,
  imports: [TopicOptionComponent],
  template: `
    <ul role="listbox">
      <li id="topic" appTopicOption role="option" name="Virtue" [count]="12" [active]="true"></li>
      <li id="uncounted" appTopicOption role="option" name="Habit"></li>
      <li id="create" appTopicOption role="option" kind="create" name="Practical wisdom"></li>
    </ul>
    <button id="button" type="button" appTopicOption name="Ethics" [count]="0"></button>
  `,
})
class HostComponent {}

describe('TopicOptionComponent', () => {
  function setup() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return (id: string) => fixture.nativeElement.querySelector(`#${id}`) as HTMLElement;
  }

  it('shows the mark, the name and the count as a plain number', () => {
    const row = setup()('topic');

    expect(row.getAttribute('role')).toBe('option');
    expect(row.classList).toContain('is-active');
    expect(row.querySelector('nostos-icon')).not.toBeNull();
    expect(row.querySelector('.topic-option-name')?.textContent).toBe('Virtue');
    const count = row.querySelector('.topic-option-count')!;
    expect(count.textContent).toBe('12');
    expect(count.classList).not.toContain('nostos-badge');
  });

  it('shows no number when the count is not known, and zero when it is zero', () => {
    const row = setup();

    expect(row('uncounted').querySelector('.topic-option-count')).toBeNull();
    expect(row('button').querySelector('.topic-option-count')?.textContent).toBe('0');
  });

  it('offers a new topic by name', () => {
    const row = setup()('create');

    expect(row.classList).toContain('is-create');
    expect(row.textContent?.trim()).toBe('Create “Practical wisdom”');
  });

  it('has one wording for an empty topic list', () => {
    expect(NO_TOPICS_YET).toBe('No topics yet. Type a name to create one.');
    expect(NO_TOPICS_MATCH).toBe('No topics match.');
  });
});
