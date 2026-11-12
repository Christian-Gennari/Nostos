import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import type { CloudManagedAiUsage } from '../../core/dtos/cloud-ai-refill.dtos';
import type { DeploymentCapabilities } from '../../core/dtos/deployment-capabilities.dtos';
import { AssistantAllowanceService } from './assistant-allowance.service';

function capabilities(overrides: Partial<DeploymentCapabilities> = {}): DeploymentCapabilities {
  return {
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
    ...overrides,
  };
}

const USAGE: CloudManagedAiUsage = {
  state: 'exhausted',
  renewsAtUtc: '2026-12-01T00:00:00Z',
  refill: { available: true, state: 'active' },
};

describe('AssistantAllowanceService (#1556)', () => {
  let service: AssistantAllowanceService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AssistantAllowanceService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('loads the Cloud renewal date and keeps the existing account link', () => {
    service.ensureUsage();

    http.expectOne('/api/runtime/capabilities').flush(capabilities());
    const usage = http.expectOne('/api/cloud/ai/usage');
    usage.flush(USAGE);

    expect(service.usage()).toEqual(USAGE);
    expect(service.accountManagementUrl()).toBe('https://nostos.page/account');
    expect(service.usageAvailable()).toBe(true);
    expect(service.usageFailed()).toBe(false);
  });

  it('never calls the usage endpoint on a host without managed metering', () => {
    service.ensureUsage();

    http.expectOne('/api/runtime/capabilities').flush(capabilities({
      deploymentMode: 'SelfHosted',
      requiresAuthentication: false,
      canConfigureAiProvider: true,
      managedAi: false,
      managedVoiceTranscription: false,
      usesCloudStorage: false,
      supportsLocalBackupConfiguration: true,
      supportsPrivateNetworkAccess: true,
      usageMeteringAvailable: false,
      accountManagementUrl: null,
    }));
    http.expectNone('/api/cloud/ai/usage');

    expect(service.usageAvailable()).toBe(false);
    expect(service.accountManagementUrl()).toBeNull();
    expect(service.canConfigureAiProvider()).toBe(true);
  });

  it('is idempotent: asking again does not open a second request', () => {
    service.ensureUsage();
    service.ensureUsage();
    service.ensureUsage();

    http.expectOne('/api/runtime/capabilities').flush(capabilities());
    http.expectOne('/api/cloud/ai/usage').flush(USAGE);

    service.ensureUsage();
    http.expectNone('/api/runtime/capabilities');
    http.expectNone('/api/cloud/ai/usage');
  });

  it('fails closed when usage cannot be read', () => {
    service.ensureUsage();

    http.expectOne('/api/runtime/capabilities').flush(capabilities());
    http.expectOne('/api/cloud/ai/usage').flush('', {
      status: 503,
      statusText: 'Service Unavailable',
    });

    expect(service.usage()).toBeNull();
    expect(service.usageFailed()).toBe(true);
  });
});
