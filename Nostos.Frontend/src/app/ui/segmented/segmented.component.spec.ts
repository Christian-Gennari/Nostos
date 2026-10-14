import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SegmentedComponent } from './segmented.component';

@Component({
  standalone: true,
  imports: [SegmentedComponent],
  template: `
    <span id="label">Margins</span>
    <nostos-segmented
      aria-labelledby="label"
      fill
      [options]="options"
      [value]="value()"
      [disabled]="disabled()"
      (valueChange)="value.set($event); changes = changes + 1"
    />
  `,
})
class HostComponent {
  readonly options = [
    { value: 'narrow', label: 'Narrow' },
    { value: 'normal', label: 'Normal' },
    { value: 'wide', label: 'Wide' },
  ];
  readonly value = signal<string | null>('normal');
  readonly disabled = signal(false);
  changes = 0;
}

describe('SegmentedComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({ imports: [HostComponent] }));

  async function setup() {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    f.detectChanges();
    const host = f.nativeElement.querySelector('nostos-segmented') as HTMLElement;
    const radios = () => Array.from(host.querySelectorAll('[role="radio"]')) as HTMLButtonElement[];
    return { f, host, radios };
  }

  it('is a labelled radio group with exactly one checked, tabbable option', async () => {
    const { host, radios } = await setup();
    expect(host.getAttribute('role')).toBe('radiogroup');
    expect(host.getAttribute('aria-labelledby')).toBe('label');
    expect(host.classList.contains('seg--fill')).toBe(true);
    expect(radios().map((r) => r.textContent?.trim())).toEqual(['Narrow', 'Normal', 'Wide']);
    expect(radios().map((r) => r.getAttribute('aria-checked'))).toEqual(['false', 'true', 'false']);
    expect(radios().map((r) => r.tabIndex)).toEqual([-1, 0, -1]);
  });

  it('emits on click and does not re-emit the current value', async () => {
    const { f, radios } = await setup();
    radios()[1].click();
    expect(f.componentInstance.changes).toBe(0);
    radios()[2].click();
    f.detectChanges();
    expect(f.componentInstance.value()).toBe('wide');
    expect(radios()[2].getAttribute('aria-checked')).toBe('true');
  });

  it('moves and selects with the arrow keys, wrapping, and Home/End jump', async () => {
    const { f, radios } = await setup();
    radios()[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    f.detectChanges();
    expect(f.componentInstance.value()).toBe('wide');
    expect(document.activeElement).toBe(radios()[2]);

    radios()[2].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    f.detectChanges();
    expect(f.componentInstance.value()).toBe('narrow');

    radios()[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    f.detectChanges();
    expect(f.componentInstance.value()).toBe('wide');
  });

  it('keeps the first option tabbable when the value matches nothing', async () => {
    const { f, radios } = await setup();
    f.componentInstance.value.set(null);
    f.detectChanges();
    expect(radios().every((r) => r.getAttribute('aria-checked') === 'false')).toBe(true);
    expect(radios()[0].tabIndex).toBe(0);
  });

  it('disables every option', async () => {
    const { f, host, radios } = await setup();
    f.componentInstance.disabled.set(true);
    f.detectChanges();
    expect(radios().every((r) => r.disabled)).toBe(true);
    expect(host.getAttribute('aria-disabled')).toBe('true');
  });
});
