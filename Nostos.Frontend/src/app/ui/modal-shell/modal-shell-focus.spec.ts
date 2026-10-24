import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ModalShell } from './modal-shell.component';

@Component({
  standalone: true,
  imports: [ModalShell],
  template: `
    <app-modal-shell [isOpen]="open()" [trapFocus]="trap()" (closed)="open.set(false)">
      <input class="field" />
    </app-modal-shell>
  `,
})
class FocusHost {
  open = signal(false);
  trap = signal(true);
}

describe('ModalShell focus', () => {
  afterEach(() => vi.restoreAllMocks());

  async function setup() {
    // The CDK only focuses elements it can see; jsdom has no layout.
    vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue([{}] as unknown as DOMRectList);
    await TestBed.configureTestingModule({ imports: [FocusHost] }).compileComponents();
    const fixture = TestBed.createComponent(FocusHost);
    await fixture.whenStable();
    const opener = document.createElement('button');
    document.body.appendChild(opener);
    opener.focus();
    return { fixture, opener };
  }

  it('traps focus by default: moves it in on open and back to the opener on close', async () => {
    const { fixture, opener } = await setup();

    fixture.componentInstance.open.set(true);
    await fixture.whenStable();
    const card = fixture.nativeElement.querySelector('.modal-card') as HTMLElement;
    expect(card.contains(document.activeElement)).toBe(true);
    expect(fixture.nativeElement.querySelectorAll('.cdk-focus-trap-anchor').length).toBeGreaterThanOrEqual(2);

    fixture.componentInstance.open.set(false);
    await fixture.whenStable();
    expect(document.activeElement).toBe(opener);
    opener.remove();
  });

  it('can still opt out of the trap', async () => {
    const { fixture, opener } = await setup();
    fixture.componentInstance.trap.set(false);

    fixture.componentInstance.open.set(true);
    await fixture.whenStable();

    expect(document.activeElement).toBe(opener);
    opener.remove();
  });
});
