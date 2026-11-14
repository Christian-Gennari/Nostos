import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import { BrainNoteRowComponent } from './brain-note-row.component';

const NOTE: NoteSearchHit = {
  id: 'note-1',
  bookId: 'book-1',
  bookTitle: 'Book',
  content: 'A note',
  selectedText: null,
  snippet: null,
  topicNames: [],
  createdAt: '2026-11-08T10:00:00Z',
};

@Component({
  standalone: true,
  imports: [BrainNoteRowComponent],
  template: `
    <app-brain-note-row
      [note]="note"
      [selected]="selected()"
      [selectable]="selectable()"
      [checked]="checked()"
      (checkedChange)="onChecked($event)"
      (click)="presses = presses + 1"
    />
  `,
})
class RowHarnessComponent {
  readonly note = NOTE;
  readonly selected = signal(false);
  readonly selectable = signal(false);
  readonly checked = signal(false);
  readonly changes: boolean[] = [];
  presses = 0;

  onChecked(checked: boolean): void {
    this.changes.push(checked);
    this.checked.set(checked);
  }
}

describe('BrainNoteRowComponent', () => {
  let fixture: ComponentFixture<RowHarnessComponent>;
  let host: RowHarnessComponent;
  let row: HTMLElement;

  const box = () => row.querySelector<HTMLInputElement>('input[type=checkbox]');

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [RowHarnessComponent] }).compileComponents();
    fixture = TestBed.createComponent(RowHarnessComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
    row = fixture.nativeElement.querySelector('app-brain-note-row') as HTMLElement;
  });

  it('shows no checkbox at rest, and a press on the row is the list’s own business', () => {
    expect(box()).toBeNull();

    row.click();

    expect(host.presses).toBe(1);
    expect(host.changes).toEqual([]);
  });

  it('shows a labelled checkbox only while selectable, and reports each tick', () => {
    host.selectable.set(true);
    fixture.detectChanges();

    expect(box()).not.toBeNull();
    expect(box()?.getAttribute('aria-label')).toBe('Select note from Book');
    expect(box()?.checked).toBe(false);

    host.checked.set(true);
    fixture.detectChanges();
    expect(box()?.checked).toBe(true);

    box()?.click();
    fixture.detectChanges();
    expect(host.changes).toEqual([false]);
    expect(host.checked()).toBe(false);

    box()?.click();
    fixture.detectChanges();
    expect(host.changes).toEqual([false, true]);
    expect(host.checked()).toBe(true);
  });

  it('keeps a checkbox press off the row, so one press ticks once', () => {
    host.selectable.set(true);
    fixture.detectChanges();

    box()?.click();
    expect(host.changes).toEqual([true]);
    expect(host.presses).toBe(0);

    row.querySelector<HTMLElement>('.row-excerpt')?.click();
    expect(host.presses).toBe(1);
  });

  it('keeps the note open in the pane separate from the tick', () => {
    host.selectable.set(true);
    host.selected.set(true);
    fixture.detectChanges();
    expect(row.getAttribute('aria-selected')).toBe('true');
    expect(box()?.checked).toBe(false);

    host.selected.set(false);
    host.checked.set(true);
    fixture.detectChanges();
    expect(row.getAttribute('aria-selected')).toBe('false');
    expect(box()?.checked).toBe(true);
  });
});
