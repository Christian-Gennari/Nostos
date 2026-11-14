import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';

import { LibraryTransferHostComponent } from '../../library-transfer/components/library-transfer-host.component';
import { HASH_WORKER_FACTORY } from '../../core/uploads/hash/hash-worker';
import { MockLibraryTransferTransport } from '../../library-transfer/testing/mock-library-transfer-transport';
import { LIBRARY_TRANSFER_TRANSPORT } from '../../library-transfer/services/library-transfer-transport';
import { LibraryMoveSettingsComponent } from './library-move-settings.component';

describe('LibraryMoveSettingsComponent', () => {
  let fixture: ComponentFixture<LibraryMoveSettingsComponent>;

  async function configure(
    inputs: { supportsSafeActivation?: boolean; focusImport?: boolean } = {},
  ): Promise<void> {
    TestBed.resetTestingModule();
await TestBed.configureTestingModule({
      imports: [LibraryMoveSettingsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate: vi.fn(), events: of([]) } },
        { provide: LIBRARY_TRANSFER_TRANSPORT, useValue: new MockLibraryTransferTransport() },
        { provide: HASH_WORKER_FACTORY, useValue: () => null },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(LibraryMoveSettingsComponent);
    fixture.componentRef.setInput('supportsSafeActivation', inputs.supportsSafeActivation ?? false);
    fixture.componentRef.setInput('focusImport', inputs.focusImport ?? false);
    fixture.detectChanges();
  }

  function host(): LibraryTransferHostComponent {
    return fixture.debugElement.query(By.directive(LibraryTransferHostComponent))
      .componentInstance as LibraryTransferHostComponent;
  }

  it('keeps the transfer card and its purpose line together', async () => {
    await configure();

    expect(fixture.nativeElement.querySelector('[data-testid="library-transfer-card"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="library-transfer-purpose"]')).not.toBeNull();
  });

  it('passes the safe-activation capability through to the shared host', async () => {
    await configure({ supportsSafeActivation: true });

    expect(host().supportsSafeActivation()).toBe(true);
  });

  it('reports a completed import to the page, which refreshes what Settings reads', async () => {
    await configure();
    let completed = 0;
    fixture.componentInstance.importCompleted.subscribe(() => (completed += 1));

    host().importCompleted.emit();

    expect(completed).toBe(1);
  });
});
