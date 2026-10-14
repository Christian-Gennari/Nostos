import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ProgressComponent } from './progress.component';

@Component({
  standalone: true,
  imports: [ProgressComponent],
  template: `
    <nostos-progress id="a" [value]="value()" label="Uploading" valueText="Uploading, 40 percent" />
    <nostos-progress id="b" decorative [value]="20" surface="overlay" size="md" />
  `,
})
class HostComponent {
  readonly value = signal<number | null>(140);
}

describe('ProgressComponent', () => {
  it('is a labelled progressbar, clamped to 0..100, and indeterminate on null', async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const f = TestBed.createComponent(HostComponent);
    f.detectChanges();
    const bar = f.nativeElement.querySelector('#a') as HTMLElement;
    const fill = () => bar.querySelector('.nostos-progress__fill') as HTMLElement;
    expect(bar.getAttribute('role')).toBe('progressbar');
    expect(bar.getAttribute('aria-label')).toBe('Uploading');
    expect(bar.getAttribute('aria-valuetext')).toBe('Uploading, 40 percent');
    expect(bar.getAttribute('aria-valuenow')).toBe('100');
    expect(fill().style.width).toBe('100%');

    f.componentInstance.value.set(null);
    f.detectChanges();
    expect(bar.classList.contains('nostos-progress--indeterminate')).toBe(true);
    expect(bar.getAttribute('aria-valuenow')).toBeNull();
    expect(fill().style.width).toBe('');
  });

  it('drops its ARIA when decorative and carries the overlay/size classes', async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const f = TestBed.createComponent(HostComponent);
    f.detectChanges();
    const bar = f.nativeElement.querySelector('#b') as HTMLElement;
    expect(bar.getAttribute('role')).toBeNull();
    expect(bar.getAttribute('aria-hidden')).toBe('true');
    expect(bar.classList.contains('nostos-progress--overlay')).toBe(true);
    expect(bar.classList.contains('nostos-progress--md')).toBe(true);
    expect((bar.querySelector('.nostos-progress__fill') as HTMLElement).style.width).toBe('20%');
  });
});
