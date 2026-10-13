import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { DropZoneComponent } from './drop-zone.component';

@Component({
  standalone: true,
  imports: [DropZoneComponent],
  template: `
    <label appDropZone class="site-class" [active]="dragging()">
      <input type="file" />
      <span class="nostos-drop-zone__title">Choose a file</span>
    </label>
    <label appDropZone density="compact"><input type="file" /></label>
  `,
})
class DropZoneHarnessComponent {
  readonly dragging = signal(false);
}

describe('DropZoneComponent', () => {
  let fixture: ComponentFixture<DropZoneHarnessComponent>;
  let zones: HTMLLabelElement[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [DropZoneHarnessComponent] }).compileComponents();
    fixture = TestBed.createComponent(DropZoneHarnessComponent);
    fixture.detectChanges();
    zones = Array.from(fixture.nativeElement.querySelectorAll('label.nostos-drop-zone'));
  });

  it('stays a label around the real file input, so a click opens the picker', () => {
    expect(zones.length).toBe(2);
    expect(zones[0].querySelector(':scope > input[type="file"]')).toBeTruthy();
    expect(zones[0].classList.contains('site-class')).toBe(true);
  });

  it('shows the drag-over state only while told a file is over it', () => {
    expect(zones[0].classList.contains('nostos-drop-zone--active')).toBe(false);

    fixture.componentInstance.dragging.set(true);
    fixture.detectChanges();

    expect(zones[0].classList.contains('nostos-drop-zone--active')).toBe(true);
  });

  it('marks the compact density', () => {
    expect(zones[0].classList.contains('nostos-drop-zone--compact')).toBe(false);
    expect(zones[1].classList.contains('nostos-drop-zone--compact')).toBe(true);
  });
});
