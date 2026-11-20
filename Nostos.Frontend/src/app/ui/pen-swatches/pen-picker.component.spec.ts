import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { HighlightColour } from '../../reader/highlight-colours';
import { PenPickerComponent } from './pen-picker.component';

@Component({
  standalone: true,
  imports: [PenPickerComponent],
  template: `
    <app-pen-picker
      [value]="value()"
      [disabled]="disabled()"
      triggerTestId="pen-picker"
      testIdPrefix="pen-option-"
      (chosen)="value.set($event)"
    />
  `,
})
class HostComponent {
  readonly value = signal<HighlightColour>('sage');
  readonly disabled = signal(false);
}

describe('PenPickerComponent', () => {
  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
    TestBed.resetTestingModule();
  });

  function setup() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const trigger = () => element.querySelector<HTMLButtonElement>('[data-testid="pen-picker"]')!;
    return { fixture, trigger };
  }

  it('announces the active colour and opens radio options for all four pens', () => {
    const { fixture, trigger } = setup();
    expect(trigger().getAttribute('aria-label')).toBe('Highlight colour: Sage');
    expect(trigger().getAttribute('title')).toBe('Highlight colour: Sage');

    trigger().click();
    fixture.detectChanges();

    const options = Array.from(
      document.querySelectorAll<HTMLButtonElement>('[data-testid^="pen-option-"]'),
    );
    expect(options.map((o) => o.dataset['testid'])).toEqual([
      'pen-option-sage',
      'pen-option-amber',
      'pen-option-blue',
      'pen-option-rose',
    ]);
    expect(options[0].getAttribute('aria-checked')).toBe('true');

    options[3].click();
    fixture.detectChanges();

    expect(fixture.componentInstance.value()).toBe('rose');
    expect(trigger().getAttribute('aria-label')).toBe('Highlight colour: Rose');
  });

  it('disables the trigger while a save is in flight', () => {
    const { fixture, trigger } = setup();
    fixture.componentInstance.disabled.set(true);
    fixture.detectChanges();

    expect(trigger().disabled).toBe(true);
  });
});
