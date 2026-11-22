import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { MapKindLegendComponent } from './map-kind-legend.component';
import type { KindLegendEntry } from './topic-map.helpers';

@Component({
  standalone: true,
  imports: [MapKindLegendComponent],
  template: `
    <app-map-kind-legend
      [entries]="entries"
      [assistantAvailable]="assistantAvailable"
      (sortWithAssistant)="asked = true"
    />
  `,
})
class HostComponent {
  entries: KindLegendEntry[] = [
    {
      key: 'none',
      label: 'No kind',
      count: 12,
      color: 'var(--color-text-muted)',
      visible: true,
      isUntyped: true,
    },
  ];
  assistantAvailable = false;
  asked = false;
}

describe('MapKindLegendComponent entry point', () => {
  function setup(assistantAvailable: boolean) {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.assistantAvailable = assistantAvailable;
    fixture.detectChanges();
    return fixture;
  }

  it('offers two separate sort actions on the No kind row when an AI provider is configured', () => {
    const fixture = setup(true);
    const element = fixture.nativeElement as HTMLElement;
    const manual = element.querySelector('[data-testid="map-legend-sort-manual"]') as HTMLAnchorElement;
    const button = element.querySelector('[data-testid="map-legend-sort-with-assistant"]') as HTMLButtonElement;

    // Two actions, each naming itself, with a separator between them.
    expect(manual.textContent).toContain('Sort them by hand');
    expect(button.textContent).toContain('Sort with Ask Nostos');
    expect(button.querySelector('svg, nostos-icon')).not.toBeNull();
    expect(element.querySelector('.map-legend-separator')).not.toBeNull();

    button.click();
    expect(fixture.componentInstance.asked).toBe(true);
  });

  it('keeps the manual sort and hides only the Ask action when no provider is configured', () => {
    const fixture = setup(false);
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('[data-testid="map-legend-sort-manual"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="map-legend-sort-with-assistant"]')).toBeNull();
  });
});
