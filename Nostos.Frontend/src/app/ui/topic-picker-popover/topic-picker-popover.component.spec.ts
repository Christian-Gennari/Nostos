import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';

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

const TOPICS = [
  { id: 't1', name: 'Memory', usageCount: 3 },
  { id: 't2', name: 'Stoicism', usageCount: 1 },
] as never;

function phone(matches: boolean): void {
  vi.stubGlobal(
    'matchMedia',
    (query: string) =>
      ({ matches, media: query, addEventListener: () => undefined, removeEventListener: () => undefined }) as unknown as MediaQueryList,
  );
}

async function create() {
  await TestBed.configureTestingModule({ imports: [TopicPickerPopoverComponent] }).compileComponents();
  const fixture = TestBed.createComponent(TopicPickerPopoverComponent);
  fixture.componentRef.setInput('topics', TOPICS);
  await fixture.whenStable();
  return fixture;
}

describe('TopicPickerPopoverComponent', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('is a popover on a wide screen', async () => {
    phone(false);
    const fixture = await create();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.picker')).not.toBeNull();
    expect(el.querySelector('app-modal-shell')).toBeNull();
    expect(el.querySelectorAll('[role="option"]').length).toBe(3 - 1);
  });

  it('is a sheet on a phone, on an overlay of its own, with the field and the list inside it', async () => {
    phone(true);
    const fixture = await create();
    const el = fixture.nativeElement as HTMLElement;
    // Not inside the host: the caller's overlay pane is transformed, which would
    // break the shell's fixed backdrop.
    expect(el.querySelector('.picker')).toBeNull();
    expect(el.querySelector('app-modal-shell')).toBeNull();
    const shell = document.querySelector('.cdk-overlay-container app-modal-shell');
    expect(shell).not.toBeNull();
    expect(shell!.querySelector('input[role="combobox"]')).not.toBeNull();
    expect(shell!.querySelectorAll('[role="option"]').length).toBe(2);

    fixture.destroy();
    expect(document.querySelector('app-modal-shell')).toBeNull();
  });

  it('closes from the sheet Close button and chooses a topic on tap', async () => {
    phone(true);
    const fixture = await create();
    const closed = vi.fn();
    const chosen = vi.fn();
    fixture.componentInstance.closed.subscribe(closed);
    fixture.componentInstance.chosen.subscribe(chosen);
    const shell = document.querySelector('app-modal-shell') as HTMLElement;

    (shell.querySelector('[role="option"]') as HTMLElement).click();
    expect(chosen).toHaveBeenCalledWith('Memory');

    (shell.querySelector('button[aria-label="Close"]') as HTMLElement).click();
    expect(closed).toHaveBeenCalledTimes(1);
    fixture.destroy();
  });
});
