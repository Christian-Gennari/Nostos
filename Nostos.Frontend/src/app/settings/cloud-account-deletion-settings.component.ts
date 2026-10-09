import { HttpErrorResponse } from '@angular/common/http';
import { CommonModule } from '@angular/common';
import { AfterViewChecked, Component, ElementRef, inject, input, output, signal, ViewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { CloudAccountDeletionStatus } from '../core/dtos/cloud-account-deletion.dtos';
import { CloudAccountDeletionService } from '../core/services/cloud-account-deletion.service';
import { CloudAuthService } from '../core/services/cloud-auth.service';
import { ButtonComponent } from '../ui/button/button.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';

@Component({
  selector: 'app-cloud-account-deletion-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonComponent, ConfirmModal],
  templateUrl: './cloud-account-deletion-settings.component.html',
  styleUrl: './cloud-account-deletion-settings.component.css',
})
export class CloudAccountDeletionSettingsComponent implements AfterViewChecked {
  private readonly deletion = inject(CloudAccountDeletionService);
  private readonly auth = inject(CloudAuthService);

  readonly accountManagementUrl = input<string | null>(null);
  readonly pending = output<CloudAccountDeletionStatus>();
  readonly dialogOpen = signal(false);
  readonly requestPending = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly sessionExpired = signal(false);
  readonly statusCheckPending = signal(false);
  confirmationText = '';
  private focusConfirmationWhenReady = false;

  @ViewChild('openButton', { read: ElementRef }) private openButton?: ElementRef<HTMLButtonElement>;
  @ViewChild('confirmationInput') private confirmationInput?: ElementRef<HTMLInputElement>;

  ngAfterViewChecked(): void {
    if (!this.focusConfirmationWhenReady || !this.confirmationInput?.nativeElement.isConnected) return;
    this.confirmationInput.nativeElement.focus({ preventScroll: true });
    this.focusConfirmationWhenReady = false;
  }

  openConfirmation(): void {
    this.confirmationText = '';
    this.errorMessage.set(null);
    this.sessionExpired.set(false);
    this.dialogOpen.set(true);
    this.focusConfirmationWhenReady = true;
  }

  closeConfirmation(): void {
    if (this.requestPending()) return;
    this.dialogOpen.set(false);
    this.confirmationText = '';
    queueMicrotask(() => this.openButton?.nativeElement.focus({ preventScroll: true }));
  }

  confirmationIsComplete(): boolean {
    return this.confirmationText.trim() === 'DELETE';
  }

  requestDeletion(): void {
    if (!this.confirmationIsComplete() || this.requestPending()) return;

    this.errorMessage.set(null);
    this.sessionExpired.set(false);
    this.requestPending.set(true);
    this.deletion.requestDeletion().subscribe({
      next: (status) => {
        this.requestPending.set(false);
        this.applyRequestStatus(status);
      },
      error: (error: unknown) => this.reconcileRequestFailure(error),
    });
  }

  checkDeletionStatus(): void {
    if (this.statusCheckPending()) return;

    this.errorMessage.set(null);
    this.sessionExpired.set(false);
    this.statusCheckPending.set(true);
    this.deletion.getStatus().subscribe({
      next: (status) => {
        this.statusCheckPending.set(false);
        this.applyRequestStatus(status);
      },
      error: (error: unknown) => {
        this.statusCheckPending.set(false);
        this.showStatusCheckError(error);
      },
    });
  }

  loginUrl(): string {
    return this.auth.loginUrl('/settings');
  }

  private reconcileRequestFailure(requestError: unknown): void {
    this.deletion.getStatus().subscribe({
      next: (status) => {
        this.requestPending.set(false);
        this.applyRequestStatus(status);
        if (status.state === 'Active') {
          this.errorMessage.set('Your account is active. Nostos has no deletion request on file.');
        }
      },
      error: (statusError: unknown) => {
        this.requestPending.set(false);
        if (this.httpStatus(requestError) === 401 || this.httpStatus(statusError) === 401) {
          this.sessionExpired.set(true);
          this.errorMessage.set(
            'Your sign-in session expired. Sign in again, then check whether the deletion request was received.',
          );
          return;
        }

        this.errorMessage.set(
          'Nostos could not confirm whether the request was received. Check the deletion status before trying again.',
        );
      },
    });
  }

  private applyRequestStatus(status: CloudAccountDeletionStatus): void {
    if (status.state === 'GracePeriod' || status.state === 'Destroying' || status.state === 'Failed') {
      this.dialogOpen.set(false);
      this.pending.emit(status);
      return;
    }

    if (status.state === 'Deleted') {
      this.dialogOpen.set(false);
      this.pending.emit(status);
      return;
    }

    this.errorMessage.set('Your account is active. Nostos has no deletion request on file.');
  }

  private showStatusCheckError(error: unknown): void {
    if (this.httpStatus(error) === 401) {
      this.sessionExpired.set(true);
      this.errorMessage.set(
        'Your sign-in session expired. Sign in again, then reopen Settings to check the deletion status.',
      );
      return;
    }

    this.errorMessage.set('Nostos could not retrieve the deletion status. Try again when you are connected.');
  }

  private httpStatus(error: unknown): number | null {
    return error instanceof HttpErrorResponse ? error.status : null;
  }
}
