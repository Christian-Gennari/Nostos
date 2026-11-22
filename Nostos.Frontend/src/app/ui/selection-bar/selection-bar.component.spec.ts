import { readFileSync } from 'node:fs';
import path from 'node:path';
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
      [note]="note()"
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
  readonly note = signal<string | null>(null);
  readonly closes = signal(0);
}

describe('SelectionBarComponent', () => {
  let fixture: ComponentFixture<SelectionBarHarnessComponent>;
  let host: SelectionBarHarnessComponent;
  const bar = () => fixture.nativeElement.querySelector('.selection-bar') as HTMLElement;
  const count = () => bar().querySelector('.selection-count') as HTMLElement;
  const actions = () => Array.from(bar().querySelectorAll('.selection-action')) as HTMLButtonElement[];
  const barSource = () =>
    readFileSync(path.resolve(process.cwd(), 'src/app/ui/selection-bar/selection-bar.component.ts'), 'utf8');

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

  it('keeps the actions on one row, with no note, when the caller passes none', () => {
    const primary = bar().querySelector('.selection-primary') as HTMLElement;
    expect(primary).not.toBeNull();
    expect(primary.contains(count())).toBe(true);
    expect(primary.contains(bar().querySelector('.selection-actions'))).toBe(true);
    expect(primary.contains(bar().querySelector('.selection-close'))).toBe(true);

    // Nothing else shares the bar with the action row: there is no second line.
    expect(bar().children).toHaveLength(1);
    expect(bar().querySelector('.selection-note')).toBeNull();

    // The bar must never wrap its children into rows; at 390px three actions
    // and the close control fit one row, and a note may not change that.
    expect(barSource()).not.toContain('flex-wrap: wrap');
  });

  it('shows the note on its own line under the actions', () => {
    host.note.set('Select at most 500 topics at once.');
    fixture.detectChanges();

    const note = bar().querySelector('.selection-note') as HTMLElement;
    expect(note.textContent?.trim()).toBe('Select at most 500 topics at once.');
    expect(note.getAttribute('role')).toBe('alert');
    expect(note.id).toBe('selection-bar-note');

    // The note is a sibling of the action row, not one of its flex items, so
    // the actions stay on their row and the note takes the line below them.
    expect(note.parentElement).toBe(bar());
    expect(note.closest('.selection-primary')).toBeNull();
    expect(note.previousElementSibling?.classList.contains('selection-primary')).toBe(true);
    expect(bar().children).toHaveLength(2);
  });

  it('removes the note again when the caller clears it', () => {
    host.note.set('Select at most 500 topics at once.');
    fixture.detectChanges();
    expect(bar().querySelector('.selection-note')).not.toBeNull();

    host.note.set(null);
    fixture.detectChanges();
    expect(bar().querySelector('.selection-note')).toBeNull();
    expect(bar().children).toHaveLength(1);
  });

  it('sits above navigation and below modal backdrops in the stacking contract', () => {
    const appCss = readFileSync(path.resolve(process.cwd(), 'src/app/app.component.css'), 'utf8');
    const barTs = barSource();
    const layer = (token: string) => {
      const match = appCss.match(new RegExp(`--layer-${token}:\\s*(\\d+)`));
      return match ? Number(match[1]) : NaN;
    };

    expect(barTs).toContain('z-index: var(--layer-selection-bar)');
    expect(barTs).not.toContain('var(--layer-system)');
    expect(layer('selection-bar')).toBeGreaterThan(layer('navigation'));
    expect(layer('selection-bar')).toBeLessThan(layer('backdrop'));
  });
});
