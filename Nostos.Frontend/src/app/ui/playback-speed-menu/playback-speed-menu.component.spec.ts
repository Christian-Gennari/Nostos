import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { PlaybackSpeedMenuComponent, formatPlaybackRate } from './playback-speed-menu.component';

@Component({
  standalone: true,
  imports: [PlaybackSpeedMenuComponent],
  template: `
    <nostos-playback-speed-menu
      [rate]="rate()"
      [rates]="rates"
      [step]="step()"
      [min]="0.5"
      [max]="3"
      label="Playback speed"
      (rateChange)="rate.set($event); changes.push($event)"
    />
  `,
})
class Host {
  readonly rate = signal(1);
  readonly step = signal<number | null>(0.05);
  readonly rates = [0.8, 1, 1.25, 1.5, 2];
  readonly changes: number[] = [];
}

describe('PlaybackSpeedMenuComponent', () => {
  let fixture: ComponentFixture<Host>;

  const trigger = (): HTMLButtonElement =>
    fixture.nativeElement.querySelector('[data-testid="playback-speed-trigger"]');
  const menu = (): HTMLElement | null => document.querySelector('[data-testid="playback-speed-menu"]');
  const choices = (): HTMLButtonElement[] =>
    Array.from(document.querySelectorAll<HTMLButtonElement>('[role="menuitemradio"]'));
  const open = () => {
    trigger().click();
    fixture.detectChanges();
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [Host] }).compileComponents();
    fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
  });

  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove()));

  it('is a real button that names the control and the current rate', () => {
    expect(trigger().tagName).toBe('BUTTON');
    expect(trigger().classList).toContain('nostos-button');
    expect(trigger().textContent?.trim()).toBe('1×');
    expect(trigger().getAttribute('aria-label')).toBe('Playback speed: 1×. Change speed');
    expect(trigger().getAttribute('aria-haspopup')).toBe('menu');
    expect(trigger().querySelector('nostos-icon')).not.toBeNull();
    expect(trigger().querySelector('svg:not(nostos-icon svg)')).toBeNull();
  });

  it('lists the common rates as choices with the current one checked', () => {
    open();
    expect(menu()?.getAttribute('role')).toBe('menu');
    expect(menu()?.getAttribute('aria-label')).toBe('Playback speed');
    expect(choices().map((c) => c.textContent?.trim())).toEqual(['0.8×', '1×', '1.25×', '1.5×', '2×']);
    expect(choices().map((c) => c.getAttribute('aria-checked'))).toEqual([
      'false',
      'true',
      'false',
      'false',
      'false',
    ]);
  });

  it('chooses a rate and closes, returning focus to the trigger', () => {
    trigger().focus();
    open();
    choices()[3].click();
    fixture.detectChanges();

    expect(fixture.componentInstance.changes).toEqual([1.5]);
    expect(menu()).toBeNull();
    expect(trigger().textContent?.trim()).toBe('1.5×');
  });

  it('steps by the fine step and stays open for the next press', () => {
    open();
    const faster = document.querySelector<HTMLButtonElement>('[data-testid="playback-speed-faster"]')!;
    expect(faster.getAttribute('role')).toBe('menuitem');
    expect(faster.getAttribute('aria-label')).toBe('Faster by 0.05×');

    faster.click();
    fixture.detectChanges();
    faster.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.changes).toEqual([1.05, 1.1]);
    expect(menu()).not.toBeNull();
    expect(document.querySelector('[data-testid="playback-speed-readout"]')?.textContent?.trim()).toBe('1.1×');
    // Between presets, none is checked.
    expect(choices().every((c) => c.getAttribute('aria-checked') === 'false')).toBe(true);
  });

  it('keeps the step inside the range it was given', () => {
    fixture.componentInstance.rate.set(3);
    fixture.detectChanges();
    open();
    const faster = document.querySelector<HTMLButtonElement>('[data-testid="playback-speed-faster"]')!;
    expect(faster.getAttribute('aria-disabled')).toBe('true');
    faster.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.changes).toEqual([]);
  });

  it('offers the presets only when no step is given (read aloud)', () => {
    fixture.componentInstance.step.set(null);
    fixture.detectChanges();
    open();
    expect(document.querySelector('[data-testid="playback-speed-faster"]')).toBeNull();
    expect(choices().length).toBe(5);
  });

  it('formats a rate with at most two decimals and the multiplication sign', () => {
    expect(formatPlaybackRate(1.1500000000000001)).toBe('1.15×');
    expect(formatPlaybackRate(1)).toBe('1×');
  });
});
