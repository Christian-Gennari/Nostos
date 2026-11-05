import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { BrainStateService } from '../../brain/brain-state.service';
import { TopicInputComponent } from './topic-input.component';

describe('TopicInputComponent topic linking', () => {
  let fixture: ComponentFixture<TopicInputComponent>;
  let component: TopicInputComponent;
  let textarea: HTMLTextAreaElement;
  let changed: ReturnType<typeof vi.fn>;
  const ensureTopicsLoaded = vi.fn();

  beforeEach(async () => {
    ensureTopicsLoaded.mockClear();
    await TestBed.configureTestingModule({
      imports: [TopicInputComponent],
      providers: [
        {
          provide: BrainStateService,
          useValue: {
            topics: signal([
              { id: 'freedom', name: 'Freedom', usageCount: 4 },
              { id: 'power', name: 'Power', usageCount: 2 },
            ]),
            ensureTopicsLoaded: ensureTopicsLoaded,
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(TopicInputComponent);
    component = fixture.componentInstance;
    changed = vi.fn();
    component.registerOnChange(changed as (value: string) => void);
    textarea = fixture.nativeElement.querySelector('textarea');
  });

  // The suggestions render into an overlay on the document, not inside the fixture.
  const inPicker = <T extends Element>(selector: string) => document.querySelector<T>(selector);
  const items = () => [...document.querySelectorAll<HTMLButtonElement>('.item')];

  function type(value: string): void {
    component.value = value;
    textarea.value = value;
    textarea.setSelectionRange(value.length, value.length);
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
  }

  const key = (name: string) => {
    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true }));
    fixture.detectChanges();
  };

  it('takes its topics from the Brain\'s state and makes no request of its own', () => {
    fixture.detectChanges();

    expect(ensureTopicsLoaded).toHaveBeenCalledTimes(1);
  });

  it('has no button of its own: the composer\'s + Topic adds a topic as a pill', () => {
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('button')).toBeNull();
  });

  it('typing [[ shows suggestions without a second search field, and Enter completes the link', () => {
    type('Think [[');

    expect(inPicker('.suggest-panel')).toBeTruthy();
    expect(items().map((item) => item.textContent?.trim())).toEqual(['Freedom4', 'Power2']);
    expect(inPicker('[role="dialog"]')).toBeNull();

    type('Think [[Fre');
    key('Enter');

    expect(component.value).toBe('Think [[Freedom]] ');
    expect(changed).toHaveBeenLastCalledWith('Think [[Freedom]] ');
    expect(inPicker('.suggest-panel')).toBeNull();
  });

  it('closes the [[ suggestions when the textarea loses focus', () => {
    type('Think [[');
    expect(inPicker('.suggest-panel')).toBeTruthy();

    textarea.dispatchEvent(new FocusEvent('blur'));
    fixture.detectChanges();

    expect(inPicker('.suggest-panel')).toBeNull();
    expect(component.value).toBe('Think [[');
  });

  it('reaches the create row with the arrow keys and links a new topic with Enter', () => {
    type('[[Free');

    // "Free" matches Freedom but is not itself a topic, so both rows are offered.
    expect(items().length).toBe(2);
    expect(textarea.getAttribute('aria-activedescendant')).toBe(items()[0].id);

    key('ArrowDown');

    expect(items()[1].classList.contains('create-item')).toBe(true);
    expect(items()[1].getAttribute('aria-selected')).toBe('true');
    expect(textarea.getAttribute('aria-activedescendant')).toBe(items()[1].id);

    key('Enter');

    expect(component.value).toBe('[[Free]] ');
  });

  it('does not offer to create a topic that already exists', () => {
    type('[[power');

    expect(inPicker('.create-item')).toBeNull();
    expect(items().length).toBe(1);
  });

  it('clicking a suggestion links it where the [[ was typed', () => {
    type('Think [[Pow');
    items()[0].click();

    expect(component.value).toBe('Think [[Power]] ');
  });

  it('Ctrl+Enter and Cmd+Enter ask to save', () => {
    const submitted = vi.fn();
    component.submitTrigger.subscribe(submitted);
    fixture.detectChanges();

    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, bubbles: true }));
    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', metaKey: true, bubbles: true }));

    expect(submitted).toHaveBeenCalledTimes(2);
  });
});
