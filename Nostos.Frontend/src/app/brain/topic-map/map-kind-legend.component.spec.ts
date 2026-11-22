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

  it('offers Sort them with Ask Nostos on the No kind row when an AI provider is configured', () => {
    const fixture = setup(true);
    const button = fixture.nativeElement.querySelector('[data-testid="map-legend-sort-with-assistant"]') as HTMLButtonElement;

    expect(button.textContent).toContain('Sort them with Ask Nostos');
    button.click();
    expect(fixture.componentInstance.asked).toBe(true);
  });

  it('hides it when no provider is configured', () => {
    const fixture = setup(false);

    expect(fixture.nativeElement.querySelector('[data-testid="map-legend-sort-with-assistant"]')).toBeNull();
  });
});
