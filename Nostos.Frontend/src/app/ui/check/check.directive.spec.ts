import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CheckboxDirective, RadioDirective } from './check.directive';

@Component({
  standalone: true,
  imports: [CheckboxDirective, RadioDirective],
  template: `
    <input type="checkbox" appCheckbox checked aria-label="Box" />
    <input type="radio" appRadio name="r" aria-label="Radio" />
  `,
})
class HostComponent {}

describe('CheckboxDirective / RadioDirective', () => {
  it('keeps the native input and adds only the shared class', async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const f = TestBed.createComponent(HostComponent);
    f.detectChanges();
    const [box, radio] = Array.from(f.nativeElement.querySelectorAll('input')) as HTMLInputElement[];
    expect(box.type).toBe('checkbox');
    expect(box.checked).toBe(true);
    expect(box.classList.contains('nostos-check')).toBe(true);
    expect(box.classList.contains('nostos-check--box')).toBe(true);
    expect(radio.type).toBe('radio');
    expect(radio.classList.contains('nostos-check--radio')).toBe(true);
    expect(radio.getAttribute('aria-label')).toBe('Radio');
  });
});
