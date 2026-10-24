import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';

import { InlineRenameComponent } from './inline-rename.component';

@Component({
  standalone: true,
  imports: [InlineRenameComponent],
  template: `
    <div class="row" data-rename-row tabindex="-1">
      @if (renaming()) {
        <nostos-inline-rename
          name="Alpha"
          (committed)="renaming.set(false)"
          (cancelled)="renaming.set(false)"
        />
      } @else {
        <span>Alpha</span>
      }
    </div>
  `,
})
class RowHost {
  readonly renaming = signal(true);
}

describe('InlineRename focus return', () => {
  const nextTick = () => new Promise((resolve) => setTimeout(resolve, 5));

  async function setup() {
    await TestBed.configureTestingModule({ imports: [RowHost] }).compileComponents();
    const fixture = TestBed.createComponent(RowHost);
    await fixture.whenStable();
    const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    input.focus();
    return { fixture, input };
  }

  it('returns focus to its row when Escape closes it', async () => {
    const { fixture, input } = await setup();

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    await fixture.whenStable();
    await nextTick();

    expect(fixture.nativeElement.querySelector('input')).toBeNull();
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('.row'));
  });

  it('returns focus to its row after Enter saves a new name', async () => {
    const { fixture, input } = await setup();
    input.value = 'Beta';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await fixture.whenStable();
    await nextTick();

    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('.row'));
  });

  it('leaves focus alone when the user already moved it elsewhere', async () => {
    const { fixture, input } = await setup();
    const elsewhere = document.createElement('button');
    document.body.appendChild(elsewhere);

    elsewhere.focus();
    input.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
    await nextTick();

    expect(document.activeElement).toBe(elsewhere);
    elsewhere.remove();
  });
});
