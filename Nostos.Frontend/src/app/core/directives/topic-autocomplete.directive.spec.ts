import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { beforeEach, describe, expect, it, vi } from 'vitest';

import { TopicAutocompleteDirective } from './topic-autocomplete.directive';
import { TopicAutocompleteService } from '../../ui/topic-autocomplete-panel/topic-autocomplete.service';

@Component({
  standalone: true,
  imports: [TopicAutocompleteDirective],
  template: `<textarea noteAutocomplete (insertTopicName)="inserted.push($event)"></textarea>`,
})
class Host {
  readonly inserted: string[] = [];
}

describe('TopicAutocompleteDirective', () => {
  let fixture: ComponentFixture<Host>;
  let textarea: HTMLTextAreaElement;
  let listed: string[];
  let active: string | null;
  let service: {
    pickerOpen: ReturnType<typeof signal<boolean>>;
    activeName: () => string | null;
    update: ReturnType<typeof vi.fn>;
    clear: ReturnType<typeof vi.fn>;
    moveDown: ReturnType<typeof vi.fn>;
    moveUp: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    active = null;
    service = {
      pickerOpen: signal(false),
      activeName: () => active,
      update: vi.fn(),
      clear: vi.fn(() => service.pickerOpen.set(false)),
      moveDown: vi.fn(),
      moveUp: vi.fn(),
    };
    await TestBed.configureTestingModule({
      imports: [Host],
      providers: [{ provide: TopicAutocompleteService, useValue: service }],
    }).compileComponents();

    fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    textarea = fixture.nativeElement.querySelector('textarea');
    listed = fixture.componentInstance.inserted;
  });

  function type(value: string, cursor = value.length): void {
    textarea.value = value;
    textarea.setSelectionRange(cursor, cursor);
    textarea.dispatchEvent(new Event('input'));
  }

  function key(name: string): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key: name, cancelable: true, bubbles: true });
    textarea.dispatchEvent(event);
    return event;
  }

  it('offers suggestions for the words before the cursor', () => {
    type('A note about [[virt');

    expect(service.update).toHaveBeenCalledWith('A note about [[virt', 19);
  });

  it('leaves the keyboard alone while no suggestion session is open', () => {
    expect(service.pickerOpen()).toBe(false);

    const escape = key('Escape');
    key('ArrowDown');
    key('ArrowUp');
    key('Enter');

    expect(escape.defaultPrevented).toBe(false);
    expect(service.clear).not.toHaveBeenCalled();
    expect(service.moveDown).not.toHaveBeenCalled();
    expect(service.moveUp).not.toHaveBeenCalled();
    expect(listed).toEqual([]);
  });

  it('closes the suggestions on Escape, and nothing around them', () => {
    service.pickerOpen.set(true);
    const reachedDocument = vi.fn();
    document.addEventListener('keydown', reachedDocument);

    const event = key('Escape');
    document.removeEventListener('keydown', reachedDocument);

    expect(event.defaultPrevented).toBe(true);
    expect(reachedDocument).not.toHaveBeenCalled();
    expect(service.clear).toHaveBeenCalledTimes(1);
  });

  it('moves through the suggestions with the arrow keys', () => {
    service.pickerOpen.set(true);

    const down = key('ArrowDown');
    expect(down.defaultPrevented).toBe(true);
    expect(service.moveDown).toHaveBeenCalledTimes(1);

    const up = key('ArrowUp');
    expect(up.defaultPrevented).toBe(true);
    expect(service.moveUp).toHaveBeenCalledTimes(1);
  });

  it('inserts the highlighted topic on Enter and ends the session', () => {
    service.pickerOpen.set(true);
    active = 'Virtue';

    const event = key('Enter');

    expect(event.defaultPrevented).toBe(true);
    expect(listed).toEqual(['Virtue']);
    expect(service.clear).toHaveBeenCalledTimes(1);
  });

  it('does nothing on Enter when no suggestion is highlighted', () => {
    service.pickerOpen.set(true);
    active = null;

    const event = key('Enter');

    expect(event.defaultPrevented).toBe(false);
    expect(listed).toEqual([]);
    expect(service.clear).not.toHaveBeenCalled();
  });

  it('ends the session when the textarea loses focus, only if one is open', () => {
    textarea.dispatchEvent(new Event('blur'));
    expect(service.clear).not.toHaveBeenCalled();

    service.pickerOpen.set(true);
    textarea.dispatchEvent(new Event('blur'));
    expect(service.clear).toHaveBeenCalledTimes(1);
  });
});
