import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { HighlightColour } from '../../reader/highlight-colours';
import { PenSwatchesComponent } from './pen-swatches.component';

@Component({
  standalone: true,
  imports: [PenSwatchesComponent],
  template: `
    <app-pen-swatches
      [value]="value()"
      [disabled]="disabled()"
      testIdPrefix="pen-"
      (chosen)="value.set($event)"
    />
  `,
})
class HostComponent {
  readonly value = signal<HighlightColour | null>('amber');
  readonly disabled = signal(false);
}

describe('PenSwatchesComponent', () => {
  function setup() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const dots = () => Array.from(element.querySelectorAll<HTMLButtonElement>('.pen-swatch'));
    return { fixture, element, dots };
  }

  it('is a labelled group of four named dots, each in its pen', () => {
    const { element, dots } = setup();
    const group = element.querySelector('app-pen-swatches')!;

    expect(group.getAttribute('role')).toBe('group');
    expect(group.getAttribute('aria-label')).toBe('Highlight colour');
    expect(dots().map((dot) => dot.getAttribute('aria-label'))).toEqual([
      'Sage highlighter',
      'Amber highlighter',
      'Blue highlighter',
      'Rose highlighter',
    ]);
    expect(dots()[3].style.backgroundColor).toBe('var(--highlight-rose)');
    expect(dots()[0].getAttribute('data-testid')).toBe('pen-sage');
  });

  it('marks the chosen pen pressed and reports a new choice', () => {
    const { fixture, dots } = setup();
    expect(dots().map((dot) => dot.getAttribute('aria-pressed'))).toEqual(['false', 'true', 'false', 'false']);
    expect(dots()[1].classList).toContain('is-active');

    dots()[2].click();
    fixture.detectChanges();

    expect(fixture.componentInstance.value()).toBe('blue');
    expect(dots()[2].getAttribute('aria-pressed')).toBe('true');
    expect(dots()[1].classList).not.toContain('is-active');
  });

  it('disables every dot while a save is in flight', () => {
    const { fixture, dots } = setup();
    fixture.componentInstance.disabled.set(true);
    fixture.detectChanges();

    expect(dots().every((dot) => dot.disabled)).toBe(true);
  });
});
