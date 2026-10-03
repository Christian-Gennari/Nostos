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

  function setValue(value: string, start = value.length, end = start): void {
    component.value = value;
    textarea.value = value;
    textarea.setSelectionRange(start, end);
    fixture.detectChanges();
  }

  it('exposes a visible Link a topic action and prefills it from selected text', () => {
    setValue('Power matters', 0, 5);

    (fixture.nativeElement.querySelector('.topic-link-action') as HTMLButtonElement).click();
    fixture.detectChanges();

    const picker = fixture.nativeElement.querySelector('[role="dialog"][aria-label="Link a topic"]');
    const search = fixture.nativeElement.querySelector(
      'input[aria-label="Find or create a topic"]'
    ) as HTMLInputElement;

    expect(picker).toBeTruthy();
    expect(search.value).toBe('Power');

    (fixture.nativeElement.querySelector('.item') as HTMLButtonElement).click();
    expect(component.value).toBe('[[Power]] matters');
    expect(changed).toHaveBeenLastCalledWith('[[Power]] matters');
  });

  it('inserts the same canonical wikilink at the caret when no text is selected', () => {
    setValue('Think about it', 6);

    (fixture.nativeElement.querySelector('.topic-link-action') as HTMLButtonElement).click();
    fixture.detectChanges();

    const search = fixture.nativeElement.querySelector(
      'input[aria-label="Find or create a topic"]'
    ) as HTMLInputElement;
    search.value = 'Freedom';
    search.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.item') as HTMLButtonElement).click();

    expect(component.value).toBe('Think [[Freedom]] about it');
  });

  it('typing [[ opens the same picker and Enter completes through the same insertion path', () => {
    setValue('Think [[');
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[role="dialog"][aria-label="Link a topic"]')
    ).toBeTruthy();
    expect(
      (fixture.nativeElement.querySelector(
        'input[aria-label="Find or create a topic"]'
      ) as HTMLInputElement).value
    ).toBe('');

    textarea.value = 'Think [[Fre';
    textarea.setSelectionRange(textarea.value.length, textarea.value.length);
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.detectChanges();

    expect(component.value).toBe('Think [[Freedom]] ');
    expect(changed).toHaveBeenLastCalledWith('Think [[Freedom]] ');
  });

  it('can create a new canonical topic link from the visible picker', () => {
    setValue('A thought worth returning to', 2, 9);

    (fixture.nativeElement.querySelector('.topic-link-action') as HTMLButtonElement).click();
    fixture.detectChanges();

    const useQuery = [...fixture.nativeElement.querySelectorAll('.create-item')].find(
      (button) => button.textContent?.includes('thought')
    ) as HTMLButtonElement | undefined;
    expect(useQuery).toBeTruthy();
    useQuery!.click();

    expect(component.value).toBe('A [[thought]] worth returning to');
  });

  it('cancels without mutating the note and restores the previous selection', async () => {
    setValue('Power matters', 0, 5);
    const original = component.value;

    (fixture.nativeElement.querySelector('.topic-link-action') as HTMLButtonElement).click();
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('.picker-cancel') as HTMLButtonElement).click();
    await new Promise((resolve) => setTimeout(resolve, 10));

    expect(component.value).toBe(original);
    expect(changed).not.toHaveBeenCalled();
    expect(textarea.selectionStart).toBe(0);
    expect(textarea.selectionEnd).toBe(5);
  });

  it('uses real buttons for pointer and touch selection targets', () => {
    setValue('Power', 0, 5);
    (fixture.nativeElement.querySelector('.topic-link-action') as HTMLButtonElement).click();
    fixture.detectChanges();

    const action = fixture.nativeElement.querySelector('.topic-link-action') as HTMLButtonElement;
    const option = fixture.nativeElement.querySelector('.item') as HTMLButtonElement;
    const cancel = fixture.nativeElement.querySelector('.picker-cancel') as HTMLButtonElement;

    expect(action.tagName).toBe('BUTTON');
    expect(option.tagName).toBe('BUTTON');
    expect(cancel.tagName).toBe('BUTTON');
    expect(action.type).toBe('button');
    expect(option.type).toBe('button');
  });
});
