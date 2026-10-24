import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';

import { TopicPickerPopoverComponent } from './topic-picker-popover.component';

@Component({
  standalone: true,
  imports: [TopicPickerPopoverComponent],
  template: `
    <button class="origin" (click)="open.set(true)">Add topic</button>
    @if (open()) {
      <app-topic-picker-popover [topics]="[]" (chosen)="open.set(false)" (closed)="open.set(false)" />
    }
  `,
})
class Host {
  readonly open = signal(false);
}

describe('TopicPickerPopoverComponent focus', () => {
  const tick = () => new Promise((resolve) => setTimeout(resolve, 5));

  async function setup() {
    await TestBed.configureTestingModule({ imports: [Host] }).compileComponents();
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const origin = fixture.nativeElement.querySelector('.origin') as HTMLButtonElement;
    origin.focus();
    fixture.componentInstance.open.set(true);
    await fixture.whenStable();
    await tick();
    return { fixture, origin };
  }

  it('takes focus into its field on open', async () => {
    const { fixture } = await setup();
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('input'));
  });

  it('gives focus back to the control that opened it when Escape closes it', async () => {
    const { fixture, origin } = await setup();
    const field = fixture.nativeElement.querySelector('input') as HTMLInputElement;

    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('input')).toBeNull();
    expect(document.activeElement).toBe(origin);
  });

  it('gives focus back after a topic is chosen too', async () => {
    const { fixture, origin } = await setup();
    const field = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    field.value = 'Stoicism';
    field.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await fixture.whenStable();

    expect(document.activeElement).toBe(origin);
  });
});
