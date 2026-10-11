import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { TopicsService } from '../../core/services/topics.service';
import { TopicInputComponent } from './topic-input.component';

describe('TopicInputComponent topic linking', () => {
  let fixture: ComponentFixture<TopicInputComponent>;
  let component: TopicInputComponent;
  let textarea: HTMLTextAreaElement;
  let changed: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TopicInputComponent],
      providers: [
        {
          provide: TopicsService,
          useValue: {
            list: () =>
              of([
                { id: 'freedom', name: 'Freedom', usageCount: 4 },
                { id: 'power', name: 'Power', usageCount: 2 },
              ]),
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

  // The picker renders into an overlay on the document, not inside the fixture.
  const inPicker = <T extends Element>(selector: string) => document.querySelector<T>(selector);
  const dialog = () => inPicker<HTMLElement>('[role="dialog"]');
  const search = () => inPicker<HTMLInputElement>('input[aria-label="Find or create a topic"]');
  const items = () => [...document.querySelectorAll<HTMLButtonElement>('.item')];

  function openDialog(): void {
    (fixture.nativeElement.querySelector('.topic-link-action') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  function setValue(value: string, start = value.length, end = start): void {
    component.value = value;
    textarea.value = value;
    textarea.setSelectionRange(start, end);
    fixture.detectChanges();
  }

  it('opens a labelled modal dialog from Link a topic, prefilled from the selected text', () => {
    setValue('Power matters', 0, 5);
    openDialog();

    expect(dialog()).toBeTruthy();
    expect(dialog()!.getAttribute('aria-modal')).toBe('true');
    const title = document.getElementById(dialog()!.getAttribute('aria-labelledby')!);
    expect(title?.textContent?.trim()).toBe('Link a topic');
    expect(search()!.value).toBe('Power');

    items()[0].click();
    fixture.detectChanges();

    expect(component.value).toBe('[[Power]] matters');
    expect(changed).toHaveBeenLastCalledWith('[[Power]] matters');
    expect(dialog()).toBeNull();
  });

  it('inserts the same canonical wikilink at the caret when no text is selected', () => {
    setValue('Think about it', 6);
    openDialog();

    search()!.value = 'Freedom';
    search()!.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();

    items()[0].click();

    expect(component.value).toBe('Think [[Freedom]] about it');
  });

  it('typing [[ shows suggestions without a second search field, and Enter completes the link', () => {
    setValue('Think [[');
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();

    expect(inPicker('.suggest-panel')).toBeTruthy();
    expect(items().map((item) => item.textContent?.trim())).toEqual(['Freedom4', 'Power2']);
    expect(dialog()).toBeNull();
    expect(search()).toBeNull();

    textarea.value = 'Think [[Fre';
    textarea.setSelectionRange(textarea.value.length, textarea.value.length);
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.detectChanges();

    expect(component.value).toBe('Think [[Freedom]] ');
    expect(changed).toHaveBeenLastCalledWith('Think [[Freedom]] ');
    expect(inPicker('.suggest-panel')).toBeNull();
  });

  it('closes the [[ suggestions when the textarea loses focus', () => {
    setValue('Think [[');
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    expect(inPicker('.suggest-panel')).toBeTruthy();

    textarea.dispatchEvent(new FocusEvent('blur'));
    fixture.detectChanges();

    expect(inPicker('.suggest-panel')).toBeNull();
    expect(component.value).toBe('Think [[');
  });

  it('can create a new canonical topic link from the dialog', () => {
    setValue('A thought worth returning to', 2, 9);
    openDialog();

    const create = inPicker<HTMLButtonElement>('.create-item');
    expect(create?.textContent).toContain('Create “thought”');
    create!.click();

    expect(component.value).toBe('A [[thought]] worth returning to');
  });

  it('reaches the create row with the arrow keys and links it with Enter', () => {
    setValue('Free will', 0, 4);
    openDialog();

    // "Free" matches Freedom but is not itself a topic, so both rows are offered.
    expect(items().length).toBe(2);
    expect(search()!.getAttribute('aria-activedescendant')).toBe(items()[0].id);

    search()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    fixture.detectChanges();

    expect(items()[1].classList.contains('create-item')).toBe(true);
    expect(items()[1].getAttribute('aria-selected')).toBe('true');
    expect(search()!.getAttribute('aria-activedescendant')).toBe(items()[1].id);

    search()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

    expect(component.value).toBe('[[Free]] will');
  });

  it('does not offer to create a topic that already exists', () => {
    setValue('power', 0, 5);
    openDialog();

    expect(inPicker('.create-item')).toBeNull();
    expect(items().length).toBe(1);
  });

  it('closes without mutating the note and restores the previous selection', async () => {
    setValue('Power matters', 0, 5);
    const original = component.value;

    openDialog();
    inPicker<HTMLButtonElement>('.picker-close')!.click();
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 10));

    expect(dialog()).toBeNull();
    expect(component.value).toBe(original);
    expect(changed).not.toHaveBeenCalled();
    expect(textarea.selectionStart).toBe(0);
    expect(textarea.selectionEnd).toBe(5);
  });

  it('Escape closes only the dialog, without reaching listeners behind it', () => {
    setValue('Power', 0, 5);
    openDialog();
    const behind = vi.fn();
    document.addEventListener('keydown', behind);

    search()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    document.removeEventListener('keydown', behind);

    expect(dialog()).toBeNull();
    expect(behind).not.toHaveBeenCalled();
    expect(component.value).toBe('Power');
  });

  it('uses real buttons for pointer and touch selection targets', () => {
    setValue('Power', 0, 5);
    openDialog();

    const action = fixture.nativeElement.querySelector('.topic-link-action') as HTMLButtonElement;
    const option = items()[0];
    const close = inPicker<HTMLButtonElement>('.picker-close')!;

    expect(action.tagName).toBe('BUTTON');
    expect(option.tagName).toBe('BUTTON');
    expect(close.tagName).toBe('BUTTON');
    expect(action.type).toBe('button');
    expect(option.type).toBe('button');
  });
});
