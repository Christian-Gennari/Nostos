import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BlockKind, EditorSelectionBubbleComponent } from './editor-selection-bubble.component';

describe('EditorSelectionBubbleComponent', () => {
  let fixture: ComponentFixture<EditorSelectionBubbleComponent>;
  let component: EditorSelectionBubbleComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [EditorSelectionBubbleComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(EditorSelectionBubbleComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    fixture?.destroy();
  });

  it('renders formatting controls and emits narrow commands under OnPush', () => {
    fixture.componentRef.setInput('isBold', true);
    fixture.componentRef.setInput('activeBlock', 'h2' as BlockKind);
    fixture.componentRef.setInput('isLink', true);
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[aria-label="Bold"]')?.getAttribute('aria-pressed')).toBe('true');
    expect(el.querySelector('[aria-label="Heading 2"]')?.getAttribute('aria-pressed')).toBe('true');

    const emitted: string[] = [];
    component.toggleBold.subscribe(() => emitted.push('bold'));
    component.toggleItalic.subscribe(() => emitted.push('italic'));
    component.toggleBlock.subscribe((b) => emitted.push(`block:${b}`));
    component.addFootnote.subscribe(() => emitted.push('footnote'));
    component.openUrlPrompt.subscribe((k) => emitted.push(`prompt:${k}`));

    (el.querySelector('[aria-label="Bold"]') as HTMLButtonElement).click();
    (el.querySelector('[aria-label="Italic"]') as HTMLButtonElement).click();
    (el.querySelector('[aria-label="Heading 3"]') as HTMLButtonElement).click();
    (el.querySelector('[aria-label="Add footnote"]') as HTMLButtonElement).click();
    (el.querySelector('[aria-label="Link"]') as HTMLButtonElement).click();

    expect(emitted).toEqual(['bold', 'italic', 'block:h3', 'footnote', 'prompt:link']);
  });

  it('focuses urlInput after timeout when the URL prompt opens', () => {
    vi.useFakeTimers();
    fixture.componentRef.setInput('urlPrompt', 'link');
    fixture.componentRef.setInput('urlValue', 'https://example.com');
    fixture.detectChanges();

    const input = fixture.nativeElement.querySelector('.editor-url-input') as HTMLInputElement;
    expect(input).toBeTruthy();
    const focusSpy = vi.spyOn(input, 'focus');

    vi.runAllTimers();
    expect(focusSpy).toHaveBeenCalledTimes(1);
  });

  it('clears the urlInput focus timer when the URL prompt closes before firing', () => {
    vi.useFakeTimers();
    fixture.componentRef.setInput('urlPrompt', 'link');
    fixture.detectChanges();

    const input = fixture.nativeElement.querySelector('.editor-url-input') as HTMLInputElement;
    const focusSpy = vi.spyOn(input, 'focus');

    fixture.componentRef.setInput('urlPrompt', null);
    fixture.detectChanges();
    vi.runAllTimers();

    expect(focusSpy).not.toHaveBeenCalled();
  });

  it('clears the urlInput focus timer on destroy', () => {
    vi.useFakeTimers();
    fixture.componentRef.setInput('urlPrompt', 'link');
    fixture.detectChanges();

    const input = fixture.nativeElement.querySelector('.editor-url-input') as HTMLInputElement;
    const focusSpy = vi.spyOn(input, 'focus');

    fixture.destroy();
    vi.runAllTimers();

    expect(focusSpy).not.toHaveBeenCalled();
  });
});
