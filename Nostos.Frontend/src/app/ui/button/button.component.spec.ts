import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ButtonComponent } from './button.component';

@Component({
  standalone: true,
  imports: [ButtonComponent],
  template: `
    <button
      appButton
      variant="primary"
      size="sm"
      type="button"
      aria-label="Save settings"
      [busy]="true"
      disabled
    >
      Save
    </button>
    <button appButton variant="ghost" type="button">Quiet action</button>
    <button appButton variant="primary" size="lg" type="button">Prominent action</button>
    <button appButton variant="overlay" type="button">Change cover</button>
  `,
})
class ButtonHarnessComponent {}

describe('ButtonComponent', () => {
  let fixture: ComponentFixture<ButtonHarnessComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ButtonHarnessComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(ButtonHarnessComponent);
    fixture.detectChanges();
  });

  it('keeps the native button host and applies the canonical variant/size contract', () => {
    const buttons = fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>;
    const primary = buttons[0];

    expect(primary.tagName).toBe('BUTTON');
    expect(primary.classList.contains('nostos-button')).toBe(true);
    expect(primary.classList.contains('nostos-button--primary')).toBe(true);
    expect(primary.classList.contains('nostos-button--sm')).toBe(true);
    expect(primary.type).toBe('button');
    expect(primary.disabled).toBe(true);
    expect(primary.getAttribute('aria-label')).toBe('Save settings');
    expect(primary.getAttribute('aria-busy')).toBe('true');
  });

  it('supports a large action size through the canonical component API', () => {
    const buttons = fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>;
    const large = buttons[2];

    expect(large.classList.contains('nostos-button--primary')).toBe(true);
    expect(large.classList.contains('nostos-button--lg')).toBe(true);
  });

  it('supports a quiet action without changing its native semantics', () => {
    const buttons = fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>;
    const ghost = buttons[1];

    expect(ghost.classList.contains('nostos-button--ghost')).toBe(true);
    expect(ghost.disabled).toBe(false);
    expect(ghost.getAttribute('aria-busy')).toBeNull();
  });

  it('has an overlay variant for actions that sit on artwork', () => {
    const buttons = fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>;
    const overlay = buttons[3];

    expect(overlay.classList.contains('nostos-button--overlay')).toBe(true);
    expect(overlay.classList.contains('nostos-button--secondary')).toBe(false);
  });
});
