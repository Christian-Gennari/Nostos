import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';

import { ModalStack } from '../../ui/modal-shell/modal-stack.service';
import { DrawerFocusDirective } from './drawer-focus.directive';

@Component({
  standalone: true,
  imports: [DrawerFocusDirective],
  template: `
    <div [appDrawerFocus]="open()" drawerPanel=".panel" (drawerDismiss)="dismissed = dismissed + 1; open.set(false)">
      <button class="opener" (click)="open.set(true)">Open</button>
      <nav class="panel">
        <a href="#one" class="first">One</a>
        <a href="#two" class="second" aria-current="page">Two</a>
      </nav>
    </div>
  `,
})
class DrawerHost {
  readonly open = signal(false);
  dismissed = 0;
}

describe('DrawerFocusDirective', () => {
  const tick = () => new Promise((resolve) => setTimeout(resolve, 5));
  // jsdom has no matchMedia.
  const media = (matches: boolean) =>
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      writable: true,
      value: (query: string) => ({ matches, media: query }) as unknown as MediaQueryList,
    });

  afterEach(() => {
    delete (window as { matchMedia?: unknown }).matchMedia;
  });

  async function setup(isDrawer = true) {
    media(isDrawer);
    await TestBed.configureTestingModule({ imports: [DrawerHost] }).compileComponents();
    const fixture = TestBed.createComponent(DrawerHost);
    await fixture.whenStable();
    return fixture;
  }

  const escape = () => {
    const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    document.dispatchEvent(event);
    return event;
  };

  it('moves focus to the current place on open and back to the opener on close', async () => {
    const fixture = await setup();
    const opener = fixture.nativeElement.querySelector('.opener') as HTMLButtonElement;
    opener.focus();

    fixture.componentInstance.open.set(true);
    await fixture.whenStable();
    await tick();
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('.second'));

    fixture.componentInstance.open.set(false);
    await fixture.whenStable();
    await tick();
    expect(document.activeElement).toBe(opener);
  });

  it('closes on Escape and consumes the key', async () => {
    const fixture = await setup();
    fixture.componentInstance.open.set(true);
    await fixture.whenStable();

    const event = escape();

    expect(fixture.componentInstance.dismissed).toBe(1);
    expect(event.defaultPrevented).toBe(true);
  });

  it('leaves Escape to a control that used it, and to a dialog above the drawer', async () => {
    const fixture = await setup();
    fixture.componentInstance.open.set(true);
    await fixture.whenStable();

    const used = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    used.preventDefault();
    document.dispatchEvent(used);
    expect(fixture.componentInstance.dismissed).toBe(0);

    const release = TestBed.inject(ModalStack).register({});
    escape();
    expect(fixture.componentInstance.dismissed).toBe(0);
    release();
  });

  it('does nothing where the sidebar is a column, not a drawer', async () => {
    const fixture = await setup(false);
    const opener = fixture.nativeElement.querySelector('.opener') as HTMLButtonElement;
    opener.focus();

    fixture.componentInstance.open.set(true);
    await fixture.whenStable();
    await tick();
    expect(document.activeElement).toBe(opener);

    escape();
    expect(fixture.componentInstance.dismissed).toBe(0);
  });
});
