import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { NOSTOS_MENU } from './menu.component';

@Component({
  standalone: true,
  imports: [...NOSTOS_MENU],
  template: `
    <button type="button" id="trigger" [nostosMenuTriggerFor]="more">More</button>
    <ng-template #more>
      <nostos-menu>
        <button nostosMenuItem icon="pencil-simple" (triggered)="log.push('edit')">Edit</button>
        <button nostosMenuItem [disabled]="true" (triggered)="log.push('locked')">Locked</button>
        <hr nostosMenuSeparator />
        <button nostosMenuItem icon="trash" tone="danger" (triggered)="log.push('remove')">
          Remove
        </button>
      </nostos-menu>
    </ng-template>
  `,
})
class Host {
  log: string[] = [];
}

describe('Menu', () => {
  let fixture: ComponentFixture<Host>;

  const trigger = (): HTMLButtonElement => fixture.nativeElement.querySelector('#trigger');
  const menu = (): HTMLElement | null => document.querySelector('.nostos-menu');
  const items = (): HTMLButtonElement[] =>
    Array.from(document.querySelectorAll<HTMLButtonElement>('.nostos-menu-item'));

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [Host] }).compileComponents();
    fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
  });

  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove()));

  it('is closed until its trigger is used, and says so to assistive tech', () => {
    expect(menu()).toBeNull();
    expect(trigger().getAttribute('aria-haspopup')).toBe('menu');
    expect(trigger().getAttribute('aria-expanded')).toBe('false');

    trigger().click();
    fixture.detectChanges();

    expect(menu()?.getAttribute('role')).toBe('menu');
    expect(trigger().getAttribute('aria-expanded')).toBe('true');
    expect(items().map((item) => item.textContent?.trim())).toEqual(['Edit', 'Locked', 'Remove']);
    expect(items().every((item) => item.getAttribute('role') === 'menuitem')).toBe(true);
  });

  it('runs the chosen action once and closes', () => {
    trigger().click();
    fixture.detectChanges();

    items()[0].click();
    fixture.detectChanges();

    expect(fixture.componentInstance.log).toEqual(['edit']);
    expect(menu()).toBeNull();
  });

  it('does not run a disabled item', () => {
    trigger().click();
    fixture.detectChanges();

    expect(items()[1].getAttribute('aria-disabled')).toBe('true');
    items()[1].click();
    fixture.detectChanges();

    expect(fixture.componentInstance.log).toEqual([]);
  });

  it('marks a destructive item and renders its icon and the separator', () => {
    trigger().click();
    fixture.detectChanges();

    expect(items()[2].classList).toContain('nostos-menu-item--danger');
    expect(items()[0].querySelector('nostos-icon')).toBeTruthy();
    expect(items()[1].querySelector('nostos-icon')).toBeNull();
    expect(document.querySelector('hr.nostos-menu-separator')?.getAttribute('role')).toBe('separator');
  });

  it('closes on Escape', () => {
    trigger().click();
    fixture.detectChanges();

    menu()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    fixture.detectChanges();

    expect(menu()).toBeNull();
  });
});
