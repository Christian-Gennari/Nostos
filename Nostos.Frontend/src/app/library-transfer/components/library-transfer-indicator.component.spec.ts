import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Router } from '@angular/router';
import { vi } from 'vitest';

import { CloudEntryService } from '../../core/services/cloud-entry.service';
import { LibraryTransferCoordinator } from '../services/library-transfer-coordinator.service';
import { TransferResumeStore } from '../services/transfer-resume-store.service';
import { LibraryTransferIndicatorComponent } from './library-transfer-indicator.component';

describe('LibraryTransferIndicatorComponent', () => {
  let fixture: ComponentFixture<LibraryTransferIndicatorComponent>;
  const state = signal<{ kind: string }>({ kind: 'idle' });
  const resumeStore = { load: vi.fn(() => null) };
  const entry = {
    firstRunImportPending: signal(false),
    view: signal({ kind: 'product' }),
    openManageLibraryFromFirstRun: vi.fn(),
  };
  const router = { navigate: vi.fn() };

  beforeEach(async () => {
    state.set({ kind: 'idle' });
    resumeStore.load.mockReturnValue(null);
    entry.firstRunImportPending.set(false);
    entry.view.set({ kind: 'product' });
    entry.openManageLibraryFromFirstRun.mockClear();
    router.navigate.mockClear();

    await TestBed.configureTestingModule({
      imports: [LibraryTransferIndicatorComponent],
      providers: [
        { provide: LibraryTransferCoordinator, useValue: { state } },
        { provide: TransferResumeStore, useValue: resumeStore },
        { provide: CloudEntryService, useValue: entry },
        { provide: Router, useValue: router },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(LibraryTransferIndicatorComponent);
    fixture.detectChanges();
  });

  it('stays hidden without active or resumable transfer state', () => {
    expect(fixture.nativeElement.querySelector('[data-testid="library-transfer-indicator"]')).toBeNull();
  });

  it('returns to the selected import section while a transfer is active', () => {
    state.set({ kind: 'uploading' });
    fixture.detectChanges();

    const indicator = fixture.nativeElement.querySelector(
      '[data-testid="library-transfer-indicator"]',
    ) as HTMLElement;
    expect(indicator.textContent).toContain('in progress');

    (indicator.querySelector('[data-testid="library-transfer-return"]') as HTMLButtonElement).click();

    expect(router.navigate).toHaveBeenCalledWith(['/settings/library'], {
      queryParams: { action: 'import' },
    });
  });

  it('re-enters Manage library from the first-run welcome after a reload', () => {
    resumeStore.load.mockReturnValue({ jobId: 'job-1' } as never);
    entry.firstRunImportPending.set(true);
    fixture.destroy();
    fixture = TestBed.createComponent(LibraryTransferIndicatorComponent);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('[data-testid="library-transfer-return"]') as HTMLButtonElement).click();

    expect(entry.openManageLibraryFromFirstRun).toHaveBeenCalledTimes(1);
    expect(router.navigate).toHaveBeenCalledWith(['/settings/library'], {
      queryParams: { action: 'import', source: 'first-run' },
    });
  });
});
