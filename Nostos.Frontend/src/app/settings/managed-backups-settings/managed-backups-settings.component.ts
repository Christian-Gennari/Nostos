import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';

import { ManagedBackupListing } from '../../core/dtos/managed-backups.dtos';
import { ManagedBackupsService } from '../../core/services/managed-backups.service';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { ConfirmModal } from '../../ui/confirm-modal/confirm-modal.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { NostosDatePipe } from '../../ui/pipes/nostos-date.pipe';
import { NoticeComponent } from '../../ui/notice/notice.component';

/**
 * Settings → Backups & data → Backups: the nightly managed backups a Cloud host
 * keeps, and the one restore question.
 *
 * Only the newest backup is shown until the restore list is opened. A restore
 * replaces the active library, so an outcome the server did not confirm is
 * reported as unknown rather than guessed at, and retrying is blocked.
 */
@Component({
  selector: 'app-managed-backups-settings',
  standalone: true,
  imports: [
    ButtonComponent,
    ConfirmModal,
    EmptyStateComponent,
    LoadingIndicatorComponent,
    NostosDatePipe,
    NostosIconComponent,
    NoticeComponent,
  ],
  templateUrl: './managed-backups-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', './managed-backups-settings.component.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ManagedBackupsSettingsComponent implements OnInit {
  private readonly managedBackupsService = inject(ManagedBackupsService);
  private readonly deploymentCapabilitiesService = inject(DeploymentCapabilitiesService);

  /** The dedicated `/settings/library` route, where a restore may be started. */
  readonly isManageLibraryPage = input(false);

  readonly supportsManagedRestore = this.deploymentCapabilitiesService.supportsManagedRestore;

  readonly managedBackups = signal<ManagedBackupListing | null>(null);
  /** Newest backup: the only one shown until the restore list is opened. */
  readonly latestManagedBackup = computed(() => {
    const backups = this.managedBackups()?.backups ?? [];
    return backups.reduce<(typeof backups)[number] | null>(
      (latest, backup) =>
        !latest || backup.createdAtUtc > latest.createdAtUtc ? backup : latest,
      null,
    );
  });
  readonly managedBackupsExpanded = signal(false);
  readonly managedBackupsLoading = signal(false);
  readonly managedBackupsFailed = signal(false);

  readonly pendingManagedRestore = signal<string | null>(null);
  readonly managedRestoreBusy = signal(false);
  readonly managedRestoreError = signal<string | null>(null);
  readonly managedRestoreUncertain = signal(false);

  ngOnInit(): void {
    if (this.isManageLibraryPage()) this.loadManagedBackups();
  }

  retryManagedBackups(): void {
    this.loadManagedBackups();
  }

  loadManagedBackups(): void {
    this.managedBackupsLoading.set(true);
    this.managedBackupsService.getBackups().subscribe({
      next: (listing) => {
        this.managedBackups.set(listing);
        this.managedBackupsFailed.set(false);
        this.managedBackupsLoading.set(false);
      },
      error: () => {
        this.managedBackups.set(null);
        this.managedBackupsFailed.set(true);
        this.managedBackupsLoading.set(false);
      },
    });
  }

  requestManagedRestore(id: string): void {
    if (
      !this.isManageLibraryPage() ||
      !this.supportsManagedRestore() ||
      this.managedBackupsLoading() ||
      this.managedRestoreBusy() ||
      this.managedRestoreUncertain() ||
      !this.managedBackups()?.backups.some(
        (backup) => backup.id === id && backup.state === 'completed',
      )
    ) {
      return;
    }

    this.managedRestoreError.set(null);
    this.pendingManagedRestore.set(id);
  }

  cancelManagedRestore(): void {
    if (this.managedRestoreBusy()) return;
    this.pendingManagedRestore.set(null);
  }

  confirmManagedRestore(): void {
    const id = this.pendingManagedRestore();

    if (
      !id ||
      this.managedRestoreBusy() ||
      this.managedRestoreUncertain() ||
      !this.isManageLibraryPage() ||
      !this.supportsManagedRestore() ||
      !this.managedBackups()?.backups.some(
        (backup) => backup.id === id && backup.state === 'completed',
      )
    ) {
      return;
    }

    this.managedRestoreBusy.set(true);
    this.managedRestoreError.set(null);

    this.managedBackupsService.restore(id, { confirm: true }).subscribe({
      next: (result) => {
        if (result.backupId !== id || !result.restoredAtUtc) {
          this.managedRestoreBusy.set(false);
          this.pendingManagedRestore.set(null);
          this.managedRestoreUncertain.set(true);
          this.managedRestoreError.set(
            'The restore response was unexpected. Your library may have ' +
              'been replaced. Reload Nostos, inspect your library, and contact ' +
              'support before attempting another restore.',
          );
          return;
        }

        // Activation has been explicitly confirmed by the server.
        // Reinitialize the application against the newly active library.
        // This discards in-memory library and managed-backup list state.
        window.location.reload();
      },
      error: (error: unknown) => {
        this.managedRestoreBusy.set(false);
        this.pendingManagedRestore.set(null);

        if (error instanceof HttpErrorResponse) {
          if (error.status === 404) {
            this.managedRestoreError.set(
              'This backup is no longer available for your account. ' +
                'Reload the backup list before selecting another.',
            );
            return;
          }

          if (error.status === 409) {
            this.managedRestoreError.set(
              'The restore was rejected because the library mapping changed. ' +
                'Reload Nostos before selecting another backup.',
            );
            return;
          }

          if (error.status === 422) {
            this.managedRestoreError.set(
              'The backup failed staged restore verification and was not activated.',
            );
            return;
          }

          if (error.status === 501) {
            this.managedRestoreError.set('Managed backup restore is not supported by this host.');
            return;
          }

          if (error.status === 401 || error.status === 403) {
            this.managedRestoreError.set(
              'Your session does not permit this restore. ' +
                'Sign in again and inspect your library before retrying.',
            );
            return;
          }

          if (error.status === 400 || error.status === 429) {
            this.managedRestoreError.set(
              'The restore request was rejected. ' +
                'Review the request or wait for the rate limit to clear.',
            );
            return;
          }
        }

        // A timeout, lost connection, or 5xx may occur after activation.
        // Do not assert that the original library is still active.
        this.managedRestoreUncertain.set(true);
        this.managedRestoreError.set(
          'Nostos could not confirm the restore outcome. Your library may ' +
            'already have been replaced. Reload Nostos and inspect the library ' +
            'before doing anything else. Contact support if the outcome is unclear.',
        );
      },
    });
  }
}
