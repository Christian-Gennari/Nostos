import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ChoiceRowComponent } from './choice-row.component';

@Component({
  standalone: true,
  imports: [ChoiceRowComponent],
  template: `
    <div appChoiceRow class="site-class" [selected]="current()">
      <span class="nostos-choice-row__text"><span class="nostos-choice-row__title">EPUB</span></span>
    </div>
    <button appChoiceRow type="button">PDF</button>
    <li appChoiceRow>Audiobook</li>
  `,
})
class ChoiceRowHarnessComponent {
  readonly current = signal(true);
}

describe('ChoiceRowComponent', () => {
  let fixture: ComponentFixture<ChoiceRowHarnessComponent>;
  let rows: HTMLElement[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ChoiceRowHarnessComponent] }).compileComponents();
    fixture = TestBed.createComponent(ChoiceRowHarnessComponent);
    fixture.detectChanges();
    rows = Array.from(fixture.nativeElement.querySelectorAll('.nostos-choice-row'));
  });

  it('is the same row on a div, a button and a list item', () => {
    expect(rows.map((row) => row.tagName)).toEqual(['DIV', 'BUTTON', 'LI']);
    expect(rows[0].classList.contains('site-class')).toBe(true);
  });

  it('marks only the current row as selected, and follows the input', () => {
    expect(rows.map((row) => row.classList.contains('nostos-choice-row--selected'))).toEqual([true, false, false]);

    fixture.componentInstance.current.set(false);
    fixture.detectChanges();

    expect(rows[0].classList.contains('nostos-choice-row--selected')).toBe(false);
  });
});
