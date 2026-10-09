import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { CloudAccountDeletionStatus } from '../dtos/cloud-account-deletion.dtos';
import { CloudSession } from '../dtos/cloud-auth.dtos';
import { CloudOnboardingSnapshot } from '../dtos/cloud-onboarding.dtos';
import { DeploymentCapabilities } from '../dtos/deployment-capabilities.dtos';
import { BooksService } from './books.service';
import { CloudAccountDeletionService } from './cloud-account-deletion.service';
import { CloudAuthService } from './cloud-auth.service';
import { CloudEntryService } from './cloud-entry.service';
import { CloudOnboardingService } from './cloud-onboarding.service';
import { DeploymentCapabilitiesService } from './deployment-capabilities.service';
import { PortableLibraryService } from './portable-library.service';

describe('CloudEntryService account deletion', () => {
  let service: CloudEntryService;
  let currentSession: CloudSession;
  let capabilities: DeploymentCapabilities;
  const pendingStatus: CloudAccountDeletionStatus = {
    state: 'GracePeriod',
    gracePeriodDays: 14,
    requestedAtUtc: '2026-10-01T12:00:00Z',
    eligibleAtUtc: '2026-10-15T12:00:00Z',
    completedAtUtc: null,
    canCancel: true,
    portableExportUrl: '/api/portability/export',
  };
  const readyState: CloudOnboardingSnapshot = {
    state: 'ready',
    subscriptionStatus: 'Active',
    ready: true,
    canCheckout: false,
    canCheckSubscription: false,
    canManageSubscription: false,
    canRetry: false,
    selectedOffer: null,
  };
  const capabilitiesService = {
    get: vi.fn(() => of(capabilities)),
  };
  const authService = {
    getSession: vi.fn(() => of(currentSession)),
    loginUrl: vi.fn(() => '/api/auth/login'),
  };
  const onboardingService = {
    getState: vi.fn(() => of(readyState)),
  };
  const deletionService = {
    getStatus: vi.fn(() => of(pendingStatus)),
    cancelDeletion: vi.fn(() => of({ ...pendingStatus, state: 'Cancelled', canCancel: false })),
  };

  beforeEach(() => {
    capabilities = {
      deploymentMode: 'Cloud',
      requiresAuthentication: true,
      canConfigureAiProvider: false,
      managedAi: true,
      managedVoiceTranscription: true,
      usesCloudStorage: true,
      supportsLocalBackupConfiguration: false,
      supportsPrivateNetworkAccess: false,
      supportsEreaderAccess: true,
      usageMeteringAvailable: true,
      accountManagementUrl: 'https://nostos.page/account',
      feedbackUrl: 'https://nostos.page/feedback?from=settings',
      supportsAccountDeletion: true,
    };
    currentSession = {
      authenticated: true,
      accountState: 'DeletionRequested',
      account: { id: 'account-1', displayName: 'Reader', email: 'reader@example.test' },
    };
    capabilitiesService.get.mockReset();
    capabilitiesService.get.mockImplementation(() => of(capabilities));
    authService.getSession.mockReset();
    authService.getSession.mockImplementation(() => of(currentSession));
    onboardingService.getState.mockReset();
    onboardingService.getState.mockReturnValue(of(readyState));
    deletionService.getStatus.mockReset();
    deletionService.getStatus.mockReturnValue(of(pendingStatus));
    deletionService.cancelDeletion.mockReset();
    deletionService.cancelDeletion.mockReturnValue(
      of({ ...pendingStatus, state: 'Cancelled', canCancel: false }),
    );

    TestBed.configureTestingModule({
      providers: [
        CloudEntryService,
        { provide: DeploymentCapabilitiesService, useValue: capabilitiesService },
        { provide: CloudAuthService, useValue: authService },
        { provide: CloudOnboardingService, useValue: onboardingService },
        { provide: PortableLibraryService, useValue: {} },
        { provide: BooksService, useValue: { getStatusCounts: vi.fn(() => of({ all: 2 })) } },
        { provide: CloudAccountDeletionService, useValue: deletionService },
      ],
    });
    service = TestBed.inject(CloudEntryService);
  });

  it('reopens the pending screen from a DeletionRequested session and shows the server deadline', async () => {
    await service.initialize();

    expect(service.view().kind).toBe('account_deletion');
    expect(service.accountDeletionStatus()).toEqual(pendingStatus);
    expect(service.accountDeletionStatusLoading()).toBe(false);
    expect(service.deletionExportUrl()).toBe('/api/portability/export');
    expect(service.canCancelDeletion()).toBe(true);
    expect(deletionService.getStatus).toHaveBeenCalledTimes(1);
    expect(onboardingService.getState).not.toHaveBeenCalled();
  });

  it('keeps deletion-status API access off when the Cloud capability is absent', async () => {
    capabilities.supportsAccountDeletion = false;

    await service.initialize();

    expect(service.view().kind).toBe('account_unavailable');
    expect(deletionService.getStatus).not.toHaveBeenCalled();
  });

  it('returns to the normal Cloud product after cancellation succeeds', async () => {
    currentSession = { ...currentSession, accountState: 'Active' };

    await service.cancelAccountDeletion();

    expect(deletionService.cancelDeletion).toHaveBeenCalledTimes(1);
    expect(authService.getSession).toHaveBeenCalledWith(true);
    expect(onboardingService.getState).toHaveBeenCalledTimes(1);
    expect(service.view().kind).toBe('product');
    expect(service.accountDeletionActionError()).toBeNull();
  });

  it('keeps the grace-period view and explains when cancellation cannot be confirmed', async () => {
    deletionService.cancelDeletion.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 0, statusText: 'Network error' })),
    );
    deletionService.getStatus.mockReturnValueOnce(of(pendingStatus));

    await service.cancelAccountDeletion();

    expect(service.view().kind).toBe('account_deletion');
    expect(service.accountDeletionStatus()).toEqual(pendingStatus);
    expect(service.canCancelDeletion()).toBe(true);
    expect(service.accountDeletionActionError()).toBe(
      'Nostos could not confirm whether cancellation succeeded. Check the status below before leaving this page.',
    );
  });

  it('explains when the grace period has ended and disables cancellation', async () => {
    deletionService.cancelDeletion.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({
        status: 409,
        error: { error: 'deletion_not_recoverable' },
      })),
    );
    deletionService.getStatus.mockReturnValueOnce(
      of({ ...pendingStatus, canCancel: false, portableExportUrl: null }),
    );

    await service.cancelAccountDeletion();

    expect(service.view().kind).toBe('account_deletion');
    expect(service.accountDeletionActionError()).toBe(
      'The 14-day grace period has ended. This deletion can no longer be cancelled.',
    );
    expect(service.canCancelDeletion()).toBe(false);
    expect(service.deletionExportUrl()).toBeNull();
  });

  it('shows the completed-deletion state when cancellation arrives after final erasure', async () => {
    deletionService.cancelDeletion.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({
        status: 409,
        error: { error: 'deletion_not_recoverable' },
      })),
    );
    deletionService.getStatus.mockReturnValueOnce(
      of({ ...pendingStatus, state: 'Deleted', canCancel: false, portableExportUrl: null }),
    );

    await service.cancelAccountDeletion();

    expect(service.view().kind).toBe('account_unavailable');
    expect(service.accountDeletionStatus()?.state).toBe('Deleted');
    expect(service.accountDeletionActionError()).toBe(
      'The 14-day grace period has ended. This deletion can no longer be cancelled.',
    );
    expect(service.canCancelDeletion()).toBe(false);
    expect(service.deletionExportUrl()).toBeNull();
  });

  it('keeps a pending screen and checkable actions when status cannot be read', async () => {
    deletionService.getStatus.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 0, statusText: 'Network error' })),
    );

    await service.initialize();

    expect(service.view().kind).toBe('account_deletion');
    expect(service.accountDeletionStatusFailed()).toBe(true);
    expect(service.deletionExportUrl()).toBe('/api/portability/export');
    expect(service.canCancelDeletion()).toBe(true);
  });
});
