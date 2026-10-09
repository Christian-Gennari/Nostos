import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { CloudAccountDeletionStatus } from '../core/dtos/cloud-account-deletion.dtos';
import { CloudAccountDeletionService } from '../core/services/cloud-account-deletion.service';
import { CloudAuthService } from '../core/services/cloud-auth.service';
import { CloudAccountDeletionSettingsComponent } from './cloud-account-deletion-settings.component';

describe('CloudAccountDeletionSettingsComponent', () => {
  let fixture: ComponentFixture<CloudAccountDeletionSettingsComponent>;
  const pendingStatus: CloudAccountDeletionStatus = {
    state: 'GracePeriod',
    gracePeriodDays: 14,
    requestedAtUtc: '2026-10-01T12:00:00Z',
    eligibleAtUtc: '2026-10-15T12:00:00Z',
    completedAtUtc: null,
    canCancel: true,
    portableExportUrl: '/api/portability/export',
  };
  const deletionApi = {
    getStatus: vi.fn(() => of(pendingStatus)),
    requestDeletion: vi.fn(() => of(pendingStatus)),
    cancelDeletion: vi.fn(),
  };
  const auth = { loginUrl: vi.fn(() => '/api/auth/login?returnUrl=%2Fsettings') };

  beforeEach(async () => {
    deletionApi.getStatus.mockReset();
    deletionApi.getStatus.mockReturnValue(of(pendingStatus));
    deletionApi.requestDeletion.mockReset();
    deletionApi.requestDeletion.mockReturnValue(of(pendingStatus));
    auth.loginUrl.mockClear();

    await TestBed.configureTestingModule({
      imports: [CloudAccountDeletionSettingsComponent],
      providers: [
        { provide: CloudAccountDeletionService, useValue: deletionApi },
        { provide: CloudAuthService, useValue: auth },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CloudAccountDeletionSettingsComponent);
    fixture.detectChanges();
  });

  it('focuses the export recommendation and keeps the request disabled until DELETE is typed', async () => {
    const open = fixture.nativeElement.querySelector(
      '[data-testid="cloud-account-deletion-open"]',
    ) as HTMLButtonElement;
    open.click();
    fixture.detectChanges();
    await fixture.whenStable();

    const confirm = fixture.nativeElement.querySelector('.btn-confirm') as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    expect(deletionApi.requestDeletion).not.toHaveBeenCalled();

    const exportLink = fixture.nativeElement.querySelector(
      'a[href="/settings/library"]',
    ) as HTMLAnchorElement;
    expect(document.activeElement).toBe(exportLink);
    const input = fixture.nativeElement.querySelector(
      '[data-testid="cloud-account-deletion-confirmation"]',
    ) as HTMLInputElement;
    input.value = 'DELETE';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect((fixture.nativeElement.querySelector('.btn-confirm') as HTMLButtonElement).disabled)
      .toBe(false);
  });

  it('explains the consequences and subscription terms before the confirmation input', () => {
    fixture.nativeElement.querySelector('[data-testid="cloud-account-deletion-open"]').click();
    fixture.detectChanges();

    const dialog = fixture.nativeElement.querySelector('[role="alertdialog"]') as HTMLElement;
    expect(dialog.textContent).toContain('Access to your library will close immediately');
    expect(dialog.textContent).toContain('14 days to change your mind');
    expect(dialog.textContent).toContain('library, files, notes, writing, backups, and sign-in identity');
    expect(dialog.textContent).toContain(
      'Your subscription will not renew while this deletion request is pending',
    );
    expect(dialog.textContent).toContain('no further charge will be made');
    expect(dialog.textContent).toContain('not refunded automatically');
    expect(dialog.textContent).not.toContain('Paddle');
    expect(dialog.querySelector('a[href="https://nostos.page/account"]')).toBeNull();

    const exportParagraph = dialog.querySelector('a[href="/settings/library"]')?.closest('p') as
      | HTMLParagraphElement
      | null;
    const input = dialog.querySelector('#account-deletion-confirmation') as HTMLInputElement;
    const hint = dialog.querySelector('#account-deletion-confirmation-help') as HTMLElement;
    const cancel = dialog.querySelector('.btn-cancel') as HTMLButtonElement;
    expect(exportParagraph?.textContent).toContain('We recommend exporting your library first');
    expect(exportParagraph).not.toBeNull();
    expect(
      exportParagraph!.compareDocumentPosition(input) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(input.compareDocumentPosition(hint) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(hint.compareDocumentPosition(cancel) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('returns focus to the account control when the dialog is dismissed', async () => {
    const open = fixture.nativeElement.querySelector(
      '[data-testid="cloud-account-deletion-open"]',
    ) as HTMLButtonElement;
    open.click();
    fixture.detectChanges();
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('.btn-cancel') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(document.activeElement).toBe(open);
  });

  it('moves to the pending screen when the server accepts the request', () => {
    const pending = vi.fn();
    fixture.componentInstance.pending.subscribe(pending);
    fixture.componentInstance.confirmationText = 'DELETE';

    fixture.componentInstance.requestDeletion();

    expect(deletionApi.requestDeletion).toHaveBeenCalledTimes(1);
    expect(pending).toHaveBeenCalledWith(pendingStatus);
  });

  it('rechecks status after a network failure and reports a pending request when found', () => {
    const pending = vi.fn();
    fixture.componentInstance.pending.subscribe(pending);
    fixture.componentInstance.confirmationText = 'DELETE';
    deletionApi.requestDeletion.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 0, statusText: 'Network error' })),
    );

    fixture.componentInstance.requestDeletion();
    fixture.detectChanges();

    expect(deletionApi.getStatus).toHaveBeenCalledTimes(1);
    expect(pending).toHaveBeenCalledWith(pendingStatus);
    expect(fixture.componentInstance.errorMessage()).toBeNull();
  });

  it('reconciles an already-pending conflict into the pending screen', () => {
    const pending = vi.fn();
    fixture.componentInstance.pending.subscribe(pending);
    fixture.componentInstance.confirmationText = 'DELETE';
    deletionApi.requestDeletion.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({
        status: 409,
        error: { error: 'account_unavailable' },
      })),
    );

    fixture.componentInstance.requestDeletion();

    expect(deletionApi.getStatus).toHaveBeenCalledTimes(1);
    expect(pending).toHaveBeenCalledWith(pendingStatus);
    expect(fixture.componentInstance.errorMessage()).toBeNull();
  });

  it('says no request is on file when a failed request is followed by Active status', () => {
    fixture.componentInstance.confirmationText = 'DELETE';
    deletionApi.requestDeletion.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 0, statusText: 'Network error' })),
    );
    deletionApi.getStatus.mockReturnValueOnce(of({ ...pendingStatus, state: 'Active' }));

    fixture.componentInstance.requestDeletion();
    fixture.detectChanges();

    expect(fixture.componentInstance.errorMessage()).toBe(
      'Your account is active. Nostos has no deletion request on file.',
    );
  });

  it('keeps the request outcome explicit when both the request and status check fail', () => {
    fixture.componentInstance.confirmationText = 'DELETE';
    deletionApi.requestDeletion.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 0, statusText: 'Network error' })),
    );
    deletionApi.getStatus.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 0, statusText: 'Network error' })),
    );

    fixture.componentInstance.requestDeletion();
    fixture.detectChanges();

    expect(fixture.componentInstance.errorMessage()).toBe(
      'Nostos could not confirm whether the request was received. Check the deletion status before trying again.',
    );
    expect(fixture.nativeElement.textContent).toContain('Check deletion status');
  });

  it('offers sign-in and status recovery when the session has expired', () => {
    fixture.componentInstance.confirmationText = 'DELETE';
    deletionApi.requestDeletion.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 401, statusText: 'Unauthorized' })),
    );
    deletionApi.getStatus.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 401, statusText: 'Unauthorized' })),
    );

    fixture.componentInstance.requestDeletion();
    fixture.detectChanges();

    expect(fixture.componentInstance.sessionExpired()).toBe(true);
    expect(fixture.componentInstance.errorMessage()).toBe(
      'Your sign-in session expired. Sign in again, then check whether the deletion request was received.',
    );
    expect(fixture.nativeElement.querySelector('a[href="/api/auth/login?returnUrl=%2Fsettings"]'))
      .not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Check deletion status');
  });
});
