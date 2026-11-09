import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ButtonComponent } from '../button/button.component';
import { SelectionBarComponent } from './selection-bar.component';

@Component({
  standalone: true,
  imports: [SelectionBarComponent, ButtonComponent],
  template: `
    <nostos-selection-bar
      [count]="count()"
      label="Selected notes"
      (closed)="closes.set(closes() + 1)"
    >
      <button
        appButton
        variant="ghost"
        size="sm"
        type="button"
        class="selection-action"
        [disabled]="count() === 0"
        aria-label="Delete"
      >
        <span class="selection-label">Delete</span>
      </button>
      <button appButton variant="danger" size="sm" type="button" class="selection-action" aria-label="Remove">
        <span class="selection-label">Remove</span>
      </button>
    </nostos-selection-bar>
  `,
})
class SelectionBarHarnessComponent {
  readonly count = signal(0);
  readonly closes = signal(0);
}

describe('SelectionBarComponent', () => {
  let fixture: ComponentFixture<SelectionBarHarnessComponent>;
  let host: SelectionBarHarnessComponent;
  const bar = () => fixture.nativeElement.querySelector('.selection-bar') as HTMLElement;
  const count = () => bar().querySelector('.selection-count') as HTMLElement;
  const actions = () => Array.from(bar().querySelectorAll('.selection-action')) as HTMLButtonElement[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SelectionBarHarnessComponent] }).compileComponents();
    fixture = TestBed.createComponent(SelectionBarHarnessComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('announces the count as a live status, singular and plural', () => {
    expect(count().textContent?.trim()).toBe('0 selected');
    host.count.set(1);
    fixture.detectChanges();
    expect(count().textContent?.trim()).toBe('1 selected');
    host.count.set(4);
    fixture.detectChanges();
    expect(count().textContent?.trim()).toBe('4 selected');
    expect(count().getAttribute('role')).toBe('status');
    expect(count().getAttribute('aria-live')).toBe('polite');
  });

  it('is a labelled toolbar', () => {
    expect(bar().getAttribute('role')).toBe('toolbar');
    expect(bar().getAttribute('aria-label')).toBe('Selected notes');
  });

  it('projects the caller’s actions untouched, including their disabled state', () => {
    expect(actions().map((action) => action.getAttribute('aria-label'))).toEqual(['Delete', 'Remove']);
    expect(actions().map((action) => action.disabled)).toEqual([true, false]);
    expect(actions()[0].querySelector('.selection-label')?.textContent).toBe('Delete');

    host.count.set(2);
    fixture.detectChanges();
    expect(actions().map((action) => action.disabled)).toEqual([false, false]);
  });

  it('owns one close control that asks the caller to stop selecting', () => {
    const close = bar().querySelector('.selection-close') as HTMLButtonElement;
    expect(close.getAttribute('aria-label')).toBe('Stop selecting');

    close.click();
    fixture.detectChanges();
    expect(host.closes()).toBe(1);
  });
});
