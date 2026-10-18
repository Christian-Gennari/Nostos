import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { RangeDirective } from './range.directive';

@Component({
  standalone: true,
  imports: [RangeDirective],
  template: `<input type="range" appRange min="0" max="1" step="0.05" [value]="value()" />`,
})
class Host {
  readonly value = signal(0.25);
}

describe('RangeDirective', () => {
  it('marks the slider and keeps the filled part at its value, bound or dragged', () => {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    expect(input.classList).toContain('nostos-range');
    expect(input.style.getPropertyValue('--range-fill')).toBe('25%');

    fixture.componentInstance.value.set(0.6);
    fixture.detectChanges();
    expect(input.style.getPropertyValue('--range-fill')).toBe('60%');

    input.value = '1';
    input.dispatchEvent(new Event('input'));
    expect(input.style.getPropertyValue('--range-fill')).toBe('100%');
  });
});
