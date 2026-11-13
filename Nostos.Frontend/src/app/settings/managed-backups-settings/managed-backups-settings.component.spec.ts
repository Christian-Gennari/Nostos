import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';

import { ManagedBackupListing } from '../../core/dtos/managed-backups.dtos';
import { ManagedBackupsService } from '../../core/services/managed-backups.service';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import { createDeploymentCapabilitiesServiceMock } from '../../core/services/deployment-capabilities.testing';
import { ManagedBackupsSettingsComponent } from './managed-backups-settings.component';

const listing: ManagedBackupListing = {
  retentionDays: 14,
  backups: [
    {
      id: 'nightly-backup-1',
      createdAtUtc: '2026-10-07T00:30:00Z',
      archiveBytes: 1_024,
      mediaBytes: 2_048,
      state: 'completed',
    },
  ],
};

const managedBackupsServiceMock = {
  getBackups: vi.fn<() => Observable<ManagedBackupListing>>(),
  restore: vi.fn<(id: string, body: { confirm: boolean }) => Observable<{
    backupId: string;
    restoredAtUtc: string;
  }>>(),
};

function capabilities(restore: boolean) {
  return createDeploymentCapabilitiesServiceMock({
    deploymentMode: 'Cloud',
    requiresAuthentication: true,
    canConfigureAiProvider: false,
    managedAi: true,
    managedVoiceTranscription: true,
    usesCloudStorage: true,
    supportsLocalBackupConfiguration: false,
    supportsManagedBackups: true,
    supportsManagedBackupRestore: restore,
    supportsPrivateNetworkAccess: false,
    supportsEreaderAccess: true,
    usageMeteringAvailable: false,
    accountManagementUrl: null,
    feedbackUrl: null,
  });
}

describe('ManagedBackupsSettingsComponent', () => {
  let fixture: ComponentFixture<ManagedBackupsSettingsComponent>;
  let component: ManagedBackupsSettingsComponent;

  async function configure(isManageLibraryPage = true, restore = true): Promise<void> {
    TestBed.resetTestingModule();
managedBackupsServiceMock.getBackups.mockClear();
    managedBackupsServiceMock.getBackups.mockReturnValue(of(listing));
    managedBackupsServiceMock.restore.mockClear();
    managedBackupsServiceMock.restore.mockReturnValue(
      of({ backupId: 'nightly-backup-1', restoredAtUtc: '2026-10-09T14:00:00Z' }),
    );

    await TestBed.configureTestingModule({
      imports: [ManagedBackupsSettingsComponent],
      providers: [
        { provide: ManagedBackupsService, useValue: managedBackupsServiceMock },
        { provide: DeploymentCapabilitiesService, useValue: capabilities(restore) },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ManagedBackupsSettingsComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isManageLibraryPage', isManageLibraryPage);
    fixture.detectChanges();
  }

  it('reads the backups on the Manage library route, and not before', async () => {
    await configure(false);
    expect(managedBackupsServiceMock.getBackups).not.toHaveBeenCalled();

    fixture.destroy();
    await configure(true);
    expect(managedBackupsServiceMock.getBackups).toHaveBeenCalledTimes(1);
  });

  it('shows the newest backup and keeps the list collapsed until asked', async () => {
    await configure();

    expect(fixture.nativeElement.querySelector('[data-testid="managed-backups-latest"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="managed-backup"]')).toBeNull();

    (
      fixture.nativeElement.querySelector('[data-testid="managed-backups-toggle"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('[data-testid="managed-backup"]')).toHaveLength(1);
  });

  it('offers no restore when the host cannot restore, only the last backup', async () => {
    await configure(true, false);

    expect(fixture.nativeElement.querySelector('[data-testid="managed-backups-latest"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="managed-backups-toggle"]')).toBeNull();
  });

  it('asks before replacing the library and stays busy while the server works', async () => {
    await configure();
    const pending = new Subject<{ backupId: string; restoredAtUtc: string }>();
    managedBackupsServiceMock.restore.mockReturnValue(pending.asObservable());
    component.requestManagedRestore('nightly-backup-1');
    fixture.detectChanges();

    expect(component.pendingManagedRestore()).toBe('nightly-backup-1');
    expect(managedBackupsServiceMock.restore).not.toHaveBeenCalled();

    component.confirmManagedRestore();

    expect(managedBackupsServiceMock.restore).toHaveBeenCalledWith('nightly-backup-1', {
      confirm: true,
    });
    expect(component.managedRestoreBusy()).toBe(true);
    component.cancelManagedRestore();
    expect(component.pendingManagedRestore()).toBe('nightly-backup-1');
  });

  it('reports a verified rejection without calling the outcome unknown', async () => {
    await configure();
    managedBackupsServiceMock.restore.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 404 })),
    );

    component.requestManagedRestore('nightly-backup-1');
    component.confirmManagedRestore();

    expect(component.managedRestoreUncertain()).toBe(false);
    expect(component.managedRestoreError()).toContain('no longer available');
  });

  it('keeps an unconfirmed outcome visible and refuses to retry', async () => {
    await configure();
    managedBackupsServiceMock.restore.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 503 })),
    );

    component.requestManagedRestore('nightly-backup-1');
    component.confirmManagedRestore();

    expect(component.managedRestoreUncertain()).toBe(true);
    component.requestManagedRestore('nightly-backup-1');
    expect(component.pendingManagedRestore()).toBeNull();
  });
});
