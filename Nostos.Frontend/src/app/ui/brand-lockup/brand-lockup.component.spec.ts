import { TestBed } from '@angular/core/testing';

import { BrandLockupComponent } from './brand-lockup.component';

describe('BrandLockupComponent', () => {
  it('is one labelled image: the tile, the arch and the wordmark', () => {
    TestBed.configureTestingModule({ imports: [BrandLockupComponent] });
    const fixture = TestBed.createComponent(BrandLockupComponent);
    fixture.detectChanges();
    const svg = fixture.nativeElement.querySelector('svg') as SVGSVGElement;

    expect(svg.getAttribute('role')).toBe('img');
    expect(svg.getAttribute('aria-label')).toBe('Nostos');
    expect(svg.getAttribute('viewBox')).toBe('0 0 1840 512');
    expect(svg.querySelector('rect.tile')).toBeTruthy();
    expect(svg.querySelector('path.arch')?.getAttribute('fill-rule')).toBe('evenodd');
    expect(svg.querySelector('text.word')?.textContent).toBe('nostos');
  });
});
