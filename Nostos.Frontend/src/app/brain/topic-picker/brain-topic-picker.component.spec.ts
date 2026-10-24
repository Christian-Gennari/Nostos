import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import type { TopicDto } from '../../core/services/topics.service';
import { BrainTopicPickerComponent } from './brain-topic-picker.component';

@Component({
  standalone: true,
  imports: [BrainTopicPickerComponent],
  template: `
    <app-brain-topic-picker
      [variant]="variant()"
      label="Choose a topic"
      heading="Link to topic"
      closeLabel="Close topic picker"
      searchLabel="Search topics to link"
      emptyText="No topics match."
      confirmLabel="Link note"
      [linkHint]="linkHint()"
      [candidates]="candidates()"
      [selectedId]="selectedId()"
      [confirmDisabled]="!selectedId()"
      [query]="query()"
      (queryChange)="query.set($event)"
      (chosen)="selectedId.set($event)"
      (confirmed)="confirmed = confirmed + 1"
      (closed)="closed = closed + 1"
    >
      <button type="button" class="projected">Create and link</button>
    </app-brain-topic-picker>
  `,
})
class HostComponent {
  variant = signal<'card' | 'inspector'>('card');
  linkHint = signal(false);
  candidates = signal<TopicDto[]>([
    { id: 'a', name: 'Alpha', usageCount: 3 },
    { id: 'b', name: 'Beta', usageCount: 1 },
  ]);
  selectedId = signal<string | null>(null);
  query = signal('');
  confirmed = 0;
  closed = 0;
}

describe('BrainTopicPickerComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let element: HTMLElement;

  const query = <T extends HTMLElement>(selector: string) => element.querySelector<T>(selector)!;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    element = fixture.nativeElement;
    fixture.detectChanges();
  });

  it('lists the candidates with their note counts and names itself', () => {
    const rows = [...element.querySelectorAll('.merge-target')].map((row) =>
      [...row.querySelectorAll('.topic-option-name, .topic-option-count')].map((part) => part.textContent),
    );
    expect(rows).toEqual([['Alpha', '3'], ['Beta', '1']]);
    expect(query('.merge-picker').getAttribute('aria-label')).toBe('Choose a topic');
    expect(query('.merge-search-input').getAttribute('aria-label')).toBe('Search topics to link');
  });

  it('reports the chosen topic and marks it once the owner selects it', () => {
    element.querySelectorAll<HTMLButtonElement>('.merge-target')[1].click();
    fixture.detectChanges();

    expect(host.selectedId()).toBe('b');
    expect(element.querySelectorAll('.merge-target')[1].classList).toContain('is-selected');
  });

  it('keeps confirm disabled until its owner allows it, then reports the confirmation', () => {
    const confirm = query<HTMLButtonElement>('.merge-picker-confirm');
    expect(confirm.disabled).toBe(true);
    confirm.click();
    expect(host.confirmed).toBe(0);

    host.selectedId.set('a');
    fixture.detectChanges();
    confirm.click();

    expect(host.confirmed).toBe(1);
  });

  it('reports the search text as it is typed', () => {
    const input = query<HTMLInputElement>('.merge-search-input');
    input.value = 'al';
    input.dispatchEvent(new Event('input'));

    expect(host.query()).toBe('al');
  });

  it('closes from the close button, Cancel and Escape in the search box', () => {
    query<HTMLButtonElement>('.merge-picker-close').click();
    query<HTMLButtonElement>('.merge-picker-cancel').click();
    query('.merge-search-input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));

    expect(host.closed).toBe(3);
  });

  it('says so when nothing matches', () => {
    host.candidates.set([]);
    fixture.detectChanges();

    expect(element.querySelector('.merge-target')).toBeNull();
    expect(query('nostos-empty-state').textContent?.trim()).toBe('No topics match.');
  });

  it('shows the linking hint and the inspector style only when asked', () => {
    expect(element.querySelector('.merge-picker-hint')).toBeNull();
    expect(query('.merge-picker').classList).not.toContain('note-inspector-picker');

    host.linkHint.set(true);
    host.variant.set('inspector');
    fixture.detectChanges();

    expect(query('.merge-picker-hint').textContent).toContain('[[Topic]]');
    expect(query('.merge-picker').classList).toContain('note-inspector-picker');
  });

  const press = (key: string) => {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    query('.merge-search-input').dispatchEvent(event);
    fixture.detectChanges();
    return event;
  };

  it('Enter in the search field chooses the first match, then continues', () => {
    press('Enter');
    expect(host.selectedId()).toBe('a');
    expect(host.confirmed).toBe(0);

    press('Enter');
    expect(host.confirmed).toBe(1);
  });

  it('Enter does nothing when nothing matches', () => {
    host.candidates.set([]);
    fixture.detectChanges();
    press('Enter');
    expect(host.selectedId()).toBeNull();
    expect(host.confirmed).toBe(0);
  });

  it('Escape in the search field closes the picker and is consumed', () => {
    const event = press('Escape');
    expect(host.closed).toBe(1);
    expect(event.defaultPrevented).toBe(true);
  });

  it('places projected content between the list and the actions', () => {
    const children = [...query('.merge-picker').children].map((child) => child.className);
    expect(children.indexOf('projected')).toBe(children.indexOf('merge-target-list') + 1);
    expect(children.indexOf('merge-picker-actions')).toBe(children.indexOf('projected') + 1);
  });
});
