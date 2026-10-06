import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LoadingIndicatorComponent } from './loading-indicator.component';

@Component({
  standalone: true,
  imports: [LoadingIndicatorComponent],
  template: `
    <nostos-loading-indicator label="Loading notes" />
    <nostos-loading-indicator label="Decorative wait" size="sm" [decorative]="true" />
  `,
})
class LoadingIndicatorHarnessComponent {}

describe('LoadingIndicatorComponent', () => {
  let fixture: ComponentFixture<LoadingIndicatorHarnessComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LoadingIndicatorHarnessComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(LoadingIndicatorHarnessComponent);
    fixture.detectChanges();
  });

  it('announces one polite loading status by default', () => {
    const indicator = fixture.nativeElement.querySelector(
      'nostos-loading-indicator',
    ) as HTMLElement;

    expect(indicator.getAttribute('role')).toBe('status');
    expect(indicator.getAttribute('aria-live')).toBe('polite');
    expect(indicator.getAttribute('aria-atomic')).toBe('true');
    expect(indicator.getAttribute('aria-label')).toBeNull();
    expect(indicator.textContent).toContain('Loading notes');
  });

  it('uses the shared Phosphor loading glyph and design-system size rungs', () => {
    const indicators = fixture.nativeElement.querySelectorAll(
      'nostos-loading-indicator',
    ) as NodeListOf<HTMLElement>;
    const regularSvg = indicators[0].querySelector('svg');
    const smallSvg = indicators[1].querySelector('svg');

    expect(regularSvg?.getAttribute('width')).toBe('20');
    expect(regularSvg?.getAttribute('height')).toBe('20');
    expect(smallSvg?.getAttribute('width')).toBe('14');
    expect(smallSvg?.getAttribute('height')).toBe('14');
  });

  it('can be decorative inside a parent status region without duplicate announcements', () => {
    const decorative = fixture.nativeElement.querySelectorAll(
      'nostos-loading-indicator',
    )[1] as HTMLElement;

    expect(decorative.getAttribute('aria-hidden')).toBe('true');
    expect(decorative.getAttribute('role')).toBeNull();
    expect(decorative.getAttribute('aria-live')).toBeNull();
  });
});
