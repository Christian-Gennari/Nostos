import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { TopicChipComponent } from './topic-chip.component';

@Component({
  standalone: true,
  imports: [TopicChipComponent],
  template: `
    <app-topic-chip id="linked" name="Virtue" [link]="['/brain/topics', 't1']" [removable]="true" (removed)="removed.set(true)" />
    <app-topic-chip id="action" name="Habit" [actionable]="true" (activated)="activated.set(true)" />
    <app-topic-chip id="plain" name="Shelf" [icon]="null" [removable]="true" removeLabel="Remove from Shelf" [disabled]="busy()" />
  `,
})
class HostComponent {
  readonly removed = signal(false);
  readonly activated = signal(false);
  readonly busy = signal(false);
}

describe('TopicChipComponent', () => {
  function setup() {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const chip = (id: string) => fixture.nativeElement.querySelector(`#${id}`) as HTMLElement;
    return { fixture, chip };
  }

  it('opens its topic as a link and removes itself through a named cross', () => {
    const { fixture, chip } = setup();
    const name = chip('linked').querySelector('a.topic-chip-name') as HTMLAnchorElement;
    const cross = chip('linked').querySelector('.topic-chip-remove') as HTMLButtonElement;

    expect(name.getAttribute('href')).toBe('/brain/topics/t1');
    expect(name.textContent?.trim()).toBe('Virtue');
    expect(name.querySelector('nostos-icon')).not.toBeNull();
    expect(cross.getAttribute('aria-label')).toBe('Remove topic Virtue');

    cross.click();
    expect(fixture.componentInstance.removed()).toBe(true);
  });

  it('is a button when the surface acts on it itself', () => {
    const { fixture, chip } = setup();
    const name = chip('action').querySelector('button.topic-chip-name') as HTMLButtonElement;

    expect(name.type).toBe('button');
    expect(chip('action').querySelector('.topic-chip-remove')).toBeNull();
    name.click();
    expect(fixture.componentInstance.activated()).toBe(true);
  });

  it('can go without a mark, with its own remove label, and disable the cross', () => {
    const { fixture, chip } = setup();
    fixture.componentInstance.busy.set(true);
    fixture.detectChanges();

    expect(chip('plain').querySelector('nostos-icon:not(.topic-chip-remove nostos-icon)')).toBeNull();
    expect(chip('plain').querySelector('span.topic-chip-name')?.textContent?.trim()).toBe('Shelf');
    const cross = chip('plain').querySelector('.topic-chip-remove') as HTMLButtonElement;
    expect(cross.getAttribute('aria-label')).toBe('Remove from Shelf');
    expect(cross.disabled).toBe(true);
  });
});
