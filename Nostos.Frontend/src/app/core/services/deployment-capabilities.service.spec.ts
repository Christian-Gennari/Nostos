import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';

import { DeploymentCapabilitiesService } from './deployment-capabilities.service';

describe('DeploymentCapabilitiesService', () => {
  let service: DeploymentCapabilitiesService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(DeploymentCapabilitiesService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('loads the runtime capability contract from the backend', async () => {
    const resultPromise = firstValueFrom(service.get());

    const request = http.expectOne('/api/runtime/capabilities');
    expect(request.request.method).toBe('GET');

    request.flush({
      deploymentMode: 'SelfHosted',
      requiresAuthentication: false,
      canConfigureAiProvider: true,
      managedAi: false,
      managedVoiceTranscription: false,
      usesCloudStorage: false,
      supportsLocalBackupConfiguration: true,
      supportsPrivateNetworkAccess: true,
      supportsEreaderAccess: true,
      usageMeteringAvailable: false,
      accountManagementUrl: null,
    });

    const result = await resultPromise;
    expect(result.deploymentMode).toBe('SelfHosted');
    expect(result.canConfigureAiProvider).toBe(true);
    expect(result.managedAi).toBe(false);
    expect(result.supportsEreaderAccess).toBe(true);
  });

  it('replays one server-authoritative result instead of refetching per consumer', async () => {
    const firstPromise = firstValueFrom(service.get());

    const request = http.expectOne('/api/runtime/capabilities');
    request.flush({
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
    });

    const first = await firstPromise;
    const second = await firstValueFrom(service.get());

    http.expectNone('/api/runtime/capabilities');
    expect(first).toEqual(second);
    expect(second.deploymentMode).toBe('Cloud');
    expect(second.accountManagementUrl).toBe('https://nostos.page/account');
  });
  it('can refetch the capability contract for a safe retry after a transient failure', async () => {
    const firstPromise = firstValueFrom(service.get());
    http.expectOne('/api/runtime/capabilities').flush({
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
    });
    await firstPromise;

    const refreshPromise = firstValueFrom(service.get(true));
    const request = http.expectOne('/api/runtime/capabilities');
    request.flush({
      deploymentMode: 'SelfHosted',
      requiresAuthentication: false,
      canConfigureAiProvider: true,
      managedAi: false,
      managedVoiceTranscription: false,
      usesCloudStorage: false,
      supportsLocalBackupConfiguration: true,
      supportsPrivateNetworkAccess: true,
      supportsEreaderAccess: true,
      usageMeteringAvailable: false,
      accountManagementUrl: null,
    });

    expect((await refreshPromise).deploymentMode).toBe('SelfHosted');
  });

  it('exposes reactive loading, failed, capabilities, peek(), and canonical computed flags', async () => {
    expect(service.capabilities()).toBeNull();
    expect(service.peek()).toBeNull();
    expect(service.loading()).toBe(false);
    expect(service.failed()).toBe(false);
    expect(service.isCloud()).toBe(false);
    expect(service.isSelfHosted()).toBe(false);
    expect(service.supportsExternalMediaLibraries()).toBe(false);
    expect(service.supportsCalibre()).toBe(false);
    expect(service.accountManagementUrl()).toBeNull();

    const pendingPromise = firstValueFrom(service.get());
    expect(service.loading()).toBe(true);
    expect(service.failed()).toBe(false);

    const request = http.expectOne('/api/runtime/capabilities');
    request.flush({
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
      supportsLibraryMigration: true,
      supportsSafeActivation: true,
      supportsManagedBackups: true,
      supportsManagedBackupRestore: true,
      supportsAccountDeletion: true,
      supportsCalibreUpload: true,
      supportsResumableBookUploads: true,
      supportsMcpConnectedApps: true,
      supportsCarrelDevices: true,
      accountManagementUrl: 'https://nostos.page/account',
      feedbackUrl: 'https://nostos.page/feedback?from=settings',
    });

    await pendingPromise;

    expect(service.loading()).toBe(false);
    expect(service.failed()).toBe(false);
    expect(service.peek()?.deploymentMode).toBe('Cloud');
    expect(service.capabilities()?.deploymentMode).toBe('Cloud');
    expect(service.isCloud()).toBe(true);
    expect(service.isSelfHosted()).toBe(false);
    expect(service.canConfigureAiProvider()).toBe(false);
    expect(service.managedAi()).toBe(true);
    expect(service.managedVoiceTranscription()).toBe(true);
    expect(service.supportsLibraryMigration()).toBe(true);
    expect(service.supportsSafeActivation()).toBe(true);
    expect(service.supportsManagedBackups()).toBe(true);
    expect(service.supportsManagedBackupRestore()).toBe(true);
    expect(service.supportsManagedRestore()).toBe(true);
    expect(service.supportsAccountDeletion()).toBe(true);
    expect(service.supportsExternalMediaLibraries()).toBe(false);
    expect(service.supportsCalibreImport()).toBe(false);
    expect(service.supportsCalibreUpload()).toBe(true);
    expect(service.supportsCalibre()).toBe(true);
    expect(service.supportsResumableBookUploads()).toBe(true);
    expect(service.supportsMcpConnectedApps()).toBe(true);
    expect(service.supportsCarrelDevices()).toBe(true);
    expect(service.accountManagementUrl()).toBe('https://nostos.page/account');
    expect(service.feedbackUrl()).toBe('https://nostos.page/feedback?from=settings');
  });

  it('sets failed and clears capabilities on error, then allows retrying with get()', async () => {
    const failedPromise = firstValueFrom(service.get()).catch((err) => err);
    expect(service.loading()).toBe(true);

    http.expectOne('/api/runtime/capabilities').flush('offline', {
      status: 503,
      statusText: 'Service Unavailable',
    });

    await failedPromise;

    expect(service.loading()).toBe(false);
    expect(service.failed()).toBe(true);
    expect(service.capabilities()).toBeNull();
    expect(service.peek()).toBeNull();
    expect(service.isCloud()).toBe(false);

    const retryPromise = firstValueFrom(service.get());
    expect(service.loading()).toBe(true);
    expect(service.failed()).toBe(false);

    http.expectOne('/api/runtime/capabilities').flush({
      deploymentMode: 'SelfHosted',
      requiresAuthentication: false,
      canConfigureAiProvider: true,
      managedAi: false,
      managedVoiceTranscription: false,
      usesCloudStorage: false,
      supportsLocalBackupConfiguration: true,
      supportsPrivateNetworkAccess: true,
      supportsEreaderAccess: true,
      usageMeteringAvailable: false,
      supportsExternalMediaLibraries: true,
      supportsCalibreImport: true,
    });

    await retryPromise;

    expect(service.loading()).toBe(false);
    expect(service.failed()).toBe(false);
    expect(service.isSelfHosted()).toBe(true);
    expect(service.canConfigureAiProvider()).toBe(true);
    expect(service.supportsExternalMediaLibraries()).toBe(true);
    expect(service.supportsCalibreImport()).toBe(true);
    expect(service.supportsCalibre()).toBe(true);
  });
});
