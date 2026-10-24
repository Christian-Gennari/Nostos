import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';

import { ModalShell } from './modal-shell.component';
import { ModalStack } from './modal-stack.service';

@Component({
  standalone: true,
  imports: [ModalShell],
  template: `
    <app-modal-shell [isOpen]="outerOpen()" (closed)="outerClosed()">
      <input class="outer-field" />
      <button class="outer-close">Close</button>
    </app-modal-shell>
    <app-modal-shell [isOpen]="innerOpen()" [layer]="120" (closed)="innerClosed()">
      <input class="inner-field" />
    </app-modal-shell>
  `,
})
class StackHost {
  outerOpen = signal(true);
  innerOpen = signal(false);
  outerClosed = vi.fn(() => this.outerOpen.set(false));
  innerClosed = vi.fn(() => this.innerOpen.set(false));
}

const escape = (target: EventTarget = document) => {
  const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
  target.dispatchEvent(event);
  return event;
};

describe('Escape across stacked layers', () => {
  async function setup() {
    await TestBed.configureTestingModule({ imports: [StackHost] }).compileComponents();
    const fixture = TestBed.createComponent(StackHost);
    await fixture.whenStable();
    return fixture;
  }

  it('closes only the top shell when two are open', async () => {
    const fixture = await setup();
    const host = fixture.componentInstance;
    host.innerOpen.set(true);
    await fixture.whenStable();

    escape();
    await fixture.whenStable();
    expect(host.innerClosed).toHaveBeenCalledTimes(1);
    expect(host.outerClosed).not.toHaveBeenCalled();

    escape();
    await fixture.whenStable();
    expect(host.outerClosed).toHaveBeenCalledTimes(1);
  });

  it('consumes the key: preventDefault and stopPropagation', async () => {
    await setup();
    const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    const stop = vi.spyOn(event, 'stopPropagation');

    document.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(stop).toHaveBeenCalled();
  });

  it('leaves a key a focused control already used (a dropdown closing) alone', async () => {
    const fixture = await setup();
    const field = fixture.nativeElement.querySelector('.outer-field') as HTMLElement;
    field.addEventListener('keydown', (e) => e.preventDefault());

    escape(field);
    await fixture.whenStable();

    expect(fixture.componentInstance.outerClosed).not.toHaveBeenCalled();
  });

  it('reports whether any layer is open, so surfaces behind can stand down', async () => {
    const fixture = await setup();
    const stack = TestBed.inject(ModalStack);
    expect(stack.hasOpen()).toBe(true);

    fixture.componentInstance.outerOpen.set(false);
    await fixture.whenStable();
    expect(stack.hasOpen()).toBe(false);
  });
});
