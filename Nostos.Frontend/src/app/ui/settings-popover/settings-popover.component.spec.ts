import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';

import {
  SETTINGS_POPOVER_SHEET_QUERY,
  SettingsPopoverComponent,
  type SettingsPopoverDismissal,
} from './settings-popover.component';

@Component({
  standalone: true,
  imports: [SettingsPopoverComponent],
  template: `
    <button #trigger type="button" id="trigger" (click)="open.set(!open())">Aa</button>
    <button type="button" id="elsewhere">Elsewhere</button>
    @if (open()) {
      <nostos-settings-popover [origin]="trigger" label="View settings" [heading]="heading" testId="popover" (dismissed)="dismiss($event)">
        <button type="button" id="first">First</button>
        <button type="button" id="second" (keydown.escape)="inner && $event.preventDefault()">Second</button>
      </nostos-settings-popover>
    }
  `,
})
class Host {
  readonly open = signal(false);
  readonly dismissals: SettingsPopoverDismissal[] = [];
  inner = false;
  heading: string | null = null;
  dismiss(reason: SettingsPopoverDismissal): void {
    this.dismissals.push(reason);
    this.open.set(false);
  }
}

describe('SettingsPopoverComponent', () => {
  let fixture: ComponentFixture<Host>;
  let sheet: boolean;
  let breakpoint: Subject<{ matches: boolean }>;

  const trigger = () => fixture.nativeElement.querySelector('#trigger') as HTMLButtonElement;
  const panel = () => document.querySelector('[data-testid="popover"]') as HTMLElement | null;
  const pane = () => panel()?.closest('.cdk-overlay-pane') as HTMLElement | null;
  const flush = () => new Promise<void>((resolve) => queueMicrotask(resolve));

  async function setup(phone = false): Promise<void> {
    sheet = phone;
    breakpoint = new Subject();
    await TestBed.configureTestingModule({
      imports: [Host],
      providers: [
        {
          provide: BreakpointObserver,
          useValue: {
            isMatched: (query: string) => query === SETTINGS_POPOVER_SHEET_QUERY && sheet,
            observe: () => breakpoint,
          },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
  }

  async function open(): Promise<void> {
    trigger().focus();
    trigger().click();
    fixture.detectChanges();
    await flush();
  }

  afterEach(() => {
    fixture.destroy();
    document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
  });

  it('shows the shared heading with one close button that says so', async () => {
    await setup();
    fixture.componentInstance.heading = 'View settings';
    await open();

    expect(panel()!.classList).toContain('nostos-surface');
    expect(panel()!.querySelector('.nostos-surface-heading h2')?.textContent).toBe('View settings');
    (panel()!.querySelector('.nostos-surface-heading button') as HTMLButtonElement).click();
    expect(fixture.componentInstance.dismissals).toEqual(['close']);
  });

  it('has no heading row unless asked', async () => {
    await setup();
    await open();
    expect(panel()!.querySelector('.nostos-surface-heading')).toBeNull();
  });

  it('opens on the overlay as a labelled, non-modal dialog and takes focus', async () => {
    await setup();
    await open();
    expect(panel()).not.toBeNull();
    expect(fixture.nativeElement.contains(panel())).toBe(false);
    expect(panel()!.getAttribute('role')).toBe('dialog');
    expect(panel()!.getAttribute('aria-modal')).toBe('false');
    expect(panel()!.getAttribute('aria-label')).toBe('View settings');
    expect(pane()!.classList).toContain('nostos-settings-popover-pane--popover');
    expect(document.activeElement?.id).toBe('first');
  });

  it('closes on Escape and gives focus back to its button', async () => {
    await setup();
    await open();
    const esc = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    document.activeElement!.dispatchEvent(esc);
    fixture.detectChanges();

    expect(esc.defaultPrevented).toBe(true);
    expect(fixture.componentInstance.dismissals).toEqual(['escape']);
    expect(panel()).toBeNull();
    expect(document.activeElement).toBe(trigger());
  });

  it('leaves an Escape that a layer inside it already handled', async () => {
    await setup();
    await open();
    fixture.componentInstance.inner = true;
    const second = document.querySelector('#second') as HTMLButtonElement;
    second.focus();
    second.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    fixture.detectChanges();
    expect(fixture.componentInstance.dismissals).toEqual([]);
    expect(panel()).not.toBeNull();
  });

  it('closes on a press outside it, not inside it or on its own button', async () => {
    await setup();
    await open();
    const press = (el: Element) => {
      el.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
      (el as HTMLElement).click();
      fixture.detectChanges();
    };

    press(document.querySelector('#second')!);
    expect(fixture.componentInstance.dismissals).toEqual([]);

    // The button toggles it closed itself; that is not a press outside.
    press(trigger());
    expect(fixture.componentInstance.dismissals).toEqual([]);
    expect(panel()).toBeNull();

    await open();
    const elsewhere = fixture.nativeElement.querySelector('#elsewhere') as HTMLButtonElement;
    elsewhere.focus();
    press(elsewhere);
    expect(fixture.componentInstance.dismissals).toEqual(['outside']);
    expect(panel()).toBeNull();
    // Focus stays where the press went.
    expect(document.activeElement).toBe(elsewhere);
  });

  it('is a full-width sheet along the bottom edge on a phone, and follows the width as it changes', async () => {
    await setup(true);
    await open();
    expect(pane()!.classList).toContain('nostos-sheet-pane');

    breakpoint.next({ matches: false });
    expect(pane()!.classList).toContain('nostos-settings-popover-pane--popover');
    expect(pane()!.classList).not.toContain('nostos-sheet-pane');
  });
});
