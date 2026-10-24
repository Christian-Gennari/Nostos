import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { HighlightColour } from '../../reader/highlight-colours';
import { QuoteBlockComponent, QuoteBlockSize } from './quote-block.component';

@Component({
  standalone: true,
  imports: [QuoteBlockComponent],
  template: `
    <blockquote appQuote [pen]="pen()" [size]="size()" [lines]="lines()">Sing, goddess, the anger</blockquote>
    <button type="button"><span appQuote size="sm">In a row</span></button>
  `,
})
class HostComponent {
  readonly pen = signal<HighlightColour | null>('rose');
  readonly size = signal<QuoteBlockSize>('md');
  readonly lines = signal<number | null>(null);
}

describe('QuoteBlockComponent', () => {
  function setup() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const quote = fixture.nativeElement.querySelector('blockquote') as HTMLElement;
    return { fixture, quote };
  }

  it('draws the rail in the pen it was marked with, and adds no quotation marks', () => {
    const { quote } = setup();

    expect(quote.classList).toContain('nostos-quote');
    expect(quote.classList).toContain('nostos-accent-rail');
    expect(quote.style.getPropertyValue('--nostos-accent-rail-color')).toBe('var(--highlight-rose)');
    expect(quote.textContent).toBe('Sing, goddess, the anger');
  });

  it('keeps the neutral rail when the pen is not known', () => {
    const { fixture, quote } = setup();
    fixture.componentInstance.pen.set(null);
    fixture.detectChanges();

    expect(quote.style.getPropertyValue('--nostos-accent-rail-color')).toBe('');
  });

  it('takes a size and an optional line clamp', () => {
    const { fixture, quote } = setup();
    expect(quote.classList).not.toContain('is-clamped');

    fixture.componentInstance.size.set('lg');
    fixture.componentInstance.lines.set(3);
    fixture.detectChanges();

    expect(quote.classList).toContain('is-lg');
    expect(quote.classList).toContain('is-clamped');
    expect(quote.style.getPropertyValue('--nostos-quote-lines')).toBe('3');
  });

  it('goes on a span where a blockquote may not, with the same look', () => {
    const { fixture } = setup();
    const inRow = fixture.nativeElement.querySelector('button span') as HTMLElement;

    expect(inRow.classList).toContain('nostos-quote');
    expect(inRow.classList).toContain('is-sm');
  });
});
