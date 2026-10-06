import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LibraryTransferProgressComponent } from './library-transfer-progress.component';
import { TransferProgressPhase } from '../library-transfer.copy';

describe('LibraryTransferProgressComponent', () => {
  let fixture: ComponentFixture<LibraryTransferProgressComponent>;
  let component: LibraryTransferProgressComponent;

  async function create(phase: TransferProgressPhase): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [LibraryTransferProgressComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(LibraryTransferProgressComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('phase', phase);
    fixture.detectChanges();
  }

  function track(): HTMLElement {
    return fixture.nativeElement.querySelector('[data-testid="transfer-progress"]') as HTMLElement;
  }

  afterEach(() => TestBed.resetTestingModule());

  it('renders a determinate bar with bytes, rate and ETA', async () => {
    await create('uploading');
    fixture.componentRef.setInput('bytesProcessed', 25 * 1024 * 1024);
    fixture.componentRef.setInput('totalBytes', 100 * 1024 * 1024);
    fixture.componentRef.setInput('rateBytesPerSecond', 12 * 1024 * 1024);
    fixture.componentRef.setInput('etaSeconds', 90);
    fixture.detectChanges();

    expect(component.percentage()).toBe(25);
    expect(component.phaseLabel()).toBe('Uploading library');
    expect(track().getAttribute('role')).toBe('progressbar');
    expect(track().getAttribute('aria-valuemin')).toBe('0');
    expect(track().getAttribute('aria-valuemax')).toBe('100');
    expect(track().getAttribute('aria-valuenow')).toBe('25');
    expect(track().getAttribute('aria-valuetext')).toContain('25 percent');

    const detail = fixture.nativeElement.querySelector(
      '[data-testid="transfer-progress-detail"]',
    ) as HTMLElement;
    expect(detail.textContent).toContain('25 MiB of 100 MiB');
    expect(detail.textContent).toContain('12 MiB/s');
    expect(detail.textContent).toContain('About 2 minutes remaining');
    expect(fixture.nativeElement.querySelector('.transfer-progress-percent').textContent).toContain(
      '25%',
    );
  });

  it('renders an indeterminate bar for phases without a known total', async () => {
    await create('checking');

    expect(component.percentage()).toBeNull();
    expect(track().classList).toContain('is-indeterminate');
    expect(track().getAttribute('aria-valuenow')).toBeNull();
    expect(track().getAttribute('aria-valuetext')).toBe('Checking the archive…');
    expect(fixture.nativeElement.querySelector('.transfer-progress-percent')).toBeNull();
    expect(fixture.nativeElement.querySelector('.transfer-progress-fill').style.width).toBe('');
  });

  it('announces bucketed progress so XHR events cannot spam the live region', async () => {
    await create('uploading');
    fixture.componentRef.setInput('bytesProcessed', 3);
    fixture.componentRef.setInput('totalBytes', 100);
    fixture.detectChanges();

    const live = () =>
      (fixture.nativeElement.querySelector('[data-testid="transfer-progress-live"]') as HTMLElement)
        .textContent?.trim();

    expect(live()).toBe('Uploading library, 0 percent complete.');

    fixture.componentRef.setInput('bytesProcessed', 42);
    fixture.detectChanges();
    expect(live()).toBe('Uploading library, 40 percent complete.');

    fixture.componentRef.setInput('bytesProcessed', 99);
    fixture.detectChanges();
    expect(live()).toBe('Uploading library, 95 percent complete.');
  });

  it('shows chunk position for multi-chunk transfers', async () => {
    await create('uploading');
    fixture.componentRef.setInput('bytesProcessed', 50);
    fixture.componentRef.setInput('totalBytes', 100);
    fixture.componentRef.setInput('completedChunks', 12);
    fixture.componentRef.setInput('totalChunks', 340);
    fixture.detectChanges();

    expect(
      (fixture.nativeElement.querySelector('[data-testid="transfer-progress-detail"]') as HTMLElement)
        .textContent,
    ).toContain('Part 12 of 340');
  });

  it('marks a paused transfer and suppresses a live rate', async () => {
    await create('uploading');
    fixture.componentRef.setInput('bytesProcessed', 50);
    fixture.componentRef.setInput('totalBytes', 100);
    fixture.componentRef.setInput('rateBytesPerSecond', 5 * 1024 * 1024);
    fixture.componentRef.setInput('paused', true);
    fixture.detectChanges();

    expect(track().classList).toContain('is-paused');
    const detail = fixture.nativeElement.querySelector(
      '[data-testid="transfer-progress-detail"]',
    ) as HTMLElement;
    expect(detail.textContent).not.toContain('MiB/s');
  });
});
